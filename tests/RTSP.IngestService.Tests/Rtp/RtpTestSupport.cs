// Building blocks for RTP output tests: an RTP/RTCP packet parser, a loopback UDP receiver bound to an even port pair,
// a fake datagram socket that records or fails sends, and a harness that drives MediaPublishSessionHandler through
// RtpOutputHandler into a RecordingMediaHandler, exactly as the service composes the stages.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Rtp;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Tests.Media;

namespace RTSP.IngestService.Tests.Rtp;

/// <summary>A parsed RTP packet.</summary>
/// <param name="Version">The version field.</param>
/// <param name="Padding">The padding bit.</param>
/// <param name="Extension">The extension bit.</param>
/// <param name="CsrcCount">The CSRC count.</param>
/// <param name="Marker">The marker bit.</param>
/// <param name="PayloadType">The payload type.</param>
/// <param name="SequenceNumber">The sequence number.</param>
/// <param name="Timestamp">The timestamp.</param>
/// <param name="Ssrc">The SSRC.</param>
/// <param name="Payload">The payload.</param>
internal sealed record RtpPacket(int Version, bool Padding, bool Extension, int CsrcCount, bool Marker, byte PayloadType, ushort SequenceNumber, uint Timestamp, uint Ssrc, byte[] Payload)
{
    /// <summary>Parses a datagram.</summary>
    /// <param name="datagram">The datagram.</param>
    /// <returns>The packet.</returns>
    public static RtpPacket Parse(ReadOnlySpan<byte> datagram)
    {
        Assert.True(datagram.Length >= 12, "RTP packet shorter than its header.");
        return new RtpPacket(
            datagram[0] >> 6,
            (datagram[0] & 0x20) != 0,
            (datagram[0] & 0x10) != 0,
            datagram[0] & 0x0F,
            (datagram[1] & 0x80) != 0,
            (byte)(datagram[1] & 0x7F),
            BinaryPrimitives.ReadUInt16BigEndian(datagram[2..]),
            BinaryPrimitives.ReadUInt32BigEndian(datagram[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(datagram[8..]),
            datagram[12..].ToArray());
    }
}

/// <summary>Reassembles H.264 NAL units from RFC 6184 packets, as a receiver does.</summary>
internal static class H264Depacketizer
{
    /// <summary>Returns the NAL units carried by consecutive packets of access units.</summary>
    /// <param name="packets">The packets in order.</param>
    /// <returns>The NAL units.</returns>
    public static List<byte[]> NalUnits(IEnumerable<RtpPacket> packets)
    {
        var units = new List<byte[]>();
        List<byte>? fragment = null;
        foreach (var packet in packets)
        {
            var payload = packet.Payload;
            var type = payload[0] & 0x1F;
            switch (type)
            {
                case H264RtpPacketizer.StapA:
                    for (var position = 1; position < payload.Length;)
                    {
                        var size = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(position));
                        units.Add(payload.AsSpan(position + 2, size).ToArray());
                        position += 2 + size;
                    }

                    break;
                case H264RtpPacketizer.FuA:
                    var header = payload[1];
                    if ((header & 0x80) != 0)
                    {
                        Assert.Null(fragment);
                        fragment = [(byte)((payload[0] & 0xE0) | (header & 0x1F))];
                    }

                    Assert.NotNull(fragment);
                    fragment.AddRange(payload.AsSpan(2).ToArray());
                    if ((header & 0x40) != 0)
                    {
                        units.Add([.. fragment]);
                        fragment = null;
                    }

                    break;
                default:
                    units.Add(payload);
                    break;
            }
        }

        Assert.Null(fragment);
        return units;
    }
}

/// <summary>A loopback UDP receiver on an even RTP port, with the RTCP port above it.</summary>
internal sealed class UdpReceiver : IDisposable
{
    /// <summary>Initializes a new instance of the <see cref="UdpReceiver"/> class.</summary>
    /// <param name="rtp">The RTP socket.</param>
    /// <param name="rtcp">The RTCP socket.</param>
    private UdpReceiver(Socket rtp, Socket rtcp)
    {
        Rtp = rtp;
        Rtcp = rtcp;
        Port = ((IPEndPoint)rtp.LocalEndPoint!).Port;
    }

