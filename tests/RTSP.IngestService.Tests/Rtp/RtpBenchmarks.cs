// Explicit (not run by default) throughput measurements of RTP output, for the performance notes in
// docs/rtp-output.md: packetisation of H.264 and AAC access units into the ring, and end-to-end sending of a 5 Mbit/s
// class stream through RtpOutputSession over loopback UDP. Run with --filter-class "*RtpBenchmarks" --explicit only.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Rtp;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Tests.Media;

namespace RTSP.IngestService.Tests.Rtp;

/// <summary>Throughput measurements of RTP output.</summary>
public sealed class RtpBenchmarks
{
    /// <summary>Measures packetisation and drain of a 25 KB H.264 access unit and a 400-byte AAC access unit, and the bytes allocated.</summary>
    [Fact(Explicit = true)]
    public void Packetization_Throughput()
    {
        var ring = new RtpPacketRing(2048, 1472);
        var video = new H264RtpPacketizer(new RtpStream(96, 90000, 1, 0, 0));
        var audio = new AacRtpPacketizer(new RtpStream(97, 48000, 2, 0, 0), 48000, 1024);
        video.SetNalUnits([RtpTestData.Nal(9, 2), RtpTestData.Nal(6, 30), RtpTestData.Nal(5, 25_000)]);
        var accessUnit = new byte[400];
        Run(1000);

        const int Iterations = 200_000;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        var (packets, bytes) = Run(Iterations);
        var seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

        Report(string.Create(
            CultureInfo.InvariantCulture,
            $"packetization: {Iterations} video+audio access units in {seconds:F3} s; {Iterations / seconds:F0} AU pairs/s; {packets / seconds / 1e6:F2} M packets/s; {bytes * 8 / seconds / 1e9:F2} Gbit/s; {allocated} bytes allocated"));
        Assert.Equal(0, allocated);

        (long Packets, long Bytes) Run(int iterations)
        {
            long packetCount = 0, byteCount = 0;
            for (var i = 0; i < iterations; i++)
            {
                video.Packetize(ring, 0, (uint)i, out var p, out var b);
                packetCount += p;
                byteCount += b;
                audio.Packetize(ring, 1, accessUnit, (uint)i, out p, out b);
                packetCount += p;
                byteCount += b;
                while (ring.TryPeek(out _, out _))
                {
                    ring.Advance();
                }
            }

            return (packetCount, byteCount);
        }
    }

    /// <summary>
    /// Measures end-to-end sending through a session over loopback UDP: 25 fps H.264 of 25 KB per frame plus AAC, as
    /// fast as possible, unmarked and with EF marking.
    /// </summary>
    /// <param name="marked">Whether the output marks its packets EF.</param>
    /// <returns>A task that completes when the measurement is finished.</returns>
    [Theory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Session_LoopbackSendThroughput(bool marked)
    {
        using var video = UdpReceiver.Bind(IPAddress.Loopback);
        using var audio = UdpReceiver.Bind(IPAddress.Loopback);
        var output = RtpTestData.Output(videoPort: video.Port, audioPort: audio.Port);
        output.QueueCapacity = 8192;
        output.SendBufferSize = 4 * 1024 * 1024;
        output.Qos = marked ? RtpQosTestData.Qos(RtpDscp.EF) : null;
        await using var harness = new RtpHarness(static (o, q) => new RtpUdpSocket(o, q), null, output);
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session);
        var frame = RtpTestData.Nal(1, 25_000);
        var idr = RtpTestData.Nal(5, 25_000);
        var drain = Task.Run(() => DrainAsync(video), TestContext.Current.CancellationToken);
        var drainAudio = Task.Run(() => DrainAsync(audio), TestContext.Current.CancellationToken);

        const int Frames = 2000;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < Frames; i++)
        {
            await harness.FrameAsync(session, (uint)(i * 40), keyframe: i % 50 == 0, i % 50 == 0 ? idr : frame);
            await harness.AudioAsync(session, (uint)(i * 40), FlvPackets.AacRaw(new byte[400]));
        }

        var status = Assert.Single(harness.Handler.GetOutputs()).Session!;
        await Eventually.WaitUntilAsync(() => status.QueuedPackets == 0, "the queue drains");
        watch.Stop();
        var statistics = status.Statistics.Snapshot();
        Assert.Equal(marked ? (byte)0xB8 : null, status.TrafficClass);
        await harness.EndAsync(session);

        var seconds = watch.Elapsed.TotalSeconds;
        Report(string.Create(
            CultureInfo.InvariantCulture,
            $"loopback session{(marked ? ", QoS EF (0xb8)" : string.Empty)}: {Frames} frames ({Frames / 25.0:F0} s of media) in {seconds:F3} s ({Frames / 25.0 / seconds:F1}x real time); {statistics.PacketsSent / seconds / 1e3:F0} k packets/s; {statistics.BytesSent * 8 / seconds / 1e6:F0} Mbit/s; dropped {statistics.PacketsDropped}; errors {statistics.SendErrors}"));
        Assert.Equal(0, statistics.SendErrors);
        video.Dispose();
        audio.Dispose();
        await Task.WhenAll(drain, drainAudio).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
    }

    /// <summary>Writes a result to the test output and, if <c>RTSP_RTP_BENCHMARK_OUTPUT</c> names a file, appends it there.</summary>
    /// <param name="line">The result.</param>
    private static void Report(string line)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        if (Environment.GetEnvironmentVariable("RTSP_RTP_BENCHMARK_OUTPUT") is { Length: > 0 } path)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    /// <summary>Discards datagrams until the socket closes.</summary>
    /// <param name="receiver">The receiver.</param>
    /// <returns>A task.</returns>
    private static async Task DrainAsync(UdpReceiver receiver)
    {
        var buffer = new byte[2048];
        while (true)
        {
            await receiver.Rtp.ReceiveAsync(buffer, System.Net.Sockets.SocketFlags.None);
        }
    }
}
