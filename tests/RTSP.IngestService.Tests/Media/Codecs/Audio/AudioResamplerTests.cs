// Tests for the polyphase resampler: exact output counts for all thirteen AAC rates, unity DC gain, passband accuracy
// against the analytic signal, alias and image rejection, zero-delay impulse placement, chunk-boundary independence,
// channel independence, timestamps, flush, reset and argument validation.

using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Audio.Aac;
using RTSP.IngestService.Media.Codecs.Audio.Processing;

namespace RTSP.IngestService.Tests.Media.Codecs.Audio;

/// <summary>Tests for <see cref="AudioResampler"/>.</summary>
public sealed class AudioResamplerTests
{
    /// <summary>The AAC sampling frequencies.</summary>
    public static TheoryData<int> Rates => [.. AacTables.SampleRates];

    /// <summary>A stream of N samples yields exactly ceil(N * 48000 / rate) samples, however it is chunked.</summary>
    /// <param name="rate">The input rate.</param>
    [Theory]
    [MemberData(nameof(Rates))]
    public void OutputCountIsExactForEveryRate(int rate)
    {
        foreach (var length in new[] { 1, 37, 1024, 10007 })
        {
            var input = AudioTestSupport.Sine(length, rate, 440, 0.5);
            var output = Run(new AudioResampler(rate, 48000, 1), [input], [length]);
            var expected = (long)Math.Ceiling(length * 48000.0 / rate - 1e-9);
            Assert.Equal(expected, output[0].Length);
            Assert.Equal(expected, new AudioResampler(rate, 48000, 1).GetOutputLength(length));
        }
    }

    /// <summary>A constant input is reproduced exactly away from the stream edges (unity DC gain, no offset).</summary>
    /// <param name="rate">The input rate.</param>
    [Theory]
    [MemberData(nameof(Rates))]
    public void ConstantIsReproduced(int rate)
    {
        var resampler = new AudioResampler(rate, 48000, 1);
        var input = Enumerable.Repeat(0.5f, rate / 4).ToArray();
        var output = Run(resampler, [input], [1000])[0];
        var margin = (int)Math.Ceiling((resampler.Lookahead + 1) * 48000.0 / rate);
        for (var n = margin; n < output.Length - margin; n++)
        {
            Assert.InRange(output[n], 0.5f - 1e-5f, 0.5f + 1e-5f);
        }
    }

    /// <summary>Passband sines match the analytic signal at the output instants to better than 80 dB.</summary>
    /// <param name="rate">The input rate.</param>
    [Theory]
    [MemberData(nameof(Rates))]
    public void PassbandSineMatchesAnalyticSignal(int rate)
    {
        var nyquist = Math.Min(rate, 48000) / 2.0;
        foreach (var frequency in new[] { 0.1 * nyquist, 0.5 * nyquist, 0.85 * nyquist })
        {
            var resampler = new AudioResampler(rate, 48000, 1);
            var input = AudioTestSupport.Sine(rate / 2, rate, frequency, 0.5);
            var output = Run(resampler, [input], [512])[0];
            var expected = AudioTestSupport.Sine(output.Length, 48000, frequency, 0.5);
            var margin = (int)Math.Ceiling((resampler.Lookahead + 1) * 48000.0 / rate);
            var range = new Range(margin, output.Length - margin);
            var snr = AudioTestSupport.Snr(expected.AsSpan()[range], output.AsSpan()[range]);
            Assert.True(snr > 80, $"{frequency:F0} Hz at {rate} Hz: SNR {snr:F1} dB");
        }
    }

    /// <summary>Downsampling rejects input above the output Nyquist frequency by more than 90 dB.</summary>
    /// <param name="rate">The input rate.</param>
    /// <param name="frequency">A frequency above 24 kHz.</param>
    [Theory]
    [InlineData(96000, 30000)]
    [InlineData(96000, 45000)]
    [InlineData(88200, 26500)]
    [InlineData(64000, 25000)]
    [InlineData(64000, 31000)]
    public void DownsamplingRejectsAliases(int rate, double frequency)
    {
        var output = Run(new AudioResampler(rate, 48000, 1), [AudioTestSupport.Sine(rate / 2, rate, frequency, 0.9)], [1024])[0];
        var alias = 48000 - frequency;
        var amplitude = AudioTestSupport.ToneAmplitude(output.AsSpan(1000, output.Length - 2000), 48000, alias);
        Assert.True(amplitude < 0.9 * 3.2e-5, $"Alias at {alias} Hz has amplitude {amplitude:E2}.");
        Assert.True(AudioTestSupport.Rms(output.AsSpan(1000, output.Length - 2000)) < 1e-4);
    }