    /// <summary>Gets the RTP port.</summary>
    public int Port { get; }

    /// <summary>Gets the RTP socket.</summary>
    public Socket Rtp { get; }

    /// <summary>Gets the RTCP socket.</summary>
    public Socket Rtcp { get; }

    /// <summary>Binds an even port and the odd port above it.</summary>
    /// <param name="address">The local address.</param>
    /// <returns>The receiver.</returns>
    public static UdpReceiver Bind(IPAddress address)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var rtp = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            rtp.Bind(new IPEndPoint(address, 0));
            var port = ((IPEndPoint)rtp.LocalEndPoint!).Port;
            if (port % 2 == 0 && port < 65534)
            {
                var rtcp = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    rtcp.Bind(new IPEndPoint(address, port + 1));
                    rtp.ReceiveBufferSize = 4 * 1024 * 1024;
                    return new UdpReceiver(rtp, rtcp);
                }
                catch (SocketException)
                {
                    rtcp.Dispose();
                }
            }

            rtp.Dispose();
        }

        throw new InvalidOperationException("No free even UDP port pair.");
    }

    /// <summary>
    /// Finds free even port pairs below the Windows and Linux ephemeral ranges, for an external receiver that binds
    /// them itself; ports the operating system hands out for other sockets (such as the RTP sender's) cannot collide.
    /// </summary>
    /// <param name="count">The number of pairs.</param>
    /// <returns>The even ports.</returns>
    public static int[] FreePortPairs(int count)
    {
        var ports = new List<int>();
        for (var attempt = 0; attempt < 500 && ports.Count < count; attempt++)
        {
            var port = 20000 + (2 * Random.Shared.Next(0, 5000));
            if (ports.Contains(port))
            {
                continue;
            }

            using var rtp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            using var rtcp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                rtp.Bind(new IPEndPoint(IPAddress.Loopback, port));
                rtcp.Bind(new IPEndPoint(IPAddress.Loopback, port + 1));
                ports.Add(port);
            }
            catch (SocketException)
            {
            }
        }

        return ports.Count == count ? [.. ports] : throw new InvalidOperationException("No free even UDP port pairs.");
    }

    /// <summary>Receives one datagram from a socket.</summary>
    /// <param name="socket">The socket.</param>
    /// <returns>The datagram.</returns>
    public static async Task<byte[]> ReceiveAsync(Socket socket)
    {
        var buffer = new byte[65536];
        var received = await socket.ReceiveAsync(buffer, SocketFlags.None, TestContext.Current.CancellationToken).AsTask().WithinSafetyTimeout();
        return buffer[..received];
    }

    /// <summary>Receives RTP packets until <paramref name="done"/> holds.</summary>
    /// <param name="done">The completion condition over the packets so far.</param>
    /// <returns>The packets.</returns>
    public async Task<List<RtpPacket>> ReceiveUntilAsync(Func<List<RtpPacket>, bool> done)
    {
        var packets = new List<RtpPacket>();
        while (!done(packets))
        {
            packets.Add(RtpPacket.Parse(await ReceiveAsync(Rtp)));
        }

        return packets;
    }

    /// <summary>Closes both sockets.</summary>
    public void Dispose()
    {
        Rtp.Dispose();
        Rtcp.Dispose();
    }
}

/// <summary>A datagram socket that records sends and can fail or block them.</summary>
internal sealed class FakeDatagramSocket : IRtpDatagramSocket
{
    /// <summary>Gets the datagrams sent, with their destinations.</summary>
    public ConcurrentQueue<(byte[] Datagram, IPEndPoint Destination)> Sent { get; } = new();

    /// <summary>Gets or sets the error each send (by zero-based index) fails with, or <see langword="null"/> to succeed.</summary>
    public Func<int, SocketError?>? Failure { get; set; }

    /// <summary>Gets or sets a gate every send awaits; <see langword="null"/> for none.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Gets the number of sends attempted.</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>Gets a value indicating whether the socket was disposed.</summary>
    public bool Disposed { get; private set; }

    /// <inheritdoc />
    public EndPoint? LocalEndPoint => new IPEndPoint(IPAddress.Loopback, 40000);

    /// <inheritdoc />
    public byte? TrafficClass { get; set; }

