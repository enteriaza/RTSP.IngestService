// Objective quality measures for encoder tests, independent of the encoder's own psychoacoustic model:
//  * SNR and segmental SNR (1024-sample segments above -60 dBFS, each clamped to [-10, 80] dB);
//  * a PEAQ-style noise-to-mask ratio. Each Hann frame (hop half a frame) of the reference and of the coding error is
//    grouped into one-Bark bands (Zwicker and Terhardt's Bark formula) below the coded bandwidth. The masking threshold
//    spreads the reference band energies with Schroeder's spreading function, lowers them by Johnston's offset
//    alpha (14.5 + z) + (1 - alpha) 5.5 dB with alpha from the frame's spectral flatness, and floors them with
//    Terhardt's threshold in quiet (96 dB = a full-scale sine). Total NMR is 10 log10 of the mean noise/mask power
//    ratio over all occupied bands and frames; the disturbed-frame fraction counts frames whose worst band exceeds
//    1.5 dB. 2048-sample frames measure spectral noise shaping; 256-sample frames are sensitive to noise spread in time
//    (pre-echo and smearing of pitch pulses), which long frames average away;
//  * the bitrate distribution of access units, and clipping (decoded samples beyond full scale).
// These are engineering measures for regression and comparison, not calibrated listening-test predictors.

using RTSP.IngestService.Media.Codecs.Audio.Aac.Encoder;
using RTSP.IngestService.Media.Codecs.Common.Transforms;

namespace RTSP.IngestService.Tests.Media.Codecs.Audio;

/// <summary>Perceptual and signal quality measures for one decoded channel.</summary>
/// <param name="Snr">The SNR in decibels.</param>
/// <param name="SegmentalSnr">The segmental SNR in decibels.</param>
/// <param name="TotalNmr">The total noise-to-mask ratio over 2048-sample frames in decibels; below 0 dB the noise is mostly masked.</param>
/// <param name="DisturbedFrames">The fraction of 2048-sample frames whose worst band NMR exceeds 1.5 dB.</param>
/// <param name="ShortNmr">The total NMR over 256-sample frames in decibels.</param>
/// <param name="ClippedSamples">Decoded samples whose magnitude exceeds 1.</param>
/// <param name="Peak">The decoded peak.</param>
internal readonly record struct ChannelQuality(double Snr, double SegmentalSnr, double TotalNmr, double DisturbedFrames, double ShortNmr, int ClippedSamples, double Peak);

/// <summary>The distribution of access-unit sizes.</summary>
/// <param name="Average">The average bitrate in bits per second.</param>
/// <param name="FrameMin">The smallest access unit in bits.</param>
/// <param name="FrameMax">The largest access unit in bits.</param>
/// <param name="FrameStdDev">The standard deviation of access-unit sizes in bits.</param>
/// <param name="SecondMin">The lowest one-second bitrate.</param>
/// <param name="SecondMax">The highest one-second bitrate.</param>
internal readonly record struct BitrateDistribution(double Average, int FrameMin, int FrameMax, double FrameStdDev, double SecondMin, double SecondMax);

/// <summary>Quality measurement helpers.</summary>
internal static class AudioQualityMetrics
{
    /// <summary>Measures one decoded channel against its reference.</summary>
    /// <param name="reference">The input.</param>
    /// <param name="decoded">The decoded output aligned with the input, the same length.</param>
    /// <param name="bandwidth">The coded bandwidth in hertz; content above it is excluded from the NMR.</param>
    /// <returns>The measures.</returns>
    public static ChannelQuality Measure(ReadOnlySpan<float> reference, ReadOnlySpan<float> decoded, double bandwidth)
    {
        var (nmr, disturbed) = NoiseToMask(reference, decoded, bandwidth, 2048);
        var (shortNmr, _) = NoiseToMask(reference, decoded, bandwidth, 256);
        var clipped = 0;
        foreach (var x in decoded)
        {
            clipped += Math.Abs(x) > 1 ? 1 : 0;
        }

        return new ChannelQuality(AudioTestSupport.Snr(reference, decoded), SegmentalSnr(reference, decoded), nmr, disturbed, shortNmr, clipped, AudioTestSupport.Peak(decoded));
    }

