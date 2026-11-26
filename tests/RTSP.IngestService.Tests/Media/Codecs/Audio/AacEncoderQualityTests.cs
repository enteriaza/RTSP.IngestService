// Objective quality regression tests for the managed AAC encoder on deterministic synthetic programmes
// (AudioQualitySignals), decoded by the managed decoder and measured by AudioQualityMetrics. The ceilings sit about
// 2 dB above the values measured when TNS was introduced (see .artifacts/codecs/managed-codec-milestone-2-remediation-
// report.md), so they catch regressions in noise allocation, TNS and stereo coding without pinning exact output.

using RTSP.IngestService.Media.Codecs.Audio.Aac.Encoder;

namespace RTSP.IngestService.Tests.Media.Codecs.Audio;

/// <summary>Quality, bitrate and artefact tests for <see cref="AacEncoder"/>.</summary>
public sealed class AacEncoderQualityTests
{
    /// <summary>Three seconds of each programme.</summary>
    private const int Length = 3 * AudioQualitySignals.Rate;

    /// <summary>Programme, bitrate, and ceilings for the 2048- and 256-sample-frame NMR in decibels.</summary>
    public static TheoryData<string, int, double, double> Cases => new()
    {
        { "speech", 32000, 11, 24 },
        { "speech", 64000, 5, 17 },
        { "speech", 160000, -11, 0 },
        { "speech", 256000, -20, -9 },
        { "tonal", 64000, 10, 10.5 },
        { "tonal", 160000, -2.5, 0.5 },
        { "complex", 64000, 8, 8 },
        { "complex", 160000, 3, 3.5 },
        { "transients", 64000, 6, 18 },
        { "transients", 160000, -4.5, 3.5 },
        { "stereo", 64000, 8, 8.5 },
        { "stereo", 160000, 1, 1.5 },
        { "near-silence", 160000, -35, -34 },
    };

    /// <summary>
    /// Each programme stays below its NMR ceilings on both channels, keeps the average bitrate within the target,
    /// keeps every access unit within 6144 bits per channel, and does not clip.
    /// </summary>
    /// <param name="programme">The programme.</param>
    /// <param name="bitrate">The bitrate.</param>
    /// <param name="nmrCeiling">The largest acceptable 2048-sample-frame NMR.</param>
    /// <param name="shortNmrCeiling">The largest acceptable 256-sample-frame NMR.</param>
    [Theory]
    [MemberData(nameof(Cases))]
    public void ProgrammeMeetsQualityAndRate(string programme, int bitrate, double nmrCeiling, double shortNmrCeiling)
    {
        var planes = AudioQualitySignals.Create(programme, Length);
        using var encoder = new AacEncoder(bitrate);
        var units = AudioTestSupport.Encode(encoder, planes[0], planes[1]);
        var decoded = AudioTestSupport.DecodeManaged(units);
        var rate = AudioQualityMetrics.Bitrate(units);

        for (var c = 0; c < 2; c++)
        {
            var quality = AudioQualityMetrics.Measure(planes[c], decoded[c].AsSpan(AacEncoder.Delay, Length), encoder.Bandwidth);
            Assert.True(quality.TotalNmr <= nmrCeiling, $"{programme} {bitrate} channel {c}: NMR {quality.TotalNmr:F1} dB, ceiling {nmrCeiling}");
            Assert.True(quality.ShortNmr <= shortNmrCeiling, $"{programme} {bitrate} channel {c}: NMR256 {quality.ShortNmr:F1} dB, ceiling {shortNmrCeiling}");
            Assert.Equal(0, quality.ClippedSamples);
        }

        Assert.True(rate.Average <= bitrate * 1.005, $"Average {rate.Average:F0} bit/s for {bitrate}");
        Assert.True(rate.SecondMax <= bitrate * 1.1, $"Busiest second {rate.SecondMax:F0} bit/s for {bitrate}");
        Assert.InRange(rate.FrameMax, 0, AacEncoder.MaxFrameBits);
    }

