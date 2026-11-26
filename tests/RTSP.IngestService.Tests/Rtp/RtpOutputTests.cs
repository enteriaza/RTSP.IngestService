// Tests of RTP output sessions driven through the real media pipeline: unicast delivery over loopback UDP (IPv4 and
// IPv6) with RTCP, several destinations per stream, forwarding of the same samples to the inner handler, the loss
// policy under backpressure, transient and terminal send errors, socket start failures, isolation between outputs and
// publications, concurrent publications, the bounded stop and shutdown.

using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtp;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Tests.Media;

namespace RTSP.IngestService.Tests.Rtp;

/// <summary>Tests for <see cref="RtpOutputHandler"/> and <see cref="RtpOutputSession"/>.</summary>
public sealed class RtpOutputTests
{
    /// <summary>
    /// Verifies a unicast output over loopback UDP: H.264 with in-band parameter sets and FU-A within the packet size
    /// limit, AAC with its AU header, distinct SSRCs, a Sender Report and SDES per track on port + 1, BYE at the end;
    /// and the inner handler receives the same samples, byte for byte, with every pooled buffer returned.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Unicast_Loopback_SendsRtpAndRtcp()
    {
        using var video = UdpReceiver.Bind(IPAddress.Loopback);
        using var audio = UdpReceiver.Bind(IPAddress.Loopback);
        var harness = new RtpHarness(static (o, q) => new RtpUdpSocket(o, q), null, RtpTestData.Output(videoPort: video.Port, audioPort: audio.Port));
        await using (harness)
        {
            var session = await harness.StartAsync();
            await harness.ConfigureAsync(session);
            var idr = RtpTestData.Nal(5, 4000);
            var accessUnit = RtpTestData.Nal(0x21, 300);
            await harness.FrameAsync(session, 0, keyframe: true, idr);
            await harness.AudioAsync(session, 0, FlvPackets.AacRaw(accessUnit));

            var videoPackets = await video.ReceiveUntilAsync(static p => p.Count > 0 && p[^1].Marker);
            var audioPacket = Assert.Single(await audio.ReceiveUntilAsync(static p => p.Count == 1));
            var report = await UdpReceiver.ReceiveAsync(video.Rtcp);
            var audioReport = await UdpReceiver.ReceiveAsync(audio.Rtcp);
            var status = Assert.Single(harness.Handler.GetOutputs());
            var sdp = RtpSdp.Render(status.Session!, status.StreamInfo!);
            await harness.EndAsync(session);
            var videoBye = await ReceiveGoodbyeAsync(video.Rtcp);
            var audioBye = await ReceiveGoodbyeAsync(audio.Rtcp);

            Assert.Equal([RtpTestData.Sps, RtpTestData.Pps, idr], H264Depacketizer.NalUnits(videoPackets));
            Assert.All(videoPackets, static p => Assert.Equal((2, (byte)96), (p.Version, p.PayloadType)));
            Assert.All(videoPackets, static p => Assert.True(p.Payload.Length + 12 <= 1472));
            Assert.Single(videoPackets.Select(static p => p.Ssrc).Distinct());
            Assert.Equal((2, (byte)97, true), (audioPacket.Version, audioPacket.PayloadType, audioPacket.Marker));
            Assert.Equal(accessUnit, audioPacket.Payload[4..]);
            Assert.NotEqual(videoPackets[0].Ssrc, audioPacket.Ssrc);

            Assert.Equal(RtcpPackets.SenderReport, report[1]);
            Assert.Equal(videoPackets[0].Ssrc, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(4)));
            Assert.Equal((RtcpPackets.SenderReport, audioPacket.Ssrc), (audioReport[1], System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(audioReport.AsSpan(4))));
            Assert.Equal(audioPacket.Ssrc, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(audioBye.AsSpan(4)));
            Assert.Equal(videoPackets[0].Ssrc, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(videoBye.AsSpan(4)));

            Assert.NotNull(sdp);
            Assert.Contains($"m=video {video.Port} RTP/AVP 96\r\n", sdp, StringComparison.Ordinal);
            Assert.Contains("a=fmtp:96 packetization-mode=1;profile-level-id=4d400a;sprop-parameter-sets=Z01ACuykvw==,aOvssg==\r\n", sdp, StringComparison.Ordinal);
            Assert.Contains($"m=audio {audio.Port} RTP/AVP 97\r\na=rtpmap:97 MPEG4-GENERIC/44100/2\r\n", sdp, StringComparison.Ordinal);
            Assert.Contains("mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3;config=1210\r\n", sdp, StringComparison.Ordinal);
            Assert.Contains($"a=ssrc:{videoPackets[0].Ssrc} cname:", sdp, StringComparison.Ordinal);
            Assert.Contains("c=IN IP4 127.0.0.1\r\n", sdp, StringComparison.Ordinal);

            var forwarded = Assert.Single(harness.Inner.Video);
            Assert.Equal(idr, Assert.Single(forwarded.NalUnits));
            Assert.Equal(accessUnit, Assert.Single(harness.Inner.Audio).Data);
            Assert.Equal(["started", "video", "audio", "ended"], harness.Inner.Events.Select(static e => e.Kind).Where(static k => k != "info"));
            Assert.Null(Assert.Single(harness.Handler.GetOutputs()).Session);
            Assert.Equal(0, harness.Handler.ActivePublicationCount);
            Assert.Contains(harness.Logs.GetSnapshot(), static r => r.Id.Id == 9400);
            Assert.Contains(harness.Logs.GetSnapshot(), static r => r.Id.Id == 9401);
        }

        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>Verifies unicast over IPv6 loopback, with packets sized for the IPv6 header.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Unicast_Ipv6Loopback_SendsRtp()
    {
        Assert.SkipUnless(Socket.OSSupportsIPv6, "IPv6 is not available.");
        using var video = UdpReceiver.Bind(IPAddress.IPv6Loopback);
        var output = RtpTestData.Output(videoPort: video.Port, audioPort: null);
        output.Destination = "::1";
        output.SourceAddress = "::1";
        await using var harness = new RtpHarness(static (o, q) => new RtpUdpSocket(o, q), null, output);
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session);
        var idr = RtpTestData.Nal(5, 3000);
        await harness.FrameAsync(session, 0, keyframe: true, idr);

