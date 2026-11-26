// The "audio-integration-smoke" diagnostic command, run instead of the service when it is the first argument. It
// publishes one ADTS AAC-LC file as an audio-only RTMP publish through the real transcoding stage with the managed
// AAC-LC engine, so the managed audio stage, the media multiplexer and the session lifecycle run exactly as in the
// service, and a Native AOT build can be shown to execute them. No transcoder process may start: the process factory
// fails the run if asked. The delivered FLV audio is checked (sequence header first and once, unit count, a
// continuous timeline, and every unit decoding with the managed decoder to 48 kHz stereo; exit code 1 on a mismatch),
// and counts and a digest are printed for comparing JIT and AOT builds. It does not build the host or read
// configuration, and it prints only stream properties, counts and digests.

using System.Buffers;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Audio.Aac;
using RTSP.IngestService.Media.Codecs.Audio.Aac.Decoder;
using RTSP.IngestService.Media.Codecs.Audio.Aac.Encoder;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Transcoding;

namespace RTSP.IngestService.Hosting;

/// <summary>Runs the managed audio integration (stage, multiplexer and session) on one file.</summary>
/// <remarks>
/// <code>
/// RTSP.IngestService audio-integration-smoke input.aac
/// </code>
/// </remarks>
public static class AudioIntegrationSmokeCommand
{
    /// <summary>The command name.</summary>
    public const string CommandName = "audio-integration-smoke";

    /// <summary>The largest accepted input file.</summary>
    private const long MaxInputBytes = 64L * 1024 * 1024;

