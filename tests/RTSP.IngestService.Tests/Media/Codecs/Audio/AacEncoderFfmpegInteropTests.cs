// Interoperability of the managed encoder with an independent decoder. FFmpeg is a test oracle only: it decodes the
// managed encoder's output (which must agree with the managed decoder to one 16-bit step and with the input to the
// expected fidelity), reports the stream format, and generates a 44.1 kHz 5.1 source with one tone per channel for
// the full managed path. Skipped when FFmpeg is absent unless RTSP_FFMPEG_REQUIRED=true.

using System.Diagnostics;
using RTSP.IngestService.Media.Codecs.Audio.Aac.Encoder;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Tests.Media.Codecs.Aac;

namespace RTSP.IngestService.Tests.Media.Codecs.Audio;

/// <summary>FFmpeg interoperability tests for <see cref="AacEncoder"/>.</summary>
public sealed class AacEncoderFfmpegInteropTests
{
    /// <summary>Signals and bitrates decoded by FFmpeg.</summary>
    public static TheoryData<string, int> Cases => new()
    {
        { "sine", 160000 },
        { "tones", 32000 },
        { "tones", 512000 },
        { "impulses", 160000 },
        { "noise", 96000 },
        { "silence", 160000 },
        { "speech", 64000 },
        { "speech", 160000 },
        { "transients", 160000 },
    };

    /// <summary>FFmpeg decodes the output identically to the managed decoder and recognises AAC LC 48 kHz stereo.</summary>
    /// <param name="signal">The test signal.</param>
    /// <param name="bitrate">The bitrate.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task FfmpegDecodesEncoderOutput(string signal, int bitrate)
    {
        var ffmpeg = FfmpegTool.Require();
        var (left, right) = Signal(signal);
        using var encoder = new AacEncoder(bitrate);
        var units = AudioTestSupport.Encode(encoder, left, right, 1000);
        if (signal is "speech" or "transients")
        {
            Assert.True(encoder.Statistics.TnsChannels > 0, "The stream exercises TNS.");
        }

        var reference = await DecodeWithFfmpegAsync(ffmpeg, AudioTestSupport.ToAdts(units));

        var stream = AacTestSupport.ParseAdts(AudioTestSupport.ToAdts(units));
        using var decoder = RTSP.IngestService.Media.Codecs.Audio.Aac.Decoder.AacDecoder.Create(stream.AudioSpecificConfig);
        var managed = AacTestSupport.DecodeAll(decoder, stream);
        Assert.Equal(units.Count * 1024 * 2, reference.Length);
        Assert.InRange(AacTestSupport.MaxDifference(managed, reference, null, 2), 0, 1);

        var ffmpegLeft = Deinterleave(reference, 0);
        var ffmpegRight = Deinterleave(reference, 1);
        if (signal == "silence")
        {
            Assert.Equal(0, AudioTestSupport.Peak(ffmpegLeft));
            return;
        }

        // White noise loses the energy above the coded bandwidth, which caps its correlation at sqrt(bandwidth / 24 kHz),
        // and noise-like bands are deliberately coded at a 6 dB signal-to-mask ratio.
        var minimum = signal == "noise" ? 0.8 * Math.Sqrt(encoder.Bandwidth / 24000.0) : bitrate >= 96000 ? 0.98 : 0.9;
        var leftCorrelation = AudioTestSupport.Correlation(left, ffmpegLeft.AsSpan(1024, left.Length));
        var rightCorrelation = AudioTestSupport.Correlation(right, ffmpegRight.AsSpan(1024, right.Length));
        Assert.True(leftCorrelation > minimum && rightCorrelation > minimum, $"Correlation {leftCorrelation:F3} / {rightCorrelation:F3}, minimum {minimum:F3}");
    }