    /// <summary>The segmental SNR over 1024-sample segments whose reference level is above -60 dBFS.</summary>
    /// <param name="reference">The reference.</param>
    /// <param name="decoded">The decoded signal.</param>
    /// <returns>The mean clamped segment SNR, or <see cref="double.NaN"/> if no segment qualifies.</returns>
    public static double SegmentalSnr(ReadOnlySpan<float> reference, ReadOnlySpan<float> decoded)
    {
        var sum = 0.0;
        var count = 0;
        for (var start = 0; start + 1024 <= reference.Length; start += 1024)
        {
            if (AudioTestSupport.Rms(reference.Slice(start, 1024)) < 1e-3)
            {
                continue;
            }

            sum += Math.Clamp(AudioTestSupport.Snr(reference.Slice(start, 1024), decoded.Slice(start, 1024)), -10, 80);
            count++;
        }

        return count == 0 ? double.NaN : sum / count;
    }

    /// <summary>Summarises access-unit sizes.</summary>
    /// <param name="units">The access units.</param>
    /// <returns>The distribution.</returns>
    public static BitrateDistribution Bitrate(IReadOnlyList<AacAccessUnit> units)
    {
        var bits = units.Select(u => u.Data.Length * 8).ToArray();
        var mean = bits.Average();
        var variance = bits.Average(b => (b - mean) * (b - mean));
        var framesPerSecond = AacEncoder.SampleRate / (double)AacEncoder.FrameLength;
        var perSecond = (int)framesPerSecond;
        var seconds = Enumerable.Range(0, Math.Max(1, bits.Length / perSecond))
            .Select(s => bits.Skip(s * perSecond).Take(perSecond).Sum() * framesPerSecond / Math.Min(perSecond, bits.Length - (s * perSecond)))
            .ToArray();
        return new BitrateDistribution(mean * framesPerSecond, bits.Min(), bits.Max(), Math.Sqrt(variance), seconds.Min(), seconds.Max());
    }

