// Real-FFmpeg integration of the managed AAC-LC audio engine: fixture FLV files are published through the transcoding
// stage with Transcoding:Audio:Engine = ManagedAacLc, the real FFmpeg process factory for video and the real media and
// HLS stages. The HLS presentation must be AAC-LC 48 kHz stereo from the managed encoder, with a contiguous timeline
// aligned to the first IDR, read by ffprobe and decoded by ffmpeg -xerror; FFmpeg must get video only, an audio-only
// publish must start no process, and an ineligible source must stay on FFmpeg audio. The tests need FFmpeg and skip
// without it unless RTSP_FFMPEG_REQUIRED=true.

using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Hls;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Tests.Media;
using RTSP.IngestService.Transcoding;
using FlvTag = RTSP.IngestService.Tests.Media.FlvTag;

namespace RTSP.IngestService.Tests.Transcoding;

/// <summary>Real-FFmpeg HLS integration of managed AAC-LC audio.</summary>
public sealed class ManagedAudioHlsTests
{
    /// <summary>The publish time after which a paced publish waits for FFmpeg's first video.</summary>
    private const uint PacedAfterMilliseconds = 4000;

    /// <summary>The FFmpeg argument that maps source audio.</summary>
    private const string AudioMap = "0:a:0";

    /// <summary>
    /// Managed audio and FFmpeg video package into a valid HLS presentation: one video-only FFmpeg process, AAC-LC
    /// 48 kHz stereo audio on a contiguous timeline that starts at the first IDR, and a clean full decode.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task ManagedAudio_AndFfmpegVideo_PackageIntoValidHls()
    {
        using var run = new Run();
        var session = await run.StartAsync();
        await run.PublishAsync(session, FlvFile.ReadFixture("h264-aac-4s.flv"));
        await run.EndAsync(session);

        Assert.Equal(TranscodingAudioEngine.ManagedAacLc, run.Engine);
        var arguments = Assert.Single(run.Factory.Arguments);
        Assert.DoesNotContain(AudioMap, arguments);
        var playlist = await run.AssertValidAsync(video: true);
        AssertAlignedToTheFirstIdr(run.Hls.Store, playlist);
    }

    /// <summary>Two fixtures published back to back on one timeline continue the managed audio stream across FFmpeg's restart.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task LongPublish_StaysValid()
    {
        using var run = new Run();
        var session = await run.StartAsync();
        await run.PublishAsync(session, FlvFile.ReadFixture("h264-aac-4s.flv"), FlvFile.ReadFixture("h264-aac-4s.flv"));
        await run.EndAsync(session);

        Assert.All(run.Factory.Arguments, static a => Assert.DoesNotContain(AudioMap, a));
        await run.AssertValidAsync(video: true);
    }

    /// <summary>
    /// A killed publish ends without flushing the encoder: the live presentation packaged before the kill decodes
    /// cleanly (read as ended, so ffmpeg does not poll it as live), the HLS stage withdraws the stream as it does for any abnormal end, and every buffer is returned.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Kill_EndsWithoutFlushing()
    {
        using var run = new Run();
        var session = await run.StartAsync();
        var tags = FlvFile.ReadFixture("h264-aac-4s.flv");
        await run.PublishAsync(session, tags, tags);
        await Eventually.WaitUntilAsync(() => run.Hls.Playlist() is { Segments.Count: > 0 }, "a segment is packaged");
        using (var directory = new HlsFfmpegInteropTests.TemporaryDirectory())
        {
            var live = run.Hls.Playlist()!;
            Assert.False(live.Ended);
            var path = directory.Write(run.Hls.Store, "live", "camera1", live);
            File.AppendAllText(path, "#EXT-X-ENDLIST\n");
            await HlsFfmpegInteropTests.DecodeAsync(run.Tools.Ffmpeg, path);
        }

        await run.EndAsync(session, PublishEndReason.ConnectionError);

        Assert.Null(run.Hls.Playlist());
        Assert.Contains(10031, run.EventIds());
        run.AssertReleased();
    }

