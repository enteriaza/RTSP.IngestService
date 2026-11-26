// Per-stream HLS packaging state machine: it turns typed H.264/AAC samples into fragmented MP4 initialization and
// media segments and publishes them to an IHlsSegmentStore. It decides segment boundaries (sync samples, target and
// maximum duration, size), maps Phase 3 timestamps onto each track's timescale, and starts a new timeline period with
// an HLS discontinuity when codec configuration or timestamps change. Calls are sequential; no queues or workers.

using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;

namespace RTSP.IngestService.Hls;

/// <summary>
/// Packages one published stream.
/// </summary>
/// <remarks>
/// <para>
/// Periods: a period is a run of segments sharing one initialization segment and one continuous timeline. It starts at
/// the first video sync sample (IDR) once video is configured, or at the first audio sample of an audio-only stream.
/// It ends when a track's codec configuration changes or appears, or when Phase 3 flags a timestamp regression or
/// jump; the next segment is then marked as a discontinuity. Samples that cannot start a period (video before a sync
/// sample, audio before video starts) are dropped and counted.
/// </para>
/// <para>
/// Timing: sample timestamps and durations are never altered to fit the playlist. A video sample lasts until the
/// next one (or, when the segment closes first, the last step observed within a segment); an AAC sample lasts one
/// frame, or until the next frame after a source gap; an AAC frame that would overlap the previous one is dropped.
/// Each segment starts where the previous one ended on the playlist timeline, so every <c>#EXTINF</c> is the
/// distance between two boundaries and the durations add up to the media timeline. TARGETDURATION + 0.499 s is
/// kept by cutting early: before a video sample whose observed step would carry the segment past it, and on an
/// audio frame that would (a video stall). Time no segment can span (a pause in all tracks), or video that would
/// overlap written video, ends the period with a discontinuity.
/// </para>
/// <para>
/// Ownership: the packager owns every sample it is given. Samples are held, without copying, until their segment is
/// written (one copy into the immutable segment) and then disposed; every other path disposes them immediately. A new
/// segment's creator reference passes to the store when it is published and is released on every other path.
/// </para>
/// <para>Not thread-safe; calls for one stream must be sequential.</para>
/// </remarks>
internal sealed partial class HlsPackager : IDisposable
{
    // Storage failures: StorageFull is transient (space returns as availability deadlines pass), so the segment is
    // dropped and packaging resumes at the next sync sample with a discontinuity. StreamLimitExceeded cannot recover
    // while the stream is live, and PlaylistFull (the entry cap with less than three target durations listed) cannot\n    // be resolved without shortening the live playlist, so after either the next sample call throws\n    // HlsPackagingStoppedException and the publish ends.

    /// <summary>Default video sample duration for the final sample when no step has been observed (1/30 s).</summary>
    private const long DefaultVideoDuration = 3000;

    /// <summary>The settings.</summary>
    private readonly HlsOptions _options;

    /// <summary>The store.</summary>
    private readonly IHlsSegmentStore _store;

    /// <summary>The chunk pool for media segment bytes.</summary>
    private readonly HlsSegmentBufferPool _pool;

    /// <summary>Metrics.</summary>
    private readonly HlsMetrics _metrics;

    /// <summary>Logger.</summary>
    private readonly ILogger _logger;

    /// <summary>Pending video samples of the open segment.</summary>
    private readonly List<PendingVideo> _video = [];

    /// <summary>Pending audio samples of the open segment.</summary>
    private readonly List<PendingAudio> _audio = [];