        var packets = await video.ReceiveUntilAsync(static p => p.Count > 0 && p[^1].Marker);

        Assert.Equal([RtpTestData.Sps, RtpTestData.Pps, idr], H264Depacketizer.NalUnits(packets));
        Assert.Contains(packets, static p => p.Payload.Length + 12 == 1452);
        Assert.All(packets, static p => Assert.True(p.Payload.Length + 12 <= 1452));
        var status = Assert.Single(harness.Handler.GetOutputs());
        Assert.Equal(IPAddress.IPv6Loopback, ((IPEndPoint)status.Session!.LocalEndPoint!).Address);
        Assert.Contains("c=IN IP6 ::1\r\n", RtpSdp.Render(status.Session, status.StreamInfo!), StringComparison.Ordinal);
        await harness.EndAsync(session);
    }

    /// <summary>
    /// Verifies several outputs of one stream are independent destinations (own ports and SSRCs, video-only where
    /// configured), and an output of another stream opens no socket.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task MultipleOutputs_SendIndependently()
    {
        var sockets = new Dictionary<string, FakeDatagramSocket> { ["a"] = new(), ["b"] = new(), ["c"] = new() };
        var opened = new List<string>();
        await using var harness = new RtpHarness(
            (o, _) =>
            {
                lock (opened)
                {
                    opened.Add(o.Name);
                }

                return sockets[o.Name];
            },
            null,
            RtpTestData.Output("a", 5004, 5006),
            RtpTestData.Output("b", 6000, null),
            RtpTestData.Output("c", 7000, 7002, stream: "other"));
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session);
        await harness.FrameAsync(session, 0, keyframe: true, RtpTestData.Nal(5, 100));
        await harness.AudioAsync(session, 0, FlvPackets.AacRaw(RtpTestData.Nal(0x21, 50)));

        await Eventually.WaitUntilAsync(() => Rtp(sockets["a"]).Count >= 2 && Rtp(sockets["b"]).Count >= 1, "both outputs send");
        await harness.EndAsync(session);

        Assert.Equal(["a", "b"], opened.Order(StringComparer.Ordinal));
        var a = Rtp(sockets["a"]);
        var b = Rtp(sockets["b"]);
        Assert.Equal([5004, 5006], a.Select(static s => s.Destination.Port).Distinct().Order());
        Assert.All(b, static s => Assert.Equal(6000, s.Destination.Port));
        Assert.All(a.Concat(b), static s => Assert.Equal(IPAddress.Loopback, s.Destination.Address));
        var ssrcs = a.Concat(b).Select(static s => RtpPacket.Parse(s.Datagram).Ssrc).Distinct().ToList();
        Assert.Equal(3, ssrcs.Count);
        Assert.Contains(sockets["a"].Sent, static s => s.Destination.Port is 5005 or 5007);
        Assert.Equal(2, harness.Inner.Video.Count + harness.Inner.Audio.Count);
        Assert.True(sockets["a"].Disposed && sockets["b"].Disposed);
    }

    /// <summary>
    /// Verifies the loss policy: when the sender cannot keep up the pipeline is never blocked; whole access units that
    /// do not fit are dropped and counted, video then waits for the next IDR, and sending resumes from it.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Backpressure_DropsWholeAccessUnitsAndWaitsForIdr()
    {
        var socket = new FakeDatagramSocket { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var output = RtpTestData.Output(audioPort: null);
        output.QueueCapacity = 64;
        output.RtcpEnabled = false;
        await using var harness = new RtpHarness((_, _) => socket, null, output);
        using var dropped = new MetricCollector<long>(harness.MeterFactory, RtpMetrics.MeterName, "rtp.packets.dropped");
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session);
        var frame = RtpTestData.Nal(5, 20 * 1400);

        for (var i = 0; i < 4; i++)
        {
            await harness.FrameAsync(session, (uint)(i * 40), keyframe: true, frame);
        }

        await harness.FrameAsync(session, 160, keyframe: false, RtpTestData.Nal(1, 500));
        await harness.FrameAsync(session, 200, keyframe: false, RtpTestData.Nal(1, 500));
        var queued = Assert.Single(harness.Handler.GetOutputs()).Session!.QueuedPackets;
        socket.Gate.SetResult();
        await Eventually.WaitUntilAsync(() => socket.Sent.Count >= queued, "the queue drains");
        await harness.FrameAsync(session, 240, keyframe: true, RtpTestData.Nal(5, 100));
        await Eventually.WaitUntilAsync(() => socket.Sent.Count >= queued + 1, "the next IDR is sent");
        var statistics = Assert.Single(harness.Handler.GetOutputs()).Session!.Statistics.Snapshot();
        await harness.EndAsync(session);

        Assert.Equal(7, harness.Inner.Video.Count);
        Assert.InRange(queued, 3 * 21, 64);
        var packets = socket.Sent.Select(static s => RtpPacket.Parse(s.Datagram)).Where(static p => p.PayloadType == 96).ToList();
        Assert.Equal(4, packets.Count(static p => p.Marker));
        Assert.Equal(packets.Count, packets.Select(static p => p.SequenceNumber).Distinct().Count());
        Assert.All(packets.Zip(packets.Skip(1)), static pair => Assert.Equal((ushort)(pair.First.SequenceNumber + 1), pair.Second.SequenceNumber));
        Assert.Equal(statistics.PacketsDropped, dropped.GetMeasurementSnapshot().Sum(static m => m.Value));
        Assert.Contains(dropped.GetMeasurementSnapshot(), static m => Equals(m.Tags["reason"], "queue_full"));
        Assert.Contains(dropped.GetMeasurementSnapshot(), static m => Equals(m.Tags["reason"], "awaiting_keyframe"));
        Assert.Single(harness.Logs.GetSnapshot(), static r => r.Id.Id == 9405);
    }

    /// <summary>Verifies transient send errors drop only the affected packets, are counted, and are logged once; the session keeps sending.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task TransientSendErrors_DropPacketsAndContinue()
    {
        var socket = new FakeDatagramSocket { Failure = static i => i is 1 or 2 ? SocketError.NoBufferSpaceAvailable : null };
        var output = RtpTestData.Output(audioPort: null);
        output.RtcpEnabled = false;
        await using var harness = new RtpHarness((_, _) => socket, null, output);
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session);

        await harness.FrameAsync(session, 0, keyframe: true, RtpTestData.Nal(5, 5000));
        await harness.FrameAsync(session, 40, keyframe: false, RtpTestData.Nal(1, 100));
        await Eventually.WaitUntilAsync(() => socket.Attempts >= 6, "every packet is attempted");
        var status = Assert.Single(harness.Handler.GetOutputs()).Session!;
        var statistics = status.Statistics.Snapshot();

        Assert.Equal(RtpOutputSessionState.Active, status.State);
        Assert.Equal((4L, 2L, 2L), (statistics.PacketsSent, statistics.SendErrors, statistics.PacketsDropped));
        Assert.Single(harness.Logs.GetSnapshot(), static r => r.Id.Id == 9404);
        await harness.EndAsync(session);
        Assert.Equal(2, harness.Inner.Video.Count);
    }

    /// <summary>
    /// Verifies failure isolation: an output whose socket fails terminally, and one whose socket cannot be opened, stop
    /// sending for the publication while the other output and the inner handler receive everything.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task FailedOutputs_AreIsolated()
    {
        var failing = new FakeDatagramSocket { Failure = static _ => SocketError.AccessDenied };
        var healthy = new FakeDatagramSocket();
        await using var harness = new RtpHarness(
            (o, _) => o.Name switch
            {
                "failing" => failing,
                "unopenable" => throw new SocketException((int)SocketError.AddressNotAvailable),
                _ => healthy,
            },
            null,
            RtpTestData.Output("failing", 5004, null),
            RtpTestData.Output("unopenable", 5008, null),
            RtpTestData.Output("healthy", 5010, null));
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session);

        for (var i = 0; i < 5; i++)
        {
            await harness.FrameAsync(session, (uint)(i * 40), keyframe: i == 0, RtpTestData.Nal(i == 0 ? (byte)5 : (byte)1, 200));
        }

        await Eventually.WaitUntilAsync(() => healthy.Sent.Count(static s => s.Destination.Port == 5010) >= 5, "the healthy output sends every frame");
        var outputs = harness.Handler.GetOutputs().ToDictionary(static s => s.Output.Name, static s => s.Session!);
        await Eventually.WaitUntilAsync(() => outputs["failing"].State == RtpOutputSessionState.Failed, "the failing output fails");

        Assert.Equal(RtpOutputSessionState.Failed, outputs["unopenable"].State);
        Assert.Equal(RtpOutputSessionState.Active, outputs["healthy"].State);
        Assert.Equal(1, failing.Attempts);
        Assert.Empty(failing.Sent);
        Assert.Single(harness.Logs.GetSnapshot(), static r => r.Id.Id == 9402);
        Assert.Single(harness.Logs.GetSnapshot(), static r => r.Id.Id == 9403);
        await harness.EndAsync(session);
        Assert.Equal(5, harness.Inner.Video.Count);
        Assert.True(failing.Disposed);
    }

    /// <summary>Verifies an unexpected packetisation failure fails only that output, is logged, and the sample still reaches the inner handler.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task PacketizationFailure_IsContained()
    {
        var healthy = new FakeDatagramSocket();
        var broken = new FakeDatagramSocket();
        await using var harness = new RtpHarness((o, _) => o.Name == "broken" ? broken : healthy, null, RtpTestData.Output("broken", 5004, null), RtpTestData.Output("healthy", 5010, null));
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session);
        var outputs = harness.Handler.GetOutputs().ToDictionary(static s => s.Output.Name, static s => s.Session!);
        outputs["broken"].FailPacketization();

        await harness.FrameAsync(session, 0, keyframe: true, RtpTestData.Nal(5, 100));
        await Eventually.WaitUntilAsync(() => !healthy.Sent.IsEmpty, "the healthy output sends");

        Assert.Empty(broken.Sent);
        Assert.Equal(RtpOutputSessionState.Failed, outputs["broken"].State);
        Assert.Equal(1, outputs["broken"].Statistics.Snapshot().PacketsDropped);
        await harness.EndAsync(session);
        Assert.Single(harness.Inner.Video);
    }

    /// <summary>
    /// Verifies concurrent publications of different streams through one handler each reach only their own output,
    /// with contiguous sequence numbers and one marker per access unit.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ConcurrentPublications_AreIndependent()
    {
        const int Frames = 60;
        var sockets = new Dictionary<string, FakeDatagramSocket> { ["one"] = new(), ["two"] = new() };
        await using var harness = new RtpHarness((o, _) => sockets[o.Name], null, RtpTestData.Output("one", 5004, 5006, "cam-one"), RtpTestData.Output("two", 6004, 6006, "cam-two"));

        await Task.WhenAll(Publish("cam-one"), Publish("cam-two")).WithinSafetyTimeout();

        foreach (var socket in sockets.Values)
        {
            var video = socket.Sent.Where(static s => s.Destination.Port % 1000 == 4).Select(static s => RtpPacket.Parse(s.Datagram)).ToList();
            var audio = socket.Sent.Where(static s => s.Destination.Port % 1000 == 6).Select(static s => RtpPacket.Parse(s.Datagram)).ToList();
            Assert.Equal(Frames, video.Count(static p => p.Marker));
            Assert.Equal(Frames, audio.Count);
            Assert.All(video.Zip(video.Skip(1)), static pair => Assert.Equal((ushort)(pair.First.SequenceNumber + 1), pair.Second.SequenceNumber));
            Assert.All(audio.Zip(audio.Skip(1)), static pair => Assert.Equal((ushort)(pair.First.SequenceNumber + 1), pair.Second.SequenceNumber));
            Assert.All(audio.Zip(audio.Skip(1)), static pair => Assert.Equal(pair.First.Timestamp + 1024, pair.Second.Timestamp));
            Assert.True(socket.Disposed);
        }

        Assert.Empty(sockets["one"].Sent.Select(static s => s.Destination.Port).Intersect(sockets["two"].Sent.Select(static s => s.Destination.Port)));
        Assert.Equal(2 * Frames, harness.Inner.Video.Count);

        async Task Publish(string stream)
        {
            await Task.Yield();
            var session = await harness.StartAsync(stream);
            await harness.ConfigureAsync(session);
            for (var i = 0; i < Frames; i++)
            {
                await harness.FrameAsync(session, (uint)(i * 40), keyframe: i % 10 == 0, RtpTestData.Nal(i % 10 == 0 ? (byte)5 : (byte)1, 2000, i));
                await harness.AudioAsync(session, (uint)(i * 1024 * 1000 / 44100), FlvPackets.AacRaw(RtpTestData.Nal(0x21, 200, i)));
            }

            await harness.EndAsync(session);
        }
    }

    /// <summary>Verifies a sender that cannot drain is cancelled after the drain timeout, its queue counted as dropped and its socket closed.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Stop_IsBoundedWhenTheSenderIsStuck()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var socket = new FakeDatagramSocket { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var harness = new RtpHarness((_, _) => socket, time, RtpTestData.Output(audioPort: null));
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session);
        await harness.FrameAsync(session, 0, keyframe: true, RtpTestData.Nal(5, 10_000));
        var status = Assert.Single(harness.Handler.GetOutputs()).Session!;
        await Eventually.WaitUntilAsync(() => socket.Attempts == 1, "the sender is stuck in a send");
        var queued = status.QueuedPackets;

        var ending = harness.EndAsync(session);
        await Eventually.WaitUntilAsync(
            () =>
            {
                time.Advance(TimeSpan.FromMilliseconds(500));
                return ending.IsCompleted;
            },
            "the stop completes");
        await ending;

        Assert.Equal(RtpOutputSessionState.Stopped, status.State);
        Assert.True(socket.Disposed);
        Assert.Equal(queued, status.Statistics.Snapshot().PacketsDropped);
        Assert.Single(harness.Logs.GetSnapshot(), static r => r.Id.Id == 9406);
        Assert.Equal(0, harness.Handler.ActivePublicationCount);
    }

    /// <summary>Verifies disposing the handler (host shutdown) stops publications still sending: queue drained, BYE sent, socket closed.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Dispose_StopsActivePublications()
    {
        var socket = new FakeDatagramSocket();
        var harness = new RtpHarness((_, _) => socket, null, RtpTestData.Output(audioPort: null));
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session);
        await harness.FrameAsync(session, 0, keyframe: true, RtpTestData.Nal(5, 3000));

        await harness.Handler.DisposeAsync().AsTask().WithinSafetyTimeout();

        Assert.True(socket.Disposed);
        Assert.Equal(0, harness.Handler.ActivePublicationCount);
        Assert.Equal(1, socket.Sent.Count(static s => RtpPacket.Parse(s.Datagram).Marker && s.Destination.Port == 5004));
        Assert.Contains(socket.Sent, static s => s.Destination.Port == 5005 && s.Datagram.Length > 30 && s.Datagram[^7] == RtcpPackets.Goodbye);
        await harness.EndAsync(session);
        await harness.DisposeAsync();
        Assert.Equal(["started", "video", "ended"], harness.Inner.Events.Select(static e => e.Kind).Where(static k => k != "info"));
    }

    /// <summary>Verifies an inner handler that fails to start a session leaves no RTP session or socket behind.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task InnerStartFailure_StartsNoOutput()
    {
        var opened = 0;
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var handler = new RtpOutputHandler(
            new ThrowingStartHandler(),
            ownsInner: false,
            Microsoft.Extensions.Options.Options.Create(new RtpOptions { Outputs = [RtpTestData.Output()] }),
            (_, _) =>
            {
                opened++;
                return new FakeDatagramSocket();
            },
            new RtpMetrics(services.GetRequiredService<IMeterFactory>()),
            NullLogger<RtpOutputHandler>.Instance,
            TimeProvider.System);
        await using (handler)
        {
            var media = new MediaSession(MediaHarness.NewSession());
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.OnSessionStartedAsync(media, TestContext.Current.CancellationToken).AsTask());

            Assert.Equal(0, opened);
            Assert.Equal(0, handler.ActivePublicationCount);
            Assert.Null(Assert.Single(handler.GetOutputs()).Session);
        }
    }

    /// <summary>Verifies binding to a configured source address, and that an address no interface owns fails the socket.</summary>
    [Fact]
    public void Socket_BindsSourceAddress()
    {
        var output = RtpTestData.Output();
        output.SourceAddress = "127.0.0.1";
        using (var socket = new RtpUdpSocket(output))
        {
            Assert.Equal(IPAddress.Loopback, ((IPEndPoint)socket.LocalEndPoint!).Address);
        }

        output.SourceAddress = "192.0.2.123";
        Assert.Throws<SocketException>(() => new RtpUdpSocket(output).Dispose());
        Assert.Throws<InvalidOperationException>(() => RtpUdpSocket.InterfaceIndex(IPAddress.Parse("2001:db8::4321")));
        Assert.Equal(7, RtpUdpSocket.InterfaceIndex(IPAddress.Parse("fe80::1%7")));
        if (Socket.OSSupportsIPv6)
        {
            Assert.True(RtpUdpSocket.InterfaceIndex(IPAddress.IPv6Loopback) > 0);
        }
    }

    /// <summary>Verifies which socket errors are transient (the packet is dropped) and which are terminal (the session fails).</summary>
    /// <param name="error">The error.</param>
    /// <param name="transient">Whether it is transient.</param>
    [Theory]
    [InlineData(SocketError.NoBufferSpaceAvailable, true)]
    [InlineData(SocketError.WouldBlock, true)]
    [InlineData(SocketError.HostUnreachable, true)]
    [InlineData(SocketError.NetworkUnreachable, true)]
    [InlineData(SocketError.NetworkDown, true)]
    [InlineData(SocketError.ConnectionRefused, true)]
    [InlineData(SocketError.MessageSize, false)]
    [InlineData(SocketError.AccessDenied, false)]
    [InlineData(SocketError.AddressNotAvailable, false)]
    [InlineData(SocketError.OperationAborted, false)]
    public void TransportErrors_AreClassified(SocketError error, bool transient) => Assert.Equal(transient, RtpTransportErrors.IsTransient(error));

    /// <summary>
    /// Verifies multicast delivery to a group joined on the loopback interface, with the outgoing interface selected
    /// by the source address and multicast loopback enabled. Skipped where the host cannot loop multicast back.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Multicast_LoopbackGroup_ReceivesRtp()
    {
        var group = IPAddress.Parse("239.255.77.10");
        using var receiver = UdpReceiver.Bind(IPAddress.Any);
        try
        {
            receiver.Rtp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group, IPAddress.Loopback));
        }
        catch (SocketException ex)
        {
            Assert.Skip($"Joining a multicast group on loopback is not supported here: {ex.SocketErrorCode}.");
        }

        var output = RtpTestData.Output(videoPort: receiver.Port, audioPort: null);
        output.Mode = RtpOutputMode.Multicast;
        output.Destination = group.ToString();
        output.SourceAddress = "127.0.0.1";
        output.MulticastLoopback = true;
        output.TimeToLive = 1;
        Assert.SkipUnless(await LoopsBackAsync(output, receiver), "Multicast is not looped back on this host's loopback interface.");

        await using var harness = new RtpHarness(static (o, q) => new RtpUdpSocket(o, q), null, output);
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session);
        var idr = RtpTestData.Nal(5, 2500);
        await harness.FrameAsync(session, 0, keyframe: true, idr);

        var packets = await receiver.ReceiveUntilAsync(static p => p.Count > 0 && p[^1].Marker && p[^1].PayloadType == 96);

        Assert.Equal([RtpTestData.Sps, RtpTestData.Pps, idr], H264Depacketizer.NalUnits(packets.Where(static p => p.PayloadType == 96)));
        var status = Assert.Single(harness.Handler.GetOutputs());
        Assert.Contains($"c=IN IP4 {group}/1\r\n", RtpSdp.Render(status.Session!, status.StreamInfo!), StringComparison.Ordinal);
        await harness.EndAsync(session);
    }

    /// <summary>Receives RTCP datagrams until one ends with a BYE.</summary>
    /// <param name="socket">The RTCP socket.</param>
    /// <returns>The datagram.</returns>
    private static async Task<byte[]> ReceiveGoodbyeAsync(Socket socket)
    {
        while (true)
        {
            var datagram = await UdpReceiver.ReceiveAsync(socket);
            if (datagram.Length >= 8 && datagram[^7] == RtcpPackets.Goodbye)
            {
                return datagram;
            }
        }
    }

    /// <summary>Returns the RTP (even-port) datagrams a socket sent.</summary>
    /// <param name="socket">The socket.</param>
    /// <returns>The datagrams.</returns>
    private static List<(byte[] Datagram, IPEndPoint Destination)> Rtp(FakeDatagramSocket socket) =>
        [.. socket.Sent.Where(static s => s.Destination.Port % 2 == 0)];

    /// <summary>Sends one probe datagram with an output's multicast settings and reports whether it arrives.</summary>
    /// <param name="output">The output.</param>
    /// <param name="receiver">The receiver.</param>
    /// <returns><see langword="true"/> if the probe was received.</returns>
    private static async Task<bool> LoopsBackAsync(RtpOutputOptions output, UdpReceiver receiver)
    {
        using var probe = new RtpUdpSocket(output);
        var destination = new IPEndPoint(IPAddress.Parse(output.Destination), receiver.Port).Serialize();
        await probe.SendToAsync(new byte[] { 0x80, 0x7F, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, destination, TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var buffer = new byte[64];
            await receiver.Rtp.ReceiveAsync(buffer, SocketFlags.None, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>A media handler whose session start fails.</summary>
    private sealed class ThrowingStartHandler : IMediaSessionHandler
    {
        /// <inheritdoc />
        public ValueTask OnSessionStartedAsync(MediaSession session, CancellationToken cancellationToken) => throw new InvalidOperationException("Inner start failure.");

        /// <inheritdoc />
        public ValueTask OnStreamInfoChangedAsync(MediaSession session, MediaStreamInfo streamInfo, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        /// <inheritdoc />
        public ValueTask OnVideoSampleAsync(MediaSession session, EncodedVideoSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        /// <inheritdoc />
        public ValueTask OnAudioSampleAsync(MediaSession session, EncodedAudioSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        /// <inheritdoc />
        public ValueTask OnMediaErrorAsync(MediaSession session, MediaProcessingError mediaError, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        /// <inheritdoc />
        public ValueTask OnSessionEndedAsync(MediaSession session, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