    /// <summary>Upsampling rejects spectral images by more than 90 dB, including for a tone near the input Nyquist frequency.</summary>
    /// <param name="rate">The input rate.</param>
    [Theory]
    [InlineData(8000)]
    [InlineData(11025)]
    [InlineData(22050)]
    [InlineData(44100)]
    public void UpsamplingRejectsImages(int rate)
    {
        var frequency = 0.88 * rate / 2;
        var output = Run(new AudioResampler(rate, 48000, 1), [AudioTestSupport.Sine(rate, rate, frequency, 0.9)], [1024])[0];
        var body = output.AsSpan(2000, output.Length - 4000);
        Assert.InRange(AudioTestSupport.ToneAmplitude(body, 48000, frequency), 0.9 * 0.999, 0.9 * 1.001);
        var image = AudioTestSupport.ToneAmplitude(body, 48000, rate - frequency);
        Assert.True(image < 0.9 * 3.2e-5, $"Image at {rate - frequency} Hz has amplitude {image:E2}.");
    }

    /// <summary>An impulse appears at its exact presentation time: the filter adds no delay and is symmetric to within the Kaiser window's edge value.</summary>
    /// <param name="rate">An input rate that divides 48000 or is a multiple of it.</param>
    [Theory]
    [InlineData(8000)]
    [InlineData(12000)]
    [InlineData(16000)]
    [InlineData(24000)]
    [InlineData(96000)]
    public void ImpulseIsCentredWithoutDelay(int rate)
    {
        var input = new float[rate / 10];
        var position = input.Length / 2;
        input[position] = 1;
        var output = Run(new AudioResampler(rate, 48000, 1), [input], [333])[0];
        var centre = (int)((long)position * 48000 / rate);
        var peak = Array.IndexOf(output, output.Max());
        Assert.Equal(centre, peak);
        for (var k = 1; k < 200; k++)
        {
            Assert.Equal(output[centre - k], output[centre + k], 5e-6f);
        }
    }

    /// <summary>The impulse peak is within one output sample of its presentation time for fractional ratios too.</summary>
    /// <param name="rate">An input rate.</param>
    [Theory]
    [InlineData(7350)]
    [InlineData(11025)]
    [InlineData(22050)]
    [InlineData(44100)]
    [InlineData(88200)]
    [InlineData(32000)]
    [InlineData(64000)]
    public void ImpulsePeakIsAtPresentationTime(int rate)
    {
        var input = new float[rate / 10];
        var position = (input.Length / 2) + 3;
        input[position] = 1;
        var output = Run(new AudioResampler(rate, 48000, 1), [input], [500])[0];
        var time = position * 48000.0 / rate;
        var peak = Array.IndexOf(output, output.Max());
        Assert.InRange(peak, time - 1, time + 1);
    }

    /// <summary>Output is bit-identical however the input is split into frames.</summary>
    /// <param name="rate">The input rate.</param>
    [Theory]
    [MemberData(nameof(Rates))]
    public void ChunkBoundariesDoNotChangeOutput(int rate)
    {
        var input = AudioTestSupport.Add(AudioTestSupport.Sine(9000, rate, 300, 0.4), AudioTestSupport.Noise(9000, 0.2, rate));
        var whole = Run(new AudioResampler(rate, 48000, 1), [input], [9000])[0];
        var random = new Random(rate);
        var sizes = Enumerable.Range(0, 2000).Select(_ => random.Next(1, 700)).ToArray();
        Assert.Equal(whole, Run(new AudioResampler(rate, 48000, 1), [input], sizes)[0]);
        Assert.Equal(whole, Run(new AudioResampler(rate, 48000, 1), [input], [1])[0]);
    }

    /// <summary>Channels are processed independently: each output equals that channel resampled alone.</summary>
    [Fact]
    public void MultichannelMatchesSingleChannel()
    {
        var planes = Enumerable.Range(0, 6).Select(c => AudioTestSupport.Sine(8000, 44100, 200 * (c + 1), 0.1 * (c + 1))).ToArray();
        var together = Run(new AudioResampler(44100, 48000, 6), planes, [1024]);
        for (var c = 0; c < 6; c++)
        {
            Assert.Equal(Run(new AudioResampler(44100, 48000, 1), [planes[c]], [1024])[0], together[c]);
        }
    }

    /// <summary>Equal rates pass samples through unchanged with no look-ahead.</summary>
    [Fact]
    public void EqualRatesPassThrough()
    {
        var resampler = new AudioResampler(48000, 48000, 2);
        var left = AudioTestSupport.Noise(5000, 1, 1);
        var right = AudioTestSupport.Noise(5000, 1, 2);
        var output = Run(resampler, [left, right], [777]);
        Assert.Equal(0, resampler.Lookahead);
        Assert.Equal(left, output[0]);
        Assert.Equal(right, output[1]);
    }

    /// <summary>Output timestamps are the anchor plus the output sample index, so frames are contiguous.</summary>
    /// <param name="rate">The input rate.</param>
    [Theory]
    [MemberData(nameof(Rates))]
    public void TimestampsAreContinuous(int rate)
    {
        var resampler = new AudioResampler(rate, 48000, 1);
        var input = AudioTestSupport.Sine(rate, rate, 100, 0.5);
        var produced = 0L;
        const long anchor = 12345;
        for (var start = 0; start < input.Length; start += 1024)
        {
            var count = Math.Min(1024, input.Length - start);
            using var frame = AudioTestSupport.Frame([AudioChannel.FrontCenter], rate, [input], start, count, anchor + (start * 1000L / rate));
            using var output = resampler.Process(frame);
            if (output is not null)
            {
                Assert.Equal(anchor + (long)Math.Round(produced * 1000.0 / 48000, MidpointRounding.AwayFromZero), output.Timestamp);
                Assert.Equal(48000, output.SampleRate);
                produced += output.SampleCount;
            }
        }

        using var last = resampler.Flush();
        produced += last?.SampleCount ?? 0;
        Assert.Equal(48000, produced);
    }