    /// <summary>Problems already logged at Warning.</summary>
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);

    /// <summary>The current period, or <see langword="null"/> while waiting to start one.</summary>
    private Period? _period;

    /// <summary>The last period, for reusing its initialization segment.</summary>
    private Period? _lastPeriod;

    /// <summary>The latest video configuration announced by media processing.</summary>
    private VideoTrackInfo? _videoInfo;

    /// <summary>The latest audio configuration announced by media processing.</summary>
    private AudioTrackInfo? _audioInfo;

    /// <summary>The last evaluated video configuration and its packaging track.</summary>
    private (AvcDecoderConfiguration Source, Mp4VideoTrack? Track)? _videoTrack;

    /// <summary>The last evaluated audio configuration and its packaging track.</summary>
    private (AacAudioConfiguration Source, Mp4AudioTrack? Track)? _audioTrack;

    /// <summary>Bytes of pending sample data.</summary>
    private long _pendingBytes;

    /// <summary>The next media sequence number.</summary>
    private long _nextSequence;

    /// <summary>Initialization segments created.</summary>
    private int _initializationCount;

    /// <summary>Whether the next published segment starts a discontinuity.</summary>
    private bool _discontinuityNext;

    /// <summary>The media-timeline millisecond that is time zero of every track timescale.</summary>
    private long? _timelineBase;

    /// <summary>The expected start of the next audio sample, in audio ticks.</summary>
    private long? _audioExpected;

    /// <summary>
    /// Where the open segment starts on the period's playlist timeline, in 90 kHz ticks: the previous segment's end,
    /// or the period's first sample. Each <c>#EXTINF</c> is the distance between consecutive boundaries.
    /// </summary>
    private long? _segmentStart;

    /// <summary>The end of the period's last written video sample, in 90 kHz ticks.</summary>
    private long? _videoWrittenEnd;

    /// <summary>The last observed step between video samples (the nominal frame duration), in 90 kHz ticks.</summary>
    private long _lastVideoStep = DefaultVideoDuration;

    /// <summary>The longest segment the playlist accepts (TARGETDURATION + 0.499 s), in 90 kHz ticks.</summary>
    private readonly long _maxSegmentTicks;

    /// <summary>Why the stream can no longer publish a segment, once it cannot.</summary>
    private HlsAppendResult? _stopped;

    /// <summary>Initializes a new instance of the <see cref="HlsPackager"/> class.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="options">The settings.</param>
    /// <param name="store">The store; the stream must already be open.</param>
    /// <param name="numbering">The numbering the store assigned to the stream.</param>
    /// <param name="pool">The chunk pool for media segment bytes.</param>
    /// <param name="metrics">Metrics.</param>
    /// <param name="logger">Logger.</param>
    public HlsPackager(Guid sessionId, HlsOptions options, IHlsSegmentStore store, HlsStreamNumbering numbering, HlsSegmentBufferPool pool, HlsMetrics metrics, ILogger logger)
    {
        SessionId = sessionId;
        _options = options;
        _store = store;
        _pool = pool;
        _metrics = metrics;
        _logger = logger;
        _nextSequence = numbering.FirstSequence;
        _initializationCount = numbering.FirstInitialization;
        _maxSegmentTicks = HlsPlaylist.LongestSegment(TargetDurationSeconds(options)).Ticks * 9 / 1000;
    }

    /// <summary>Gets the session.</summary>
    public Guid SessionId { get; }

    /// <summary>Gets the number of media segments published.</summary>
    public long SegmentCount { get; private set; }

    /// <summary>Gets the bytes of segments published.</summary>
    public long SegmentBytes { get; private set; }

    /// <summary>Gets the number of samples not packaged.</summary>
    public long DroppedSamples { get; private set; }

    /// <summary>Gets the <c>#EXT-X-TARGETDURATION</c> for <paramref name="options"/>: the maximum segment duration in whole seconds, rounded up.</summary>
    /// <param name="options">The settings.</param>
    /// <returns>The target duration.</returns>
    public static int TargetDurationSeconds(HlsOptions options) => (int)Math.Ceiling(options.MaxSegmentDuration.TotalSeconds);

    /// <summary>Records the latest track configurations.</summary>
    /// <param name="info">The stream description.</param>
    public void OnStreamInfo(MediaStreamInfo info)
    {
        _videoInfo = info.Video;
        _audioInfo = info.Audio;
    }

    /// <summary>Adds a video sample, taking ownership of it.</summary>
    /// <param name="sample">The sample.</param>
    /// <exception cref="HlsPackagingStoppedException">The stream can no longer publish a segment.</exception>
    public void AddVideo(EncodedVideoSample sample)
    {
        AddVideoCore(sample);
        ThrowIfExhausted();
    }

    /// <summary>Adds an audio sample, taking ownership of it.</summary>
    /// <param name="sample">The sample.</param>
    /// <exception cref="HlsPackagingStoppedException">The stream can no longer publish a segment.</exception>
    public void AddAudio(EncodedAudioSample sample)
    {
        AddAudioCore(sample);
        ThrowIfExhausted();
    }

    /// <summary>Throws once the stream can no longer publish a segment.</summary>
    private void ThrowIfExhausted()
    {
        switch (_stopped)
        {
            case HlsAppendResult.StreamLimitExceeded:
                throw new HlsPackagingStoppedException(HlsAppendResult.StreamLimitExceeded, $"Session {SessionId} cannot publish another HLS segment: its listed segments fill MaxSessionStorageMegabytes and no segment can be released without breaking HLS availability.");
            case HlsAppendResult.PlaylistFull:
                throw new HlsPackagingStoppedException(HlsAppendResult.PlaylistFull, $"Session {SessionId} cannot publish another HLS segment: its playlist lists {HlsPlaylist.MaxEntries} segments spanning less than three target durations, and removing one would shorten the live playlist below that minimum.");
        }
    }

    /// <summary>Adds a video sample, taking ownership of it.</summary>
    /// <param name="sample">The sample.</param>
    private void AddVideoCore(EncodedVideoSample sample)
    {
        if (_stopped is { } stopped)
        {
            sample.Dispose();
            Drop(video: true, stopped == HlsAppendResult.PlaylistFull ? "playlist_full" : "storage_full");
            return;
        }

        var owned = true;
        try
        {
            var source = sample.Track.Configuration;
            if (VideoTrackFor(source) is null)
            {
                Drop(video: true, "untracked");
                return;
            }

            if (_period is { } current)
            {
                if (current.VideoSource is null || !current.VideoSource.HasRecord(source.Record.Span))
                {
                    EndPeriod(HlsSegmentBoundary.Configuration, "configuration");
                }
                else if (sample.Discontinuity != TimestampDiscontinuity.None)
                {
                    EndPeriod(HlsSegmentBoundary.Discontinuity, "timestamp");
                }
            }

            var sync = sample.ContainsIdr;
            if (sample.IsKeyframe != sync && _logged.Add("keyframe-mismatch"))
            {
                LogKeyframeMismatch(SessionId, sample.IsKeyframe, sync);
            }

            if (_period is null && (!sync || !TryStartPeriod(sample.Timestamps.DecodeTimestamp)))
            {
                Drop(video: true, "before_sync_sample");
                return;
            }

            var period = _period!;
            var dts = checked((sample.Timestamps.DecodeTimestamp - period.BaseMilliseconds) * 90);
            if (period.Video is null || dts < 0)
            {
                Drop(video: true, "before_sync_sample");
                return;
            }

            if (_segmentStart is { } segmentStart)
            {
                if (_videoWrittenEnd is { } written && dts < written)
                {
                    EndPeriod(HlsSegmentBoundary.Discontinuity, "timeline_overlap");
                }
                else if (dts - segmentStart > _maxSegmentTicks)
                {
                    EndPeriod(HlsSegmentBoundary.Discontinuity, "timeline_gap");
                }
                else
                {
                    var observed = _video.Count > 0;
                    var step = observed ? Math.Max(dts - _video[^1].DecodeTime, 0) : _lastVideoStep;
                    var span = dts - segmentStart;
                    if (sync && span >= ToVideoTicks(_options.TargetSegmentDuration))
                    {
                        Cut(HlsSegmentBoundary.Keyframe, dts);
                    }
                    else if (span >= ToVideoTicks(_options.MaxSegmentDuration) || dts + step - segmentStart > _maxSegmentTicks)
                    {
                        LogForcedOnce("duration");
                        Cut(HlsSegmentBoundary.Forced, dts);
                    }

                    // Learned only between samples of one segment, so it is at most TARGETDURATION + 0.499 s and a final sample using it fits.
                    if (observed)
                    {
                        _lastVideoStep = step;
                    }
                }
            }

            if (!EnsurePeriod(sync, sample.Timestamps.DecodeTimestamp))
            {
                Drop(video: true, "before_sync_sample");
                return;
            }

            if (!TryReserve(sample.Length, video: true, dts))
            {
                return;
            }

            _video.Add(new PendingVideo(sample, dts, checked(sample.Timestamps.CompositionTimeOffset * 90), sync));
            owned = false;
            _segmentStart ??= dts;
        }
        finally
        {
            if (owned)
            {
                sample.Dispose();
            }
        }
    }

    /// <summary>Adds an audio sample, taking ownership of it.</summary>
    /// <param name="sample">The sample.</param>
    private void AddAudioCore(EncodedAudioSample sample)
    {
        if (_stopped is { } stopped)
        {
            sample.Dispose();
            Drop(video: false, stopped == HlsAppendResult.PlaylistFull ? "playlist_full" : "storage_full");
            return;
        }

        var owned = true;
        try
        {
            var source = sample.Track.Configuration;
            var track = AudioTrackFor(source);
            if (track is null)
            {
                Drop(video: false, "untracked");
                return;
            }

            if (_period is { } current)
            {
                if (current.AudioSource is null || !current.AudioSource.HasRecord(source.Record.Span))
                {
                    EndPeriod(HlsSegmentBoundary.Configuration, "configuration");
                }
                else if (sample.Discontinuity != TimestampDiscontinuity.None)
                {
                    EndPeriod(HlsSegmentBoundary.Discontinuity, "timestamp");
                }
            }

            if (_period is null
                && ((_videoInfo is { } video && VideoTrackFor(video.Configuration) is not null) || !TryStartPeriod(sample.Timestamps.DecodeTimestamp)))
            {
                Drop(video: false, "before_sync_sample");
                return;
            }

            var period = _period!;
            var actual = checked(((sample.Timestamps.DecodeTimestamp - period.BaseMilliseconds) * track.SampleRate) + 500) / 1000;
            if (period.Audio is null || actual < 0)
            {
                Drop(video: false, "before_sync_sample");
                return;
            }

            if (AudioStart(actual, track) is not { } start)
            {
                Drop(video: false, "timeline_overlap");
                return;
            }

            if (_segmentStart is { } segmentStart)
            {
                var at = ToVideoTicks(start, track);
                var span = at - segmentStart;
                var overrun = ToVideoTicks(start + track.FrameLength, track) - segmentStart > _maxSegmentTicks;
                if (span > _maxSegmentTicks)
                {
                    EndPeriod(HlsSegmentBoundary.Discontinuity, "timeline_gap");
                }
                else if (period.Video is null && span >= ToVideoTicks(_options.TargetSegmentDuration))
                {
                    Cut(HlsSegmentBoundary.Keyframe, null, at);
                }
                else if ((period.Video is null && span >= ToVideoTicks(_options.MaxSegmentDuration)) || overrun)
                {
                    LogForcedOnce("duration");
                    Cut(HlsSegmentBoundary.Forced, null, at);
                }
            }

            if (!EnsurePeriod(!(_videoInfo is { } configured && VideoTrackFor(configured.Configuration) is not null), sample.Timestamps.DecodeTimestamp))
            {
                Drop(video: false, "before_sync_sample");
                return;
            }

            if (_period != period)
            {
                period = _period!;
                track = period.Audio!;
                start = AudioStart(checked(((sample.Timestamps.DecodeTimestamp - period.BaseMilliseconds) * track.SampleRate) + 500) / 1000, track)!.Value;
            }

            if (!TryReserve(sample.Length, video: false, null))
            {
                return;
            }

            _audio.Add(new PendingAudio(sample, start));
            _audioExpected = start + track.FrameLength;
            owned = false;
            _segmentStart ??= ToVideoTicks(start, track);
        }
        finally
        {
            if (owned)
            {
                sample.Dispose();
            }
        }
    }

    /// <summary>Ends packaging: writes the pending samples as a final segment when completed, then closes the stream.</summary>
    /// <param name="completed">Whether the publisher stopped normally; otherwise pending samples are discarded.</param>
    public void Finish(bool completed)
    {
        if (completed)
        {
            Cut(HlsSegmentBoundary.Final, null);
        }
        else
        {
            DropPending("aborted");
        }

        _store.CloseStream(SessionId, completed);
    }

    /// <summary>Disposes every pending sample.</summary>
    public void Dispose() => DropPending("aborted");

    /// <summary>Converts a duration to 90 kHz ticks.</summary>
    /// <param name="duration">The duration.</param>
    /// <returns>The ticks.</returns>
    private static long ToVideoTicks(TimeSpan duration) => duration.Ticks * 9 / 1000;

    /// <summary>Converts a duration to audio ticks.</summary>
    /// <param name="duration">The duration.</param>
    /// <param name="track">The audio track.</param>
    /// <returns>The ticks.</returns>
    private static long ToAudioTicks(TimeSpan duration, Mp4AudioTrack track) => duration.Ticks * track.SampleRate / TimeSpan.TicksPerSecond;

    /// <summary>Converts an absolute audio time to 90 kHz ticks (rounded down; never accumulated).</summary>
    /// <param name="audioTicks">The time in audio ticks.</param>
    /// <param name="track">The audio track.</param>
    /// <returns>The ticks.</returns>
    private static long ToVideoTicks(long audioTicks, Mp4AudioTrack track) => audioTicks * 90_000 / track.SampleRate;

    /// <summary>
    /// Chooses an audio sample's start: the exact continuation of the previous frame when the timestamp agrees
    /// within a quarter frame, otherwise the timestamp itself when later (a gap). A frame timestamped earlier than
    /// that would overlap the previous frame's decoded samples, so it has no valid start.
    /// </summary>
    /// <param name="actual">The sample's timestamp in audio ticks.</param>
    /// <param name="track">The audio track.</param>
    /// <returns>The start in audio ticks, or <see langword="null"/> if the frame would overlap the previous one.</returns>
    private long? AudioStart(long actual, Mp4AudioTrack track)
    {
        if (_audioExpected is not { } expected)
        {
            return actual;
        }

        if (Math.Abs(actual - expected) <= Math.Max(track.FrameLength / 4, track.SampleRate / 500))
        {
            return expected;
        }

        return actual > expected ? actual : null;
    }

    /// <summary>Makes room for a sample within the maximum segment size, closing the open segment early if needed.</summary>
    /// <param name="length">The sample length.</param>
    /// <param name="video">Whether it is a video sample.</param>
    /// <param name="videoDecodeTime">The sample's decode time, when video.</param>
    /// <returns><see langword="false"/> if the sample alone cannot fit in a segment (it is dropped).</returns>
    private bool TryReserve(int length, bool video, long? videoDecodeTime)
    {
        if (EstimateSize(length, video) > _options.MaxSegmentSize && (_video.Count > 0 || _audio.Count > 0))
        {
            LogForcedOnce("size");
            Cut(HlsSegmentBoundary.Forced, videoDecodeTime);
            if (_period is null)
            {
                Drop(video, "before_sync_sample");
                return false;
            }
        }

        if (EstimateSize(length, video) > _options.MaxSegmentSize)
        {
            _metrics.Error(HlsErrorKind.SegmentTooLarge);
            LogProblem("too-large", $"A {length}-byte sample cannot fit in a {_options.MaxSegmentSize}-byte segment; it is dropped and a new period starts.");
            Drop(video, "too_large");
            EndPeriod(HlsSegmentBoundary.Discontinuity, "dropped_media");
            return false;
        }

        _pendingBytes += length;
        _metrics.PendingChanged(length);
        return true;
    }

    /// <summary>Returns the fragment size with one more sample.</summary>
    /// <param name="length">The new sample's length.</param>
    /// <param name="video">Whether it is video.</param>
    /// <returns>The size in bytes.</returns>
    private long EstimateSize(int length, bool video) =>
        8 + 16 + (2 * 64) + (16L * (_video.Count + (video ? 1 : 0))) + (8L * (_audio.Count + (video ? 0 : 1))) + 8 + _pendingBytes + length;

    /// <summary>Starts a period with the current track configurations.</summary>
    /// <param name="firstMilliseconds">The decode timestamp of the sample starting it.</param>
    /// <returns><see langword="false"/> if no track can be packaged or the initialization segment cannot be stored.</returns>
    private bool TryStartPeriod(long firstMilliseconds)
    {
        var videoSource = _videoInfo?.Configuration;
        var audioSource = _audioInfo?.Configuration;
        var video = videoSource is null ? null : VideoTrackFor(videoSource);
        var audio = audioSource is null ? null : AudioTrackFor(audioSource);
        if (video is null && audio is null)
        {
            return false;
        }

        string name;
        if (_lastPeriod is { } last && last.Video == video && last.Audio == audio)
        {
            name = last.InitializationName;
        }
        else
        {
            byte[] bytes;
            try
            {
                bytes = Mp4Segments.WriteInitialization(video, audio, _options.MaxInitializationSegmentSize);
            }
            catch (Mp4SizeException ex)
            {
                _metrics.Error(HlsErrorKind.SegmentTooLarge);
                LogProblem("init-too-large", ex.Message);
                return false;
            }

            name = $"init-{_initializationCount}.mp4";
            var result = Store(new HlsSegment(HlsSegmentKind.Initialization, name, bytes), s => _store.AddInitialization(SessionId, s));
            if (result == HlsAppendResult.StreamLimitExceeded)
            {
                _metrics.Error(HlsErrorKind.StorageFull);
                LogStorageExhausted(SessionId, _nextSequence);
                _stopped = result;
                return false;
            }

            if (result != HlsAppendResult.Published)
            {
                _metrics.Error(HlsErrorKind.StorageFull);
                LogProblem("storage-full", "The initialization segment could not be stored.");
                return false;
            }

            _initializationCount++;
            _metrics.Initialization(bytes.Length);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                LogInitialization(SessionId, name, video?.Width, video?.Height, audio?.SampleRate, audio?.ChannelCount, bytes.Length);
            }
        }

        if (_timelineBase is null || firstMilliseconds < _timelineBase)
        {
            _timelineBase = firstMilliseconds;
        }

        _period = new Period(video is null ? null : videoSource, video, audio is null ? null : audioSource, audio, name, _timelineBase.Value);
        _lastPeriod = _period;
        _audioExpected = null;
        _segmentStart = null;
        _videoWrittenEnd = null;
        return true;
    }

    /// <summary>Closes the open segment and the period; the next segment starts a discontinuity.</summary>
    /// <param name="boundary">Why.</param>
    /// <param name="reason">The discontinuity metric reason.</param>
    private void EndPeriod(HlsSegmentBoundary boundary, string reason)
    {
        Cut(boundary, null);
        if (_period is null)
        {
            return;
        }

        _period = null;
        _discontinuityNext = SegmentCount > 0;
        _metrics.Discontinuity(reason);
        if (_logged.Add("discontinuity-" + reason))
        {
            LogDiscontinuity(SessionId, reason);
        }
        else
        {
            LogRepeatedDiscontinuity(SessionId, reason);
        }
    }

    /// <summary>Writes the pending samples as a media segment and publishes it; pending samples are always released.</summary>
    /// <param name="boundary">Why the segment closes.</param>
    /// <param name="nextVideoDecodeTime">The decode time of the video sample that follows, if known; the segment ends there.</param>
    /// <param name="boundaryTime">
    /// For a cut made by an audio sample, its start in 90 kHz ticks; otherwise the segment ends where its last samples
    /// end (the final video sample lasting the last observed frame step, the final audio sample one frame).
    /// </param>
    private void Cut(HlsSegmentBoundary boundary, long? nextVideoDecodeTime, long? boundaryTime = null)
    {
        if (_period is not { } period || (_video.Count == 0 && _audio.Count == 0) || _segmentStart is not { } segmentStart)
        {
            return;
        }

        try
        {
            var tracks = new List<Mp4TrackFragment>(2);
            long? videoEnd = null;
            long? audioEnd = null;
            if (_video.Count > 0)
            {
                var samples = new Mp4Sample[_video.Count];
                for (var i = 0; i < _video.Count; i++)
                {
                    var next = i + 1 < _video.Count ? _video[i + 1].DecodeTime : nextVideoDecodeTime ?? (_video[i].DecodeTime + _lastVideoStep);
                    var pending = _video[i];
                    samples[i] = new Mp4Sample(checked((uint)(next - pending.DecodeTime)), pending.Sync ? Mp4Sample.SyncFlags : Mp4Sample.NonSyncFlags, pending.CompositionOffset, pending.Sample.Data);
                }

                tracks.Add(new Mp4TrackFragment(Mp4VideoTrack.Id, checked((ulong)_video[0].DecodeTime), true, samples));
                videoEnd = _video[^1].DecodeTime + samples[^1].Duration;
            }

            if (_audio.Count > 0)
            {
                var audio = period.Audio!;
                var samples = new Mp4Sample[_audio.Count];
                for (var i = 0; i < _audio.Count; i++)
                {
                    var next = i + 1 < _audio.Count ? _audio[i + 1].Start : _audio[i].Start + audio.FrameLength;
                    samples[i] = new Mp4Sample(checked((uint)(next - _audio[i].Start)), 0, 0, _audio[i].Sample.Data);
                }

                tracks.Add(new Mp4TrackFragment(Mp4AudioTrack.Id, checked((ulong)_audio[0].Start), false, samples));
                audioEnd = ToVideoTicks(_audio[^1].Start + audio.FrameLength, audio);
            }

            var segmentEnd = nextVideoDecodeTime
                ?? (boundaryTime is { } at ? Math.Max(at, videoEnd ?? at) : Math.Max(videoEnd ?? long.MinValue, audioEnd ?? long.MinValue));
            // Each boundary is converted on its own so the durations add up to the timeline with no accumulated rounding.
            var duration = TimeSpan.FromTicks((segmentEnd * 1000 / 9) - (segmentStart * 1000 / 9));

            var sequence = _nextSequence;
            var name = $"segment-{sequence}.m4s";
            HlsSegment segment;
            try
            {
                segment = Mp4Segments.WriteFragment(checked((uint)(sequence + 1)), tracks, _options.MaxSegmentSize, _pool, name);
            }
            catch (Mp4SizeException ex)
            {
                _metrics.Error(HlsErrorKind.SegmentTooLarge);
                LogProblem("segment-too-large", ex.Message);
                DropPending("too_large");
                AbandonPeriod();
                return;
            }

            var entry = new HlsSegmentEntry(sequence, name, duration, period.InitializationName, _discontinuityNext);
            var length = segment.Length;
            var result = Store(segment, s => _store.AppendSegment(SessionId, s, entry));
            if (result is HlsAppendResult.StreamLimitExceeded or HlsAppendResult.PlaylistFull)
            {
                if (result == HlsAppendResult.PlaylistFull)
                {
                    _metrics.Error(HlsErrorKind.PlaylistFull);
                    LogPlaylistFull(SessionId, sequence, HlsPlaylist.MaxEntries);
                    DropPending("playlist_full");
                }
                else
                {
                    _metrics.Error(HlsErrorKind.StorageFull);
                    LogStorageExhausted(SessionId, sequence);
                    DropPending("storage_full");
                }

                AbandonPeriod();
                _stopped = result;
                return;
            }

            if (result != HlsAppendResult.Published)
            {
                _metrics.Error(HlsErrorKind.StorageFull);
                LogProblem("storage-full", $"Segment {sequence} could not be stored within the storage limits without releasing segments clients may still need; it is dropped and packaging resumes at the next sync sample with a discontinuity.");
                DropPending("storage_full");
                AbandonPeriod();
                return;
            }

            _nextSequence++;
            _discontinuityNext = false;
            _segmentStart = segmentEnd;
            _videoWrittenEnd = videoEnd ?? _videoWrittenEnd;
            SegmentCount++;
            SegmentBytes += length;
            var wait = boundary == HlsSegmentBoundary.Keyframe && _video.Count > 0 ? duration - _options.TargetSegmentDuration : TimeSpan.Zero;
            _metrics.Segment(length, duration, boundary, wait);
            LogSegment(SessionId, sequence, duration, length, boundary);
        }
        finally
        {
            ReleasePending();
        }
    }

    /// <summary>Hands a new segment to the store; unless the store publishes it, the creator's reference is released.</summary>
    /// <param name="segment">The segment.</param>
    /// <param name="store">The store call.</param>
    /// <returns>The store's result.</returns>
    private static HlsAppendResult Store(HlsSegment segment, Func<HlsSegment, HlsAppendResult> store)
    {
        var result = HlsAppendResult.StorageFull;
        try
        {
            result = store(segment);
            return result;
        }
        finally
        {
            if (result != HlsAppendResult.Published)
            {
                segment.Release();
            }
        }
    }

    /// <summary>
    /// Ends the period after a segment could not be published: the published playlist and segments are unchanged, and
    /// packaging resumes with a new period (same initialization segment when the tracks are unchanged) at the next
    /// sample that can start one, marked as a discontinuity, so no segment follows a gap without a sync sample.
    /// </summary>
    private void AbandonPeriod()
    {
        if (_period is null)
        {
            return;
        }

        _period = null;
        _discontinuityNext = SegmentCount > 0;
        _metrics.Discontinuity("dropped_media");
    }

    /// <summary>Returns whether a period is open, starting one at the current sample if it can start one.</summary>
    /// <param name="canStart">Whether the sample can start a period.</param>
    /// <param name="decodeMilliseconds">The sample's decode timestamp.</param>
    /// <returns><see langword="true"/> if a period is open.</returns>
    private bool EnsurePeriod(bool canStart, long decodeMilliseconds) =>
        _period is not null || (canStart && TryStartPeriod(decodeMilliseconds));

    /// <summary>Disposes pending samples, counting them as dropped.</summary>
    /// <param name="reason">The drop reason tag.</param>
    private void DropPending(string reason)
    {
        if (_video.Count > 0)
        {
            Drop(video: true, reason, _video.Count);
        }

        if (_audio.Count > 0)
        {
            Drop(video: false, reason, _audio.Count);
        }

        ReleasePending();
    }

    /// <summary>Disposes pending samples.</summary>
    private void ReleasePending()
    {
        foreach (var pending in _video)
        {
            pending.Sample.Dispose();
        }

        foreach (var pending in _audio)
        {
            pending.Sample.Dispose();
        }

        _video.Clear();
        _audio.Clear();
        _metrics.PendingChanged(-_pendingBytes);
        _pendingBytes = 0;
    }

    /// <summary>Counts dropped samples.</summary>
    /// <param name="video">Whether video.</param>
    /// <param name="reason">The reason tag.</param>
    /// <param name="count">How many.</param>
    private void Drop(bool video, string reason, int count = 1)
    {
        DroppedSamples += count;
        _metrics.SamplesDropped(video, reason, count);
    }

    /// <summary>Returns the packaging track for a video configuration, evaluating each configuration once.</summary>
    /// <param name="source">The configuration.</param>
    /// <returns>The track, or <see langword="null"/> if it cannot be packaged.</returns>
    private Mp4VideoTrack? VideoTrackFor(AvcDecoderConfiguration source)
    {
        if (_videoTrack is { } cached && ReferenceEquals(cached.Source, source))
        {
            return cached.Track;
        }

        Mp4VideoTrack? track = null;
        if (AvcSequenceParameterSet.TryParse(source.SequenceParameterSets[0].Span, out var sps, out var error))
        {
            track = new Mp4VideoTrack(sps.Width, sps.Height, source.Record);
        }
        else
        {
            _metrics.Error(HlsErrorKind.InvalidTrack);
            LogTrackRejected("video", "The first SPS gives no valid picture size: " + error);
        }

        _videoTrack = (source, track);
        return track;
    }

    /// <summary>Returns the packaging track for an audio configuration, evaluating each configuration once.</summary>
    /// <param name="source">The configuration.</param>
    /// <returns>The track, or <see langword="null"/> if it cannot be packaged.</returns>
    private Mp4AudioTrack? AudioTrackFor(AacAudioConfiguration source)
    {
        if (_audioTrack is { } cached && ReferenceEquals(cached.Source, source))
        {
            return cached.Track;
        }

        string? problem = null;
        if (source.AudioObjectType is not (1 or 2 or 4))
        {
            problem = $"AAC object type {source.AudioObjectType} is not packaged; Main, LC and LTP (optionally with SBR/PS) are.";
        }
        else if (source.FrameLength is not (1024 or 960))
        {
            problem = "The AudioSpecificConfig does not give a frame length.";
        }
        else if (source.ChannelCount is not (>= 1 and <= 8))
        {
            problem = $"AAC channel configuration {source.ChannelConfiguration} is not packaged; configurations 1 to 7, 11, 12 and 14 are.";
        }
        else if (source.SampleRate > ushort.MaxValue)
        {
            problem = $"AAC core sample rate {source.SampleRate} Hz exceeds the 65535 Hz the mp4a sample entry can describe.";
        }

        Mp4AudioTrack? track = null;
        if (problem is null)
        {
            track = new Mp4AudioTrack(source.SampleRate, source.ChannelCount!.Value, source.Record, source.FrameLength!.Value);
        }
        else
        {
            _metrics.Error(HlsErrorKind.UnsupportedTrack);
            LogTrackRejected("audio", problem);
        }

        _audioTrack = (source, track);
        return track;
    }

    /// <summary>Logs the first forced boundary of each cause at Warning.</summary>
    /// <param name="cause">duration or size.</param>
    private void LogForcedOnce(string cause)
    {
        if (_logged.Add("forced-" + cause))
        {
            LogForced(SessionId, cause);
        }
    }

    /// <summary>Logs a problem at Warning the first time and at Debug afterwards.</summary>
    /// <param name="key">The problem kind.</param>
    /// <param name="detail">The description.</param>
    private void LogProblem(string key, string detail)
    {
        if (_logged.Add(key))
        {
            LogSegmentProblem(SessionId, detail);
        }
        else
        {
            LogRepeatedSegmentProblem(SessionId, detail);
        }
    }

    /// <summary>Logs a published initialization segment.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="name">The segment name.</param>
    /// <param name="width">Video width.</param>
    /// <param name="height">Video height.</param>
    /// <param name="sampleRate">Audio sample rate.</param>
    /// <param name="channels">Audio channels.</param>
    /// <param name="bytes">Size.</param>
    [LoggerMessage(EventId = 7001, Level = LogLevel.Information, Message = "Session {SessionId} initialization segment {Name}: video {Width}x{Height}, audio {SampleRate} Hz with {Channels} channels, {Bytes} bytes")]
    private partial void LogInitialization(Guid sessionId, string name, int? width, int? height, int? sampleRate, int? channels, int bytes);

    /// <summary>Logs a published media segment.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="sequence">Its media sequence number.</param>
    /// <param name="duration">Its duration.</param>
    /// <param name="bytes">Its size.</param>
    /// <param name="boundary">Why it closed.</param>
    [LoggerMessage(EventId = 7002, Level = LogLevel.Debug, Message = "Session {SessionId} segment {Sequence}: {Duration}, {Bytes} bytes, closed at {Boundary}")]
    private partial void LogSegment(Guid sessionId, long sequence, TimeSpan duration, int bytes, HlsSegmentBoundary boundary);

    /// <summary>Logs a track that cannot be packaged.</summary>
    /// <param name="track">video or audio.</param>
    /// <param name="detail">Why.</param>
    [LoggerMessage(EventId = 7003, Level = LogLevel.Warning, Message = "A {Track} track configuration cannot be packaged; its samples are dropped: {Detail}")]
    private partial void LogTrackRejected(string track, string detail);

    /// <summary>Logs the first segment problem of a kind.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="detail">The problem.</param>
    [LoggerMessage(EventId = 7004, Level = LogLevel.Warning, Message = "Session {SessionId}: {Detail}")]
    private partial void LogSegmentProblem(Guid sessionId, string detail);

    /// <summary>Logs a repeated segment problem.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="detail">The problem.</param>
    [LoggerMessage(EventId = 7005, Level = LogLevel.Debug, Message = "Session {SessionId}: {Detail}")]
    private partial void LogRepeatedSegmentProblem(Guid sessionId, string detail);

    /// <summary>Logs the first discontinuity of a reason.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="reason">The reason.</param>
    [LoggerMessage(EventId = 7006, Level = LogLevel.Information, Message = "Session {SessionId} starts a new HLS period ({Reason}); the next segment is marked as a discontinuity")]
    private partial void LogDiscontinuity(Guid sessionId, string reason);

    /// <summary>Logs a repeated discontinuity.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="reason">The reason.</param>
    [LoggerMessage(EventId = 7007, Level = LogLevel.Debug, Message = "Session {SessionId} starts a new HLS period ({Reason})")]
    private partial void LogRepeatedDiscontinuity(Guid sessionId, string reason);

    /// <summary>Logs the first forced segment boundary of a cause.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cause">duration or size.</param>
    [LoggerMessage(EventId = 7008, Level = LogLevel.Warning, Message = "Session {SessionId} reached the maximum segment {Cause} without a video sync sample; the segment is closed early and the next one does not start with an IDR")]
    private partial void LogForced(Guid sessionId, string cause);

    /// <summary>Logs a keyframe flag that disagrees with the access unit.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="flagged">The FLV keyframe flag.</param>
    /// <param name="idr">Whether an IDR slice is present.</param>
    [LoggerMessage(EventId = 7009, Level = LogLevel.Warning, Message = "Session {SessionId} sent a video sample with keyframe flag {Flagged} but IDR present {Idr}; the IDR NAL unit decides sync samples")]
    private partial void LogKeyframeMismatch(Guid sessionId, bool flagged, bool idr);

    /// <summary>Logs that the stream's storage is permanently exhausted.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="sequence">The segment that could not be stored.</param>
    [LoggerMessage(EventId = 7012, Level = LogLevel.Error, Message = "Session {SessionId} segment {Sequence} does not fit in MaxSessionStorageMegabytes beside the segments its playlist lists, and none can be released without breaking HLS availability; packaging stops and the publish is ended")]
    private partial void LogStorageExhausted(Guid sessionId, long sequence);

    /// <summary>Logs that the live playlist is at its entry cap and cannot shrink.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="sequence">The segment that could not be listed.</param>
    /// <param name="entries">The entry cap.</param>
    [LoggerMessage(EventId = 7013, Level = LogLevel.Error, Message = "Session {SessionId} segment {Sequence} cannot be listed: the playlist holds {Entries} segments spanning less than three target durations, and removing one would shorten the live playlist below that minimum; packaging stops and the publish is ended")]
    private partial void LogPlaylistFull(Guid sessionId, long sequence, int entries);

    /// <summary>A timeline period: the tracks of one initialization segment.</summary>
    /// <param name="VideoSource">The video configuration, if packaged.</param>
    /// <param name="Video">The video track, if packaged.</param>
    /// <param name="AudioSource">The audio configuration, if packaged.</param>
    /// <param name="Audio">The audio track, if packaged.</param>
    /// <param name="InitializationName">The initialization segment name.</param>
    /// <param name="BaseMilliseconds">The media-timeline millisecond mapped to tick zero.</param>
    private sealed record Period(AvcDecoderConfiguration? VideoSource, Mp4VideoTrack? Video, AacAudioConfiguration? AudioSource, Mp4AudioTrack? Audio, string InitializationName, long BaseMilliseconds);

    /// <summary>A pending video sample.</summary>
    /// <param name="Sample">The owned sample.</param>
    /// <param name="DecodeTime">Decode time in 90 kHz ticks.</param>
    /// <param name="CompositionOffset">Composition offset in 90 kHz ticks.</param>
    /// <param name="Sync">Whether it is a sync sample.</param>
    private readonly record struct PendingVideo(EncodedVideoSample Sample, long DecodeTime, int CompositionOffset, bool Sync);

    /// <summary>A pending audio sample.</summary>
    /// <param name="Sample">The owned sample.</param>
    /// <param name="Start">Start in audio ticks.</param>
    private readonly record struct PendingAudio(EncodedAudioSample Sample, long Start);
}
