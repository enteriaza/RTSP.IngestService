// Tests for the stereo downmix, the canonical sample format and the canonical converter: documented coefficients for
// every AAC channel configuration, per-channel routing verified with a distinct frequency in each channel, LFE
// exclusion, worst-case headroom, clipping, integer conversion, DC behaviour and the composed 48 kHz stereo path.

using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Audio.Processing;

namespace RTSP.IngestService.Tests.Media.Codecs.Audio;

/// <summary>Tests for <see cref="StereoDownmixer"/>, <see cref="AudioSampleFormat"/> and <see cref="CanonicalAudioConverter"/>.</summary>
public sealed class StereoDownmixerTests
{
    /// <summary>The 5.1 and 7.1 normalisation: 1 / (1 + sqrt 2).</summary>
    private const double Surround = 1 / (1 + 1.41421356);

    /// <summary>The managed AAC decoder's layouts for channel configurations 1 to 7.</summary>
    public static TheoryData<int> Configurations => [1, 2, 3, 4, 5, 6, 7];

    /// <summary>Each configuration gets the documented coefficients.</summary>
    /// <param name="configuration">The AAC channel configuration.</param>
    [Theory]
    [MemberData(nameof(Configurations))]
    public void CoefficientsMatchTheDocumentedMatrix(int configuration)
    {
        const float c = 0.70710678f;
        var (left, right) = configuration switch
        {
            1 => (new[] { c }, new[] { c }),
            2 => ([1f, 0], [0f, 1]),
            3 => (Scale([c, 1, 0], 1 / 1.70710678f), Scale([c, 0, 1], 1 / 1.70710678f)),
            4 => (Scale([c, 1, 0, 0.5f], 1 / 2.20710678f), Scale([c, 0, 1, 0.5f], 1 / 2.20710678f)),
            5 => (Scale([c, 1, 0, c, 0], (float)Surround), Scale([c, 0, 1, 0, c], (float)Surround)),
            6 => (Scale([c, 1, 0, c, 0, 0], (float)Surround), Scale([c, 0, 1, 0, c, 0], (float)Surround)),
            _ => (Scale([c, 1, 0, 1, 0, c, 0, 0], 1 / 3.41421356f), Scale([c, 0, 1, 0, 1, 0, c, 0], 1 / 3.41421356f)),
        };

        var downmixer = new StereoDownmixer(Layout(configuration));
        Assert.Equal(left.Length, downmixer.LeftCoefficients.Length);
        for (var i = 0; i < left.Length; i++)
        {
            Assert.Equal(left[i], downmixer.LeftCoefficients[i], 1e-6f);
            Assert.Equal(right[i], downmixer.RightCoefficients[i], 1e-6f);
        }
    }

    /// <summary>5.1 routing, identified by a distinct frequency per channel: C, L, R, Ls, Rs, LFE in bitstream order.</summary>
    [Fact]
    public void FivePointOneRoutesEveryChannel()
    {
        double[] frequencies = [300, 500, 700, 900, 1100, 60];
        var (left, right) = Mix(6, frequencies);
        var g = Surround;
        AssertTone(left, 300, 0.70710678 * g);
        AssertTone(right, 300, 0.70710678 * g);
        AssertTone(left, 500, g);
        AssertTone(right, 500, 0);
        AssertTone(left, 700, 0);
        AssertTone(right, 700, g);
        AssertTone(left, 900, 0.70710678 * g);
        AssertTone(right, 900, 0);
        AssertTone(left, 1100, 0);
        AssertTone(right, 1100, 0.70710678 * g);
        AssertTone(left, 60, 0);
        AssertTone(right, 60, 0);
    }

    /// <summary>7.1 routing: C, L, R, Lw, Rw, Ls, Rs, LFE; wide channels fold into their side at unity before normalisation.</summary>
    [Fact]
    public void SevenPointOneRoutesEveryChannel()
    {
        double[] frequencies = [300, 500, 700, 900, 1100, 1300, 1500, 60];
        var (left, right) = Mix(7, frequencies);
        var g = 1 / 3.41421356;
        AssertTone(left, 300, 0.70710678 * g);
        AssertTone(right, 300, 0.70710678 * g);
        AssertTone(left, 500, g);
        AssertTone(right, 700, g);
        AssertTone(left, 900, g);
        AssertTone(right, 900, 0);
        AssertTone(right, 1100, g);
        AssertTone(left, 1100, 0);
        AssertTone(left, 1300, 0.70710678 * g);
        AssertTone(right, 1500, 0.70710678 * g);
        AssertTone(left, 60, 0);
        AssertTone(right, 60, 0);
    }

    /// <summary>Mono is a lone centre channel: both outputs carry it at -3 dB, identically.</summary>
    [Fact]
    public void MonoFeedsBothSidesAtMinusThreeDecibels()
    {
        var (left, right) = Mix(1, [440]);
        AssertTone(left, 440, 0.70710678);
        Assert.Equal(left, right);
    }

