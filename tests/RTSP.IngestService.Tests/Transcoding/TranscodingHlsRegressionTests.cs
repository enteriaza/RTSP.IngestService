// Real-FFmpeg regression of the transcoder output path: fixture FLV files are published through the transcoding stage
// with the real FFmpeg process factory into the real media and HLS stages on a fake clock. Each transcoder's FLV
// output is teed, and the media messages that reach the media stage must be exactly that output in FFmpeg's order,
// with the session timeline mapping (offset removed, process base added, sequence headers clamped to the base) and
// overlap rule applied: the same messages, in the same order, the pre-multiplexer reader delivered. Setting
// RTSP_TRANSCODING_HLS_DUMP to a directory writes each scenario's playlist and segments there, so HLS output can be
// compared byte for byte across builds. The tests need FFmpeg and skip without it unless RTSP_FFMPEG_REQUIRED=true.

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Hls;
using RTSP.IngestService.Tests.Media;
using RTSP.IngestService.Transcoding;
using FlvTag = RTSP.IngestService.Tests.Media.FlvTag;

namespace RTSP.IngestService.Tests.Transcoding;

/// <summary>Real-FFmpeg regression of the messages and HLS output produced from transcoder output.</summary>
public sealed class TranscodingHlsRegressionTests
{
    /// <summary>The environment variable naming the dump directory.</summary>
    private const string DumpVariable = "RTSP_TRANSCODING_HLS_DUMP";

    /// <summary>Gets the scenarios: a name and the fixtures published one after another.</summary>
    public static TheoryData<string, string[]> Scenarios => new()
    {
        { "h264-aac", ["h264-aac.flv"] },
        { "h264-aac-4s", ["h264-aac-4s.flv"] },
        { "hevc", ["hevc.flv"] },
        { "hevc-pq", ["hevc-pq.flv"] },
        { "av1", ["av1.flv"] },
        { "vp9", ["vp9.flv"] },
        { "opus", ["opus.flv"] },
        { "flac", ["flac.flv"] },
        { "mp3", ["mp3.flv"] },
        { "ac3", ["ac3.flv"] },
        { "eac3", ["eac3.flv"] },
        { "restart", ["h264-aac-4s.flv", "hevc.flv"] },
        { "long", ["h264-aac-4s.flv", "h264-aac-4s.flv", "h264-aac-4s.flv"] },
    };

