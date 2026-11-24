// Tests for the RTMP TCP listener over real loopback sockets on OS-assigned ports: concurrent publishers, the
// connection limit, abrupt disconnects, isolation of a malformed client, graceful shutdown with active publishers,
// bind failures and IPv6.

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Rtmp;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Rtmp;

/// <summary>
/// Tests for <see cref="RtmpListener"/>.
/// </summary>
public sealed class RtmpListenerTests : IAsyncDisposable
{
    /// <summary>Clients to dispose after the test.</summary>
    private readonly System.Collections.Concurrent.ConcurrentBag<RtmpTestClient> _clients = [];

    /// <summary>The server dependencies.</summary>
    private RtmpTestServer _server = new(LoopbackOptions());

    /// <summary>The listener under test, once started.</summary>
    private RtmpListener? _listener;

    /// <summary>Verifies that several clients publish concurrently and each stream's media reaches the handler.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ConcurrentPublishers_AreServedIndependently()
    {
        var listener = await StartAsync();
        string[] keys = [RtmpTestServer.StreamKey, "secret-key-0002", "secret-key-0003"];

        await Task.WhenAll(keys.Select(async key =>
        {
            var client = await ConnectAsync(listener);
            var (streamId, code) = await client.PublishFlowAsync(key);
            Assert.Equal("NetStream.Publish.Start", code);
            await client.SendMessageAsync(6, streamId, RtmpMessageType.Video, 0, new byte[2000]);
        })).WithinSafetyTimeout();

        await Eventually.WaitUntilAsync(() => _server.Handler.Events.Count(static e => e.Kind == "message") == 3, "every publisher's frame arrives");
        Assert.Equal(
            ["camera1", "camera2", "camera3"],
            _server.Handler.Events.Where(static e => e.Kind == "message").Select(static e => e.Session.StreamIdentity).Order());
        Assert.Equal(3, _server.Registry.Count);
        Assert.Equal(3, listener.ActiveConnectionCount);
    }

    /// <summary>
    /// Verifies the connection limit: a connection beyond it is closed on arrival and counted as rejected, and a new
    /// connection is admitted once an existing one has closed.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ConnectionLimit_RejectsExcessConnections()
    {
        var options = LoopbackOptions();
        options.MaxConnections = 2;
        _server.Dispose();
        _server = new RtmpTestServer(options);
        using var rejected = new MetricCollector<long>(_server.MeterFactory, RtmpMetrics.MeterName, "rtmp.connections.rejected");
        using var accepted = new MetricCollector<long>(_server.MeterFactory, RtmpMetrics.MeterName, "rtmp.connections.accepted");
        var listener = await StartAsync();
        var first = await ConnectAsync(listener);
        await first.HandshakeAsync();
        var second = await ConnectAsync(listener);
        await second.HandshakeAsync();

        var third = await ConnectAsync(listener);
        await third.WaitForCloseAsync().WithinSafetyTimeout();

        Assert.Equal("connection_limit", rejected.LastMeasurement?.Tags["reason"]);
        Assert.Contains(_server.Logs.GetSnapshot(), static r => r.Id.Id == 5002);
        Assert.Equal(2, listener.ActiveConnectionCount);

        await first.DisposeAsync();
        await Eventually.WaitUntilAsync(() => listener.ActiveConnectionCount == 1, "the first connection is released");
        var fourth = await ConnectAsync(listener);
        await fourth.HandshakeAsync();
        Assert.Equal(3, accepted.GetMeasurementSnapshot().Count);
    }

    /// <summary>Verifies that a TCP reset while publishing ends the session as a disconnect and the listener keeps accepting.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task AbruptDisconnect_EndsSessionAndListenerContinues()
    {
        var listener = await StartAsync();
        var client = await ConnectAsync(listener);
        await client.PublishFlowAsync(RtmpTestServer.StreamKey);

        client.Abort();

        var ended = await _server.Handler.NextAsync("ended");
        Assert.Equal(PublishEndReason.ClientDisconnected, ended.Session.EndReason);
        await Eventually.WaitUntilAsync(() => listener.ActiveConnectionCount == 0, "the connection is released");
        Assert.Equal(0, _server.Registry.Count);

        var next = await ConnectAsync(listener);
        Assert.Equal("NetStream.Publish.Start", (await next.PublishFlowAsync(RtmpTestServer.StreamKey)).Code);
    }

