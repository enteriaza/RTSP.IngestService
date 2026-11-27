// Picture quality and rate control of the managed H.264 encoder against acceptance thresholds recorded in the phase
// plan before any result was observed (they are not to be relaxed after a failure):
//   luma PSNR at base QP 22: >= 40 dB for flat, bars, ramp and gradient; >= 34 dB for detail and noise; >= 36 dB
//   for motion; chroma PSNR >= 38 dB and luma SSIM >= 0.95 for every source; no frame more than 6 dB below the
//   sequence mean PSNR except the first P frame after a scene cut (10 dB); AverageBitrate within +-15 % of the
//   target over >= 4 s when the QP bounds do not bind; a decoder-side VBV model never underflows.
// PSNR of identical planes is infinite; for means and consistency it is capped at 100 dB (decided with the
// thresholds). Quality is measured on the decoder's output, which the round-trip tests prove equals the
// encoder's reconstruction.

using System.Globalization;
using RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

namespace RTSP.IngestService.Tests.Media.Codecs.H264;

/// <summary>Managed H.264 encoder quality and rate control.</summary>
public sealed class H264EncoderQualityTests
{
    /// <summary>The PSNR cap for identical planes.</summary>
    private const double PsnrCap = 100;

    /// <summary>Gets the sources with their luma PSNR threshold.</summary>
    public static TheoryData<SyntheticPattern, double> Sources => new()
    {
        { SyntheticPattern.Flat, 40 },
        { SyntheticPattern.Bars, 40 },
        { SyntheticPattern.Ramp, 40 },
        { SyntheticPattern.Gradient, 40 },
        { SyntheticPattern.Detail, 34 },
        { SyntheticPattern.Noise, 34 },
        { SyntheticPattern.Motion, 36 },
        { SyntheticPattern.Cuts, 36 },
    };

    /// <summary>Quality at base QP 22 meets the thresholds for every source.</summary>
    /// <param name="pattern">The source.</param>
    /// <param name="lumaThreshold">The required mean luma PSNR.</param>
    [Theory]
    [MemberData(nameof(Sources))]
    public void QualityAtQp22MeetsThresholds(SyntheticPattern pattern, double lumaThreshold)
    {
        const int width = 176, height = 144, count = 36;
        var options = H264EncoderTestSupport.Options(width, height, 22) with { MaxBitrateKilobits = 100000 };
        using var encoder = new H264Encoder(options);
        var frames = H264EncoderTestSupport.EncodeAll(encoder, pattern, count);
        var decoded = H264EncoderTestSupport.DecodeManaged(encoder.DecoderConfigurationRecord, frames);
        Assert.Equal(count, decoded.Count);
        var luma = new double[count];
        var chroma = new double[count];
        var ssim = new double[count];
        var lumaSize = width * height;
        var chromaSize = lumaSize / 4;
        for (var i = 0; i < count; i++)
        {
            using var source = H264EncoderTestSupport.CreateFrame(pattern, width, height, i);
            var reference = H264EncoderTestSupport.Pack(source);
            var test = decoded[i].Samples;
            luma[i] = Math.Min(PsnrCap, VideoQualityMetrics.Psnr(reference.AsSpan(0, lumaSize), test.AsSpan(0, lumaSize)));
            chroma[i] = Math.Min(
                Math.Min(PsnrCap, VideoQualityMetrics.Psnr(reference.AsSpan(lumaSize, chromaSize), test.AsSpan(lumaSize, chromaSize))),
                Math.Min(PsnrCap, VideoQualityMetrics.Psnr(reference.AsSpan(lumaSize + chromaSize, chromaSize), test.AsSpan(lumaSize + chromaSize, chromaSize))));
            ssim[i] = VideoQualityMetrics.Ssim(reference.AsSpan(0, lumaSize), test.AsSpan(0, lumaSize), width, height);
        }

        var meanLuma = luma.Average();
        var summary = string.Create(CultureInfo.InvariantCulture, $"{pattern}: luma {meanLuma:F2} dB (min {luma.Min():F2}), chroma min {chroma.Min():F2} dB, SSIM min {ssim.Min():F4}, {frames.Sum(static f => f.Data.Length)} bytes");
        TestContext.Current.TestOutputHelper?.WriteLine(summary);
        Assert.True(meanLuma >= lumaThreshold, summary);
        Assert.True(chroma.Min() >= 38, summary);
        Assert.True(ssim.Min() >= 0.95, summary);
        for (var i = 0; i < count; i++)
        {
            var cut = pattern == SyntheticPattern.Cuts && i % 12 == 0 && i > 0;
            var allowed = cut ? 10 : 6;
            Assert.True(luma[i] >= meanLuma - allowed, string.Create(CultureInfo.InvariantCulture, $"{summary}; frame {i} at {luma[i]:F2} dB"));
        }
    }