    /// <summary>ffprobe reports the encoder output as AAC LC, 48 kHz, two channels.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task FfprobeReportsCanonicalFormat()
    {
        FfmpegTool.Require(probe: true);
        var probe = FfmpegTool.ProbePath!;
        var signal = AudioTestSupport.Sine(48000, 48000, 1000, 0.5);
        var directory = Directory.CreateTempSubdirectory("rtsp-aacenc-");
        try
        {
            var path = Path.Combine(directory.FullName, "out.aac");
            await File.WriteAllBytesAsync(path, AudioTestSupport.ToAdts(AudioTestSupport.Encode(new AacEncoder(), signal, signal)), TestContext.Current.CancellationToken);
            var output = await RunAsync(probe, ["-v", "error", "-show_entries", "stream=codec_name,profile,sample_rate,channels", "-of", "default=noprint_wrappers=1", path]);
            Assert.Contains("codec_name=aac", output, StringComparison.Ordinal);
            Assert.Contains("profile=LC", output, StringComparison.Ordinal);
            Assert.Contains("sample_rate=48000", output, StringComparison.Ordinal);
            Assert.Contains("channels=2", output, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A 44.1 kHz 5.1 source with one tone per channel passes the full managed path (decode, downmix, resample,
    /// encode); FFmpeg's decode of the result has each tone on the documented side at the documented level.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task SurroundSourceIsDownmixedResampledAndEncoded()
    {
        var ffmpeg = FfmpegTool.Require();
        var directory = Directory.CreateTempSubdirectory("rtsp-aacenc-");
        try
        {
            var source = Path.Combine(directory.FullName, "source.aac");
            await RunAsync(ffmpeg,
            [
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "lavfi", "-i",
                "aevalsrc=exprs=0.4*sin(2*PI*500*t)|0.4*sin(2*PI*700*t)|0.4*sin(2*PI*300*t)|0.4*sin(2*PI*60*t)|0.4*sin(2*PI*900*t)|0.4*sin(2*PI*1100*t):channel_layout=5.1:sample_rate=44100:duration=1",
                "-c:a", "aac", "-b:a", "384k", "-f", "adts", source,
            ]);
            var stream = AacTestSupport.ParseAdts(await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken));
            Assert.Equal(44100, stream.SampleRate);
            Assert.Equal(6, stream.Channels);
            var (units, _) = ManagedAudioPipelineTests.Transcode(stream);
            var decoded = await DecodeWithFfmpegAsync(ffmpeg, AudioTestSupport.ToAdts(units));
            var left = Deinterleave(decoded, 0).AsSpan(8192, 32768);
            var right = Deinterleave(decoded, 1).AsSpan(8192, 32768);

            // FFmpeg order is L R C LFE Ls Rs; the managed downmix gain is 1 / (1 + sqrt 2), centre and surround -3 dB.
            const double g = 0.4 / 2.41421356;
            AssertTone(left, 500, g);
            AssertTone(right, 500, 0);
            AssertTone(right, 700, g);
            AssertTone(left, 700, 0);
            AssertTone(left, 300, g * 0.70710678);
            AssertTone(right, 300, g * 0.70710678);
            AssertTone(left, 900, g * 0.70710678);
            AssertTone(right, 900, 0);
            AssertTone(right, 1100, g * 0.70710678);
            AssertTone(left, 1100, 0);
            AssertTone(left, 60, 0);
            AssertTone(right, 60, 0);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Generates a stereo test signal.</summary>
    /// <param name="name">The signal name.</param>
    /// <returns>Left and right samples.</returns>
    private static (float[] Left, float[] Right) Signal(string name)
    {
        const int length = 48000;
        switch (name)
        {
            case "sine":
                return (AudioTestSupport.Sine(length, 48000, 1000, 0.5), AudioTestSupport.Sine(length, 48000, 2000, 0.3));
            case "tones":
                return (
                    AudioTestSupport.Add(AudioTestSupport.Sine(length, 48000, 220, 0.3), AudioTestSupport.Sine(length, 48000, 3300, 0.1)),
                    AudioTestSupport.Add(AudioTestSupport.Sine(length, 48000, 330, 0.3), AudioTestSupport.Sine(length, 48000, 4400, 0.1)));
            case "impulses":
                var clicks = AudioTestSupport.Sine(length, 48000, 440, 0.05);
                for (var n = 4000; n < length; n += 9000)
                {
                    clicks[n] = 0.9f;
                }

                return (clicks, clicks.Select(x => -x).ToArray());
            case "noise":
                return (AudioTestSupport.Noise(length, 0.3, 1), AudioTestSupport.Noise(length, 0.3, 2));
            case "speech" or "transients":
                var planes = AudioQualitySignals.Create(name, length);
                return (planes[0], planes[1]);
            default:
                return (new float[length], new float[length]);
        }
    }

    /// <summary>Decodes ADTS with FFmpeg's native AAC decoder to interleaved float (16-bit PCM scale).</summary>
    /// <param name="ffmpeg">The FFmpeg path.</param>
    /// <param name="adts">The ADTS stream.</param>
    /// <returns>Interleaved samples.</returns>
    private static async Task<float[]> DecodeWithFfmpegAsync(string ffmpeg, byte[] adts)
    {
        var directory = Directory.CreateTempSubdirectory("rtsp-aacenc-");
        try
        {
            var input = Path.Combine(directory.FullName, "in.aac");
            var output = Path.Combine(directory.FullName, "out.s16");
            await File.WriteAllBytesAsync(input, adts, TestContext.Current.CancellationToken);
            await RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-c:a", "aac", "-i", input, "-f", "s16le", "-c:a", "pcm_s16le", output]);
            return AacTestSupport.ToFloat(await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Extracts one channel of interleaved stereo.</summary>
    /// <param name="interleaved">The samples.</param>
    /// <param name="channel">0 or 1.</param>
    /// <returns>The channel.</returns>
    private static float[] Deinterleave(float[] interleaved, int channel) =>
        [.. Enumerable.Range(0, interleaved.Length / 2).Select(i => interleaved[(2 * i) + channel])];

    /// <summary>Asserts a tone amplitude within 0.5 dB, or below -40 dB of the reference level when absent.</summary>
    /// <param name="samples">The samples.</param>
    /// <param name="frequency">The frequency.</param>
    /// <param name="expected">The expected amplitude.</param>
    private static void AssertTone(ReadOnlySpan<float> samples, double frequency, double expected)
    {
        var amplitude = AudioTestSupport.ToneAmplitude(samples, 48000, frequency);
        if (expected == 0)
        {
            Assert.True(amplitude < 0.004, $"{frequency} Hz leaked: {amplitude:F4}");
        }
        else
        {
            Assert.True(Math.Abs(20 * Math.Log10(amplitude / expected)) < 0.5, $"{frequency} Hz: {amplitude:F4}, expected {expected:F4}");
        }
    }

    /// <summary>Runs a tool and returns its standard output.</summary>
    /// <param name="tool">The executable.</param>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The standard output.</returns>
    private static async Task<string> RunAsync(string tool, string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, $"{Path.GetFileName(tool)} failed: {errors}");
        return await output;
    }
}