    /// <summary>Sends attempted.</summary>
    private int _attempts;

    /// <inheritdoc />
    public async ValueTask<int> SendToAsync(ReadOnlyMemory<byte> datagram, SocketAddress destination, CancellationToken cancellationToken)
    {
        var index = Interlocked.Increment(ref _attempts) - 1;
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        ObjectDisposedException.ThrowIf(Disposed, this);
        if (Failure?.Invoke(index) is { } error)
        {
            throw new SocketException((int)error);
        }

        var any = destination.Family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
        var endPoint = (IPEndPoint)new IPEndPoint(any, 0).Create(destination);
        Sent.Enqueue((datagram.ToArray(), endPoint));
        return datagram.Length;
    }

    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}

/// <summary>
/// Drives <see cref="MediaPublishSessionHandler"/> with pooled FLV messages through <see cref="RtpOutputHandler"/>
/// into a <see cref="RecordingMediaHandler"/>.
/// </summary>
internal sealed class RtpHarness : IAsyncDisposable
{
    /// <summary>Owns the meter factory.</summary>
    private readonly ServiceProvider _services;

    /// <summary>The logger factory.</summary>
    private readonly LoggerFactory _loggerFactory;

    /// <summary>Builds pooled messages.</summary>
    private readonly MediaHarness _messages = new();

    /// <summary>Initializes a new instance of the <see cref="RtpHarness"/> class.</summary>
    /// <param name="sockets">Creates each output's socket.</param>
    /// <param name="time">The clock; the system clock when <see langword="null"/>.</param>
    /// <param name="outputs">The outputs.</param>
    public RtpHarness(RtpSocketFactory sockets, TimeProvider? time, params RtpOutputOptions[] outputs)
        : this(sockets, time, new RtpTransportOptions(), outputs)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RtpHarness"/> class with transport settings.</summary>
    /// <param name="sockets">Creates each output's socket.</param>
    /// <param name="time">The clock; the system clock when <see langword="null"/>.</param>
    /// <param name="transport">The transport settings shared by the outputs.</param>
    /// <param name="outputs">The outputs.</param>
    public RtpHarness(RtpSocketFactory sockets, TimeProvider? time, RtpTransportOptions transport, params RtpOutputOptions[] outputs)
    {
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meters = _services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
        _loggerFactory = new LoggerFactory([new FakeLoggerProvider(Logs)]);
        Metrics = new RtpMetrics(meters);
        Handler = new RtpOutputHandler(
            Inner,
            ownsInner: false,
            Options.Create(new RtpOptions { Outputs = outputs, Transport = transport }),
            sockets,
            Metrics,
            _loggerFactory.CreateLogger<RtpOutputHandler>(),
            time ?? TimeProvider.System);
        Processor = new MediaPublishSessionHandler(
            Options.Create(new MediaOptions()),
            Handler,
            new MediaMetrics(meters),
            _loggerFactory.CreateLogger<MediaPublishSessionHandler>());
        MeterFactory = meters;
    }

    /// <summary>Gets the meter factory.</summary>
    public System.Diagnostics.Metrics.IMeterFactory MeterFactory { get; }

    /// <summary>Gets the captured logs.</summary>
    public FakeLogCollector Logs { get; } = new();

    /// <summary>Gets the handler RTP output forwards to.</summary>
    public RecordingMediaHandler Inner { get; } = new();

    /// <summary>Gets the RTP output stage.</summary>
    public RtpOutputHandler Handler { get; }

    /// <summary>Gets the RTP metrics.</summary>
    public RtpMetrics Metrics { get; }

    /// <summary>Gets the media processor.</summary>
    public MediaPublishSessionHandler Processor { get; }

    /// <summary>Gets the pool payloads are rented from.</summary>
    public CountingArrayPool Pool => _messages.Pool;