    /// <summary>A long stream keeps exact counts and bounded state.</summary>
    [Fact]
    public void LongStreamKeepsExactCount()
    {
        var resampler = new AudioResampler(44100, 48000, 1);
        var chunk = AudioTestSupport.Sine(1024, 44100, 1000, 0.5);
        var produced = 0L;
        const int frames = 30 * 44100 / 1024;
        for (var i = 0; i < frames; i++)
        {
            using var frame = AudioTestSupport.Frame([AudioChannel.FrontCenter], 44100, [chunk], 0, 1024, i * 1024L * 1000 / 44100);
            using var output = resampler.Process(frame);
            produced += output?.SampleCount ?? 0;
        }

        using var last = resampler.Flush();
        produced += last?.SampleCount ?? 0;
        Assert.Equal(resampler.GetOutputLength(frames * 1024L), produced);
    }

    /// <summary>Flush releases the held samples and resets, so the same input produces the same output again.</summary>
    [Fact]
    public void FlushResetsForANewStream()
    {
        var resampler = new AudioResampler(22050, 48000, 1);
        var input = AudioTestSupport.Noise(3000, 0.5, 3);
        var first = Run(resampler, [input], [1000]);
        var second = Run(resampler, [input], [1000]);
        Assert.Equal(first[0], second[0]);
        Assert.Null(resampler.Flush());
    }

    /// <summary>Reset discards held samples and re-anchors on the next frame, as after a timestamp discontinuity.</summary>
    [Fact]
    public void ResetStartsANewTimeline()
    {
        var resampler = new AudioResampler(32000, 48000, 1);
        var input = AudioTestSupport.Sine(4000, 32000, 500, 0.5);
        using (var frame = AudioTestSupport.Frame([AudioChannel.FrontCenter], 32000, [input], 0, 4000, 0))
        {
            resampler.Process(frame)?.Dispose();
        }

        resampler.Reset();
        using var after = AudioTestSupport.Frame([AudioChannel.FrontCenter], 32000, [input], 0, 4000, 90000);
        using var output = resampler.Process(after);
        Assert.NotNull(output);
        Assert.Equal(90000, output.Timestamp);
        Assert.Equal(Run(new AudioResampler(32000, 48000, 1), [input], [4000])[0].AsSpan(0, output.SampleCount).ToArray(), output.GetChannel(0).ToArray());
    }

    /// <summary>Mismatched frames and unsupported configurations are rejected.</summary>
    [Fact]
    public void RejectsInvalidUse()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioResampler(0, 48000, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioResampler(44100, 48000, 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioResampler(44100, 47999, 1));
        var resampler = new AudioResampler(44100, 48000, 1);
        using var wrongRate = new AudioFrame(new[] { AudioChannel.FrontCenter }, 10, 48000, 0, MediaTimeBase.Milliseconds);
        Assert.Throws<ArgumentException>(() => resampler.Process(wrongRate));
        using var wrongChannels = new AudioFrame(new[] { AudioChannel.FrontLeft, AudioChannel.FrontRight }, 10, 44100, 0, MediaTimeBase.Milliseconds);
        Assert.Throws<ArgumentException>(() => resampler.Process(wrongChannels));
        using var tooLong = new AudioFrame(new[] { AudioChannel.FrontCenter }, AudioResampler.MaxInputSamples + 1, 44100, 0, MediaTimeBase.Milliseconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => resampler.Process(tooLong));
    }

    /// <summary>Runs planes through a resampler in the given chunk sizes (repeating the last) and flushes.</summary>
    /// <param name="resampler">The resampler.</param>
    /// <param name="planes">The input planes.</param>
    /// <param name="sizes">Chunk sizes.</param>
    /// <returns>The output planes.</returns>
    private static float[][] Run(AudioResampler resampler, float[][] planes, int[] sizes)
    {
        var layout = Enumerable.Range(0, planes.Length).Select(c => (AudioChannel)c).ToArray();
        var outputs = planes.Select(_ => new List<float>()).ToArray();
        var start = 0;
        var index = 0;
        while (start < planes[0].Length)
        {
            var count = Math.Min(sizes[Math.Min(index++, sizes.Length - 1)], planes[0].Length - start);
            using var frame = AudioTestSupport.Frame(layout, resampler.InputRate, planes, start, count, start);
            Append(resampler.Process(frame));
            start += count;
        }

        Append(resampler.Flush());
        return [.. outputs.Select(o => o.ToArray())];

        void Append(AudioFrame? frame)
        {
            if (frame is null)
            {
                return;
            }

            using (frame)
            {
                for (var c = 0; c < planes.Length; c++)
                {
                    outputs[c].AddRange(frame.GetChannel(c).ToArray());
                }
            }
        }
    }
}