    /// <summary>The total NMR and disturbed-frame fraction.</summary>
    /// <param name="reference">The reference.</param>
    /// <param name="decoded">The decoded signal.</param>
    /// <param name="bandwidth">The highest frequency included.</param>
    /// <param name="frameLength">The analysis frame length, a power of two; frames overlap by half.</param>
    /// <returns>The total NMR in decibels and the disturbed-frame fraction.</returns>
    private static (double Nmr, double Disturbed) NoiseToMask(ReadOnlySpan<float> reference, ReadOnlySpan<float> decoded, double bandwidth, int frameLength)
    {
        var fft = new ComplexFft(frameLength);
        var window = Enumerable.Range(0, frameLength).Select(n => (float)(0.5 - (0.5 * Math.Cos(2 * Math.PI * n / frameLength)))).ToArray();
        var bins = (int)Math.Min(frameLength / 2, bandwidth * frameLength / AudioQualitySignals.Rate);
        var bandOf = new int[bins];
        var bands = 0;
        for (var k = 1; k < bins; k++)
        {
            bandOf[k] = (int)Bark(k * (double)AudioQualitySignals.Rate / frameLength);
            bands = Math.Max(bands, bandOf[k] + 1);
        }

        var occupied = new bool[bands];
        for (var k = 1; k < bins; k++)
        {
            occupied[bandOf[k]] = true;
        }

        var quiet = new double[bands];
        var fullScale = 3.0 * frameLength * frameLength / 32;
        for (var b = 0; b < bands; b++)
        {
            var kilohertz = Math.Max(0.05, BarkToHertz(b + 0.5) / 1000);
            var decibels = (3.64 * Math.Pow(kilohertz, -0.8)) - (6.5 * Math.Exp(-0.6 * Math.Pow(kilohertz - 3.3, 2))) + (0.001 * Math.Pow(kilohertz, 4));
            quiet[b] = fullScale * Math.Pow(10, (decibels - 96) / 10);
        }

        var spreading = new double[bands, bands];
        for (var i = 0; i < bands; i++)
        {
            for (var j = 0; j < bands; j++)
            {
                var dz = i - j + 0.474;
                spreading[i, j] = Math.Pow(10, (15.81 + (7.5 * dz) - (17.5 * Math.Sqrt(1 + (dz * dz)))) / 10);
            }
        }

        var re = new float[frameLength];
        var im = new float[frameLength];
        var signal = new double[bands];
        var noise = new double[bands];
        var ratioSum = 0.0;
        var ratioCount = 0;
        var disturbed = 0;
        var frames = 0;
        for (var start = 0; start + frameLength <= reference.Length; start += frameLength / 2)
        {
            Array.Clear(signal);
            Array.Clear(noise);
            Spectrum(fft, window, reference.Slice(start, frameLength), re, im);
            double logSum = 0, linearSum = 0;
            for (var k = 1; k < bins; k++)
            {
                var power = ((double)re[k] * re[k]) + ((double)im[k] * im[k]);
                signal[bandOf[k]] += power;
                logSum += Math.Log(power + 1e-12);
                linearSum += power + 1e-12;
            }

            var flatness = 10 * Math.Log10(Math.Exp(logSum / (bins - 1)) / (linearSum / (bins - 1)));
            var tonality = Math.Min(flatness / -60, 1);
            for (var n = 0; n < frameLength; n++)
            {
                re[n] = decoded[start + n] - reference[start + n];
            }

            Spectrum(fft, window, re, re, im);
            for (var k = 1; k < bins; k++)
            {
                noise[bandOf[k]] += ((double)re[k] * re[k]) + ((double)im[k] * im[k]);
            }

            var worst = 0.0;
            for (var b = 0; b < bands; b++)
            {
                if (!occupied[b])
                {
                    continue;
                }

                var spread = 0.0;
                for (var j = 0; j < bands; j++)
                {
                    spread += signal[j] * spreading[b, j];
                }

                var offset = (tonality * (14.5 + b)) + ((1 - tonality) * 5.5);
                var mask = Math.Max(spread * Math.Pow(10, -offset / 10), quiet[b]);
                var ratio = noise[b] / mask;
                ratioSum += ratio;
                ratioCount++;
                worst = Math.Max(worst, ratio);
            }

            disturbed += 10 * Math.Log10(Math.Max(worst, 1e-30)) > 1.5 ? 1 : 0;
            frames++;
        }

        return (10 * Math.Log10(Math.Max(ratioSum / Math.Max(1, ratioCount), 1e-30)), frames == 0 ? 0 : disturbed / (double)frames);
    }

    /// <summary>Windows and transforms one frame.</summary>
    /// <param name="fft">The transform.</param>
    /// <param name="window">The window.</param>
    /// <param name="samples">The samples; may be <paramref name="re"/>.</param>
    /// <param name="re">Receives the real parts.</param>
    /// <param name="im">Receives the imaginary parts.</param>
    private static void Spectrum(ComplexFft fft, float[] window, ReadOnlySpan<float> samples, float[] re, float[] im)
    {
        for (var n = 0; n < window.Length; n++)
        {
            re[n] = samples[n] * window[n];
            im[n] = 0;
        }

        fft.Forward(re, im);
    }

    /// <summary>The critical-band rate of a frequency (Zwicker and Terhardt).</summary>
    /// <param name="hertz">The frequency.</param>
    /// <returns>The Bark value.</returns>
    private static double Bark(double hertz) => (13 * Math.Atan(0.00076 * hertz)) + (3.5 * Math.Atan(Math.Pow(hertz / 7500, 2)));

    /// <summary>Inverts <see cref="Bark"/> by bisection.</summary>
    /// <param name="bark">The Bark value.</param>
    /// <returns>The frequency.</returns>
    private static double BarkToHertz(double bark)
    {
        double low = 0, high = 24000;
        for (var i = 0; i < 60; i++)
        {
            var middle = (low + high) / 2;
            (low, high) = Bark(middle) < bark ? (middle, high) : (low, middle);
        }

        return low;
    }
}