    /// <summary>How long the publish may take to drain.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>Usage text.</summary>
    private const string Usage =
        """
        Usage:
          RTSP.IngestService audio-integration-smoke <input.aac>
        Publishes an ADTS AAC-LC file as audio-only RTMP through the transcoding stage with the managed AAC-LC engine
        and checks the 48 kHz stereo AAC-LC it delivers. Diagnostic only; starts no transcoder process.
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

        if (args.Length != 2)
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
            var failures = RunAsync(units, configuration, output).GetAwaiter().GetResult();
            foreach (var failure in failures)
            {
                error.WriteLine($"audio-integration-smoke check failed: {failure}");
            }

            exitCode = failures.Count == 0 ? ExitCodes.Success : ExitCodes.Failure;
            return true;
        }
        catch (Exception exception) when (exception is CodecException or IOException or UnauthorizedAccessException or TimeoutException or TranscodingFailedException or TranscodingRejectedException)
        {
            error.WriteLine($"audio-integration-smoke failed: {exception.Message}");
            exitCode = ExitCodes.Failure;
            return true;
        }
    }

    /// <summary>Publishes the units and checks what is delivered.</summary>
    /// <param name="units">The source access units.</param>
    /// <param name="configuration">The source AudioSpecificConfig.</param>
    /// <param name="output">Standard output.</param>
    /// <returns>The failed checks, empty when all pass.</returns>
    private static async Task<List<string>> RunAsync(List<ReadOnlyMemory<byte>> units, byte[] configuration, TextWriter output)
    {
        using var source = AacDecoder.Create(configuration);
        var rate = source.SampleRate;
        var options = new TranscodingOptions();
        options.Audio.Engine = TranscodingAudioEngine.ManagedAacLc;
        var factory = new RefusingProcessFactory();
        var downstream = new RecordingStage();
        using var meters = new MeterFactory();
        var handler = new TranscodingPublishSessionHandler(
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Options.Options.Create(new MediaOptions()),
            Microsoft.Extensions.Options.Options.Create(new HlsOptions()),
            downstream,
            factory,
            new TranscodingMetrics(meters),
            NullLogger<TranscodingPublishSessionHandler>.Instance);

        var now = DateTimeOffset.UtcNow;
        var session = new PublisherSession(1, null, "live", "smoke", "live", 1, now, now);
        await handler.OnSessionStartedAsync(session, CancellationToken.None).ConfigureAwait(false);
        await SendAsync(handler, session, 0, [0xAF, 0x00], configuration).ConfigureAwait(false);
        for (var i = 0; i < units.Count; i++)
        {
            await SendAsync(handler, session, (uint)(i * 1024L * 1000 / rate), [0xAF, 0x01], units[i].Span).ConfigureAwait(false);
        }

        session.TryEnd(PublishEndReason.Unpublished, DateTimeOffset.UtcNow);
        await handler.OnSessionEndedAsync(session, CancellationToken.None).AsTask().WaitAsync(Timeout).ConfigureAwait(false);

        var failures = new List<string>();
        var audio = downstream.Audio;
        var samples = audio.Where(static m => m.Payload is [0xAF, 0x01, ..]).ToList();
        var canonical = (long)Math.Ceiling((units.Count * 1024.0 * AacEncoder.SampleRate / rate) - 1e-9);
        var expected = ((canonical + 1023) / 1024) + 1;
        Check(factory.Starts == 0, $"{factory.Starts} transcoder process(es) were requested");
        Check(downstream.Ended, "the downstream publish did not end");
        Check(downstream.Other == 0, $"{downstream.Other} non-audio message(s) were delivered");
        Check(audio.Count > 0 && audio[0].Payload.AsSpan().SequenceEqual(AacSequenceHeader), "the first audio message is not the 48 kHz stereo AAC-LC sequence header");
        Check(audio.Count(static m => m.Payload is [0xAF, 0x00, ..]) == 1, "the sequence header was not delivered exactly once");
        Check(samples.Count == expected, $"delivered {samples.Count} access units, expected {expected}");
        for (var i = 1; i < samples.Count; i++)
        {
            var step = samples[i].Timestamp - samples[i - 1].Timestamp;
            Check(step is 21 or 22, $"access unit {i} is {step} ms after the previous one");
        }

        using var decoder = AacDecoder.Create(AacEncoder.AudioSpecificConfig);
        Check(decoder.SampleRate == AacEncoder.SampleRate && decoder.Layout.Length == 2, "the output is not 48 kHz stereo");
        long decoded = 0;
        var digest = 14695981039346656037UL;
        foreach (var sample in samples)
        {
            using var frame = decoder.Decode(new EncodedPacket(sample.Payload.AsMemory(2), decoded, new MediaTimeBase(1, AacEncoder.SampleRate)));
            decoded += frame.SampleCount;
            foreach (var value in sample.Payload)
            {
                digest = (digest ^ value) * 1099511628211UL;
            }
        }

        Check(decoded == samples.Count * 1024L, $"decoded {decoded} samples, expected {samples.Count * 1024L}");
        Check(handler.BufferedBytes == 0 && handler.Budget.Used == 0, "buffers were not released");
        output.WriteLine($"Input: AAC-LC {rate} Hz, {source.Layout.Length} channel(s), {units.Count} access units");
        output.WriteLine($"Output: AAC-LC {AacEncoder.SampleRate} Hz, 2 channels, {samples.Count} access units, {decoded} decoded samples, {factory.Starts} transcoder processes");
        output.WriteLine($"Timeline: first {(samples.Count > 0 ? (int)samples[0].Timestamp : 0)} ms, last {(samples.Count > 0 ? (int)samples[^1].Timestamp : 0)} ms (RTMP timestamps; the first unit primes the encoder before the anchor)");
        output.WriteLine($"Digest: delivered audio {digest:x16}");
        return failures;

        void Check(bool condition, string message)
        {
            if (!condition)
            {
                failures.Add(message);
            }
        }
    }

    /// <summary>Gets the FLV sequence header of the canonical output.</summary>
    private static ReadOnlySpan<byte> AacSequenceHeader => [0xAF, 0x00, 0x11, 0x90];

    /// <summary>Sends one FLV audio message.</summary>
    /// <param name="handler">The stage.</param>
    /// <param name="session">The session.</param>
    /// <param name="timestamp">The RTMP timestamp.</param>
    /// <param name="prefix">The FLV audio tag header.</param>
    /// <param name="body">The AAC payload.</param>
    /// <returns>A task.</returns>
    private static ValueTask SendAsync(TranscodingPublishSessionHandler handler, PublisherSession session, uint timestamp, byte[] prefix, ReadOnlySpan<byte> body)
    {
        var buffer = new PooledBuffer(ArrayPool<byte>.Shared);
        var payload = new byte[prefix.Length + body.Length];
        prefix.CopyTo(payload, 0);
        body.CopyTo(payload.AsSpan(prefix.Length));
        buffer.EnsureCapacity(payload.Length);
        buffer.Append(new ReadOnlySequence<byte>(payload));
        return handler.OnMessageAsync(session, new PublishedMessage(PublishedMessageKind.Audio, 8, timestamp, buffer), CancellationToken.None);
    }

    /// <summary>A delivered message.</summary>
    /// <param name="Timestamp">Its timestamp.</param>
    /// <param name="Payload">A copy of its payload.</param>
    private sealed record Delivered(uint Timestamp, byte[] Payload);

    /// <summary>Records delivered audio and releases every message.</summary>
    private sealed class RecordingStage : IPublishSessionHandler
    {
        /// <summary>The delivered audio.</summary>
        private readonly List<Delivered> _audio = [];

        /// <summary>The count of other messages.</summary>
        private int _other;

        /// <summary>Whether the publish ended.</summary>
        private int _ended;

        /// <summary>Gets a copy of the delivered audio, in order.</summary>
        public List<Delivered> Audio
        {
            get
            {
                lock (_audio)
                {
                    return [.. _audio];
                }
            }
        }

        /// <summary>Gets the count of non-audio messages delivered.</summary>
        public int Other => Volatile.Read(ref _other);

        /// <summary>Gets a value indicating whether the publish ended.</summary>
        public bool Ended => Volatile.Read(ref _ended) == 1;

        /// <inheritdoc />
        public ValueTask OnSessionStartedAsync(PublisherSession session, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        /// <inheritdoc />
        public ValueTask OnMessageAsync(PublisherSession session, PublishedMessage message, CancellationToken cancellationToken)
        {
            using (message)
            {
                if (message.Kind == PublishedMessageKind.Audio)
                {
                    lock (_audio)
                    {
                        _audio.Add(new Delivered(message.Timestamp, message.Payload.ToArray()));
                    }
                }
                else
                {
                    Interlocked.Increment(ref _other);
                }
            }

            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask OnSessionEndedAsync(PublisherSession session, CancellationToken cancellationToken)
        {
            Volatile.Write(ref _ended, 1);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Counts and refuses process starts: an audio-only managed publish must start none.</summary>
    private sealed class RefusingProcessFactory : ITranscoderProcessFactory
    {
        /// <summary>The start count.</summary>
        private int _starts;

        /// <summary>Gets the number of starts requested.</summary>
        public int Starts => Volatile.Read(ref _starts);

        /// <inheritdoc />
        public ITranscoderProcess Start(IReadOnlyList<string> arguments)
        {
            Interlocked.Increment(ref _starts);
            throw new TranscodingFailedException(TranscodingStage.Process, true, "The smoke run must not start a transcoder process.");
        }
    }

    /// <summary>Creates meters for the stage's metrics.</summary>
    private sealed class MeterFactory : IMeterFactory
    {
        /// <summary>The created meters.</summary>
        private readonly List<Meter> _meters = [];

        /// <inheritdoc />
        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            lock (_meters)
            {
                _meters.Add(meter);
            }

            return meter;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_meters)
            {
                _meters.ForEach(static m => m.Dispose());
                _meters.Clear();
            }
        }
    }
}
