// FFmpeg-dependent interoperability tests of the managed AAC-LC decoder, kept apart from the fixture-based tests: a
// fresh signal is encoded by FFmpeg's AAC encoder at every indexed sampling frequency and decoded both by FFmpeg and
// by the managed decoder, which must agree to within one 16-bit step. FFmpeg is a test oracle only. The tests skip
// without FFmpeg unless RTSP_FFMPEG_REQUIRED=true.

using System.Diagnostics;
using RTSP.IngestService.Media.Codecs.Audio.Aac.Decoder;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Media.Codecs.Aac;

/// <summary>Managed AAC decoder versus FFmpeg's decoder on freshly encoded streams.</summary>
public sealed class AacFfmpegInteropTests
{
    /// <summary>Gets the cases: sample rate, channels, bitrate.</summary>
    public static TheoryData<int, int, string> Cases => new()
    {
        { 96000, 2, "192k" },
        { 88200, 1, "96k" },
        { 64000, 2, "128k" },
        { 48000, 2, "64k" },
        { 44100, 2, "160k" },
        { 32000, 1, "48k" },
        { 24000, 2, "64k" },
        { 22050, 2, "48k" },
        { 16000, 1, "32k" },
        { 12000, 1, "24k" },
        { 11025, 2, "32k" },
        { 8000, 1, "12k" },
        { 7350, 1, "12k" },
    };

    /// <summary>The managed decoder matches FFmpeg's decoder on an FFmpeg-encoded stream.</summary>
    /// <param name="sampleRate">The sample rate.</param>
    /// <param name="channels">The channel count.</param>
    /// <param name="bitrate">The bitrate.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task MatchesFfmpegDecoder(int sampleRate, int channels, string bitrate)
    {
        var ffmpeg = FfmpegTool.Require();
        var directory = Directory.CreateTempSubdirectory("rtsp-aac-");
        try
        {
            var aac = Path.Combine(directory.FullName, "input.aac");
            var pcm = Path.Combine(directory.FullName, "reference.s16");
            var expression = "0.3*sin(2*PI*(200+300*t)*t)+0.2*(random(0)-0.5)*lt(mod(t\\,0.2)\\,0.01)";
            var exprs = channels == 1 ? expression : expression + "|0.2*sin(2*PI*1000*t)+0.1*sin(2*PI*" + (sampleRate / 5) + "*t)";
            await RunAsync(ffmpeg, ["-f", "lavfi", "-i", $"aevalsrc=exprs={exprs}:sample_rate={sampleRate}:duration=0.5", "-c:a", "aac", "-b:a", bitrate, "-aac_pns", "0", "-f", "adts", aac]);
            await RunAsync(ffmpeg, ["-c:a", "aac", "-i", aac, "-f", "s16le", "-c:a", "pcm_s16le", pcm]);

            var stream = AacTestSupport.ParseAdts(await File.ReadAllBytesAsync(aac, TestContext.Current.CancellationToken));
            using var decoder = AacDecoder.Create(stream.AudioSpecificConfig);
            Assert.Equal(sampleRate, decoder.SampleRate);
            var decoded = AacTestSupport.DecodeAll(decoder, stream);
            var reference = AacTestSupport.ToFloat(await File.ReadAllBytesAsync(pcm, TestContext.Current.CancellationToken));
            Assert.InRange(AacTestSupport.MaxDifference(decoded, reference, null, channels), 0, 1);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Runs FFmpeg to completion.</summary>
    /// <param name="ffmpeg">The executable.</param>
    /// <param name="arguments">Arguments after the common ones.</param>
    /// <returns>A task.</returns>
    private static async Task RunAsync(string ffmpeg, string[] arguments)
    {
        var start = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in (string[])["-hide_banner", "-loglevel", "error", "-nostdin", "-y", .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var errors = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, $"FFmpeg failed: {errors}");
    }
}