    /// <summary>A replacement publish of the same stream gets a new managed stage and a new video-only process, and stays valid.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Replacement_StartsANewStream()
    {
        using var run = new Run();
        var first = await run.StartAsync();
        await run.PublishAsync(first, FlvFile.ReadFixture("h264-aac.flv"));
        await run.EndAsync(first);
        var second = await run.StartAsync();
        await run.PublishAsync(second, FlvFile.ReadFixture("h264-aac-4s.flv"));
        await run.EndAsync(second);

        Assert.Equal(2, run.Factory.Arguments.Count);
        Assert.All(run.Factory.Arguments, static a => Assert.DoesNotContain(AudioMap, a));
        Assert.Equal(2, run.EventIds().Count(static id => id == 10031));
        await run.AssertValidAsync(video: true);
    }

    /// <summary>An audio-only AAC-LC publish starts no FFmpeg process and drains into a valid audio-only presentation.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task AudioOnly_StartsNoProcess()
    {
        using var run = new Run();
        var session = await run.StartAsync();
        await run.PublishAsync(session, [.. FlvFile.ReadFixture("h264-aac-4s.flv").Where(static t => t.Type == 8)]);
        await run.EndAsync(session);

        Assert.Empty(run.Factory.Arguments);
        await run.AssertValidAsync(video: false);
    }

    /// <summary>An ineligible source (MP3) stays on FFmpeg audio under the managed engine and still packages into valid HLS.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task UnsupportedAudio_FallsBackToFfmpeg()
    {
        using var run = new Run();
        var session = await run.StartAsync();
        await run.PublishAsync(session, FlvFile.ReadFixture("mp3.flv"));
        await run.EndAsync(session);

        var arguments = Assert.Single(run.Factory.Arguments);
        Assert.Contains(AudioMap, arguments);
        Assert.Contains(10025, run.EventIds());
        Assert.DoesNotContain(10031, run.EventIds());
        await run.AssertValidAsync(video: false);
    }

    /// <summary>
    /// When managed audio runs more than the multiplexer's 5 s retention window ahead of FFmpeg's first video
    /// keyframe, the multiplexer releases the audio rather than block the publisher: HLS starts an audio-only period
    /// and, at the first IDR, a new audio and video period with its own initialization segment after a
    /// discontinuity. Nothing is lost or leaked, and the stream ends normally.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task AudioFarAheadOfVideo_StartsAudioOnly_ThenAddsVideo()
    {
        using var run = new Run(outputDelay: TimeSpan.FromSeconds(3));
        var session = await run.StartAsync();
        var tags = FlvFile.ReadFixture("h264-aac-4s.flv");
        await run.PublishAsync(session, paced: false, tags, tags);
        await run.EndAsync(session);

        run.AssertReleased();
        var playlist = run.Hls.Playlist();
        Assert.NotNull(playlist);
        Assert.True(playlist.Ended);
        var initializations = playlist.Segments.Select(static s => s.InitializationName).Distinct(StringComparer.Ordinal).ToList();
        Assert.Equal(2, initializations.Count);
        var first = playlist.Segments.First(s => s.InitializationName == initializations[1]);
        Assert.True(first.Discontinuity);
        var audioOnly = Mp4Parser.Parse(run.Hls.Segment(initializations[0])).All("moov/trak").Count();
        var both = Mp4Parser.Parse(run.Hls.Segment(initializations[1])).All("moov/trak").Count();
        Assert.Equal((1, 2), (audioOnly, both));
    }

