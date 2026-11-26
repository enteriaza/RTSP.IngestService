// Tests for the managed AAC-LC encoder, decoded with the managed decoder: configuration, access-unit structure and
// timestamps, legal window-sequence transitions, silence, sines, multiple tones, impulses (block switching), channel
// identity, full-scale input, bitrate control over long input, chunk independence, flush, invalid use, disposal,
// determinism and concurrent instances.

using RTSP.IngestService.Media;
using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Audio.Aac;
using RTSP.IngestService.Media.Codecs.Audio.Aac.Encoder;

namespace RTSP.IngestService.Tests.Media.Codecs.Audio;

/// <summary>Tests for <see cref="AacEncoder"/>.</summary>
public sealed class AacEncoderTests
{
    /// <summary>The AudioSpecificConfig describes AAC-LC, 48 kHz, stereo, 1024-sample frames.</summary>
    [Fact]
    public void AudioSpecificConfigDescribesCanonicalFormat()
    {
        Assert.Equal([0x11, 0x90], AacEncoder.AudioSpecificConfig.ToArray());
        Assert.True(AacAudioConfiguration.TryParse(AacEncoder.AudioSpecificConfig, out var configuration, out _));
        Assert.Equal(2, configuration!.AudioObjectType);
        Assert.Equal(48000, configuration.SampleRate);
        Assert.Equal(2, configuration.ChannelConfiguration);
        Assert.Equal(1024, configuration.FrameLength);
    }

    /// <summary>N samples give ceil(N / 1024) + 1 access units, each a CPE within 6144 bits per channel, timestamped from -1024 samples.</summary>
    /// <param name="length">The input length.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(48000)]
    public void AccessUnitsAreFramedAndTimestamped(int length)
    {
        using var encoder = new AacEncoder();
        var signal = AudioTestSupport.Sine(length, 48000, 1000, 0.5);
        var units = AudioTestSupport.Encode(encoder, signal, signal);
        Assert.Equal(((length + 1023) / 1024) + 1, units.Count);
        for (var k = 0; k < units.Count; k++)
        {
            Assert.InRange(units[k].Data.Length, 1, AacEncoder.MaxFrameBits / 8);
            Assert.Equal(0x20, units[k].Data.Span[0] & 0xFE);
            Assert.Equal(MediaTimeBase.Milliseconds, units[k].TimeBase);
            Assert.Equal((long)Math.Round((k - 1) * 1024 * 1000.0 / 48000, MidpointRounding.AwayFromZero), units[k].Timestamp);
            if (k > 0)
            {
                Assert.Equal(units[k].Timestamp, units[k - 1].Timestamp + units[k - 1].Duration);
            }
        }
    }

    /// <summary>Silence costs a few bytes per frame and decodes to exact silence.</summary>
    [Fact]
    public void SilenceDecodesToSilence()
    {
        using var encoder = new AacEncoder();
        var silence = new float[48000];
        var units = AudioTestSupport.Encode(encoder, silence, silence);
        Assert.All(units, unit => Assert.InRange(unit.Data.Length, 1, 16));
        var decoded = AudioTestSupport.DecodeManaged(units);
        Assert.Equal(0, AudioTestSupport.Peak(decoded[0]));
        Assert.Equal(0, AudioTestSupport.Peak(decoded[1]));
    }

