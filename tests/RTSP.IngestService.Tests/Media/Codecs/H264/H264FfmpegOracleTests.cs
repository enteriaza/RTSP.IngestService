// Live oracle for the managed H.264 decoder: FFmpeg encodes fresh streams that exercise features beyond the committed
// fixtures (all partitions, strict B-pyramid, weighted bi-prediction, constrained intra, intra refresh, extreme QPs,
// odd cropping, many slices, noise-heavy residuals), decodes them single-threaded as the reference, and the managed
// decoder's output is compared frame by frame.
//
// Tolerances, fixed before any comparison was run and never to be relaxed: the decoder is a bit-exact implementation
// of a deterministic standard, so every plane of every frame must be identical — PSNR = +infinity and SSIM = 1 per
// plane. PSNR and SSIM are computed and reported so that a regression shows its magnitude, not just its presence.
// The tests skip when FFmpeg with libx264 is unavailable (fail when RTSP_FFMPEG_REQUIRED=true).

using System.Diagnostics;
using System.Globalization;
using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Media.Codecs.H264;

/// <summary>Comparison with FFmpeg's H.264 decoder on freshly encoded streams.</summary>
public sealed class H264FfmpegOracleTests
{
    /// <summary>The required PSNR per plane, in dB.</summary>
    private const double RequiredPsnr = double.PositiveInfinity;

    /// <summary>The required SSIM per plane.</summary>
    private const double RequiredSsim = 1.0;

    /// <summary>Gets the oracle streams: name, lavfi source, frame count and libx264 arguments.</summary>
    public static TheoryData<string, string, int, string> Streams => new()
    {
        { "cabac-all-partitions-strict-pyramid", "testsrc2=size=272x208:rate=25,noise=alls=12:allf=t", 40, "-profile:v high -crf 23 -x264-params keyint=40:bframes=3:b-pyramid=strict:b-adapt=2:weightb=1:weightp=2:ref=5:8x8dct=1:partitions=all:direct=auto:mixed-refs=1:trellis=2:me=umh:subme=9" },
        { "cavlc-constrained-intra-slices", "mandelbrot=size=224x160:rate=25", 30, "-profile:v high -crf 20 -x264-params keyint=15:cabac=0:constrained-intra=1:slices=5:bframes=2:ref=3:8x8dct=1:weightp=1" },
        { "intra-refresh-no-idr", "testsrc2=size=192x128:rate=25", 50, "-profile:v high -crf 26 -x264-params keyint=20:intra-refresh=1:bframes=0:ref=1" },
        { "high-qp-strong-deblock", "life=size=160x112:rate=25:mold=10:ratio=0.3,format=yuv420p,noise=alls=30:allf=t", 24, "-profile:v main -qp 48 -x264-params keyint=12:bframes=2:deblock=3,3" },
        { "low-qp-large-coefficients", "testsrc2=size=144x96:rate=25,noise=alls=60:allf=t", 12, "-profile:v high -qp 2 -x264-params keyint=6:bframes=1:8x8dct=1:deblock=-3,-3" },
        { "odd-crop-temporal-direct-cqm", "testsrc2=size=130x74:rate=25", 30, "-profile:v high -crf 24 -x264-params keyint=30:bframes=3:direct=temporal:cqm=jvt:ref=4:weightb=1:no-deblock=1" },
        { "baseline-multiref-cavlc", "testsrc2=size=176x144:rate=25,noise=alls=8:allf=t", 30, "-profile:v baseline -crf 22 -x264-params keyint=30:ref=6:partitions=all:slices=2" },
    };