    /// <summary>Stereo passes through bit-exactly, without a swap.</summary>
    [Fact]
    public void StereoPassesThrough()
    {
        var planes = new[] { AudioTestSupport.Noise(1000, 1, 1), AudioTestSupport.Noise(1000, 1, 2) };
        using var frame = AudioTestSupport.Frame(Layout(2), 48000, planes, 0, 1000, 7);
        using var output = new StereoDownmixer(Layout(2)).Process(frame);
        Assert.Equal(planes[0], output.GetChannel(0).ToArray());
        Assert.Equal(planes[1], output.GetChannel(1).ToArray());
        Assert.Equal(7, output.Timestamp);
    }

    /// <summary>An LFE-only signal produces silence.</summary>
    [Fact]
    public void LowFrequencyEffectsAreDropped()
    {
        var planes = Enumerable.Range(0, 6).Select(c => c == 5 ? AudioTestSupport.Sine(4800, 48000, 50, 1) : new float[4800]).ToArray();
        using var frame = AudioTestSupport.Frame(Layout(6), 48000, planes, 0, 4800, 0);
        using var output = new StereoDownmixer(Layout(6)).Process(frame);
        Assert.Equal(0, AudioTestSupport.Peak(output.GetChannel(0)));
        Assert.Equal(0, AudioTestSupport.Peak(output.GetChannel(1)));
    }

    /// <summary>Worst case: every channel at full scale with the same sign never exceeds full scale.</summary>
    /// <param name="configuration">The AAC channel configuration.</param>
    [Theory]
    [MemberData(nameof(Configurations))]
    public void FullScaleInputCannotClip(int configuration)
    {
        var layout = Layout(configuration);
        foreach (var sign in new[] { 1f, -1f })
        {
            var planes = layout.Select(_ => Enumerable.Repeat(sign, 64).ToArray()).ToArray();
            using var frame = AudioTestSupport.Frame(layout, 48000, planes, 0, 64, 0);
            using var output = new StereoDownmixer(layout).Process(frame);
            Assert.InRange(AudioTestSupport.Peak(output.GetChannel(0)), 0, 1 + 1e-6);
            Assert.InRange(AudioTestSupport.Peak(output.GetChannel(1)), 0, 1 + 1e-6);
        }
    }

