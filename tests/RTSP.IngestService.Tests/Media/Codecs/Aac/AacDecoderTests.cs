// Tests of the managed AAC-LC decoder. Interoperability: committed ADTS fixtures produced by two independent encoders
// (FFmpeg's native AAC encoder and Microsoft Media Foundation's, via FFmpeg aac_mf) are decoded and compared sample by
// sample with the reference PCM that FFmpeg's AAC decoder produced from the same files. These tests need no FFmpeg.
// Also covered: the configuration gate, malformed and truncated access units, faulting, flush, timestamps,
// determinism and independent concurrent decoders.

using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Audio.Aac.Decoder;

namespace RTSP.IngestService.Tests.Media.Codecs.Aac;

/// <summary>Managed AAC-LC decoder tests.</summary>
public sealed class AacDecoderTests
{
    /// <summary>Reference channel for each of our channels in 5.1: ours is C L R Ls Rs LFE, FFmpeg's is L R C LFE Ls Rs.</summary>
    private static readonly int[] SurroundMap = [2, 0, 1, 4, 5, 3];

    /// <summary>Gets fixtures decoded without noise substitution: name, sample rate, channels.</summary>
    public static TheoryData<string, int, int> ExactFixtures => new()
    {
        { "aac-lc-48k-stereo", 48000, 2 },
        { "aac-lc-44k-mono", 44100, 1 },
        { "aac-lc-32k-stereo-lowrate", 32000, 2 },
        { "aac-lc-22k-mono", 22050, 1 },
        { "aac-lc-8k-mono", 8000, 1 },
        { "aac-lc-48k-5.1", 48000, 6 },
        { "aac-mf-48k-stereo", 48000, 2 },
    };

    /// <summary>Every frame matches the independent decoder to within one 16-bit step.</summary>
    /// <param name="name">The fixture.</param>
    /// <param name="sampleRate">The expected rate.</param>
    /// <param name="channels">The expected channels.</param>
    [Theory]
    [MemberData(nameof(ExactFixtures))]
    public void DecodesFixtureLikeTheReferenceDecoder(string name, int sampleRate, int channels)
    {
        var stream = AacTestSupport.ReadFixture(name);
        using var decoder = AacDecoder.Create(stream.AudioSpecificConfig);
        Assert.Equal(sampleRate, decoder.SampleRate);
        Assert.Equal(channels, decoder.Layout.Length);

        var decoded = AacTestSupport.DecodeAll(decoder, stream);
        var reference = AacTestSupport.ReadReference(name);

        Assert.InRange(AacTestSupport.MaxDifference(decoded, reference, channels == 6 ? SurroundMap : null, channels), 0, 1);
        Assert.Equal(stream.AccessUnits.Count, decoder.Usage.Frames);
    }

    /// <summary>The fixtures exercise the coding tools the support statement claims.</summary>
    [Fact]
    public void FixturesExerciseTheClaimedTools()
    {
        var total = new AacToolUsage();
        foreach (var name in new[] { "aac-lc-48k-stereo", "aac-lc-44k-mono", "aac-lc-32k-stereo-lowrate", "aac-lc-22k-mono", "aac-lc-8k-mono", "aac-lc-48k-5.1", "aac-lc-48k-stereo-pns", "aac-mf-48k-stereo" })
        {
            var stream = AacTestSupport.ReadFixture(name);
            using var decoder = AacDecoder.Create(stream.AudioSpecificConfig);
            AacTestSupport.DecodeAll(decoder, stream);
            total.ShortWindowChannels += decoder.Usage.ShortWindowChannels;
            total.TransitionWindowChannels += decoder.Usage.TransitionWindowChannels;
            total.TnsChannels += decoder.Usage.TnsChannels;
            total.NoiseBands += decoder.Usage.NoiseBands;
            total.MidSideBands += decoder.Usage.MidSideBands;
            total.IntensityBands += decoder.Usage.IntensityBands;
            total.KbdWindowChannels += decoder.Usage.KbdWindowChannels;
            total.PulseChannels += decoder.Usage.PulseChannels;
        }

        Assert.True(total.ShortWindowChannels > 0, "No fixture uses eight short windows.");
        Assert.True(total.TransitionWindowChannels > 0, "No fixture uses start/stop windows.");
        Assert.True(total.TnsChannels > 0, "No fixture uses TNS.");
        Assert.True(total.NoiseBands > 0, "No fixture uses noise substitution.");
        Assert.True(total.MidSideBands > 0, "No fixture uses M/S stereo.");
        Assert.True(total.IntensityBands > 0, "No fixture uses intensity stereo.");
    }