    /// <summary>Verifies that a client sending garbage is closed without affecting a concurrent publisher.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task MalformedClient_DoesNotAffectOtherConnections()
    {
        var listener = await StartAsync();
        var publisher = await ConnectAsync(listener);
        var (streamId, _) = await publisher.PublishFlowAsync(RtmpTestServer.StreamKey);

        var attacker = await ConnectAsync(listener);
        await attacker.HandshakeAsync();
        await attacker.SendRawAsync(new byte[] { 0xC3, 0xFF, 0xFF, 0xFF, 0xFF });
        await attacker.WaitForCloseAsync().WithinSafetyTimeout();
        await Eventually.WaitUntilAsync(() => listener.ActiveConnectionCount == 1, "the malformed connection is closed");

        await publisher.SendMessageAsync(6, streamId, RtmpMessageType.Video, 7, new byte[] { 1 });
        Assert.Equal(7u, (await _server.Handler.NextAsync("message")).Timestamp);
        Assert.Contains(_server.Logs.GetSnapshot(), static r => r.Id.Id == 5104);
    }

    /// <summary>
    /// Verifies graceful shutdown with active publishers: stopping the listener closes every connection, ends every
    /// session as a server shutdown, notifies clients and completes the background task without faulting.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Stop_WithActivePublishers_EndsAllSessions()
    {
        var listener = await StartAsync();
        var first = await ConnectAsync(listener);
        await first.PublishFlowAsync(RtmpTestServer.StreamKey);
        var second = await ConnectAsync(listener);
        await second.PublishFlowAsync("secret-key-0002");
        var idle = await ConnectAsync(listener);
        await idle.HandshakeAsync();

        await listener.StopAsync(TestContext.Current.CancellationToken).WithinSafetyTimeout();

        Assert.True(listener.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(0, listener.ActiveConnectionCount);
        Assert.Equal(0, _server.Registry.Count);
        var ended = _server.Handler.Events.Where(static e => e.Kind == "ended").ToList();
        Assert.Equal(2, ended.Count);
        Assert.All(ended, static e => Assert.Equal(PublishEndReason.ServerShutdown, e.Session.EndReason));
        await Task.WhenAll(first.WaitForCloseAsync(), second.WaitForCloseAsync(), idle.WaitForCloseAsync()).WithinSafetyTimeout();
        Assert.Contains(_server.Logs.GetSnapshot(), static r => r.Id.Id == 5004);
        Assert.Contains(_server.Logs.GetSnapshot(), static r => r.Id.Id == 5005);
        await Assert.ThrowsAnyAsync<SocketException>(() => RtmpTestClient.ConnectAsync(listener.LocalEndPoint!));
    }

    /// <summary>Verifies that binding to a port in use fails start with the socket error as the inner exception.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Start_PortInUse_Throws()
    {
        using var occupier = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        occupier.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        occupier.Listen();
        var options = LoopbackOptions();
        options.Port = ((IPEndPoint)occupier.LocalEndPoint!).Port;
        _server.Dispose();
        _server = new RtmpTestServer(options);
        using var listener = _server.CreateListener();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => listener.StartAsync(TestContext.Current.CancellationToken));

        Assert.IsType<SocketException>(error.InnerException);
        Assert.Null(listener.LocalEndPoint);
        Assert.Contains(_server.Logs.GetSnapshot(), static r => r.Id.Id == 5001);
    }

    /// <summary>Verifies listening on the IPv6 loopback address.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Ipv6Loopback_AcceptsConnections()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Assert.Skip("IPv6 is not available on this machine.");
        }

        var options = LoopbackOptions();
        options.ListenAddress = "::1";
        _server.Dispose();
        _server = new RtmpTestServer(options);
        var listener = await StartAsync();

        Assert.Equal(AddressFamily.InterNetworkV6, listener.LocalEndPoint!.AddressFamily);
        var client = await ConnectAsync(listener);
        Assert.Equal("NetStream.Publish.Start", (await client.PublishFlowAsync(RtmpTestServer.StreamKey)).Code);
    }

    /// <summary>Stops the listener and disposes clients and the server.</summary>
    /// <returns>A task that completes when everything is released.</returns>
    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            await client.DisposeAsync();
        }

        if (_listener is not null)
        {
            await _listener.StopAsync(CancellationToken.None);
            _listener.Dispose();
        }

        _server.Dispose();
    }

    /// <summary>Options listening on an OS-assigned IPv4 loopback port.</summary>
    /// <returns>The options.</returns>
    private static RtmpOptions LoopbackOptions() => new() { ListenAddress = "127.0.0.1", Port = 0 };

    /// <summary>Starts a listener for the current server.</summary>
    /// <returns>The started listener.</returns>
    private async Task<RtmpListener> StartAsync()
    {
        _listener = _server.CreateListener();
        await _listener.StartAsync(TestContext.Current.CancellationToken);
        return _listener;
    }

    /// <summary>Connects a client to <paramref name="listener"/> and tracks it for disposal.</summary>
    /// <param name="listener">The listener.</param>
    /// <returns>The client.</returns>
    private async Task<RtmpTestClient> ConnectAsync(RtmpListener listener)
    {
        var client = await RtmpTestClient.ConnectAsync(listener.LocalEndPoint!);
        _clients.Add(client);
        return client;
    }
}
