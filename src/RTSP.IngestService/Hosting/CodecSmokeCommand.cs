// The "codec-smoke" diagnostic command, run instead of the service when it is the first argument. It transcodes one
// ADTS AAC-LC file through the fully managed audio path (decode, downmix, resample, encode) and writes canonical
// 48 kHz stereo AAC-LC as ADTS, so a Native AOT build can be shown to compile and execute every managed audio
// component and its output can be checked with an independent decoder. It then decodes its own output again with the
// managed decoder, checks sample counts and levels (exit code 1 on a mismatch), and prints representative samples
// and digests for comparing JIT and AOT builds. The "h264" form decodes an H.264 Annex B elementary stream with the
// managed video decoder, writes the frames as raw planar 4:2:0, decodes the stream a second time after a reset, and
// checks frame counts, geometry, determinism and that every frame and pooled buffer is released. It does not build the
// host or read configuration, and it prints only stream properties, counts, levels, sample values and digests.

using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Audio;
using RTSP.IngestService.Media.Codecs.Audio.Aac;
using RTSP.IngestService.Media.Codecs.Audio.Aac.Encoder;
using RTSP.IngestService.Media.Codecs.Audio.Processing;
using RTSP.IngestService.Media.Codecs.Video.H264;

namespace RTSP.IngestService.Hosting;

