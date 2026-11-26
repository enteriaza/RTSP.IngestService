// The canonical PCM representation between managed audio stages: planar 32-bit IEEE float, nominal full scale +-1.0
// (16-bit PCM value / 32768). The final stage clips hard to [-1, 1] and maps NaN to 0; nothing adds a DC term,
// gain, dither, compression or loudness processing. Conversion to 16-bit integers multiplies by 32768, rounds half
// away from zero and saturates to [-32768, 32767].

namespace RTSP.IngestService.Media.Codecs.Audio.Processing;

/// <summary>Sample-format conversions for the canonical float representation.</summary>
internal static class AudioSampleFormat
{
    /// <summary>The 16-bit scale: float = integer / 32768.</summary>
    public const float Int16Scale = 32768f;

    /// <summary>Clips samples to [-1, 1] in place and replaces NaN with 0.</summary>
    /// <param name="samples">The samples.</param>
    /// <returns>The number of samples that were changed.</returns>
    public static int Clip(Span<float> samples)
    {
        var changed = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var value = samples[i];
            if (float.IsNaN(value))
            {
                samples[i] = 0;
                changed++;
            }
            else if (value > 1f)
            {
                samples[i] = 1f;
                changed++;
            }
            else if (value < -1f)
            {
                samples[i] = -1f;
                changed++;
            }
        }

        return changed;
    }

    /// <summary>Converts 16-bit integer samples to float.</summary>
    /// <param name="input">The integer samples.</param>
    /// <param name="output">The float samples, the same length.</param>
    public static void FromInt16(ReadOnlySpan<short> input, Span<float> output)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(output.Length, input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            output[i] = input[i] / Int16Scale;
        }
    }

    /// <summary>Converts float samples to 16-bit integers, rounding half away from zero and saturating.</summary>
    /// <param name="input">The float samples.</param>
    /// <param name="output">The integer samples, the same length.</param>
    public static void ToInt16(ReadOnlySpan<float> input, Span<short> output)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(output.Length, input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            var value = float.IsNaN(input[i]) ? 0 : Math.Round(input[i] * (double)Int16Scale, MidpointRounding.AwayFromZero);
            output[i] = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
        }
    }
}
