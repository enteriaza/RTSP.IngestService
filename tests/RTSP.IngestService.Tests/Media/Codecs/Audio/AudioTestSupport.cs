// Shared helpers for the managed audio processing and encoder tests: deterministic signal generators, frame
// construction, encode/decode round trips with the managed decoder, ADTS framing so FFmpeg can read encoder output,
// and the signal metrics the tests assert on (SNR, correlation, RMS, peak and single-frequency amplitude).

using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Audio.Aac.Decoder;
using RTSP.IngestService.Media.Codecs.Audio.Aac.Encoder;
using RTSP.IngestService.Media.Codecs.Audio.Processing;

namespace RTSP.IngestService.Tests.Media.Codecs.Audio;

/// <summary>Helpers for audio processing and encoder tests.</summary>
internal static class AudioTestSupport
{
    /// <summary>A sine wave.</summary>
    /// <param name="length">The sample count.</param>
    /// <param name="rate">The sample rate.</param>
    /// <param name="frequency">The frequency in hertz.</param>
    /// <param name="amplitude">The peak amplitude.</param>
    /// <param name="phase">The starting phase in radians.</param>
    /// <returns>The samples.</returns>
    public static float[] Sine(int length, int rate, double frequency, double amplitude, double phase = 0)
    {
        var samples = new float[length];
        for (var n = 0; n < length; n++)
        {
            samples[n] = (float)(amplitude * Math.Sin((2 * Math.PI * frequency * n / rate) + phase));
        }

        return samples;
    }

    /// <summary>Deterministic white noise, uniform in [-amplitude, amplitude].</summary>
    /// <param name="length">The sample count.</param>
    /// <param name="amplitude">The peak amplitude.</param>
    /// <param name="seed">The seed.</param>
    /// <returns>The samples.</returns>
    public static float[] Noise(int length, double amplitude, int seed)
    {
        var random = new Random(seed);
        var samples = new float[length];
        for (var n = 0; n < length; n++)
        {
            samples[n] = (float)(amplitude * ((random.NextDouble() * 2) - 1));
        }

        return samples;
    }

    /// <summary>Adds signals of equal length.</summary>
    /// <param name="signals">The signals.</param>
    /// <returns>The sum.</returns>
    public static float[] Add(params float[][] signals)
    {
        var sum = new float[signals[0].Length];
        foreach (var signal in signals)
        {
            for (var n = 0; n < sum.Length; n++)
            {
                sum[n] += signal[n];
            }
        }

        return sum;
    }

    /// <summary>Creates a frame from planes.</summary>
    /// <param name="layout">The layout.</param>
    /// <param name="rate">The sample rate.</param>
    /// <param name="planes">One array per channel.</param>
    /// <param name="start">The first sample to copy.</param>
    /// <param name="count">The samples to copy.</param>
    /// <param name="timestamp">The timestamp in milliseconds.</param>
    /// <returns>The frame.</returns>
    public static AudioFrame Frame(AudioChannel[] layout, int rate, float[][] planes, int start, int count, long timestamp)
    {
        var frame = new AudioFrame(layout, count, rate, timestamp, MediaTimeBase.Milliseconds);
        for (var c = 0; c < planes.Length; c++)
        {
            planes[c].AsSpan(start, count).CopyTo(frame.GetChannel(c));
        }

        return frame;
    }

    /// <summary>Encodes stereo planes in chunks and flushes.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="left">The left channel.</param>
    /// <param name="right">The right channel.</param>
    /// <param name="chunk">The chunk length.</param>
    /// <returns>The access units.</returns>
    public static List<AacAccessUnit> Encode(AacEncoder encoder, float[] left, float[] right, int chunk = 1024)
    {
        var units = new List<AacAccessUnit>();
        for (var start = 0; start < left.Length; start += chunk)
        {
            var count = Math.Min(chunk, left.Length - start);
            using var frame = Frame([AudioChannel.FrontLeft, AudioChannel.FrontRight], 48000, [left, right], start, count, start * 1000L / 48000);
            encoder.Encode(frame, units);
        }

        encoder.Flush(units);
        return units;
    }

    /// <summary>Decodes access units with the managed decoder.</summary>
    /// <param name="units">The access units.</param>
    /// <returns>Left and right planes, including the priming frame.</returns>
    public static float[][] DecodeManaged(IReadOnlyList<AacAccessUnit> units)
    {
        using var decoder = AacDecoder.Create(AacEncoder.AudioSpecificConfig);
        var left = new List<float>();
        var right = new List<float>();
        foreach (var unit in units)
        {
            using var frame = decoder.Decode(new EncodedPacket(unit.Data, unit.Timestamp, unit.TimeBase));
            left.AddRange(frame.GetChannel(0).ToArray());
            right.AddRange(frame.GetChannel(frame.ChannelCount - 1).ToArray());
        }

        return [[.. left], [.. right]];
    }