    /// <summary>Every frame equals FFmpeg's decode of the same freshly encoded stream.</summary>
    /// <param name="name">The stream name.</param>
    /// <param name="source">The lavfi source graph.</param>
    /// <param name="frames">The frame count.</param>
    /// <param name="encoder">The libx264 arguments.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(Streams))]
    public async Task MatchesFfmpegExactly(string name, string source, int frames, string encoder)
    {
        var ffmpeg = FfmpegTool.Require();
        var directory = Directory.CreateTempSubdirectory("h264-oracle-");
        try
        {
            var stream = Path.Combine(directory.FullName, $"{name}.h264");
            var raw = Path.Combine(directory.FullName, $"{name}.yuv");
            string[] encode =
            [
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "lavfi", "-i", source, "-frames:v", frames.ToString(CultureInfo.InvariantCulture),
                "-c:v", "libx264", "-threads", "1", "-pix_fmt", "yuv420p", .. encoder.Split(' '), "-an", "-f", "h264", stream,
            ];
            await RunAsync(ffmpeg, encode);
            await RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-threads", "1", "-i", stream, "-f", "rawvideo", "-pix_fmt", "yuv420p", raw]);

            using var decoder = H264TestSupport.CreateAnnexB();
            var units = H264TestSupport.SplitAccessUnits(await File.ReadAllBytesAsync(stream, TestContext.Current.CancellationToken));
            var reference = await File.ReadAllBytesAsync(raw, TestContext.Current.CancellationToken);
            var output = new List<VideoFrame>();
            var index = 0;
            var failures = new List<string>();
            for (var i = 0; i <= units.Count; i++)
            {
                if (i < units.Count)
                {
                    decoder.Decode(new EncodedPacket(units[i], i, H264TestSupport.TimeBase), output, TestContext.Current.CancellationToken);
                }
                else
                {
                    decoder.Drain(output);
                }

                foreach (var frame in output)
                {
                    var size = frame.ByteCount;
                    Assert.True(reference.Length >= (index + 1) * size, $"{name}: the managed decoder output more frames than FFmpeg.");
                    var expected = reference.AsMemory(index * size, size);
                    Compare(frame, expected.Span, index, failures);
                    frame.Dispose();
                    index++;
                }

                output.Clear();
            }

            Assert.True(index == frames, $"{name}: decoded {index} frames, expected {frames}.");
            Assert.True(failures.Count == 0, $"{name}: {failures.Count} plane(s) outside tolerance; first: {string.Join("; ", failures.Take(4))}");
            Assert.Equal(0, decoder.SkippedPictures);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    /// <summary>The metrics report identity as +infinity and 1 and degrade monotonically with distortion.</summary>
    [Fact]
    public void MetricsBehave()
    {
        var a = new byte[32 * 32];
        new Random(3).NextBytes(a);
        Assert.Equal(double.PositiveInfinity, VideoQualityMetrics.Psnr(a, a));
        Assert.Equal(1.0, VideoQualityMetrics.Ssim(a, a, 32, 32), 12);
        var b = a.Select(static v => (byte)Math.Clamp(v + 2, 0, 255)).ToArray();
        var c = a.Select(static (v, i) => (byte)Math.Clamp(v + ((i % 7) * 6) - 18, 0, 255)).ToArray();
        Assert.InRange(VideoQualityMetrics.Psnr(a, b), 40, 49);
        Assert.True(VideoQualityMetrics.Psnr(a, c) < VideoQualityMetrics.Psnr(a, b));
        Assert.True(VideoQualityMetrics.Ssim(a, c, 32, 32) < VideoQualityMetrics.Ssim(a, b, 32, 32));
        Assert.True(VideoQualityMetrics.Ssim(a, b, 32, 32) < 1.0);
    }

    /// <summary>Compares each plane of a frame against the reference and records any outside tolerance.</summary>
    /// <param name="frame">The decoded frame.</param>
    /// <param name="expected">The reference frame, packed planar 4:2:0.</param>
    /// <param name="index">The frame index.</param>
    /// <param name="failures">Receives descriptions.</param>
    private static void Compare(VideoFrame frame, ReadOnlySpan<byte> expected, int index, List<string> failures)
    {
        var offset = 0;
        for (var plane = 0; plane < 3; plane++)
        {
            var actual = frame.GetPlane(plane);
            var reference = expected.Slice(offset, actual.Length);
            offset += actual.Length;
            var width = frame.GetStride(plane);
            var height = actual.Length / width;
            var psnr = VideoQualityMetrics.Psnr(reference, actual);
            var ssim = VideoQualityMetrics.Ssim(reference, actual, width, height);
            if (!actual.SequenceEqual(reference) || psnr < RequiredPsnr || ssim < RequiredSsim)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture, $"frame {index} plane {plane}: PSNR {psnr:F2} dB, SSIM {ssim:F6}"));
            }
        }
    }

    /// <summary>Runs a tool to completion.</summary>
    /// <param name="tool">The executable.</param>
    /// <param name="arguments">The arguments.</param>
    /// <returns>A task.</returns>
    private static async Task RunAsync(string tool, string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        await output;
        Assert.True(process.ExitCode == 0, $"{Path.GetFileName(tool)} failed: {errors}");
    }
}

/// <summary>Full-reference picture quality metrics over one 8-bit plane.</summary>
internal static class VideoQualityMetrics
{
    /// <summary>Returns the PSNR in dB with peak 255; +infinity for identical planes.</summary>
    /// <param name="reference">The reference samples.</param>
    /// <param name="test">The test samples.</param>
    /// <returns>The PSNR.</returns>
    public static double Psnr(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> test)
    {
        long sum = 0;
        for (var i = 0; i < reference.Length; i++)
        {
            var d = reference[i] - test[i];
            sum += d * d;
        }

        return sum == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 * reference.Length / sum);
    }

    /// <summary>Returns the mean SSIM (Wang et al. 2004) over 8x8 windows at a stride of 4, with K1 = 0.01 and K2 = 0.03.</summary>
    /// <param name="reference">The reference samples.</param>
    /// <param name="test">The test samples.</param>
    /// <param name="width">The plane width.</param>
    /// <param name="height">The plane height.</param>
    /// <returns>The SSIM, 1 for identical planes.</returns>
    public static double Ssim(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> test, int width, int height)
    {
        const double c1 = 0.01 * 255 * 0.01 * 255;
        const double c2 = 0.03 * 255 * 0.03 * 255;
        var window = Math.Min(8, Math.Min(width, height));
        double total = 0;
        var count = 0;
        for (var y = 0; y + window <= height; y += Math.Max(1, window / 2))
        {
            for (var x = 0; x + window <= width; x += Math.Max(1, window / 2))
            {
                double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
                for (var j = 0; j < window; j++)
                {
                    for (var i = 0; i < window; i++)
                    {
                        double a = reference[((y + j) * width) + x + i], b = test[((y + j) * width) + x + i];
                        sa += a;
                        sb += b;
                        saa += a * a;
                        sbb += b * b;
                        sab += a * b;
                    }
                }

                var n = window * window;
                double ma = sa / n, mb = sb / n;
                var va = (saa / n) - (ma * ma);
                var vb = (sbb / n) - (mb * mb);
                var cov = (sab / n) - (ma * mb);
                total += ((2 * ma * mb) + c1) * ((2 * cov) + c2) / (((ma * ma) + (mb * mb) + c1) * (va + vb + c2));
                count++;
            }
        }

        return total / count;
    }
}
