// Helpers for tests of the production transcoding path into HLS: a run of the real transcoding, media and HLS stages
// (no process factory exists any more), and an independent reader of the stored presentation that parses each
// initialization and media segment, checks the canonical sample entries, and decodes every video sample with the
// Phase 3 managed H.264 decoder and every audio sample with the managed AAC decoder, so a test verifies the pictures
// and audio actually packaged rather than only the framing. FFmpeg and ffprobe, when present, add an optional oracle.

using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Audio.Aac.Decoder;
using RTSP.IngestService.Media.Codecs.Video.H264;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Hls;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Transcoding;
using FlvTag = RTSP.IngestService.Tests.Media.FlvTag;

namespace RTSP.IngestService.Tests.Transcoding;

/// <summary>One timeline period of a stored HLS presentation (segments sharing an initialization segment), decoded.</summary>
/// <param name="Initialization">The initialization segment name.</param>
/// <param name="Discontinuity">Whether the playlist marks a discontinuity before the period.</param>
/// <param name="AvcRecord">The <c>avcC</c> of the video sample entry, or <see langword="null"/> without video.</param>
/// <param name="Pictures">The decoded pictures in output order, timestamps in milliseconds of the period's timeline.</param>
/// <param name="AudioStarts">The start of each audio sample in milliseconds of the period's timeline.</param>
internal sealed record HlsPeriod(string Initialization, bool Discontinuity, byte[]? AvcRecord, IReadOnlyList<CanonicalPicture> Pictures, IReadOnlyList<double> AudioStarts);

/// <summary>Decodes a stored HLS presentation independently of the packaging code.</summary>
internal static class HlsOutputDecoder
{
    /// <summary>The canonical AudioSpecificConfig: AAC-LC, 48 kHz, stereo.</summary>
    private static readonly byte[] CanonicalAudioConfig = [0x11, 0x90];

    /// <summary>
    /// Decodes every segment of the playlist. Video must be one <c>avc1</c> track at 90 kHz without composition
    /// offsets (the canonical encoder has no B-frames); audio must be one <c>mp4a</c> track of AAC-LC 48 kHz stereo
    /// with 1024-sample frames. Each period is decoded with fresh decoders configured from its own initialization.
    /// </summary>
    /// <param name="playlist">The playlist.</param>
    /// <param name="read">Reads a segment by name.</param>
    /// <returns>The periods in playlist order.</returns>
    public static List<HlsPeriod> Decode(HlsPlaylistSnapshot playlist, Func<string, byte[]> read)
    {
        var periods = new List<HlsPeriod>();
        var index = 0;
        while (index < playlist.Segments.Count)
        {
            var first = playlist.Segments[index];
            var end = index;
            while (end < playlist.Segments.Count && playlist.Segments[end].InitializationName == first.InitializationName && (end == index || !playlist.Segments[end].Discontinuity))
            {
                end++;
            }

            periods.Add(DecodePeriod(first, playlist.Segments.Skip(index).Take(end - index).ToList(), read));
            index = end;
        }

        return periods;
    }

