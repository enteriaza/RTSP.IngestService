// The "codec-smoke" diagnostic command, run instead of the service when it is the first argument. It transcodes one
// ADTS AAC-LC file through the fully managed audio path (decode, downmix, resample, encode) and writes canonical
// 48 kHz stereo AAC-LC as ADTS, so a Native AOT build can be shown to compile and execute every managed audio
// component and its output can be checked with an independent decoder. It does not build the host or read
// configuration, and it prints only stream properties and counts.

using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Audio;
using RTSP.IngestService.Media.Codecs.Audio.Aac;
using RTSP.IngestService.Media.Codecs.Audio.Aac.Encoder;

namespace RTSP.IngestService.Hosting;

/// <summary>Runs the managed audio codecs on one file.</summary>
/// <remarks>
/// <code>
/// RTSP.IngestService codec-smoke input.aac output.aac
/// </code>
/// </remarks>
public static class CodecSmokeCommand
{
    /// <summary>The command name.</summary>
    public const string CommandName = "codec-smoke";

    /// <summary>The largest accepted input file.</summary>
    private const long MaxInputBytes = 64L * 1024 * 1024;

    /// <summary>Usage text.</summary>
    private const string Usage =
        """
        Usage:
          RTSP.IngestService codec-smoke <input.aac> <output.aac>
        Transcodes an ADTS AAC-LC file with the managed decoder, downmixer, resampler and encoder to 48 kHz stereo
        AAC-LC (ADTS). Diagnostic only; the service's transcoding does not use this path.
        """;

    /// <summary>Runs the command if <paramref name="args"/> selects it.</summary>
    /// <param name="args">The process arguments.</param>
    /// <param name="output">Standard output.</param>
    /// <param name="error">Standard error.</param>
    /// <param name="exitCode">The exit code when the command ran.</param>
    /// <returns><see langword="true"/> if the first argument is <see cref="CommandName"/> and the command ran.</returns>
    public static bool TryRun(string[] args, TextWriter output, TextWriter error, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        exitCode = ExitCodes.Success;
        if (args.Length == 0 || !string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (args.Length != 3)
        {
            error.WriteLine(Usage);
            exitCode = ExitCodes.UsageError;
            return true;
        }

        try
        {
            var info = new FileInfo(args[1]);
            if (!info.Exists || info.Length > MaxInputBytes)
            {
                error.WriteLine($"The input must be an existing file of at most {MaxInputBytes} bytes.");
                exitCode = ExitCodes.UsageError;
                return true;
            }

            var units = AdtsFraming.Read(File.ReadAllBytes(info.FullName), out var configuration);
            using var transcoder = new ManagedAudioTranscoder(configuration);
            var encoded = new List<AacAccessUnit>();
            var timestamp = 0L;
            foreach (var unit in units)
            {
                transcoder.Transcode(new EncodedPacket(unit, timestamp, new MediaTimeBase(1, transcoder.SourceSampleRate)), encoded);
                timestamp += 1024;
            }

            transcoder.Flush(encoded);
            using (var file = File.Create(args[2]))
            {
                foreach (var unit in encoded)
                {
                    AdtsFraming.Write(file, AacEncoder.AudioSpecificConfig, unit.Data.Span);
                }
            }

            output.WriteLine($"Input: AAC-LC {transcoder.SourceSampleRate} Hz, {transcoder.SourceChannels} channel(s), {units.Count} access units, {transcoder.DecodedSamples} samples");
            output.WriteLine($"Output: AAC-LC {AacEncoder.SampleRate} Hz, 2 channels, {encoded.Count} access units, {transcoder.CanonicalSamples} samples, {transcoder.EncoderStatistics.Bits / 8} bytes");
            output.WriteLine($"Encoder: {transcoder.EncoderStatistics.ShortFrames} short-window frames, {transcoder.EncoderStatistics.MidSideBands} mid/side bands, {transcoder.ClippedSamples} clipped samples");
            return true;
        }
        catch (Exception exception) when (exception is CodecException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"codec-smoke failed: {exception.Message}");
            exitCode = ExitCodes.Failure;
            return true;
        }
    }
}