    /// <summary>A frame with a different layout is rejected.</summary>
    [Fact]
    public void RejectsMismatchedLayout()
    {
        using var frame = new AudioFrame(Layout(2), 8, 48000, 0, MediaTimeBase.Milliseconds);
        Assert.Throws<ArgumentException>(() => new StereoDownmixer(Layout(6)).Process(frame));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StereoDownmixer([]));
    }

    /// <summary>Clipping limits to [-1, 1], maps NaN to zero and reports how many samples changed.</summary>
    [Fact]
    public void ClipLimitsToFullScale()
    {
        float[] samples = [0.5f, 1.5f, -2f, float.NaN, 1f, -1f, float.PositiveInfinity];
        Assert.Equal(4, AudioSampleFormat.Clip(samples));
        Assert.Equal([0.5f, 1f, -1f, 0f, 1f, -1f, 1f], samples);
    }

    /// <summary>16-bit conversion round-trips every value exactly, rounds half away from zero and saturates.</summary>
    [Fact]
    public void Int16ConversionIsExactAndSaturating()
    {
        var all = Enumerable.Range(short.MinValue, 65536).Select(v => (short)v).ToArray();
        var floats = new float[all.Length];
        var back = new short[all.Length];
        AudioSampleFormat.FromInt16(all, floats);
        AudioSampleFormat.ToInt16(floats, back);
        Assert.Equal(all, back);

        var converted = new short[6];
        AudioSampleFormat.ToInt16([1.5f / 32768, -1.5f / 32768, 2f, -2f, float.NaN, 0.25f], converted);
        Assert.Equal([2, -2, short.MaxValue, short.MinValue, 0, 8192], converted);
    }

    /// <summary>The canonical converter turns 44.1 kHz 5.1 into 48 kHz stereo with the downmix gains and no DC offset.</summary>
    [Fact]
    public void ConverterProducesCanonicalStereo()
    {
        var layout = Layout(6);
        var planes = new[]
        {
            AudioTestSupport.Sine(44100, 44100, 300, 0.5), AudioTestSupport.Sine(44100, 44100, 500, 0.5),
            AudioTestSupport.Sine(44100, 44100, 700, 0.5), new float[44100], new float[44100], Enumerable.Repeat(0.9f, 44100).ToArray(),
        };
        var converter = new CanonicalAudioConverter(layout, 44100);
        var left = new List<float>();
        var right = new List<float>();
        for (var start = 0; start < 44100; start += 1024)
        {
            using var frame = AudioTestSupport.Frame(layout, 44100, planes, start, Math.Min(1024, 44100 - start), start * 1000L / 44100);
            Collect(converter.Process(frame));
        }

        Collect(converter.Flush());
        Assert.Equal(48000, left.Count);
        var body = new Range(2000, 46000);
        AssertTone(left.ToArray()[body], 300, 0.5 * 0.70710678 * Surround);
        AssertTone(left.ToArray()[body], 500, 0.5 * Surround);
        AssertTone(right.ToArray()[body], 700, 0.5 * Surround);
        AssertTone(right.ToArray()[body], 500, 0);
        Assert.InRange(AudioTestSupport.Mean(left.ToArray()[body]), -1e-4, 1e-4);
        Assert.Equal(0, converter.ClippedSamples);

        void Collect(AudioFrame? frame)
        {
            if (frame is null)
            {
                return;
            }

            using (frame)
            {
                Assert.Equal(48000, frame.SampleRate);
                Assert.Equal(StereoDownmixer.StereoLayout.ToArray(), frame.Layout.ToArray());
                left.AddRange(frame.GetChannel(0).ToArray());
                right.AddRange(frame.GetChannel(1).ToArray());
            }
        }
    }

    /// <summary>Resampler overshoot on a full-scale square wave is clipped at the final stage and counted.</summary>
    [Fact]
    public void ConverterClipsOvershoot()
    {
        var square = Enumerable.Range(0, 4410).Select(n => n / 20 % 2 == 0 ? 1f : -1f).ToArray();
        var converter = new CanonicalAudioConverter(Layout(2), 44100);
        using var frame = AudioTestSupport.Frame(Layout(2), 44100, [square, square], 0, 4410, 0);
        using var output = converter.Process(frame);
        Assert.NotNull(output);
        Assert.InRange(AudioTestSupport.Peak(output.GetChannel(0)), 0, 1);
        Assert.True(converter.ClippedSamples > 0);
    }

    /// <summary>The managed AAC decoder's layout for a channel configuration.</summary>
    /// <param name="configuration">1 to 7.</param>
    /// <returns>The channel positions in bitstream order.</returns>
    private static AudioChannel[] Layout(int configuration) => configuration switch
    {
        1 => [AudioChannel.FrontCenter],
        2 => [AudioChannel.FrontLeft, AudioChannel.FrontRight],
        3 => [AudioChannel.FrontCenter, AudioChannel.FrontLeft, AudioChannel.FrontRight],
        4 => [AudioChannel.FrontCenter, AudioChannel.FrontLeft, AudioChannel.FrontRight, AudioChannel.BackCenter],
        5 => [AudioChannel.FrontCenter, AudioChannel.FrontLeft, AudioChannel.FrontRight, AudioChannel.SurroundLeft, AudioChannel.SurroundRight],
        6 => [AudioChannel.FrontCenter, AudioChannel.FrontLeft, AudioChannel.FrontRight, AudioChannel.SurroundLeft, AudioChannel.SurroundRight, AudioChannel.LowFrequency],
        _ =>
        [
            AudioChannel.FrontCenter, AudioChannel.FrontLeft, AudioChannel.FrontRight, AudioChannel.FrontLeftWide,
            AudioChannel.FrontRightWide, AudioChannel.SurroundLeft, AudioChannel.SurroundRight, AudioChannel.LowFrequency,
        ],
    };

    /// <summary>Scales coefficients.</summary>
    /// <param name="values">The values.</param>
    /// <param name="gain">The gain.</param>
    /// <returns>The scaled values.</returns>
    private static float[] Scale(float[] values, float gain) => [.. values.Select(v => v * gain)];

    /// <summary>Downmixes one full-scale sine per channel.</summary>
    /// <param name="configuration">The AAC channel configuration.</param>
    /// <param name="frequencies">One frequency per channel.</param>
    /// <returns>The left and right outputs.</returns>
    private static (float[] Left, float[] Right) Mix(int configuration, double[] frequencies)
    {
        var layout = Layout(configuration);
        var planes = frequencies.Select(f => AudioTestSupport.Sine(48000, 48000, f, 1)).ToArray();
        using var frame = AudioTestSupport.Frame(layout, 48000, planes, 0, 48000, 0);
        using var output = new StereoDownmixer(layout).Process(frame);
        return (output.GetChannel(0).ToArray(), output.GetChannel(1).ToArray());
    }

    /// <summary>Asserts the amplitude of one frequency.</summary>
    /// <param name="samples">The samples.</param>
    /// <param name="frequency">The frequency.</param>
    /// <param name="expected">The expected amplitude.</param>
    private static void AssertTone(float[] samples, double frequency, double expected) =>
        Assert.InRange(AudioTestSupport.ToneAmplitude(samples, 48000, frequency), expected - 1e-3, expected + 1e-3);
}
