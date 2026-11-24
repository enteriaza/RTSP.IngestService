// Tests for the RTMP handshake as run by a connection over in-memory pipes: the valid exchange, fragmented input,
// unsupported versions, an invalid C2, a client that disconnects part-way, and the handshake timeout driven by a fake
// clock.

using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Rtmp;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Rtmp;

/// <summary>
/// Tests for <see cref="RtmpHandshake"/> through <see cref="RtmpConnection"/>.
/// </summary>
public sealed class RtmpHandshakeTests : IDisposable
{
    /// <summary>The server under test.</summary>
    private readonly RtmpTestServer _server = new();

    /// <summary>Verifies that a valid handshake moves the connection to Connected and records a successful handshake.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ValidHandshake_ConnectionBecomesConnected()
    {
        using var handshakes = new MetricCollector<long>(_server.MeterFactory, RtmpMetrics.MeterName, "rtmp.handshakes");
        var (connection, run, client) = _server.Start();

        await client.HandshakeAsync();
        await Eventually.WaitUntilAsync(() => connection.State == RtmpConnectionState.Connected, "the connection is connected");
        await client.DisposeAsync();
        await run.WithinSafetyTimeout();

        Assert.Equal(RtmpCloseReason.ClientDisconnected, connection.CloseReason);
        Assert.Equal(RtmpConnectionState.Closed, connection.State);
        Assert.Equal("success", Assert.Single(handshakes.GetMeasurementSnapshot()).Tags["result"]);
    }

    /// <summary>Verifies the server's S0/S1/S2 layout: version 3, S1 time and zero fields, and S2 echoing C1.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Handshake_ServerPacketsFollowSpecification()
    {
        var (_, run, client) = _server.Start();
        var c1 = new byte[RtmpHandshake.PacketSize];
        RandomNumberGenerator.Fill(c1.AsSpan(8));
        await client.SendRawAsync(new byte[] { 3 }.Concat(c1).ToArray());

        var response = await ReadExactlyAsync(client, 1 + (2 * RtmpHandshake.PacketSize));

        Assert.Equal(3, response[0]);
        Assert.Equal(new byte[4], response[5..9]);
        Assert.Equal(c1.AsSpan(0, 4).ToArray(), response[(1 + RtmpHandshake.PacketSize)..(5 + RtmpHandshake.PacketSize)]);
        Assert.Equal(c1[8..], response[(9 + RtmpHandshake.PacketSize)..]);
        await client.DisposeAsync();
        await run.WithinSafetyTimeout();
    }

    /// <summary>Verifies that a handshake delivered in small fragments completes.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task FragmentedHandshake_Completes()
    {
        var (connection, run, client) = _server.Start();
        var c0c1 = new byte[1 + RtmpHandshake.PacketSize];
        c0c1[0] = 3;
        for (var offset = 0; offset < c0c1.Length; offset += 97)
        {
            await client.SendRawAsync(c0c1.AsMemory(offset, Math.Min(97, c0c1.Length - offset)));
        }

        var response = await ReadExactlyAsync(client, 1 + (2 * RtmpHandshake.PacketSize));
        var c2 = response.AsMemory(1, RtmpHandshake.PacketSize);
        for (var offset = 0; offset < c2.Length; offset += 1000)
        {
            await client.SendRawAsync(c2.Slice(offset, Math.Min(1000, c2.Length - offset)));
        }

        await Eventually.WaitUntilAsync(() => connection.State == RtmpConnectionState.Connected, "the connection is connected");
        await client.DisposeAsync();
        await run.WithinSafetyTimeout();
    }

    /// <summary>Verifies that versions other than 3, including RTMPE versions, fail the handshake without a response.</summary>
    /// <param name="version">The C0 version byte.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(31)]
    public async Task UnsupportedVersion_FailsHandshake(byte version)
    {
        using var handshakes = new MetricCollector<long>(_server.MeterFactory, RtmpMetrics.MeterName, "rtmp.handshakes");
        var (connection, run, client) = _server.Start();
        var c0c1 = new byte[1 + RtmpHandshake.PacketSize];
        c0c1[0] = version;

        await client.SendRawAsync(c0c1);
        await run.WithinSafetyTimeout();

        Assert.Equal(RtmpCloseReason.HandshakeFailed, connection.CloseReason);
        Assert.Equal(RtmpConnectionState.Failed, connection.State);
        Assert.Equal("failure", Assert.Single(handshakes.GetMeasurementSnapshot()).Tags["result"]);
        await client.DisposeAsync();
    }

