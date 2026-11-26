// Tests for the RTMP TCP listener over real loopback sockets on OS-assigned ports: concurrent publishers, the
// connection limit, the listen backlog under connect bursts within and beyond the limit, abrupt disconnects,
// isolation of a malformed client, graceful shutdown with active publishers and during a burst, bind failures and IPv6.

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

    /// <summary>Verifies that the listen backlog has room for a burst of the connection limit and never falls below the minimum.</summary>
    /// <param name="maxConnections">The connection limit.</param>
    /// <param name="expected">The expected backlog.</param>
    [Theory]
    [InlineData(1, RtmpListener.MinimumListenBacklog)]
    [InlineData(100, RtmpListener.MinimumListenBacklog)]
    [InlineData(RtmpListener.MinimumListenBacklog, RtmpListener.MinimumListenBacklog)]
    [InlineData(129, 129)]
    [InlineData(2000, 2000)]
    [InlineData(10000, 10000)]
    public void ListenBacklog_CoversTheConnectionLimit(int maxConnections, int expected)
    {
        Assert.Equal(expected, RtmpListener.GetListenBacklog(new RtmpOptions { MaxConnections = maxConnections }));
    }

    /// <summary>
    /// Verifies that the operating system queues the whole requested backlog, above the 200 that Windows allows for a
    /// plain backlog, while nothing accepts.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Listen_QueuesTheWholeBacklogWithoutAccepting()
    {
        const int Backlog = 4 * RtmpListener.MinimumListenBacklog;
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        RtmpListener.Listen(socket, Backlog);
        var clients = Enumerable.Range(0, Backlog).Select(static _ => new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)).ToList();
        try
        {
            await Task.WhenAll(clients.Select(c => c.ConnectAsync(socket.LocalEndPoint!, TestContext.Current.CancellationToken).AsTask())).WithinSafetyTimeout();

            Assert.All(clients, static c => Assert.True(c.Connected));
        }
        finally
        {
            foreach (var client in clients)
            {
                client.LingerState = new LingerOption(enable: true, seconds: 0);
                client.Dispose();
            }
        }
    }

    /// <summary>
    /// Verifies that a burst of simultaneous connections several times the minimum backlog is served completely, and
    /// that stopping the listener then closes all of them.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ConnectBurst_WithinTheLimit_IsServed()
    {
        const int Burst = 4 * RtmpListener.MinimumListenBacklog;
        var options = LoopbackOptions();
        options.MaxConnections = Burst;
        _server.Dispose();
        _server = new RtmpTestServer(options);
        var listener = await StartAsync();

        var clients = await Task.WhenAll(Enumerable.Range(0, Burst).Select(async _ =>
        {
            var client = await ConnectAsync(listener);
            await client.HandshakeAsync();
            return client;
        })).WithinSafetyTimeout();

        Assert.Equal(Burst, listener.ActiveConnectionCount);
        Assert.DoesNotContain(_server.Logs.GetSnapshot(), static r => r.Id.Id == 5002);

        await listener.StopAsync(TestContext.Current.CancellationToken).WithinSafetyTimeout();

        Assert.True(listener.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(0, listener.ActiveConnectionCount);
        await Task.WhenAll(clients.Select(static c => c.WaitForCloseAsync())).WithinSafetyTimeout();
    }

    /// <summary>
    /// Verifies a burst beyond the connection limit: the limit is admitted, every other connection that reaches the
    /// listener is closed and logged rather than queued, and connections are admitted again once admitted ones close.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ConnectBurst_BeyondTheLimit_AdmitsTheLimitAndRecovers()
    {
        const int Limit = 2 * RtmpListener.MinimumListenBacklog;
        var options = LoopbackOptions();
        options.MaxConnections = Limit;
        _server.Dispose();
        _server = new RtmpTestServer(options);
        var listener = await StartAsync();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2 * Limit).Select(async _ =>
        {
            RtmpTestClient client;
            try
            {
                client = await ConnectAsync(listener);
            }
            catch (SocketException)
            {
                return (Client: (RtmpTestClient?)null, Closed: false);
            }

            try
            {
                await client.HandshakeAsync();
                return (Client: client, Closed: false);
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                return (Client: (RtmpTestClient?)null, Closed: true);
            }
        })).WithinSafetyTimeout();

        var admitted = outcomes.Where(static o => o.Client is not null).Select(static o => o.Client!).ToList();
        var closed = outcomes.Count(static o => o.Closed);
        Assert.Equal(Limit, admitted.Count);
        Assert.Equal(Limit, listener.ActiveConnectionCount);
        await Eventually.WaitUntilAsync(() => _server.Logs.GetSnapshot().Count(static r => r.Id.Id == 5002) == closed, "every connection beyond the limit is logged");

        foreach (var client in admitted)
        {
            await client.DisposeAsync();
        }

        await Eventually.WaitUntilAsync(() => listener.ActiveConnectionCount == 0, "the admitted connections are released");
        var next = await ConnectAsync(listener);
        Assert.Equal("NetStream.Publish.Start", (await next.PublishFlowAsync(RtmpTestServer.StreamKey)).Code);
    }

    /// <summary>
    /// Verifies that stopping the listener while a burst is still arriving completes, closes every admitted connection
    /// and leaves no client waiting, including those still queued in the backlog.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Stop_DuringConnectBurst_ClosesEverything()
    {
        const int Burst = 4 * RtmpListener.MinimumListenBacklog;
        var options = LoopbackOptions();
        options.MaxConnections = Burst;
        _server.Dispose();
        _server = new RtmpTestServer(options);
        var listener = await StartAsync();

        var attempts = Enumerable.Range(0, Burst).Select(async _ =>
        {
            try
            {
                var client = await ConnectAsync(listener);
                await client.HandshakeAsync();
                await client.WaitForCloseAsync();
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
            }
        }).ToList();
        await listener.StopAsync(TestContext.Current.CancellationToken).WithinSafetyTimeout();

        Assert.True(listener.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(0, listener.ActiveConnectionCount);
        Assert.False(listener.IsListening);
        await Task.WhenAll(attempts).WithinSafetyTimeout();
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