    /// <summary>Starts a publication.</summary>
    /// <param name="stream">The stream identity.</param>
    /// <returns>The session.</returns>
    public async Task<PublisherSession> StartAsync(string stream = "camera1")
    {
        var session = new PublisherSession(1, null, "live", stream, "live", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        await Processor.OnSessionStartedAsync(session, TestContext.Current.CancellationToken);
        return session;
    }

    /// <summary>Sends both sequence headers.</summary>
    /// <param name="session">The session.</param>
    /// <returns>A task.</returns>
    public async Task ConfigureAsync(PublisherSession session)
    {
        await VideoAsync(session, 0, FlvPackets.AvcSequenceHeader());
        await AudioAsync(session, 0, FlvPackets.AacSequenceHeader());
    }

    /// <summary>Sends a video packet.</summary>
    /// <param name="session">The session.</param>
    /// <param name="timestamp">The RTMP timestamp.</param>
    /// <param name="payload">The FLV packet.</param>
    /// <returns>A task.</returns>
    public Task VideoAsync(PublisherSession session, uint timestamp, byte[] payload) =>
        Processor.OnMessageAsync(session, _messages.Message(PublishedMessageKind.Video, timestamp, payload), TestContext.Current.CancellationToken).AsTask();

    /// <summary>Sends an audio packet.</summary>
    /// <param name="session">The session.</param>
    /// <param name="timestamp">The RTMP timestamp.</param>
    /// <param name="payload">The FLV packet.</param>
    /// <returns>A task.</returns>
    public Task AudioAsync(PublisherSession session, uint timestamp, byte[] payload) =>
        Processor.OnMessageAsync(session, _messages.Message(PublishedMessageKind.Audio, timestamp, payload), TestContext.Current.CancellationToken).AsTask();

    /// <summary>Sends an access unit of NAL units (4-byte lengths).</summary>
    /// <param name="session">The session.</param>
    /// <param name="timestamp">The RTMP timestamp.</param>
    /// <param name="keyframe">Whether it is a keyframe.</param>
    /// <param name="nalUnits">The NAL units.</param>
    /// <returns>A task.</returns>
    public Task FrameAsync(PublisherSession session, uint timestamp, bool keyframe, params byte[][] nalUnits) =>
        VideoAsync(session, timestamp, FlvPackets.AvcNalu(keyframe, 0, 4, nalUnits));

    /// <summary>Ends a publication.</summary>
    /// <param name="session">The session.</param>
    /// <returns>A task.</returns>
    public Task EndAsync(PublisherSession session) => Processor.OnSessionEndedAsync(session, TestContext.Current.CancellationToken).AsTask();

    /// <summary>Disposes the RTP stage, then the logger and meter factories.</summary>
    /// <returns>A task.</returns>
    public async ValueTask DisposeAsync()
    {
        await Handler.DisposeAsync();
        _messages.Dispose();
        _loggerFactory.Dispose();
        _services.Dispose();
    }
}

/// <summary>RTP test values.</summary>
internal static class RtpTestData
{
    /// <summary>The SPS in <see cref="AvcDecoderConfigurationTests.Record"/>.</summary>
    public static readonly byte[] Sps = [0x67, 0x4D, 0x40, 0x0A, 0xEC, 0xA4, 0xBF];

    /// <summary>The PPS in <see cref="AvcDecoderConfigurationTests.Record"/>.</summary>
    public static readonly byte[] Pps = [0x68, 0xEB, 0xEC, 0xB2];

    /// <summary>Returns a NAL unit of a type and size with a recognisable body.</summary>
    /// <param name="type">The NAL unit type.</param>
    /// <param name="length">The total length.</param>
    /// <param name="seed">Varies the body.</param>
    /// <returns>The NAL unit.</returns>
    public static byte[] Nal(byte type, int length, int seed = 0)
    {
        var nal = new byte[length];
        nal[0] = (byte)(0x60 | type);
        for (var i = 1; i < length; i++)
        {
            nal[i] = (byte)((i * 31) + seed);
        }

        return nal;
    }

    /// <summary>Returns a unicast output to loopback.</summary>
    /// <param name="name">The name.</param>
    /// <param name="videoPort">The video port.</param>
    /// <param name="audioPort">The audio port.</param>
    /// <param name="stream">The source stream.</param>
    /// <returns>The options.</returns>
    public static RtpOutputOptions Output(string name = "out", int? videoPort = 5004, int? audioPort = 5006, string stream = "camera1") => new()
    {
        Name = name,
        Application = "live",
        Stream = stream,
        Destination = "127.0.0.1",
        VideoPort = videoPort,
        AudioPort = audioPort,
    };
}