/// <summary>Runs the managed audio codecs on one file.</summary>
/// <remarks>
/// <code>
/// RTSP.IngestService codec-smoke input.aac output.aac
/// RTSP.IngestService codec-smoke h264 input.h264 output.yuv
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
          RTSP.IngestService codec-smoke h264 <input.h264> <output.yuv>
        Transcodes an ADTS AAC-LC file with the managed decoder, downmixer, resampler and encoder to 48 kHz stereo
        AAC-LC (ADTS), or decodes an H.264 Annex B stream with the managed decoder to raw planar 4:2:0 frames.
        Diagnostic only; the service's transcoding does not use these paths.
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

        if (args.Length == 4 && string.Equals(args[1], "h264", StringComparison.OrdinalIgnoreCase))
        {
            exitCode = RunH264(args[2], args[3], output, error);
            return true;
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
            output.WriteLine($"Encoder: {transcoder.EncoderStatistics.ShortFrames} short-window frames, {transcoder.EncoderStatistics.TnsChannels} TNS channel frames, {transcoder.EncoderStatistics.MidSideBands} mid/side bands, {transcoder.ClippedSamples} clipped samples");
            var failures = Verify(units.Count, transcoder, File.ReadAllBytes(args[2]), output);
            foreach (var failure in failures)
            {
                error.WriteLine($"codec-smoke check failed: {failure}");
            }

            exitCode = failures.Count == 0 ? ExitCodes.Success : ExitCodes.Failure;
            return true;
        }
        catch (Exception exception) when (exception is CodecException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"codec-smoke failed: {exception.Message}");
            exitCode = ExitCodes.Failure;
            return true;
        }
    }

    /// <summary>
    /// Decodes the written output again with the managed decoder and checks the sample counts against the input
    /// (ceil(N * 48000 / rate) canonical samples, ceil(canonical / 1024) + 1 access units of 1024 samples each) and the
    /// level of the decoded output against the canonical input (within 1.5 dB per channel above -60 dBFS). Prints
    /// representative 16-bit samples and FNV-1a digests of the output file and of the decoded 16-bit PCM, which
    /// are identical for JIT and Native AOT builds of the same source.
    /// </summary>
    /// <param name="inputUnits">The input access units.</param>
    /// <param name="transcoder">The finished transcoder.</param>
    /// <param name="file">The written ADTS output.</param>
    /// <param name="output">Standard output.</param>
    /// <returns>The failed checks, empty when all pass.</returns>
    private static List<string> Verify(int inputUnits, ManagedAudioTranscoder transcoder, byte[] file, TextWriter output)
    {
        var failures = new List<string>();
        var units = AdtsFraming.Read(file, out var configuration);
        using var decoder = Media.Codecs.Audio.Aac.Decoder.AacDecoder.Create(configuration);
        var pcm = new List<short>();
        var timestamp = 0L;
        foreach (var unit in units)
        {
            using var frame = decoder.Decode(new EncodedPacket(unit, timestamp, new MediaTimeBase(1, AacEncoder.SampleRate)));
            timestamp += frame.SampleCount;
            var left = new short[frame.SampleCount];
            var right = new short[frame.SampleCount];
            AudioSampleFormat.ToInt16(frame.GetChannel(0), left);
            AudioSampleFormat.ToInt16(frame.GetChannel(frame.ChannelCount - 1), right);
            for (var n = 0; n < left.Length; n++)
            {
                pcm.Add(left[n]);
                pcm.Add(right[n]);
            }
        }

        var canonical = (long)Math.Ceiling((inputUnits * 1024.0 * AacEncoder.SampleRate / transcoder.SourceSampleRate) - 1e-9);
        Check(transcoder.DecodedSamples == inputUnits * 1024L, $"decoded {transcoder.DecodedSamples} samples, expected {inputUnits * 1024L}");
        Check(transcoder.CanonicalSamples == canonical, $"resampled to {transcoder.CanonicalSamples} samples, expected {canonical}");
        Check(units.Count == ((canonical + 1023) / 1024) + 1, $"wrote {units.Count} access units, expected {((canonical + 1023) / 1024) + 1}");
        Check(pcm.Count == units.Count * 1024L * 2, $"decoded the output to {pcm.Count / 2} samples, expected {units.Count * 1024L}");
        Check(decoder.Layout.Length == 2 && decoder.SampleRate == AacEncoder.SampleRate, "the output is not 48 kHz stereo");

        for (var c = 0; c < 2; c++)
        {
            var energy = 0.0;
            for (var n = 0; n < canonical && ((AacEncoder.Delay + n) * 2) + c < pcm.Count; n++)
            {
                var sample = pcm[((AacEncoder.Delay + (int)n) * 2) + c] / 32768.0;
                energy += sample * sample;
            }

            var decodedDb = 10 * Math.Log10((energy / Math.Max(1, canonical)) + 1e-20);
            var canonicalDb = 20 * Math.Log10(transcoder.CanonicalRms(c) + 1e-10);
            output.WriteLine($"Level: channel {c} canonical {canonicalDb:F2} dBFS, decoded output {decodedDb:F2} dBFS");
            if (canonicalDb > -60)
            {
                Check(Math.Abs(decodedDb - canonicalDb) <= 1.5, $"channel {c} decoded level {decodedDb:F2} dBFS differs from {canonicalDb:F2} dBFS");
            }
        }

        var middle = (AacEncoder.Delay + (int)(canonical / 2)) * 2;
        var samples = Enumerable.Range(0, 8).Where(i => middle + i < pcm.Count).Select(i => pcm[middle + i].ToString(System.Globalization.CultureInfo.InvariantCulture));
        output.WriteLine($"Samples: L/R pairs at output sample {middle / 2}: {string.Join(' ', samples)}");
        output.WriteLine($"Digest: output {Fnv1a(file):x16}, decoded PCM {Fnv1a(System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pcm))):x16}");
        return failures;

        void Check(bool condition, string message)
        {
            if (!condition)
            {
                failures.Add(message);
            }
        }
    }

    /// <summary>Decodes an H.264 Annex B file twice and writes the first decode's frames.</summary>
    /// <param name="inputPath">The elementary stream.</param>
    /// <param name="outputPath">Receives the frames as raw planar 4:2:0.</param>
    /// <param name="output">Standard output.</param>
    /// <param name="error">Standard error.</param>
    /// <returns>The exit code.</returns>
    private static int RunH264(string inputPath, string outputPath, TextWriter output, TextWriter error)
    {
        try
        {
            var info = new FileInfo(inputPath);
            if (!info.Exists || info.Length > MaxInputBytes)
            {
                error.WriteLine($"The input must be an existing file of at most {MaxInputBytes} bytes.");
                return ExitCodes.UsageError;
            }

            var units = SplitAccessUnits(File.ReadAllBytes(info.FullName));
            using var decoder = new H264Decoder();
            decoder.Configure(H264DecoderConfiguration.AnnexB);
            var failures = new List<string>();
            H264SmokeResult first;
            using (var file = File.Create(outputPath))
            {
                first = DecodeH264(decoder, units, file, failures);
            }

            decoder.Reset();
            var second = DecodeH264(decoder, units, null, failures);
            Check(first.Digest == second.Digest && first.Frames == second.Frames, "a second decode after Reset differs from the first");
            Check(first.Frames == units.Count - first.Skipped, $"decoded {first.Frames} frames from {units.Count} access units with {first.Skipped} skipped");
            Check(first.Frames == 0 || new FileInfo(outputPath).Length == (long)first.Frames * first.FrameBytes, "the output size is not frames times frame size");
            Check(decoder.OutstandingFrames == 0 && decoder.AllocatedPictures == 0, "frames or pooled pictures were not released");

            output.WriteLine($"Input: H.264 Annex B, {units.Count} access units, {info.Length} bytes");
            output.WriteLine($"Output: {first.Frames} frames, {first.Width}x{first.Height} {first.Format} {first.BitDepth}-bit, {first.Keyframes} keyframes, {first.Skipped} skipped pictures");
            output.WriteLine($"Digest: first frame {first.FirstFrameDigest:x16}, all frames {first.Digest:x16}");
            foreach (var failure in failures)
            {
                error.WriteLine($"codec-smoke check failed: {failure}");
            }

            return failures.Count == 0 ? ExitCodes.Success : ExitCodes.Failure;

            void Check(bool condition, string message)
            {
                if (!condition)
                {
                    failures.Add(message);
                }
            }
        }
        catch (Exception exception) when (exception is CodecException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"codec-smoke failed: {exception.Message}");
            return ExitCodes.Failure;
        }
    }

    /// <summary>Decodes access units and drains, checking each frame and disposing it.</summary>
    /// <param name="decoder">The configured decoder.</param>
    /// <param name="units">The access units.</param>
    /// <param name="file">Receives the frames, or <see langword="null"/>.</param>
    /// <param name="failures">Receives failed checks.</param>
    /// <returns>The summary.</returns>
    private static H264SmokeResult DecodeH264(H264Decoder decoder, List<byte[]> units, Stream? file, List<string> failures)
    {
        var result = new H264SmokeResult { Digest = 14695981039346656037UL };
        var frames = new List<VideoFrame>();
        var skippedBefore = decoder.SkippedPictures;
        for (var i = 0; i <= units.Count; i++)
        {
            if (i < units.Count)
            {
                decoder.Decode(new EncodedPacket(units[i], i, new MediaTimeBase(1, 25)), frames);
            }
            else
            {
                decoder.Drain(frames);
            }

            foreach (var frame in frames)
            {
                using (frame)
                {
                    if (result.Frames == 0)
                    {
                        result.Width = frame.Width;
                        result.Height = frame.Height;
                        result.Format = frame.Format;
                        result.BitDepth = frame.BitDepth;
                        result.FrameBytes = frame.ByteCount;
                        result.FirstIndex = frame.PresentationIndex;
                    }

                    if (frame.PresentationIndex != result.FirstIndex + result.Frames || frame.Format != VideoPixelFormat.Yuv420P8 || frame.BitDepth != 8)
                    {
                        failures.Add($"frame {result.Frames} has presentation index {frame.PresentationIndex} or an unexpected format");
                    }

                    var frameDigest = 14695981039346656037UL;
                    for (var plane = 0; plane < 3; plane++)
                    {
                        var samples = frame.GetPlane(plane);
                        file?.Write(samples);
                        foreach (var value in samples)
                        {
                            frameDigest = (frameDigest ^ value) * 1099511628211UL;
                            result.Digest = (result.Digest ^ value) * 1099511628211UL;
                        }
                    }

                    if (result.Frames == 0)
                    {
                        result.FirstFrameDigest = frameDigest;
                    }

                    result.Keyframes += frame.IsKeyframe ? 1 : 0;
                    result.Frames++;
                }
            }

            frames.Clear();
        }

        result.Skipped = (int)(decoder.SkippedPictures - skippedBefore);
        return result;
    }

    /// <summary>Groups an Annex B stream into access units: a new one starts at an access unit delimiter, SEI, SPS or PPS after a slice, or at a slice with first_mb_in_slice equal to 0.</summary>
    /// <param name="data">The stream.</param>
    /// <returns>The access units, each starting with a start code.</returns>
    private static List<byte[]> SplitAccessUnits(byte[] data)
    {
        var nals = new List<(int Offset, int Length)>();
        H264NalUnits.SplitAnnexB(data, nals, int.MaxValue);
        var result = new List<byte[]>();
        var start = -1;
        var hasSlice = false;
        foreach (var (offset, length) in nals)
        {
            var type = data[offset] & 0x1F;
            var isSlice = type is 1 or 5;
            var firstSlice = isSlice && length > 1 && (data[offset + 1] & 0x80) != 0;
            var codeStart = offset - 3;
            if (hasSlice && (type is 6 or 7 or 8 or 9 || firstSlice))
            {
                result.Add(data[start..codeStart]);
                start = -1;
                hasSlice = false;
            }

            if (start < 0)
            {
                start = codeStart;
            }

            hasSlice |= isSlice;
        }

        if (start >= 0)
        {
            result.Add(data[start..]);
        }

        return result;
    }

    /// <summary>The outcome of one H.264 smoke decode.</summary>
    private sealed class H264SmokeResult
    {
        /// <summary>Gets or sets the frame count.</summary>
        public int Frames { get; set; }

        /// <summary>Gets or sets the keyframe count.</summary>
        public int Keyframes { get; set; }

        /// <summary>Gets or sets the skipped picture count.</summary>
        public int Skipped { get; set; }

        /// <summary>Gets or sets the first frame's width.</summary>
        public int Width { get; set; }

        /// <summary>Gets or sets the first frame's height.</summary>
        public int Height { get; set; }

        /// <summary>Gets or sets the first frame's format.</summary>
        public VideoPixelFormat Format { get; set; }

        /// <summary>Gets or sets the first frame's bit depth.</summary>
        public int BitDepth { get; set; }

        /// <summary>Gets or sets the first frame's presentation index; indexes continue across a reset.</summary>
        public long FirstIndex { get; set; }

        /// <summary>Gets or sets the first frame's size in bytes.</summary>
        public int FrameBytes { get; set; }

        /// <summary>Gets or sets the FNV-1a digest of the first frame.</summary>
        public ulong FirstFrameDigest { get; set; }

        /// <summary>Gets or sets the FNV-1a digest of all frames.</summary>
        public ulong Digest { get; set; }
    }
    /// <summary>The 64-bit FNV-1a hash, a stable digest that needs no cryptographic library.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The hash.</returns>
    private static ulong Fnv1a(ReadOnlySpan<byte> data)
    {
        var hash = 14695981039346656037UL;
        foreach (var value in data)
        {
            hash = (hash ^ value) * 1099511628211UL;
        }

        return hash;
    }
}