    /// <summary>Sines across the band are reproduced with at least 30 dB SNR, the right level and no extra delay beyond 1024 samples.</summary>
    /// <param name="frequency">The frequency.</param>
    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(5000)]
    [InlineData(12000)]
    public void SinesAreReproduced(double frequency)
    {
        using var encoder = new AacEncoder();
        var left = AudioTestSupport.Sine(48000, 48000, frequency, 0.5);
        var right = AudioTestSupport.Sine(48000, 48000, frequency, 0.25, 1);
        var decoded = AudioTestSupport.DecodeManaged(AudioTestSupport.Encode(encoder, left, right));
        AssertReproduced(left, decoded[0], 30);
        AssertReproduced(right, decoded[1], 30);
        Assert.InRange(AudioTestSupport.ToneAmplitude(decoded[0].AsSpan(1024, 48000), 48000, frequency), 0.5 * 0.94, 0.5 * 1.06);
    }

    /// <summary>A multi-tone signal keeps every component within 1 dB.</summary>
    [Fact]
    public void MultipleTonesArePreserved()
    {
        using var encoder = new AacEncoder();
        double[] frequencies = [150, 600, 2500, 7000, 11000];
        var signal = AudioTestSupport.Add([.. frequencies.Select((f, i) => AudioTestSupport.Sine(96000, 48000, f, 0.15, i))]);
        var decoded = AudioTestSupport.DecodeManaged(AudioTestSupport.Encode(encoder, signal, signal));
        AssertReproduced(signal, decoded[0], 30);
        foreach (var frequency in frequencies)
        {
            var amplitude = AudioTestSupport.ToneAmplitude(decoded[1].AsSpan(1024, 96000), 48000, frequency);
            Assert.InRange(20 * Math.Log10(amplitude / 0.15), -1, 1);
        }
    }

    /// <summary>Impulses trigger short windows through legal transitions and decode at their original positions.</summary>
    [Fact]
    public void ImpulsesUseShortWindows()
    {
        using var encoder = new AacEncoder();
        var signal = AudioTestSupport.Sine(96000, 48000, 440, 0.01);
        int[] clicks = [10000, 30000, 50500, 70001, 90000];
        foreach (var click in clicks)
        {
            signal[click] = 0.9f;
        }

        var units = AudioTestSupport.Encode(encoder, signal, signal);
        Assert.True(encoder.Statistics.ShortFrames >= clicks.Length);
        AssertLegalSequences(units);
        var decoded = AudioTestSupport.DecodeManaged(units)[0].AsSpan(1024, 96000);
        foreach (var click in clicks)
        {
            var window = decoded.Slice(click - 64, 128);
            var peak = 0;
            for (var i = 1; i < window.Length; i++)
            {
                peak = Math.Abs(window[i]) > Math.Abs(window[peak]) ? i : peak;
            }

            Assert.InRange(peak - 64, -2, 2);
            Assert.True(AudioTestSupport.Rms(decoded.Slice(click - 1024, 768)) < 0.03, $"Pre-echo before {click}.");
        }
    }

    /// <summary>A signal in one channel stays in that channel.</summary>
    [Fact]
    public void ChannelsAreNotSwapped()
    {
        using var encoder = new AacEncoder();
        var left = AudioTestSupport.Sine(48000, 48000, 800, 0.5);
        var right = AudioTestSupport.Sine(48000, 48000, 3000, 0.1);
        var decoded = AudioTestSupport.DecodeManaged(AudioTestSupport.Encode(encoder, left, right));
        Assert.True(AudioTestSupport.ToneAmplitude(decoded[0].AsSpan(1024, 48000), 48000, 800) > 0.45);
        Assert.True(AudioTestSupport.ToneAmplitude(decoded[0].AsSpan(1024, 48000), 48000, 3000) < 0.005);
        Assert.True(AudioTestSupport.ToneAmplitude(decoded[1].AsSpan(1024, 48000), 48000, 3000) > 0.09);
        Assert.True(AudioTestSupport.ToneAmplitude(decoded[1].AsSpan(1024, 48000), 48000, 800) < 0.005);
    }

    /// <summary>Full-scale square waves (the hardest case for the quantiser's range) encode and decode without fault.</summary>
    [Fact]
    public void FullScaleInputIsCoded()
    {
        using var encoder = new AacEncoder();
        var square = Enumerable.Range(0, 48000).Select(n => n / 24 % 2 == 0 ? 1f : -1f).ToArray();
        var decoded = AudioTestSupport.DecodeManaged(AudioTestSupport.Encode(encoder, square, square.Select(x => -x).ToArray()));
        Assert.True(AudioTestSupport.Correlation(square, decoded[0].AsSpan(1024, 48000)) > 0.95);
        Assert.InRange(AudioTestSupport.Peak(decoded[0]), 0.8, 1.5);
    }

    /// <summary>Over long varied input the average bitrate never exceeds the target, and complex input uses most of it.</summary>
    /// <param name="bitrate">The target bitrate.</param>
    [Theory]
    [InlineData(32000)]
    [InlineData(96000)]
    [InlineData(160000)]
    [InlineData(320000)]
    [InlineData(512000)]
    public void BitrateStaysWithinTarget(int bitrate)
    {
        using var encoder = new AacEncoder(bitrate);
        const int length = 48000 * 10;
        var left = AudioTestSupport.Add(AudioTestSupport.Noise(length, 0.2, 1), AudioTestSupport.Sine(length, 48000, 330, 0.3));
        var right = AudioTestSupport.Add(AudioTestSupport.Noise(length, 0.2, 2), AudioTestSupport.Sine(length, 48000, 550, 0.3));
        var units = AudioTestSupport.Encode(encoder, left, right, 4096);
        var seconds = units.Count * 1024 / 48000.0;
        var average = units.Sum(u => u.Data.Length * 8L) / seconds;
        Assert.InRange(average, bitrate * 0.8, bitrate);
        Assert.All(units, u => Assert.InRange(u.Data.Length, 1, AacEncoder.MaxFrameBits / 8));
        var decoded = AudioTestSupport.DecodeManaged(units);
        var correlation = AudioTestSupport.Correlation(left, decoded[0].AsSpan(1024, length));
        Assert.True(correlation > 0.7, $"Correlation {correlation:F3} at {average / 1000:F1} kbit/s");
    }

    /// <summary>Higher bitrates give higher fidelity on the same input.</summary>
    [Fact]
    public void QualityIncreasesWithBitrate()
    {
        var signal = AudioTestSupport.Add(AudioTestSupport.Sine(48000, 48000, 440, 0.3), AudioTestSupport.Sine(48000, 48000, 5100, 0.1), AudioTestSupport.Sine(48000, 48000, 9300, 0.02));
        var previous = double.NegativeInfinity;
        foreach (var bitrate in new[] { 32000, 64000, 160000, 320000 })
        {
            using var encoder = new AacEncoder(bitrate);
            var decoded = AudioTestSupport.DecodeManaged(AudioTestSupport.Encode(encoder, signal, signal));
            var snr = AudioTestSupport.Snr(signal, decoded[0].AsSpan(1024, 48000));
            Assert.True(snr > previous - 0.5, $"{bitrate}: {snr:F1} dB after {previous:F1} dB");
            previous = snr;
        }

        Assert.True(previous > 30, $"{previous:F1} dB at 320 kbit/s");
    }

    /// <summary>Output is byte-identical however the input is split into frames.</summary>
    [Fact]
    public void ChunkBoundariesDoNotChangeOutput()
    {
        var left = AudioTestSupport.Add(AudioTestSupport.Sine(30000, 48000, 700, 0.4), AudioTestSupport.Noise(30000, 0.1, 5));
        var right = AudioTestSupport.Noise(30000, 0.3, 6);
        left[15000] = 0.95f;
        var reference = Bytes(AudioTestSupport.Encode(new AacEncoder(), left, right, 1024));
        foreach (var chunk in new[] { 1, 7, 333, 1023, 4096, 30000 })
        {
            Assert.Equal(reference, Bytes(AudioTestSupport.Encode(new AacEncoder(), left, right, chunk)));
        }
    }

    /// <summary>Flush emits the tail and resets: encoding again reproduces the first stream exactly; an empty flush emits nothing.</summary>
    [Fact]
    public void FlushResetsForANewStream()
    {
        using var encoder = new AacEncoder();
        var units = new List<AacAccessUnit>();
        encoder.Flush(units);
        Assert.Empty(units);

        var signal = AudioTestSupport.Noise(5000, 0.5, 7);
        var first = Bytes(AudioTestSupport.Encode(encoder, signal, signal));
        var second = Bytes(AudioTestSupport.Encode(encoder, signal, signal));
        Assert.Equal(first, second);
    }

    /// <summary>Unsupported bitrates and non-canonical frames are rejected; a disposed encoder cannot be used.</summary>
    [Fact]
    public void RejectsInvalidUse()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AacEncoder(AacEncoder.MinBitrate - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AacEncoder(AacEncoder.MaxBitrate + 1));

        var encoder = new AacEncoder();
        var units = new List<AacAccessUnit>();
        using var wrongRate = new AudioFrame(AudioTestSupport.Stereo(), 1024, 44100, 0, MediaTimeBase.Milliseconds);
        Assert.Throws<ArgumentException>(() => encoder.Encode(wrongRate, units));
        using var mono = new AudioFrame(new[] { AudioChannel.FrontCenter }, 1024, 48000, 0, MediaTimeBase.Milliseconds);
        Assert.Throws<ArgumentException>(() => encoder.Encode(mono, units));
        using var swapped = new AudioFrame(new[] { AudioChannel.FrontRight, AudioChannel.FrontLeft }, 1024, 48000, 0, MediaTimeBase.Milliseconds);
        Assert.Throws<ArgumentException>(() => encoder.Encode(swapped, units));
        Assert.Empty(units);

        encoder.Dispose();
        encoder.Dispose();
        using var frame = new AudioFrame(AudioTestSupport.Stereo(), 1024, 48000, 0, MediaTimeBase.Milliseconds);
        Assert.Throws<ObjectDisposedException>(() => encoder.Encode(frame, units));
        Assert.Throws<ObjectDisposedException>(() => encoder.Flush(units));
    }

    /// <summary>Independent encoders on parallel threads produce the same bytes as sequential runs.</summary>
    [Fact]
    public void ConcurrentEncodersAreIndependent()
    {
        var inputs = Enumerable.Range(0, 6).Select(i => AudioTestSupport.Add(AudioTestSupport.Sine(24000, 48000, 300 + (i * 450), 0.4), AudioTestSupport.Noise(24000, 0.05, i))).ToArray();
        var sequential = inputs.Select(input => Bytes(AudioTestSupport.Encode(new AacEncoder(), input, input))).ToArray();
        var parallel = new byte[inputs.Length][];
        Parallel.For(0, inputs.Length, i => parallel[i] = Bytes(AudioTestSupport.Encode(new AacEncoder(), inputs[i], inputs[i])));
        Assert.Equal(sequential, parallel);
    }

    /// <summary>Asserts that the decoded signal, after the 1024-sample priming frame, matches the input.</summary>
    /// <param name="expected">The input.</param>
    /// <param name="decoded">The decoded channel including priming.</param>
    /// <param name="minimumSnr">The minimum SNR in decibels.</param>
    private static void AssertReproduced(float[] expected, float[] decoded, double minimumSnr)
    {
        Assert.True(decoded.Length >= expected.Length + AacEncoder.Delay);
        var aligned = decoded.AsSpan(AacEncoder.Delay, expected.Length);
        var snr = AudioTestSupport.Snr(expected, aligned);
        Assert.True(snr >= minimumSnr, $"SNR {snr:F1} dB");
        Assert.True(AudioTestSupport.Correlation(expected, aligned) > 0.999);
        Assert.InRange(AudioTestSupport.Rms(decoded.AsSpan(0, AacEncoder.Delay)), 0, 0.1 * AudioTestSupport.Rms(expected));
    }

    /// <summary>Asserts every transition follows ISO/IEC 14496-3 4.6.11.3 (window_sequence is in the second byte of a CPE).</summary>
    /// <param name="units">The access units.</param>
    private static void AssertLegalSequences(IEnumerable<AacAccessUnit> units)
    {
        var previous = AacWindowSequence.OnlyLong;
        foreach (var unit in units)
        {
            var sequence = (AacWindowSequence)((unit.Data.Span[1] >> 5) & 3);
            var afterShort = previous is AacWindowSequence.LongStart or AacWindowSequence.EightShort;
            Assert.Equal(afterShort, sequence is AacWindowSequence.EightShort or AacWindowSequence.LongStop);
            previous = sequence;
        }
    }

    /// <summary>Concatenates access units.</summary>
    /// <param name="units">The access units.</param>
    /// <returns>The bytes.</returns>
    private static byte[] Bytes(IEnumerable<AacAccessUnit> units) => [.. units.SelectMany(u => u.Data.ToArray())];
}