    /// <summary>
    /// TNS is chosen for long frames with an uneven temporal envelope that is predictable across frequency (voice
    /// onsets and cut-offs, decays after attacks), and not for stationary tones or dense music, whose spectra may be
    /// predictable but whose envelopes have nothing to shape.
    /// </summary>
    /// <param name="programme">The programme.</param>
    /// <param name="minimum">The fewest channel frames using TNS.</param>
    /// <param name="maximum">The most.</param>
    [Theory]
    [InlineData("speech", 16, 64)]
    [InlineData("transients", 10, 60)]
    [InlineData("tonal", 0, 2)]
    [InlineData("complex", 0, 2)]
    [InlineData("sine", 0, 2)]
    public void TnsIsUsedWhereTheEnvelopeIsUneven(string programme, int minimum, int maximum)
    {
        var planes = programme == "sine" ? [AudioTestSupport.Sine(Length, 48000, 5000, 0.5), AudioTestSupport.Sine(Length, 48000, 5000, 0.5)] : AudioQualitySignals.Create(programme, Length);
        using var encoder = new AacEncoder();
        AudioTestSupport.Encode(encoder, planes[0], planes[1]);
        Assert.InRange(encoder.Statistics.TnsChannels, minimum, maximum);
    }

    /// <summary>
    /// Coding error in the 500 samples before each attack (pre-echo) stays well below the attack, relative to its
    /// first 496 samples, at 64 and 160 kbit/s.
    /// </summary>
    /// <param name="bitrate">The bitrate.</param>
    /// <param name="ceiling">The largest acceptable pre-echo-to-attack ratio in decibels.</param>
    [Theory]
    [InlineData(64000, -12)]
    [InlineData(160000, -26)]
    public void PreEchoStaysBelowTheAttack(int bitrate, double ceiling)
    {
        var planes = AudioQualitySignals.Transients(Length, 4, out var onsets);
        using var encoder = new AacEncoder(bitrate);
        var decoded = AudioTestSupport.DecodeManaged(AudioTestSupport.Encode(encoder, planes[0], planes[1]));
        Assert.NotEmpty(onsets);
        foreach (var onset in onsets)
        {
            var error = 0.0;
            for (var n = onset - 512; n < onset - 16; n++)
            {
                var d = decoded[0][n + AacEncoder.Delay] - planes[0][n];
                error += d * d;
            }

            var attack = 0.0;
            for (var n = onset; n < onset + 496; n++)
            {
                attack += planes[0][n] * planes[0][n];
            }

            var ratio = 10 * Math.Log10(error / attack);
            Assert.True(ratio <= ceiling, $"Pre-echo {ratio:F1} dB before the attack at {onset}, ceiling {ceiling}");
        }
    }

    /// <summary>
    /// Near-silence decodes without bursts: digital silence stays exactly zero and the -90 dBFS noise and fading tone
    /// never exceed their input peak by more than 6 dB, at every bitrate.
    /// </summary>
    /// <param name="bitrate">The bitrate.</param>
    [Theory]
    [InlineData(32000)]
    [InlineData(160000)]
    [InlineData(512000)]
    public void NearSilenceHasNoBursts(int bitrate)
    {
        var planes = AudioQualitySignals.Create("near-silence", Length);
        using var encoder = new AacEncoder(bitrate);
        var decoded = AudioTestSupport.DecodeManaged(AudioTestSupport.Encode(encoder, planes[0], planes[1]));
        var silent = (Length / 3) - (2 * AacEncoder.FrameLength);
        Assert.Equal(0, AudioTestSupport.Peak(decoded[0].AsSpan(AacEncoder.Delay, silent)));
        Assert.Equal(0, AudioTestSupport.Peak(decoded[1].AsSpan(AacEncoder.Delay, silent)));
        var inputPeak = AudioTestSupport.Peak(planes[0]);
        Assert.InRange(AudioTestSupport.Peak(decoded[0].AsSpan(AacEncoder.Delay, Length)), 0, 2 * inputPeak);
    }
}