    /// <summary>Verifies that a C2 that does not echo S1 fails the handshake.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task InvalidC2_FailsHandshake()
    {
        var (connection, run, client) = _server.Start();
        var c0c1 = new byte[1 + RtmpHandshake.PacketSize];
        c0c1[0] = 3;
        await client.SendRawAsync(c0c1);
        await ReadExactlyAsync(client, 1 + (2 * RtmpHandshake.PacketSize));

        var c2 = new byte[RtmpHandshake.PacketSize];
        RandomNumberGenerator.Fill(c2);
        await client.SendRawAsync(c2);
        await run.WithinSafetyTimeout();

        Assert.Equal(RtmpCloseReason.HandshakeFailed, connection.CloseReason);
        await client.DisposeAsync();
    }

    /// <summary>Verifies that a client disconnecting part-way through the handshake closes the connection as a disconnect.</summary>
    /// <param name="bytesSent">Bytes sent before disconnecting.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    public async Task DisconnectDuringHandshake_ClosesConnection(int bytesSent)
    {
        var (connection, run, client) = _server.Start();
        var data = new byte[bytesSent];
        if (bytesSent > 0)
        {
            data[0] = 3;
            await client.SendRawAsync(data);
        }

        await client.DisposeAsync();
        await run.WithinSafetyTimeout();

        Assert.Equal(RtmpCloseReason.ClientDisconnected, connection.CloseReason);
        Assert.Equal(RtmpConnectionState.Closed, connection.State);
    }

    /// <summary>Verifies that a client that stalls in the handshake is closed when the handshake timeout elapses on the clock.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task StalledHandshake_TimesOut()
    {
        var time = new FakeTimeProvider();
        var options = new RtmpOptions { HandshakeTimeout = TimeSpan.FromSeconds(5) };
        using var server = new RtmpTestServer(options, time);
        var (connection, run, client) = server.Start();
        await client.SendRawAsync(new byte[] { 3, 0, 0 });

        time.Advance(TimeSpan.FromSeconds(4));
        Assert.False(run.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        await run.WithinSafetyTimeout();

        Assert.Equal(RtmpCloseReason.HandshakeTimeout, connection.CloseReason);
        Assert.Equal(RtmpConnectionState.Failed, connection.State);
        await client.DisposeAsync();
    }

    /// <summary>Verifies that cancelling the connection during the handshake closes it as a server shutdown.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task CancellationDuringHandshake_ClosesAsShutdown()
    {
        using var shutdown = new CancellationTokenSource();
        var (connection, run, client) = _server.StartWithShutdown(shutdown.Token);
        await client.SendRawAsync(new byte[] { 3 });

        await shutdown.CancelAsync();
        await run.WithinSafetyTimeout();

        Assert.Equal(RtmpCloseReason.ServerShutdown, connection.CloseReason);
        await client.DisposeAsync();
    }

    /// <summary>Disposes the server.</summary>
    public void Dispose() => _server.Dispose();

    /// <summary>Reads exactly <paramref name="count"/> raw bytes from the client end.</summary>
    /// <param name="client">The client.</param>
    /// <param name="count">Number of bytes.</param>
    /// <returns>The bytes.</returns>
    private static async Task<byte[]> ReadExactlyAsync(RtmpTestClient client, int count)
    {
        var input = client.Transport.Input;
        while (true)
        {
            var result = await input.ReadAsync(TestContext.Current.CancellationToken).AsTask().WithinSafetyTimeout();
            if (result.Buffer.Length >= count)
            {
                var bytes = result.Buffer.Slice(0, count).ToArray();
                input.AdvanceTo(result.Buffer.GetPosition(count));
                return bytes;
            }

            input.AdvanceTo(result.Buffer.Start, result.Buffer.End);
            Assert.False(result.IsCompleted, "the server closed the connection during the handshake");
        }
    }
}