    /// <summary>Wraps access units in ADTS headers (AAC-LC, 48 kHz, two channels) for external decoders.</summary>
    /// <param name="units">The access units.</param>
    /// <returns>The ADTS stream.</returns>
    public static byte[] ToAdts(IEnumerable<AacAccessUnit> units)
    {
        using var stream = new MemoryStream();
        foreach (var unit in units)
        {
            var length = unit.Data.Length + 7;
            stream.Write(
            [
                0xFF, 0xF1, (byte)((1 << 6) | (3 << 2) | (2 >> 2)), (byte)(((2 & 3) << 6) | (length >> 11)),
                (byte)((length >> 3) & 0xFF), (byte)(((length & 7) << 5) | 0x1F), 0xFC,
            ]);
            stream.Write(unit.Data.Span);
        }

        return stream.ToArray();
    }

    /// <summary>The signal-to-noise ratio of <paramref name="actual"/> against <paramref name="expected"/> in decibels.</summary>
    /// <param name="expected">The reference.</param>
    /// <param name="actual">The signal under test.</param>
    /// <returns>10 log10(sum e^2 / sum (a - e)^2).</returns>
    public static double Snr(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual)
    {
        double signal = 0, noise = 0;
        for (var n = 0; n < expected.Length; n++)
        {
            signal += (double)expected[n] * expected[n];
            var d = (double)actual[n] - expected[n];
            noise += d * d;
        }

        return 10 * Math.Log10(signal / Math.Max(noise, 1e-30));
    }

    /// <summary>The Pearson correlation coefficient.</summary>
    /// <param name="a">The first signal.</param>
    /// <param name="b">The second signal, the same length.</param>
    /// <returns>The correlation.</returns>
    public static double Correlation(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double meanA = 0, meanB = 0;
        for (var n = 0; n < a.Length; n++)
        {
            meanA += a[n];
            meanB += b[n];
        }

        meanA /= a.Length;
        meanB /= a.Length;
        double ab = 0, aa = 0, bb = 0;
        for (var n = 0; n < a.Length; n++)
        {
            ab += (a[n] - meanA) * (b[n] - meanB);
            aa += (a[n] - meanA) * (a[n] - meanA);
            bb += (b[n] - meanB) * (b[n] - meanB);
        }

        return ab / Math.Sqrt(Math.Max(aa * bb, 1e-30));
    }

    /// <summary>The root-mean-square level.</summary>
    /// <param name="samples">The samples.</param>
    /// <returns>The RMS.</returns>
    public static double Rms(ReadOnlySpan<float> samples)
    {
        var sum = 0.0;
        foreach (var x in samples)
        {
            sum += (double)x * x;
        }

        return Math.Sqrt(sum / Math.Max(1, samples.Length));
    }

    /// <summary>The largest absolute sample.</summary>
    /// <param name="samples">The samples.</param>
    /// <returns>The peak.</returns>
    public static double Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0.0;
        foreach (var x in samples)
        {
            peak = Math.Max(peak, Math.Abs(x));
        }

        return peak;
    }

    /// <summary>
    /// The amplitude of one frequency, by correlation with a Hann-windowed quadrature pair: a single-bin DFT whose
    /// leakage from other tones falls off steeply.
    /// </summary>
    /// <param name="samples">The samples.</param>
    /// <param name="rate">The sample rate.</param>
    /// <param name="frequency">The frequency.</param>
    /// <returns>The peak amplitude at that frequency.</returns>
    public static double ToneAmplitude(ReadOnlySpan<float> samples, int rate, double frequency)
    {
        double re = 0, im = 0, weight = 0;
        for (var n = 0; n < samples.Length; n++)
        {
            var window = 0.5 - (0.5 * Math.Cos(2 * Math.PI * n / samples.Length));
            var angle = 2 * Math.PI * frequency * n / rate;
            re += window * samples[n] * Math.Cos(angle);
            im += window * samples[n] * Math.Sin(angle);
            weight += window;
        }

        return 2 * Math.Sqrt((re * re) + (im * im)) / weight;
    }

    /// <summary>The mean (DC) level.</summary>
    /// <param name="samples">The samples.</param>
    /// <returns>The mean.</returns>
    public static double Mean(ReadOnlySpan<float> samples)
    {
        var sum = 0.0;
        foreach (var x in samples)
        {
            sum += x;
        }

        return sum / Math.Max(1, samples.Length);
    }

    /// <summary>The canonical stereo layout.</summary>
    /// <returns>Front left and front right.</returns>
    public static AudioChannel[] Stereo() => [.. StereoDownmixer.StereoLayout.Span];
}