    /// <summary>Asserts the first audio sample starts within one AAC frame at or after the first video presentation time.</summary>    /// <param name="store">The store.</param>
    /// <param name="playlist">The playlist.</param>
    private static void AssertAlignedToTheFirstIdr(InMemoryHlsSegmentStore store, HlsPlaylistSnapshot playlist)
    {
        long? video = null;
        long? audio = null;
        foreach (var segment in playlist.Segments)
        {
            foreach (var run in Mp4Fragments.Read(store.ReadSegment("live", "camera1", segment.Name)!).Runs)
            {
                var time = (long)run.BaseDecodeTime;
                foreach (var sample in run.Samples)
                {
                    if (run.TrackId == 1)
                    {
                        var pts = time + (sample.CompositionOffset ?? 0);
                        video = Math.Min(video ?? pts, pts);
                    }
                    else
                    {
                        audio ??= time;
                    }

                    time += sample.Duration;
                }
            }
        }

        Assert.NotNull(video);
        Assert.NotNull(audio);
        var offset = TimeSpan.FromTicks((audio.Value * TimeSpan.TicksPerSecond / 48_000) - (video.Value * TimeSpan.TicksPerSecond / 90_000));
        Assert.InRange(offset, TimeSpan.Zero, TimeSpan.FromMilliseconds((1024 * 1000 / 48_000) + 1));
    }

    /// <summary>The transcoding stage with the managed engine, real FFmpeg for video, and the real media and HLS stages.</summary>
    private sealed class Run : IDisposable
    {
        /// <summary>Owns the meter factory.</summary>
        private readonly ServiceProvider _services;

        /// <summary>The logger factory.</summary>
        private readonly LoggerFactory _loggerFactory;

        /// <summary>The transcoding options.</summary>
        private readonly TranscodingOptions _options;

        /// <summary>Initializes a new instance of the <see cref="Run"/> class.</summary>
        /// <param name="outputDelay">How long each FFmpeg process's output is held back before it is first read.</param>
        public Run(TimeSpan outputDelay = default)
        {
            Tools = HlsFfmpegInteropTests.Tools();
            var hlsOptions = new HlsOptions { TargetSegmentDuration = TimeSpan.FromSeconds(2), MaxSegmentDuration = TimeSpan.FromSeconds(4) };
            Hls = new HlsHarness(hlsOptions);
            Downstream = new CountingStage(Hls.Processor);
            _options = new TranscodingOptions { FfmpegPath = Tools.Ffmpeg, OverloadPolicy = TranscodingOverloadPolicy.Backpressure };
            _options.Audio.Engine = TranscodingAudioEngine.ManagedAacLc;
            Factory = new RecordingProcessFactory(new FfmpegProcessFactory(Microsoft.Extensions.Options.Options.Create(_options)), outputDelay);
            _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
            _loggerFactory = new LoggerFactory([new FakeLoggerProvider(Logs)]);
            Handler = new TranscodingPublishSessionHandler(
                Microsoft.Extensions.Options.Options.Create(_options),
                Microsoft.Extensions.Options.Options.Create(new MediaOptions()),
                Microsoft.Extensions.Options.Options.Create(hlsOptions),
                Downstream,
                Factory,
                new TranscodingMetrics(_services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()),
                _loggerFactory.CreateLogger<TranscodingPublishSessionHandler>());
        }

        /// <summary>Gets ffmpeg and ffprobe.</summary>
        public (string Ffmpeg, string Probe) Tools { get; }

        /// <summary>Gets the media and HLS stages.</summary>
        public HlsHarness Hls { get; }

        /// <summary>Gets the media stage as the transcoding stage sees it.</summary>
        public CountingStage Downstream { get; }

        /// <summary>Gets the process factory.</summary>
        public RecordingProcessFactory Factory { get; }

        /// <summary>Gets the transcoding stage.</summary>
        public TranscodingPublishSessionHandler Handler { get; }

        /// <summary>Gets the transcoding logs.</summary>
        public FakeLogCollector Logs { get; } = new();

        /// <summary>Gets the configured audio engine.</summary>
        public TranscodingAudioEngine Engine => _options.Audio.Engine;