    /// <summary>
    /// The media stage receives exactly the transcoders' output, in order, on the session timeline, and the HLS
    /// presentation is complete.
    /// </summary>
    /// <param name="name">The scenario.</param>
    /// <param name="fixtures">The fixtures, published back to back on one timeline.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task TranscoderOutput_ReachesTheMediaStageVerbatim(string name, string[] fixtures)
    {
        var ffmpeg = HlsFfmpegInteropTests.Tools().Ffmpeg;
        var hlsOptions = new HlsOptions { TargetSegmentDuration = TimeSpan.FromSeconds(2), MaxSegmentDuration = TimeSpan.FromSeconds(4) };
        using var hls = new HlsHarness(hlsOptions);
        var recorder = new RecordingStage(hls.Processor);
        var options = new TranscodingOptions { FfmpegPath = ffmpeg, OverloadPolicy = TranscodingOverloadPolicy.Backpressure };
        var factory = new TeeProcessFactory(new FfmpegProcessFactory(Microsoft.Extensions.Options.Options.Create(options)));
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        using var loggerFactory = new LoggerFactory([new FakeLoggerProvider(new FakeLogCollector())]);
        var handler = new TranscodingPublishSessionHandler(
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Options.Options.Create(new MediaOptions()),
            Microsoft.Extensions.Options.Options.Create(hlsOptions),
            recorder,
            factory,
            new TranscodingMetrics(services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()),
            loggerFactory.CreateLogger<TranscodingPublishSessionHandler>());

        var session = new PublisherSession(1, null, "live", "camera1", "live", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        await handler.OnSessionStartedAsync(session, TestContext.Current.CancellationToken);
        uint shift = 0;
        foreach (var fixture in fixtures)
        {
            var tags = FlvFile.ReadFixture(fixture);
            foreach (var tag in tags)
            {
                await handler.OnMessageAsync(session, Message(tag, shift, hls.Pool), TestContext.Current.CancellationToken);
            }

            shift += tags.Max(static t => t.Timestamp) + 40;
        }

        session.TryEnd(PublishEndReason.Unpublished, DateTimeOffset.UnixEpoch);
        await handler.OnSessionEndedAsync(session, TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Null(handler.FindSession(session.SessionId));
        Assert.Equal(0, handler.BufferedBytes);
        Assert.Equal(0, hls.Pool.Outstanding);
        Assert.NotEmpty(factory.Outputs);

        var delivered = recorder.Messages.Where(static m => m.Kind != PublishedMessageKind.Data).ToList();
        var expected = Expected(factory.Outputs.Select(static o => o.ToArray()).ToList(), delivered);
        Assert.Equal(expected.Count, delivered.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.True(expected[i].Kind == delivered[i].Kind && expected[i].Timestamp == delivered[i].Timestamp && expected[i].Payload.AsSpan().SequenceEqual(delivered[i].Payload), $"Message {i}: expected {expected[i].Kind} at {expected[i].Timestamp}, delivered {delivered[i].Kind} at {delivered[i].Timestamp}.");
        }

        var playlist = hls.Playlist();
        Assert.NotNull(playlist);
        Assert.True(playlist.Ended);
        Assert.NotEmpty(playlist.Segments);
        Dump(name, hls, playlist, recorder.Messages);
    }

    /// <summary>Builds a pooled message from a fixture tag.</summary>
    /// <param name="tag">The tag.</param>
    /// <param name="shift">Added to its timestamp.</param>
    /// <param name="pool">The pool.</param>
    /// <returns>The message.</returns>
    private static PublishedMessage Message(FlvTag tag, uint shift, ArrayPool<byte> pool)
    {
        var buffer = new PooledBuffer(pool);
        buffer.EnsureCapacity(Math.Max(tag.Body.Length, 1));
        buffer.Append(new ReadOnlySequence<byte>(tag.Body));
        var kind = TranscodingHarness.Kind(tag);
        return new PublishedMessage(kind, tag.Type, tag.Timestamp + shift, buffer);
    }

    /// <summary>
    /// Maps the teed transcoder output to the messages the pre-multiplexer reader delivered: audio and video tags in
    /// output order, at the output timestamp minus <see cref="FfmpegArguments.TimestampOffset"/> plus the process base
    /// (sequence headers clamped to the base), dropping samples that do not advance their track's timeline.
    /// </summary>
    /// <param name="outputs">Each process's FLV output, in start order.</param>
    /// <param name="delivered">The delivered media messages, which give each process's base: its first message is a sequence header at the base.</param>
    /// <returns>The expected messages.</returns>
    private static List<Delivered> Expected(List<byte[]> outputs, List<Delivered> delivered)
    {
        var expected = new List<Delivered>();
        long? lastVideo = null;
        long? lastAudio = null;
        foreach (var output in outputs)
        {
            var tags = ReadFlv(output).Where(static t => t.Type is 8 or 9).ToList();
            Assert.NotEmpty(tags);
            Assert.True(tags[0].Body.Length >= 2 && tags[0].Body[1] == 0, "Each transcoder output starts with a sequence header.");
            Assert.True(expected.Count < delivered.Count, "A transcoder's output was not delivered.");
            long baseTimestamp = delivered[expected.Count].Timestamp;
            foreach (var tag in tags)
            {
                var video = tag.Type == 9;
                var kind = video ? PublishedMessageKind.Video : PublishedMessageKind.Audio;
                var timestamp = (long)tag.Timestamp - FfmpegArguments.TimestampOffset + baseTimestamp;
                if (tag.Body.Length < 2 || tag.Body[1] != 1)
                {
                    expected.Add(new Delivered(kind, (uint)Math.Max(timestamp, baseTimestamp), tag.Body));
                    continue;
                }

                var last = video ? lastVideo : lastAudio;
                if (last is { } previous && (video ? timestamp <= previous : timestamp < previous))
                {
                    continue;
                }

                if (video)
                {
                    lastVideo = timestamp;
                }
                else
                {
                    lastAudio = timestamp;
                }

                expected.Add(new Delivered(kind, (uint)timestamp, tag.Body));
            }
        }

        return expected;
    }

    /// <summary>Parses an FLV file.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>Its tags.</returns>
    private static List<FlvTag> ReadFlv(byte[] data)
    {
        Assert.True(data.Length >= 13 && data[0] == (byte)'F' && data[1] == (byte)'L' && data[2] == (byte)'V', "The transcoder output is not FLV.");
        var tags = new List<FlvTag>();
        var position = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(5)) + 4;
        while (position + 11 <= data.Length)
        {
            var type = (byte)(data[position] & 0x1F);
            var size = (data[position + 1] << 16) | (data[position + 2] << 8) | data[position + 3];
            var timestamp = (uint)((data[position + 7] << 24) | (data[position + 4] << 16) | (data[position + 5] << 8) | data[position + 6]);
            Assert.True(position + 11 + size + 4 <= data.Length, "Truncated FLV tag.");
            tags.Add(new FlvTag(type, timestamp, data[(position + 11)..(position + 11 + size)]));
            position += 11 + size + 4;
        }

        Assert.Equal(data.Length, position);
        return tags;
    }

    /// <summary>Writes the playlist, segments and delivered message digest when <see cref="DumpVariable"/> is set.</summary>
    /// <param name="name">The scenario.</param>
    /// <param name="hls">The harness.</param>
    /// <param name="playlist">The playlist.</param>
    /// <param name="messages">Every delivered message.</param>
    private static void Dump(string name, HlsHarness hls, HlsPlaylistSnapshot playlist, IReadOnlyList<Delivered> messages)
    {
        var root = Environment.GetEnvironmentVariable(DumpVariable);
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "playlist.m3u8"), playlist.Text.Replace(hls.Store.Generation, "GENERATION", StringComparison.Ordinal));
        var names = playlist.Segments.Select(static s => s.InitializationName).Distinct(StringComparer.Ordinal).Concat(playlist.Segments.Select(static s => s.Name));
        foreach (var segment in names)
        {
            File.WriteAllBytes(Path.Combine(directory, segment.Replace('/', '_')), hls.Segment(segment));
        }