    /// <summary>Noise-substituted bands are random, so the comparison is of per-frame energy, within 1 dB.</summary>
    [Fact]
    public void NoiseSubstitutionMatchesReferenceEnergy()
    {
        const string Name = "aac-lc-48k-stereo-pns";
        var stream = AacTestSupport.ReadFixture(Name);
        using var decoder = AacDecoder.Create(stream.AudioSpecificConfig);
        var decoded = AacTestSupport.DecodeAll(decoder, stream);
        var reference = AacTestSupport.ReadReference(Name);
        Assert.Equal(reference.Length, decoded.Length);
        Assert.True(decoder.Usage.NoiseBands > 0);

        const int Block = AacDecoder.FrameLength * 2;
        for (var start = Block; start + Block <= decoded.Length; start += Block)
        {
            var ours = Energy(decoded.AsSpan(start, Block));
            var theirs = Energy(reference.AsSpan(start, Block));
            Assert.InRange(10 * Math.Log10((ours + 1e-9) / (theirs + 1e-9)), -1.0, 1.0);
        }
    }

    /// <summary>Decoding is deterministic: the same input gives identical output.</summary>
    [Fact]
    public void DecodingIsDeterministic()
    {
        var stream = AacTestSupport.ReadFixture("aac-lc-48k-stereo-pns");
        using var first = AacDecoder.Create(stream.AudioSpecificConfig);
        using var second = AacDecoder.Create(stream.AudioSpecificConfig);
        Assert.Equal(AacTestSupport.DecodeAll(first, stream), AacTestSupport.DecodeAll(second, stream));
    }