    /// <summary>AverageBitrate holds the target within 15 % over eight seconds when the QP bounds do not bind.</summary>
    /// <param name="target">The target in kbit/s.</param>
    [Theory]
    [InlineData(300)]
    [InlineData(900)]
    public void AverageBitrateTracksTarget(int target)
    {
        const int fps = 25, seconds = 8;
        var options = H264EncoderTestSupport.Options(320, 240, 26) with
        {
            RateControl = H264RateControlMode.AverageBitrate,
            TargetBitrateKilobits = target,
            MaxBitrateKilobits = target * 2,
            KeyframeInterval = TimeSpan.FromSeconds(2),
        };
        using var encoder = new H264Encoder(options);
        var frames = H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Motion, fps * seconds);
        var kilobits = frames.Sum(static f => f.Data.Length * 8L) / 1000.0;
        var rate = kilobits / seconds;
        var qps = frames.Select(static f => f.Statistics.Qp).ToArray();
        var summary = string.Create(CultureInfo.InvariantCulture, $"target {target} kbit/s, achieved {rate:F1} kbit/s, QP {qps.Min()}-{qps.Max()}");
        TestContext.Current.TestOutputHelper?.WriteLine(summary);
        Assert.True(qps.Min() > options.MinQp && qps.Max() < options.MaxQp, $"The QP bounds bound the test: {summary}");
        Assert.InRange(rate, target * 0.85, target * 1.15);
        AssertNoUnderflow(options, frames);
    }

    /// <summary>A tight VBV never underflows in a decoder-side model, even for content far above the peak rate.</summary>
    /// <param name="average">Whether to use AverageBitrate rather than ConstantQuality.</param>
    /// <param name="pattern">The source.</param>
    [Theory]
    [InlineData(false, SyntheticPattern.Noise)]
    [InlineData(false, SyntheticPattern.Cuts)]
    [InlineData(true, SyntheticPattern.Motion)]
    public void VbvNeverUnderflows(bool average, SyntheticPattern pattern)
    {
        var mode = average ? H264RateControlMode.AverageBitrate : H264RateControlMode.ConstantQuality;
        var options = H264EncoderTestSupport.Options(176, 144, 10) with
        {
            RateControl = mode,
            MaxBitrateKilobits = 100,
            TargetBitrateKilobits = mode == H264RateControlMode.AverageBitrate ? 100 : 0,
            KeyframeInterval = TimeSpan.FromSeconds(1),
        };
        using var encoder = new H264Encoder(options);
        var frames = H264EncoderTestSupport.EncodeAll(encoder, pattern, 75);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(" ", frames.Take(12).Select(static f => string.Create(CultureInfo.InvariantCulture, $"{f.Statistics.Qp}/{f.Statistics.Attempts}/{f.Data.Length}/{f.Statistics.BufferFullnessBits}"))));
        Assert.All(frames, static f => Assert.False(f.Statistics.VbvOverflow));
        Assert.Contains(frames, static f => f.Statistics.Qp > 10);
        AssertNoUnderflow(options, frames);
        H264EncoderTestSupport.AssertMatchesReconstruction(frames, H264EncoderTestSupport.DecodeManaged(encoder.DecoderConfigurationRecord, frames));
    }

    /// <summary>ConstantQuality codes the base QP (and the intra offset) when the VBV does not bind.</summary>
    [Fact]
    public void ConstantQualityUsesBaseQp()
    {
        var options = H264EncoderTestSupport.Options(176, 144, 24) with { MaxBitrateKilobits = 100000 };
        using var encoder = new H264Encoder(options);
        var frames = H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Motion, 60);
        Assert.All(frames, static f => Assert.Equal(f.IsKeyframe ? 21 : 24, f.Statistics.Qp));
        Assert.All(frames, static f => Assert.Equal(1, f.Statistics.Attempts));
    }

    /// <summary>
    /// Simulates the decoder buffer: it starts 90 % full one maximum-frame-rate interval before the first picture,
    /// fills at the peak rate up to its size, and each access unit must be present in full when removed.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="frames">The frames.</param>
    private static void AssertNoUnderflow(H264EncoderOptions options, List<EncodedFrame> frames)
    {
        double size = options.BufferBits, rate = options.MaxBitrateKilobits * 1000.0;
        var fullness = size * 0.9;
        var previous = frames[0].Timestamp - (90000.0 / options.MaxFrameRate);
        for (var i = 0; i < frames.Count; i++)
        {
            fullness = Math.Min(size, fullness + (rate * (frames[i].Timestamp - previous) / 90000.0));
            previous = frames[i].Timestamp;
            var bits = frames[i].Data.Length * 8.0;
            Assert.True(bits <= fullness, string.Create(CultureInfo.InvariantCulture, $"VBV underflow at frame {i}: {bits} bits with {fullness:F0} buffered."));
            fullness -= bits;
        }
    }
}