    /// <summary>Decodes one period.</summary>
    /// <param name="first">Its first segment.</param>
    /// <param name="segments">Its segments.</param>
    /// <param name="read">Reads a segment by name.</param>
    /// <returns>The period.</returns>
    private static HlsPeriod DecodePeriod(HlsSegmentEntry first, List<HlsSegmentEntry> segments, Func<string, byte[]> read)
    {
        var init = Mp4Parser.Parse(read(first.InitializationName));
        uint? videoTrack = null;
        uint? audioTrack = null;
        byte[]? avcC = null;
        foreach (var trak in init.All("moov/trak"))
        {
            var id = trak.One("tkhd").U32(12);
            var timescale = trak.One("mdia/mdhd").U32(12);
            var handler = System.Text.Encoding.ASCII.GetString(trak.One("mdia/hdlr").Body, 8, 4);
            if (handler == "vide")
            {
                Assert.Null(videoTrack);
                Assert.Equal(90000u, timescale);
                avcC = trak.One("mdia/minf/stbl/stsd/avc1/avcC").Body;
                videoTrack = id;
            }
            else
            {
                Assert.Equal("soun", handler);
                Assert.Null(audioTrack);
                Assert.Equal(48000u, timescale);
                var esds = trak.One("mdia/minf/stbl/stsd/mp4a/esds").Body;
                Assert.True(esds.AsSpan().IndexOf(CanonicalAudioConfig) >= 0, "The audio sample entry is AAC-LC 48 kHz stereo.");
                audioTrack = id;
            }
        }

        var pictures = new List<CanonicalPicture>();
        var audioStarts = new List<double>();
        var frames = new List<VideoFrame>();
        H264Decoder? video = null;
        AacDecoder? audio = null;
        try
        {
            if (avcC is not null)
            {
                video = new H264Decoder();
                video.Configure(H264DecoderConfiguration.FromAvcC(avcC));
            }

            if (audioTrack is not null)
            {
                audio = AacDecoder.Create(CanonicalAudioConfig);
            }

            foreach (var segment in segments)
            {
                var bytes = read(segment.Name);
                foreach (var run in Mp4Fragments.Read(bytes).Runs)
                {
                    var time = (long)run.BaseDecodeTime;
                    var offset = run.DataOffset;
                    foreach (var sample in run.Samples)
                    {
                        var data = bytes.AsMemory(offset, (int)sample.Size);
                        if (run.TrackId == videoTrack)
                        {
                            Assert.True(sample.CompositionOffset is null or 0, "Canonical video has no composition offsets.");
                            video!.Decode(new EncodedPacket(data, time, H264EncoderTimeBase), frames, TestContext.Current.CancellationToken);
                            Collect(frames, pictures);
                        }
                        else
                        {
                            Assert.Equal(audioTrack, run.TrackId);
                            Assert.Equal(1024u, sample.Duration);
                            audio!.Decode(new EncodedPacket(data, time, new MediaTimeBase(1, 48000))).Dispose();
                            audioStarts.Add(time * 1000.0 / 48000);
                        }

                        time += sample.Duration;
                        offset += (int)sample.Size;
                    }
                }
            }

            if (video is not null)
            {
                video.Drain(frames);
                Collect(frames, pictures);
            }
        }
        finally
        {
            frames.ForEach(static f => f.Dispose());
            video?.Dispose();
            audio?.Dispose();
        }

        return new HlsPeriod(first.InitializationName, first.Discontinuity, avcC, pictures, audioStarts);
    }

    /// <summary>Gets the 90 kHz time base of canonical video.</summary>
    private static MediaTimeBase H264EncoderTimeBase { get; } = new(1, 90000);

    /// <summary>Records and disposes decoded frames, converting 90 kHz timestamps to milliseconds.</summary>
    /// <param name="frames">The frames; cleared.</param>
    /// <param name="pictures">Receives the records.</param>
    private static void Collect(List<VideoFrame> frames, List<CanonicalPicture> pictures)
    {
        var start = pictures.Count;
        ManagedVideoTestSupport.Collect(frames, pictures);
        for (var i = start; i < pictures.Count; i++)
        {
            Assert.Equal(0, pictures[i].Timestamp % 90);
            pictures[i] = pictures[i] with { Timestamp = pictures[i].Timestamp / 90 };
        }
    }
}

/// <summary>The production transcoding stage over the real media and HLS stages, on fixture input.</summary>
internal sealed class ManagedHlsRun : IDisposable
{
    /// <summary>The publish time after which a paced publish waits for the first video to reach the media stage.</summary>
    private const uint PacedAfterMilliseconds = 4000;

    /// <summary>Owns the meter factory.</summary>
    private readonly ServiceProvider _services;