    /// <summary>Independent decoders on parallel threads produce the same output as one decoder alone.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task ConcurrentDecodersAreIndependent()
    {
        var names = new[] { "aac-lc-48k-stereo", "aac-lc-44k-mono", "aac-lc-48k-5.1", "aac-mf-48k-stereo" };
        var expected = names.ToDictionary(name => name, name =>
        {
            var stream = AacTestSupport.ReadFixture(name);
            using var decoder = AacDecoder.Create(stream.AudioSpecificConfig);
            return AacTestSupport.DecodeAll(decoder, stream);
        });

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            var name = names[i % names.Length];
            var stream = AacTestSupport.ReadFixture(name);
            using var decoder = AacDecoder.Create(stream.AudioSpecificConfig);
            return (name, samples: AacTestSupport.DecodeAll(decoder, stream));
        }, TestContext.Current.CancellationToken)));

        foreach (var (name, samples) in results)
        {
            Assert.Equal(expected[name], samples);
        }
    }

    /// <summary>A malformed access unit in one decoder does not disturb another decoder.</summary>
    [Fact]
    public void FailureInOneDecoderDoesNotAffectAnother()
    {
        var stream = AacTestSupport.ReadFixture("aac-lc-48k-stereo");
        using var healthy = AacDecoder.Create(stream.AudioSpecificConfig);
        using var broken = AacDecoder.Create(stream.AudioSpecificConfig);
        var reference = AacTestSupport.ReadReference("aac-lc-48k-stereo");
        var decoded = new List<float[]>();
        for (var i = 0; i < stream.AccessUnits.Count; i++)
        {
            if (i == 5)
            {
                Assert.Throws<CodecException>(() => broken.Decode(Packet(stream.AccessUnits[i].AsSpan(0, 3).ToArray(), 0)).Dispose());
            }

            using var frame = healthy.Decode(Packet(stream.AccessUnits[i], i * 1024L));
            decoded.Add(Interleave(frame));
        }

        Assert.InRange(AacTestSupport.MaxDifference([.. decoded.SelectMany(static frame => frame)], reference, null, 2), 0, 1);
    }

    /// <summary>Output frames carry the packet timestamp, the sample rate and the 1024-sample duration.</summary>
    [Fact]
    public void FramesCarryTimestampsAndDuration()
    {
        var stream = AacTestSupport.ReadFixture("aac-lc-44k-mono");
        using var decoder = AacDecoder.Create(stream.AudioSpecificConfig);
        using var frame = decoder.Decode(new EncodedPacket(stream.AccessUnits[0], 123_456, MediaTimeBase.Milliseconds));
        Assert.Equal(123_456, frame.Timestamp);
        Assert.Equal(MediaTimeBase.Milliseconds, frame.TimeBase);
        Assert.Equal(1024, frame.SampleCount);
        Assert.Equal(44100, frame.SampleRate);
        Assert.Equal(23, frame.Duration);
        Assert.Equal([AudioChannel.FrontCenter], frame.Layout.ToArray());
    }

    /// <summary>Flush discards the overlap, so the next frame equals a fresh decoder's first frame.</summary>
    [Fact]
    public void FlushResetsOverlap()
    {
        var stream = AacTestSupport.ReadFixture("aac-lc-48k-stereo");
        using var decoder = AacDecoder.Create(stream.AudioSpecificConfig);
        for (var i = 0; i < 4; i++)
        {
            decoder.Decode(Packet(stream.AccessUnits[i], i)).Dispose();
        }

        decoder.Flush();
        using var afterFlush = decoder.Decode(Packet(stream.AccessUnits[4], 4));
        using var fresh = AacDecoder.Create(stream.AudioSpecificConfig);
        using var first = fresh.Decode(Packet(stream.AccessUnits[4], 4));
        Assert.Equal(Interleave(first), Interleave(afterFlush));
    }

    /// <summary>Configurations outside the supported AAC-LC subset are rejected with the reason.</summary>
    /// <param name="hex">The AudioSpecificConfig.</param>
    /// <param name="kind">The expected failure kind.</param>
    /// <param name="fragment">Part of the expected message.</param>
    [Theory]
    [InlineData("0A10", "Unsupported", "object type 1")]
    [InlineData("2190", "Unsupported", "object type 4")]
    [InlineData("2B92080000", "Unsupported", "HE-AAC")]
    [InlineData("EB09880000", "Unsupported", "HE-AAC")]
    [InlineData("1194", "Unsupported", "960-sample")]
    [InlineData("1192", "Unsupported", "dependsOnCoreCoder")]
    [InlineData("1191", "InvalidData", "extensionFlag")]
    [InlineData("1180", "Unsupported", "channel configuration 0")]
    [InlineData("1780BB8010", "Unsupported", "explicit")]
    [InlineData("11", "InvalidData", "at least 2")]
    [InlineData("1690", "InvalidData", "reserved sampling frequency")]
    public void RejectsUnsupportedConfigurations(string hex, string kind, string fragment)
    {
        var error = Assert.Throws<CodecException>(() => AacDecoder.Create(Convert.FromHexString(hex)).Dispose());
        Assert.Equal(Enum.Parse<CodecErrorKind>(kind), error.Kind);
        Assert.Contains(fragment, error.Message, StringComparison.Ordinal);
    }

    /// <summary>Every supported configuration builds: 13 frequencies by 7 channel configurations.</summary>
    [Fact]
    public void AcceptsAllIndexedFrequenciesAndChannelConfigurations()
    {
        int[] channels = [0, 1, 2, 3, 4, 5, 6, 8];
        for (var frequency = 0; frequency < 13; frequency++)
        {
            for (var configuration = 1; configuration <= 7; configuration++)
            {
                byte[] config = [(byte)((2 << 3) | (frequency >> 1)), (byte)(((frequency & 1) << 7) | (configuration << 3))];
                using var decoder = AacDecoder.Create(config);
                Assert.Equal(channels[configuration], decoder.Layout.Length);
            }
        }
    }

    /// <summary>Every truncation of a valid access unit fails with a codec error and faults the decoder.</summary>
    [Fact]
    public void TruncatedAccessUnitsFailCleanly()
    {
        var stream = AacTestSupport.ReadFixture("aac-lc-48k-stereo");
        var unit = stream.AccessUnits[10];
        for (var length = 0; length < unit.Length - 1; length++)
        {
            using var decoder = AacDecoder.Create(stream.AudioSpecificConfig);
            var error = Assert.Throws<CodecException>(() => decoder.Decode(Packet(unit.AsSpan(0, length).ToArray(), 0)).Dispose());
            Assert.Equal(CodecErrorKind.InvalidData, error.Kind);
            Assert.Throws<InvalidOperationException>(() => decoder.Decode(Packet(unit, 0)).Dispose());
            Assert.Throws<InvalidOperationException>(decoder.Flush);
        }
    }

    /// <summary>Corrupted access units either decode or fail with a codec error; nothing else escapes and nothing hangs.</summary>
    [Fact]
    public void CorruptedAccessUnitsOnlyFailWithCodecErrors()
    {
        var random = new Random(20261126);
        foreach (var name in new[] { "aac-lc-48k-stereo", "aac-lc-48k-5.1", "aac-lc-32k-stereo-lowrate" })
        {
            var stream = AacTestSupport.ReadFixture(name);
            for (var trial = 0; trial < 300; trial++)
            {
                var unit = stream.AccessUnits[random.Next(stream.AccessUnits.Count)].ToArray();
                var flips = random.Next(1, 8);
                for (var i = 0; i < flips; i++)
                {
                    unit[random.Next(unit.Length)] ^= (byte)(1 << random.Next(8));
                }

                using var decoder = AacDecoder.Create(stream.AudioSpecificConfig);
                try
                {
                    using var frame = decoder.Decode(Packet(unit, 0));
                    for (var c = 0; c < frame.ChannelCount; c++)
                    {
                        foreach (var sample in frame.GetChannel(c))
                        {
                            Assert.True(float.IsFinite(sample));
                        }
                    }
                }
                catch (CodecException)
                {
                }
            }

            for (var trial = 0; trial < 100; trial++)
            {
                var garbage = new byte[random.Next(0, 2048)];
                random.NextBytes(garbage);
                using var decoder = AacDecoder.Create(stream.AudioSpecificConfig);
                try
                {
                    decoder.Decode(Packet(garbage, 0)).Dispose();
                }
                catch (CodecException)
                {
                }
            }
        }
    }

    /// <summary>Elements that do not match the channel configuration are invalid.</summary>
    [Fact]
    public void RejectsElementsThatDoNotMatchTheConfiguration()
    {
        var stereo = AacTestSupport.ReadFixture("aac-lc-48k-stereo");
        var mono = AacTestSupport.ReadFixture("aac-lc-44k-mono");
        using var decoder = AacDecoder.Create(stereo.AudioSpecificConfig);
        var error = Assert.Throws<CodecException>(() => decoder.Decode(Packet(mono.AccessUnits[3], 0)).Dispose());
        Assert.Equal(CodecErrorKind.InvalidData, error.Kind);
    }

    /// <summary>Oversized access units are a resource-limit failure.</summary>
    [Fact]
    public void RejectsOversizedAccessUnits()
    {
        using var decoder = AacDecoder.Create(Convert.FromHexString("1190"));
        var error = Assert.Throws<CodecException>(() => decoder.Decode(Packet(new byte[AacDecoder.MaxAccessUnitSize + 1], 0)).Dispose());
        Assert.Equal(CodecErrorKind.ResourceLimit, error.Kind);
    }

    /// <summary>A disposed decoder cannot be used.</summary>
    [Fact]
    public void DisposedDecoderThrows()
    {
        var decoder = AacDecoder.Create(Convert.FromHexString("1190"));
        decoder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => decoder.Decode(Packet([0xE0], 0)).Dispose());
        Assert.Throws<ObjectDisposedException>(decoder.Flush);
    }

    /// <summary>Wraps data in a millisecond packet.</summary>
    /// <param name="data">The access unit.</param>
    /// <param name="timestamp">The timestamp.</param>
    /// <returns>The packet.</returns>
    private static EncodedPacket Packet(byte[] data, long timestamp) => new(data, timestamp, MediaTimeBase.Milliseconds);

    /// <summary>Interleaves a frame.</summary>
    /// <param name="frame">The frame.</param>
    /// <returns>Interleaved samples.</returns>
    private static float[] Interleave(AudioFrame frame)
    {
        var output = new float[frame.SampleCount * frame.ChannelCount];
        for (var c = 0; c < frame.ChannelCount; c++)
        {
            var plane = frame.GetChannel(c);
            for (var n = 0; n < plane.Length; n++)
            {
                output[(n * frame.ChannelCount) + c] = plane[n];
            }
        }

        return output;
    }

    /// <summary>Sum of squares.</summary>
    /// <param name="samples">The samples.</param>
    /// <returns>The energy.</returns>
    private static double Energy(ReadOnlySpan<float> samples)
    {
        var sum = 0.0;
        foreach (var sample in samples)
        {
            sum += (double)sample * sample;
        }

        return sum;
    }
}