        /// <summary>Starts a publish of <c>live/camera1</c>.</summary>
        /// <returns>The session.</returns>
        public async Task<PublisherSession> StartAsync()
        {
            var session = new PublisherSession(1, null, "live", "camera1", "live", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            await Handler.OnSessionStartedAsync(session, TestContext.Current.CancellationToken);
            return session;
        }

        /// <summary>
        /// Publishes fixtures back to back on one timeline. Like a live source, nothing after
        /// <see cref="PacedAfterMilliseconds"/> is sent until FFmpeg's first video reached the media stage: the
        /// multiplexer holds managed audio for the first video keyframe only within its 5 s retention window, so a
        /// publish far faster than real time would otherwise start HLS audio-only.
        /// </summary>
        /// <param name="session">The session.</param>
        /// <param name="fixtures">The fixtures' tags.</param>
        /// <returns>A task.</returns>
        public Task PublishAsync(PublisherSession session, params IReadOnlyList<FlvTag>[] fixtures) => PublishAsync(session, paced: true, fixtures);

        /// <summary>Publishes fixtures back to back on one timeline, optionally paced as <see cref="PublishAsync(PublisherSession, IReadOnlyList{FlvTag}[])"/> is.</summary>
        /// <param name="session">The session.</param>
        /// <param name="paced">Whether to wait for FFmpeg's first video.</param>
        /// <param name="fixtures">The fixtures' tags.</param>
        /// <returns>A task.</returns>
        public async Task PublishAsync(PublisherSession session, bool paced, params IReadOnlyList<FlvTag>[] fixtures)
        {
            var video = Downstream.VideoMessages;
            var waited = !paced || !fixtures.Any(static f => f.Any(static t => t.Type == 9));
            uint shift = 0;
            foreach (var tags in fixtures)
            {
                foreach (var tag in tags)
                {
                    if (!waited && tag.Timestamp + shift >= PacedAfterMilliseconds)
                    {
                        await Eventually.WaitUntilAsync(() => Downstream.VideoMessages > video, "FFmpeg's first video reaches the media stage");
                        waited = true;
                    }

                    var buffer = new PooledBuffer(Hls.Pool);
                    buffer.EnsureCapacity(Math.Max(tag.Body.Length, 1));
                    buffer.Append(new ReadOnlySequence<byte>(tag.Body));
                    await Handler.OnMessageAsync(session, new PublishedMessage(TranscodingHarness.Kind(tag), tag.Type, tag.Timestamp + shift, buffer), TestContext.Current.CancellationToken);
                }

                shift += tags.Max(static t => t.Timestamp) + 40;
            }
        }

        /// <summary>Ends a publish and waits for the stage to finish it.</summary>
        /// <param name="session">The session.</param>
        /// <param name="reason">The end reason.</param>
        /// <returns>A task.</returns>
        public async Task EndAsync(PublisherSession session, PublishEndReason reason = PublishEndReason.Unpublished)
        {
            session.TryEnd(reason, DateTimeOffset.UnixEpoch);
            await Handler.OnSessionEndedAsync(session, TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            Assert.Null(Handler.FindSession(session.SessionId));
        }

        /// <summary>Gets the transcoding log event IDs.</summary>
        /// <returns>The IDs.</returns>
        public IReadOnlyList<int> EventIds() => [.. Logs.GetSnapshot().Select(static r => r.Id.Id)];

        /// <summary>Asserts every buffer was returned.</summary>
        public void AssertReleased()
        {
            Assert.Equal(0, Handler.BufferedBytes);
            Assert.Equal(0, Handler.Budget.Used);
            Assert.Equal(0, Hls.Pool.Outstanding);
        }

        /// <summary>
        /// Asserts the ended presentation is AAC-LC 48 kHz stereo (with H.264 video when expected), with packaged
        /// timing ffprobe reads back exactly, and decodes without error.
        /// </summary>
        /// <param name="video">Whether the presentation has video.</param>
        /// <returns>The playlist.</returns>
        public async Task<HlsPlaylistSnapshot> AssertValidAsync(bool video)
        {
            AssertReleased();
            var playlist = Hls.Playlist();
            Assert.NotNull(playlist);
            Assert.True(playlist.Ended);
            Assert.NotEmpty(playlist.Segments);
            using var directory = new HlsFfmpegInteropTests.TemporaryDirectory();
            var path = directory.Write(Hls.Store, "live", "camera1", playlist);
            var probe = await HlsFfmpegInteropTests.ProbeAsync(Tools.Probe, path);
            var audio = probe.Streams.Single(static s => s.CodecType == "audio");
            Assert.Equal(("aac", "48000", 2), (audio.CodecName, audio.SampleRate, audio.Channels));
            Assert.Equal(video ? 1 : 0, probe.Streams.Count(static s => s.CodecType == "video"));
            var profile = await HlsFfmpegInteropTests.RunAsync(Tools.Probe, "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=profile", "-of", "csv=p=0", path);
            Assert.All(profile.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), static p => Assert.Equal("LC", p));
            if (video)
            {
                await HlsFfmpegInteropTests.AssertTimingAsync(Tools.Probe, path, Hls.Store, "camera1", playlist, 48_000);
            }

            await HlsFfmpegInteropTests.DecodeAsync(Tools.Ffmpeg, path);
            return playlist;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Hls.Dispose();
            _loggerFactory.Dispose();
            _services.Dispose();
        }
    }