        var digest = new StringBuilder();
        foreach (var message in messages)
        {
            digest.Append(message.Kind).Append(' ').Append(message.Timestamp).Append(' ').Append(message.Payload.Length).Append(' ').Append(Convert.ToHexStringLower(SHA256.HashData(message.Payload))).Append('\n');
        }

        File.WriteAllText(Path.Combine(directory, "messages.txt"), digest.ToString());
    }

    /// <summary>A delivered message.</summary>
    /// <param name="Kind">Its kind.</param>
    /// <param name="Timestamp">Its timestamp.</param>
    /// <param name="Payload">A copy of its payload.</param>
    private sealed record Delivered(PublishedMessageKind Kind, uint Timestamp, byte[] Payload);

    /// <summary>Records every message and forwards it to the media stage.</summary>
    /// <param name="inner">The media stage.</param>
    private sealed class RecordingStage(IPublishSessionHandler inner) : IPublishSessionHandler
    {
        /// <summary>Gets the messages, in delivery order.</summary>
        public List<Delivered> Messages { get; } = [];

        /// <inheritdoc />
        public ValueTask OnSessionStartedAsync(PublisherSession session, CancellationToken cancellationToken) => inner.OnSessionStartedAsync(session, cancellationToken);

        /// <inheritdoc />
        public ValueTask OnMessageAsync(PublisherSession session, PublishedMessage message, CancellationToken cancellationToken)
        {
            lock (Messages)
            {
                Messages.Add(new Delivered(message.Kind, message.Timestamp, message.Payload.ToArray()));
            }

            return inner.OnMessageAsync(session, message, cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask OnSessionEndedAsync(PublisherSession session, CancellationToken cancellationToken) => inner.OnSessionEndedAsync(session, cancellationToken);
    }

    /// <summary>Starts processes whose output is copied as it is read.</summary>
    /// <param name="inner">The real factory.</param>
    private sealed class TeeProcessFactory(ITranscoderProcessFactory inner) : ITranscoderProcessFactory
    {
        /// <summary>Gets each process's output copy, in start order.</summary>
        public ConcurrentQueue<MemoryStream> Outputs { get; } = new();

        /// <inheritdoc />
        public ITranscoderProcess Start(IReadOnlyList<string> arguments)
        {
            var copy = new MemoryStream();
            Outputs.Enqueue(copy);
            return new TeeProcess(inner.Start(arguments), copy);
        }
    }

    /// <summary>A process whose output is copied.</summary>
    /// <param name="inner">The real process.</param>
    /// <param name="copy">Receives the output.</param>
    private sealed class TeeProcess(ITranscoderProcess inner, MemoryStream copy) : ITranscoderProcess
    {
        /// <inheritdoc />
        public Stream Input => inner.Input;

        /// <inheritdoc />
        public Stream Output { get; } = new TeeStream(inner.Output, copy);

        /// <inheritdoc />
        public Task<int> Exited => inner.Exited;

        /// <inheritdoc />
        public string Diagnostics => inner.Diagnostics;

        /// <inheritdoc />
        public void CompleteInput() => inner.CompleteInput();

        /// <inheritdoc />
        public void Kill() => inner.Kill();

        /// <inheritdoc />
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>A read-only stream that copies what is read.</summary>
    /// <param name="inner">The source.</param>
    /// <param name="copy">Receives the bytes.</param>
    private sealed class TeeStream(Stream inner, MemoryStream copy) : Stream
    {
        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        /// <inheritdoc />
        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            copy.Write(buffer[..read]);
            return read;
        }

        /// <inheritdoc />
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            copy.Write(buffer.Span[..read]);
            return read;
        }

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
