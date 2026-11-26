// Fixed-matrix downmix of any decoder channel layout to stereo, selected by channel position rather than by index, so
// the bitstream channel order (for AAC: C L R ... LFE) cannot cause a swap. Coefficients follow ITU-R BS.775 and the
// FFmpeg/libswresample defaults that the FFmpeg transcoder uses today:
//   front left / right            -> same side, 1
//   front centre                  -> both sides, 1/sqrt(2)   (-3 dB; a mono source is a lone centre channel)
//   front left / right wide       -> same side, 1
//   surround left / right         -> same side, 1/sqrt(2)   (-3 dB)
//   back centre                   -> both sides, 1/2        (surround -3 dB, split -3 dB)
//   low-frequency effects         -> dropped, 0
// Headroom: if an output's coefficient magnitudes sum to more than 1, the whole matrix is divided by the largest sum,
// so inputs within [-1, 1] can never produce output outside [-1, 1] (5.1 and 7.1 are attenuated by 1 / (1 + sqrt 2)).
// Stereo passes through unchanged. libswresample does not apply this normalisation for float output; it leaves the
// encoder to clip.

namespace RTSP.IngestService.Media.Codecs.Audio.Processing;

/// <summary>Downmixes a fixed channel layout to stereo with a documented matrix. Stateless per frame; thread-safe.</summary>
internal sealed class StereoDownmixer
{
    /// <summary>The centre and surround coefficient, -3 dB.</summary>
    public const float MinusThreeDb = 0.70710678f;

    /// <summary>The stereo output layout.</summary>
    public static readonly ReadOnlyMemory<AudioChannel> StereoLayout = new[] { AudioChannel.FrontLeft, AudioChannel.FrontRight };

    /// <summary>The left-output coefficient per input channel.</summary>
    private readonly float[] _left;

    /// <summary>The right-output coefficient per input channel.</summary>
    private readonly float[] _right;

    /// <summary>Initializes a new instance of the <see cref="StereoDownmixer"/> class.</summary>
    /// <param name="layout">The input channel positions, in plane order.</param>
    public StereoDownmixer(ReadOnlySpan<AudioChannel> layout)
    {
        ArgumentOutOfRangeException.ThrowIfZero(layout.Length);
        _left = new float[layout.Length];
        _right = new float[layout.Length];
        for (var c = 0; c < layout.Length; c++)
        {
            (_left[c], _right[c]) = layout[c] switch
            {
                AudioChannel.FrontLeft or AudioChannel.FrontLeftWide => (1f, 0f),
                AudioChannel.FrontRight or AudioChannel.FrontRightWide => (0f, 1f),
                AudioChannel.FrontCenter => (MinusThreeDb, MinusThreeDb),
                AudioChannel.SurroundLeft => (MinusThreeDb, 0f),
                AudioChannel.SurroundRight => (0f, MinusThreeDb),
                AudioChannel.BackCenter => (0.5f, 0.5f),
                AudioChannel.LowFrequency => (0f, 0f),
                _ => throw new ArgumentOutOfRangeException(nameof(layout), layout[c], "Unknown channel position."),
            };
        }

        var largest = Math.Max(Sum(_left), Sum(_right));
        if (largest > 1)
        {
            for (var c = 0; c < layout.Length; c++)
            {
                _left[c] /= largest;
                _right[c] /= largest;
            }
        }

        Layout = layout.ToArray();
    }

    /// <summary>Gets the input channel positions.</summary>
    public ReadOnlyMemory<AudioChannel> Layout { get; }

    /// <summary>Gets the left-output coefficients, one per input plane.</summary>
    public ReadOnlySpan<float> LeftCoefficients => _left;

    /// <summary>Gets the right-output coefficients, one per input plane.</summary>
    public ReadOnlySpan<float> RightCoefficients => _right;

    /// <summary>Downmixes one frame.</summary>
    /// <param name="input">The input frame, with this downmixer's layout; the caller keeps ownership.</param>
    /// <returns>A stereo frame with the same rate, timestamp and length, owned by the caller.</returns>
    public AudioFrame Process(AudioFrame input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.Layout.Span.SequenceEqual(Layout.Span))
        {
            throw new ArgumentException("The frame layout does not match the downmixer layout.", nameof(input));
        }

        var output = new AudioFrame(StereoLayout, input.SampleCount, input.SampleRate, input.Timestamp, input.TimeBase);
        var left = output.GetChannel(0);
        var right = output.GetChannel(1);
        for (var c = 0; c < input.ChannelCount; c++)
        {
            var samples = input.GetChannel(c);
            Accumulate(samples, _left[c], left);
            Accumulate(samples, _right[c], right);
        }

        return output;
    }

    /// <summary>Adds a scaled input to an output.</summary>
    /// <param name="input">The input samples.</param>
    /// <param name="gain">The coefficient.</param>
    /// <param name="output">The accumulator.</param>
    private static void Accumulate(ReadOnlySpan<float> input, float gain, Span<float> output)
    {
        if (gain == 0)
        {
            return;
        }

        for (var i = 0; i < input.Length; i++)
        {
            output[i] += input[i] * gain;
        }
    }

    /// <summary>The sum of coefficient magnitudes.</summary>
    /// <param name="coefficients">The coefficients.</param>
    /// <returns>The sum.</returns>
    private static float Sum(float[] coefficients)
    {
        var sum = 0f;
        foreach (var value in coefficients)
        {
            sum += Math.Abs(value);
        }

        return sum;
    }
}