    /// <summary>The logger factory.</summary>
    private readonly LoggerFactory _loggerFactory;

    /// <summary>Initializes a new instance of the <see cref="ManagedHlsRun"/> class.</summary>
    /// <param name="configure">
    /// Changes to the transcoding options, which default to backpressure so nothing is dropped and the longest drain
    /// timeout, so a drain of everything a faster-than-real-time publisher queued completes on a slow (Debug) build.
    /// </param>
    public ManagedHlsRun(Action<TranscodingOptions>? configure = null)
    {
        var hlsOptions = new HlsOptions { TargetSegmentDuration = TimeSpan.FromSeconds(2), MaxSegmentDuration = TimeSpan.FromSeconds(4) };
        Hls = new HlsHarness(hlsOptions);
        Downstream = new RecordingStage(Hls.Processor);
        Options = new TranscodingOptions { OverloadPolicy = TranscodingOverloadPolicy.Backpressure, DrainTimeout = TimeSpan.FromMinutes(1) };
        configure?.Invoke(Options);
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        _loggerFactory = new LoggerFactory([new FakeLoggerProvider(Logs)]);
        Handler = new TranscodingPublishSessionHandler(
            Microsoft.Extensions.Options.Options.Create(Options),
            Microsoft.Extensions.Options.Options.Create(new MediaOptions()),
            Microsoft.Extensions.Options.Options.Create(hlsOptions),
            Downstream,
            new TranscodingMetrics(_services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()),
            _loggerFactory.CreateLogger<TranscodingPublishSessionHandler>());
    }

    /// <summary>Gets the transcoding options.</summary>
    public TranscodingOptions Options { get; }

    /// <summary>Gets the media and HLS stages.</summary>
    public HlsHarness Hls { get; }

    /// <summary>Gets the media stage as the transcoding stage sees it.</summary>
    public RecordingStage Downstream { get; }

    /// <summary>Gets the transcoding stage.</summary>
    public TranscodingPublishSessionHandler Handler { get; }

    /// <summary>Gets the transcoding logs.</summary>
    public FakeLogCollector Logs { get; } = new();