    /// <summary>Counts video messages and forwards everything to the media stage.</summary>
    /// <param name="inner">The media stage.</param>
    private sealed class CountingStage(IPublishSessionHandler inner) : IPublishSessionHandler
    {
        /// <summary>The video message count.</summary>
        private int _video;

        /// <summary>Gets the number of video messages forwarded.</summary>
        public int VideoMessages => Volatile.Read(ref _video);

        /// <inheritdoc />
        public ValueTask OnSessionStartedAsync(PublisherSession session, CancellationToken cancellationToken) => inner.OnSessionStartedAsync(session, cancellationToken);

        /// <inheritdoc />
        public ValueTask OnMessageAsync(PublisherSession session, PublishedMessage message, CancellationToken cancellationToken)
        {
            if (message.Kind == PublishedMessageKind.Video)
            {
                Interlocked.Increment(ref _video);
            }

            return inner.OnMessageAsync(session, message, cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask OnSessionEndedAsync(PublisherSession session, CancellationToken cancellationToken) => inner.OnSessionEndedAsync(session, cancellationToken);
    }

    /// <summary>Starts real processes, records their arguments and optionally holds their output back.</summary>
    /// <param name="inner">The real factory.</param>
    /// <param name="outputDelay">How long each process's output is held back before it is first read.</param>
    private sealed class RecordingProcessFactory(ITranscoderProcessFactory inner, TimeSpan outputDelay) : ITranscoderProcessFactory
    {
        /// <summary>The argument lists, in start order.</summary>
        private readonly ConcurrentQueue<IReadOnlyList<string>> _arguments = new();

        /// <summary>Gets the argument lists, in start order.</summary>
        public IReadOnlyList<IReadOnlyList<string>> Arguments => [.. _arguments];

        /// <inheritdoc />
        public ITranscoderProcess Start(IReadOnlyList<string> arguments)
        {
            _arguments.Enqueue([.. arguments]);
            var process = inner.Start(arguments);
            return outputDelay > TimeSpan.Zero ? new DelayedProcess(process, outputDelay) : process;
        }
    }

    /// <summary>A process whose output is first readable after a delay.</summary>
    /// <param name="inner">The process.</param>
    /// <param name="delay">The delay.</param>
    private sealed class DelayedProcess(ITranscoderProcess inner, TimeSpan delay) : ITranscoderProcess
    {
        /// <inheritdoc />
        public Stream Input => inner.Input;

        /// <inheritdoc />
        public Stream Output { get; } = new DelayedStream(inner.Output, delay);

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

    /// <summary>A read-only stream whose first read waits for a delay.</summary>
    /// <param name="inner">The stream.</param>
    /// <param name="delay">The delay.</param>
    private sealed class DelayedStream(Stream inner, TimeSpan delay) : Stream
    {
        /// <summary>Completes when reads may proceed.</summary>
        private readonly Task _ready = Task.Delay(delay);

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
        public override int Read(byte[] buffer, int offset, int count)
        {
            _ready.GetAwaiter().GetResult();
            return inner.Read(buffer, offset, count);
        }

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _ready.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

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
    }
}
