// End-to-end tests of the fully managed audio path on the committed AAC fixtures: managed decode, downmix, resample
// and encode, then decode of the result. The reference is the canonical converter's output for the same decoded
// source, so the comparison isolates the encoder round trip while the length, duration, silence and channel checks
// cover the whole chain. Also covers ADTS framing and the codec-smoke command.

using RTSP.IngestService.Hosting;
using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Audio;
using RTSP.IngestService.Media.Codecs.Audio.Aac;
using RTSP.IngestService.Media.Codecs.Audio.Aac.Decoder;
using RTSP.IngestService.Media.Codecs.Audio.Aac.Encoder;
using RTSP.IngestService.Media.Codecs.Audio.Processing;
using RTSP.IngestService.Tests.Media.Codecs.Aac;

namespace RTSP.IngestService.Tests.Media.Codecs.Audio;

/// <summary>End-to-end tests for <see cref="ManagedAudioTranscoder"/>.</summary>
public sealed class ManagedAudioPipelineTests
{
    /// <summary>The fixtures: every committed AAC-LC stream (8 to 48 kHz, mono, stereo and 5.1).</summary>
    public static TheoryData<string> Fixtures =>
    [
        "aac-lc-48k-stereo", "aac-lc-44k-mono", "aac-lc-32k-stereo-lowrate", "aac-lc-22k-mono", "aac-lc-8k-mono",
        "aac-lc-48k-5.1", "aac-lc-48k-stereo-pns", "aac-mf-48k-stereo",
    ];

    /// <summary>Each fixture transcodes to 48 kHz stereo with the expected length, no lost audio, no swap and good fidelity.</summary>
    /// <param name="name">The fixture.</param>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void FixtureTranscodesToCanonicalAac(string name)
    {
        var source = AacTestSupport.ReadFixture(name);
        var canonical = Canonical(source);
        var (units, transcoder) = Transcode(source);
        Assert.Equal(canonical[0].Length, transcoder.CanonicalSamples);
        Assert.Equal((long)Math.Ceiling(source.AccessUnits.Count * 1024 * 48000.0 / source.SampleRate - 1e-9), canonical[0].Length);
        Assert.Equal(((canonical[0].Length + 1023) / 1024) + 1, units.Count);

        var decoded = AudioTestSupport.DecodeManaged(units);
        Assert.InRange(decoded[0].Length, canonical[0].Length + AacEncoder.Delay, canonical[0].Length + AacEncoder.Delay + 1023);
        var timeBase = units[0].TimeBase;
        Assert.Equal(new MediaTimeBase(1, source.SampleRate), timeBase);
        Assert.InRange(units[0].Timestamp, (-1024L * source.SampleRate / 48000) - 1, (-1024L * source.SampleRate / 48000) + 1);
        var duration = timeBase.Rescale(units[^1].Timestamp + units[^1].Duration - units[0].Timestamp, MediaTimeBase.Milliseconds);
        var canonicalMilliseconds = canonical[0].Length * 1000.0 / 48000;
        Assert.InRange(duration, canonicalMilliseconds + 21, canonicalMilliseconds + 43);

        for (var c = 0; c < 2; c++)
        {
            var aligned = decoded[c].AsSpan(AacEncoder.Delay, canonical[c].Length);
            var snr = AudioTestSupport.Snr(canonical[c], aligned);
            var correlation = AudioTestSupport.Correlation(canonical[c], aligned);
            Assert.True(snr > 12 && correlation > 0.97, $"{name} channel {c}: SNR {snr:F1} dB, correlation {correlation:F4}");
            for (var block = 0; block + 1024 <= canonical[c].Length; block += 1024)
            {
                var expected = AudioTestSupport.Rms(canonical[c].AsSpan(block, 1024));
                if (expected > 0.01)
                {
                    Assert.True(AudioTestSupport.Rms(aligned.Slice(block, 1024)) > 0.5 * expected, $"{name}: block at {block} lost its audio.");
                }
            }
        }

        if (AudioTestSupport.Correlation(canonical[0], canonical[1]) < 0.9)
        {
            var left = decoded[0].AsSpan(AacEncoder.Delay, canonical[0].Length);
            Assert.True(AudioTestSupport.Correlation(canonical[0], left) > AudioTestSupport.Correlation(canonical[1], left));
        }

        Assert.Equal(0, transcoder.ClippedSamples);
    }