    /// <summary>Starts a publish of <c>live/camera1</c>.</summary>
    /// <returns>The session.</returns>
    public async Task<PublisherSession> StartAsync()
    {
        var session = new PublisherSession(1, null, "live", "camera1", "live", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        await Handler.OnSessionStartedAsync(session, TestContext.Current.CancellationToken);
        return session;
    }

    /// <summary>
    /// Publishes fixtures back to back on one timeline (each shifted past the previous one's last tag by 40 ms). Like
    /// a live source, nothing after 4 s of media is sent until the first canonical video reached the media stage: the
    /// multiplexer holds audio for the first video keyframe only within its retention window, so a publish far faster
    /// than real time would otherwise start HLS audio-only.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="fixtures">The fixtures' tags.</param>
    /// <returns>The tags as sent, shifted onto the publish timeline.</returns>
    public Task<List<FlvTag>> PublishAsync(PublisherSession session, params IReadOnlyList<FlvTag>[] fixtures) => PublishAsync(session, paced: true, fixtures);

    /// <summary>Publishes fixtures back to back on one timeline, optionally paced as the other overload is.</summary>
    /// <param name="session">The session.</param>
    /// <param name="paced">Whether to wait for the first canonical video.</param>
    /// <param name="fixtures">The fixtures' tags.</param>
    /// <returns>The tags as sent, shifted onto the publish timeline.</returns>
    public async Task<List<FlvTag>> PublishAsync(PublisherSession session, bool paced, params IReadOnlyList<FlvTag>[] fixtures)
    {
        var sent = new List<FlvTag>();
        var video = Downstream.Count(PublishedMessageKind.Video);
        var waited = !paced || !fixtures.Any(static f => f.Any(static t => t.Type == 9));
        uint shift = 0;
        foreach (var tags in fixtures)
        {
            foreach (var tag in tags)
            {
                if (!waited && tag.Timestamp + shift >= PacedAfterMilliseconds)
                {
                    await Eventually.WaitUntilAsync(() => Downstream.Count(PublishedMessageKind.Video) > video, "the first canonical video reaches the media stage");
                    waited = true;
                }

                var shifted = tag with { Timestamp = tag.Timestamp + shift };
                sent.Add(shifted);
                var buffer = new PooledBuffer(Hls.Pool);
                buffer.EnsureCapacity(Math.Max(tag.Body.Length, 1));
                buffer.Append(new ReadOnlySequence<byte>(tag.Body));
                await Handler.OnMessageAsync(session, new PublishedMessage(TranscodingHarness.Kind(tag), tag.Type, shifted.Timestamp, buffer), TestContext.Current.CancellationToken);
            }

            shift += tags.Max(static t => t.Timestamp) + 40;
        }

        return sent;
    }

    /// <summary>Ends a publish and waits for the stage to finish it.</summary>
    /// <param name="session">The session.</param>
    /// <param name="reason">The end reason.</param>
    /// <returns>A task.</returns>
    public async Task EndAsync(PublisherSession session, PublishEndReason reason = PublishEndReason.Unpublished)
    {
        session.TryEnd(reason, DateTimeOffset.UnixEpoch);
        await Handler.OnSessionEndedAsync(session, TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);
        Assert.Null(Handler.FindSession(session.SessionId));
    }

    /// <summary>Gets the transcoding log event IDs.</summary>
    /// <returns>The IDs.</returns>
    public IReadOnlyList<int> EventIds() => [.. Logs.GetSnapshot().Select(static r => r.Id.Id)];

    /// <summary>Asserts every buffer was returned.</summary>
    public void AssertReleased()
    {
        Assert.Equal(0, Handler.ActiveSessionCount);
        Assert.Equal(0, Handler.BufferedBytes);
        Assert.Equal(0, Handler.Budget.Used);
        Assert.Equal(0, Hls.Pool.Outstanding);
    }

    /// <summary>Gets the ended presentation, decoded by <see cref="HlsOutputDecoder"/>.</summary>
    /// <returns>The playlist and its periods.</returns>
    public (HlsPlaylistSnapshot Playlist, List<HlsPeriod> Periods) DecodeEnded()
    {
        var playlist = Hls.Playlist();
        Assert.NotNull(playlist);
        Assert.True(playlist.Ended);
        Assert.NotEmpty(playlist.Segments);
        return (playlist, HlsOutputDecoder.Decode(playlist, name => Hls.Segment(name)));
    }

    /// <summary>
    /// When FFmpeg and ffprobe are available, probes the presentation as AAC-LC 48 kHz stereo (and H.264 High when it
    /// has video), checks the packaged timing against ffprobe's reading for single-period video presentations, and
    /// decodes it in full with <c>-xerror</c>. Without FFmpeg this oracle is skipped and the managed decode stands alone.
    /// </summary>
    /// <param name="playlist">The playlist.</param>
    /// <param name="video">Whether the presentation has video.</param>
    /// <param name="audio">Whether the presentation has audio.</param>
    /// <returns>A task.</returns>
    public async Task OracleAsync(HlsPlaylistSnapshot playlist, bool video, bool audio = true)
    {
        if (FfmpegTool.Path is not { } ffmpeg || FfmpegTool.ProbePath is not { } probe)
        {
            Assert.False(FfmpegTool.Required, "FFmpeg (and ffprobe) were not found but RTSP_FFMPEG_REQUIRED is set; the oracle must not be skipped.");
            return;
        }

        using var directory = new HlsFfmpegInteropTests.TemporaryDirectory();
        var path = directory.Write(Hls.Store, "live", "camera1", playlist);
        var result = await HlsFfmpegInteropTests.ProbeAsync(probe, path);
        Assert.Equal(video ? 1 : 0, result.Streams.Count(static s => s.CodecType == "video"));
        Assert.Equal(audio ? 1 : 0, result.Streams.Count(static s => s.CodecType == "audio"));
        if (audio)
        {
            var stream = result.Streams.Single(static s => s.CodecType == "audio");
            Assert.Equal(("aac", "48000", 2), (stream.CodecName, stream.SampleRate, stream.Channels));
            var profile = await HlsFfmpegInteropTests.RunAsync(probe, "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=profile", "-of", "csv=p=0", path);
            Assert.All(profile.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), static p => Assert.Equal("LC", p));
        }

        if (video)
        {
            var profile = await HlsFfmpegInteropTests.RunAsync(probe, "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name,profile,pix_fmt,color_range,color_space,color_transfer,color_primaries", "-of", "csv=p=0", path);
            Assert.All(profile.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), static p => Assert.Equal("h264,High,yuv420p,tv,bt709,bt709,bt709", p));
            if (audio && playlist.Segments.Select(static s => s.InitializationName).Distinct(StringComparer.Ordinal).Count() == 1 && !playlist.Segments.Any(static s => s.Discontinuity))
            {
                await HlsFfmpegInteropTests.AssertTimingAsync(probe, path, Hls.Store, "camera1", playlist, 48_000);
            }
        }

        await HlsFfmpegInteropTests.DecodeAsync(ffmpeg, path);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Hls.Dispose();
        _loggerFactory.Dispose();
        _services.Dispose();
    }

    /// <summary>A delivered message.</summary>
    /// <param name="Kind">Its kind.</param>
    /// <param name="Timestamp">Its timestamp.</param>
    /// <param name="Payload">A copy of its payload.</param>
    internal sealed record Delivered(PublishedMessageKind Kind, uint Timestamp, byte[] Payload);

    /// <summary>Records every message and forwards it to the media stage.</summary>
    /// <param name="inner">The media stage.</param>
    internal sealed class RecordingStage(IPublishSessionHandler inner) : IPublishSessionHandler
    {
        /// <summary>The messages, in delivery order.</summary>
        private readonly List<Delivered> _messages = [];

        /// <summary>Gets a snapshot of the messages, in delivery order.</summary>
        public IReadOnlyList<Delivered> Messages
        {
            get
            {
                lock (_messages)
                {
                    return [.. _messages];
                }
            }
        }

        /// <summary>Counts the delivered messages of a kind.</summary>
        /// <param name="kind">The kind.</param>
        /// <returns>The count.</returns>
        public int Count(PublishedMessageKind kind)
        {
            lock (_messages)
            {
                return _messages.Count(m => m.Kind == kind);
            }
        }

        /// <summary>Gets the delivered messages of a kind as handler events, for <see cref="ManagedVideoTestSupport.Decode"/>.</summary>
        /// <param name="session">The session the messages belong to.</param>
        /// <param name="kind">The kind.</param>
        /// <returns>The events.</returns>
        public IReadOnlyList<HandlerEvent> Events(PublisherSession session, PublishedMessageKind kind) =>
            [.. Messages.Where(m => m.Kind == kind).Select(m => new HandlerEvent("message", session, m.Kind, m.Timestamp, m.Payload))];

        /// <inheritdoc />
        public ValueTask OnSessionStartedAsync(PublisherSession session, CancellationToken cancellationToken) => inner.OnSessionStartedAsync(session, cancellationToken);

        /// <inheritdoc />
        public ValueTask OnMessageAsync(PublisherSession session, PublishedMessage message, CancellationToken cancellationToken)
        {
            lock (_messages)
            {
                _messages.Add(new Delivered(message.Kind, message.Timestamp, message.Payload.ToArray()));
            }

            return inner.OnMessageAsync(session, message, cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask OnSessionEndedAsync(PublisherSession session, CancellationToken cancellationToken) => inner.OnSessionEndedAsync(session, cancellationToken);
    }
}