    /// <summary>ADTS framing round-trips the encoder's access units and configuration.</summary>
    [Fact]
    public void AdtsRoundTrips()
    {
        var signal = AudioTestSupport.Sine(9000, 48000, 1000, 0.3);
        var units = AudioTestSupport.Encode(new AacEncoder(), signal, signal);
        using var stream = new MemoryStream();
        foreach (var unit in units)
        {
            AdtsFraming.Write(stream, AacEncoder.AudioSpecificConfig, unit.Data.Span);
        }

        Assert.Equal(AudioTestSupport.ToAdts(units), stream.ToArray());
        var read = AdtsFraming.Read(stream.ToArray(), out var configuration);
        Assert.Equal(AacEncoder.AudioSpecificConfig.ToArray(), configuration);
        Assert.Equal(units.Select(u => u.Data.ToArray()), read.Select(u => u.ToArray()));

        var fixture = File.ReadAllBytes(Path.Combine(AacTestSupport.FixtureDirectory, "aac-lc-48k-5.1.aac"));
        Assert.Equal(AacTestSupport.ParseAdts(fixture).AudioSpecificConfig, AdtsFraming.Read(fixture, out var fixtureConfig).Count > 0 ? fixtureConfig : null);
        Assert.Throws<CodecException>(() => AdtsFraming.Read(fixture.AsMemory(0, fixture.Length - 1), out _));
        Assert.Throws<CodecException>(() => AdtsFraming.Read(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 }, out _));
    }

    /// <summary>The codec-smoke command transcodes a file and reports usage errors without throwing.</summary>
    [Fact]
    public void SmokeCommandTranscodesAFile()
    {
        var directory = Directory.CreateTempSubdirectory("rtsp-codec-smoke-");
        try
        {
            var output = Path.Combine(directory.FullName, "out.aac");
            var input = Path.Combine(AacTestSupport.FixtureDirectory, "aac-lc-44k-mono.aac");
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            Assert.True(CodecSmokeCommand.TryRun([CodecSmokeCommand.CommandName, input, output], stdout, stderr, out var exitCode));
            Assert.Equal(ExitCodes.Success, exitCode);
            Assert.Contains("Input: AAC-LC 44100 Hz, 1 channel(s)", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("Output: AAC-LC 48000 Hz, 2 channels", stdout.ToString(), StringComparison.Ordinal);
            var units = AdtsFraming.Read(File.ReadAllBytes(output), out var configuration);
            Assert.Equal(AacEncoder.AudioSpecificConfig.ToArray(), configuration);
            Assert.NotEmpty(units);

            Assert.False(CodecSmokeCommand.TryRun(["publisher-key"], stdout, stderr, out _));
            Assert.True(CodecSmokeCommand.TryRun([CodecSmokeCommand.CommandName], stdout, stderr, out exitCode));
            Assert.Equal(ExitCodes.UsageError, exitCode);
            Assert.True(CodecSmokeCommand.TryRun([CodecSmokeCommand.CommandName, Path.Combine(directory.FullName, "missing.aac"), output], stdout, stderr, out exitCode));
            Assert.Equal(ExitCodes.UsageError, exitCode);
            var garbage = Path.Combine(directory.FullName, "garbage.aac");
            File.WriteAllBytes(garbage, [1, 2, 3, 4, 5, 6, 7, 8, 9]);
            Assert.True(CodecSmokeCommand.TryRun([CodecSmokeCommand.CommandName, garbage, output], stdout, stderr, out exitCode));
            Assert.Equal(ExitCodes.Failure, exitCode);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Decodes a fixture and converts it to canonical audio with the standalone stages.</summary>
    /// <param name="source">The fixture.</param>
    /// <returns>Left and right canonical planes.</returns>
    internal static float[][] Canonical(AdtsStream source)
    {
        using var decoder = AacDecoder.Create(source.AudioSpecificConfig);
        var converter = new CanonicalAudioConverter(decoder.Layout.Span, decoder.SampleRate);
        var left = new List<float>();
        var right = new List<float>();
        var timestamp = 0L;
        foreach (var unit in source.AccessUnits)
        {
            using var frame = decoder.Decode(new EncodedPacket(unit, timestamp, new MediaTimeBase(1, source.SampleRate)));
            Collect(converter.Process(frame));
            timestamp += 1024;
        }

        Collect(converter.Flush());
        return [[.. left], [.. right]];

        void Collect(AudioFrame? frame)
        {
            if (frame is null)
            {
                return;
            }

            using (frame)
            {
                left.AddRange(frame.GetChannel(0).ToArray());
                right.AddRange(frame.GetChannel(1).ToArray());
            }
        }
    }

    /// <summary>Transcodes a fixture with the managed transcoder.</summary>
    /// <param name="source">The fixture.</param>
    /// <returns>The access units and the transcoder (disposed).</returns>
    internal static (List<AacAccessUnit> Units, ManagedAudioTranscoder Transcoder) Transcode(AdtsStream source)
    {
        var units = new List<AacAccessUnit>();
        using var transcoder = new ManagedAudioTranscoder(source.AudioSpecificConfig);
        var timestamp = 0L;
        foreach (var unit in source.AccessUnits)
        {
            transcoder.Transcode(new EncodedPacket(unit, timestamp, new MediaTimeBase(1, source.SampleRate)), units);
            timestamp += 1024;
        }

        transcoder.Flush(units);
        return (units, transcoder);
    }
}
