// One publisher's transcoding pipeline. The RTMP receive loop submits validated source samples into bounded queues
// (frame counts per track, bytes per session and a service-wide byte budget) of two independent managed stages: the
// video stage decodes H.264, converts colour to BT.709 and re-encodes canonical H.264, and the audio stage decodes,
// resamples and re-encodes canonical AAC-LC. Both feed the session's media multiplexer, which delivers their output
// to the existing media/HLS stage, so that stage only ever sees canonical H.264/AAC. The multiplexer is the only
// caller of the downstream handler while the session runs, so its calls stay sequential. A failure ends only this
// session.

using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Rtp;

namespace RTSP.IngestService.Transcoding;

/// <summary>
/// A service-wide budget of bytes held in transcoding queues, which is never exceeded. It is split into a shared pool,
/// from which source queues and non-empty multiplexer queues reserve, and <see cref="Floors"/> floors of
/// <see cref="FloorBytes"/> each, one of which an empty multiplexer queue uses for its head unit. A multiplexer queue
/// can therefore always take a unit of up to <see cref="FloorBytes"/> however full the shared pool is, so a producer
/// that must deliver output before its source queue can drain always progresses, without any overdraft.
/// </summary>
internal sealed class TranscodingBufferBudget
{
    /// <summary>Guards <see cref="_waiters"/>.</summary>
    private readonly Lock _gate = new();

    /// <summary>Signals of producers waiting for shared bytes.</summary>
    private readonly List<RtpSenderSignal> _waiters = [];

    /// <summary>Bytes reserved from the shared pool.</summary>
    private long _shared;

    /// <summary>Bytes held by all reservations, shared and floor.</summary>
    private long _used;

    /// <summary>The most bytes held at once.</summary>
    private long _peak;

    /// <summary>Floors in use.</summary>
    private int _floorsInUse;

    /// <summary>Entries in <see cref="_waiters"/>; read without the lock.</summary>
    private int _waiterCount;

    /// <summary>Initializes a new instance of the <see cref="TranscodingBufferBudget"/> class with no floors.</summary>
    /// <param name="limit">The budget in bytes.</param>
    public TranscodingBufferBudget(long limit)
        : this(limit, 0, 0)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TranscodingBufferBudget"/> class.</summary>
    /// <param name="limit">The budget in bytes.</param>
    /// <param name="floorBytes">The size of each floor.</param>
    /// <param name="floors">The number of floors.</param>
    /// <exception cref="ArgumentOutOfRangeException">The floors do not fit in the budget.</exception>
    public TranscodingBufferBudget(long limit, long floorBytes, int floors)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(floorBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(floors);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(floorBytes * floors, limit, nameof(floorBytes));
        Limit = limit;
        FloorBytes = floorBytes;
        Floors = floors;
        SharedLimit = limit - (floorBytes * floors);
    }

    /// <summary>
    /// Derives the floors for a transcoding configuration: two per session (one per multiplexer track queue), each as
    /// large as the largest unit a multiplexer accepts, but together at most an eighth of the budget, so the shared
    /// pool keeps at least seven eighths.
    /// </summary>
    /// <param name="options">The transcoding options.</param>
    /// <param name="media">The media limits.</param>
    /// <returns>The budget.</returns>
    public static TranscodingBufferBudget FromOptions(TranscodingOptions options, MediaOptions media)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(media);
        var floors = 2 * options.MaxConcurrentSessions;
        var floorBytes = Math.Min(media.MaxSampleSize + 64L, options.MaxTotalBufferBytes / 8 / floors);
        return new TranscodingBufferBudget(options.MaxTotalBufferBytes, floorBytes, floors);
    }

    /// <summary>Gets the bytes held.</summary>
    public long Used => Interlocked.Read(ref _used);

    /// <summary>Gets the most bytes held at once; never more than <see cref="Limit"/>.</summary>
    public long Peak => Interlocked.Read(ref _peak);

    /// <summary>Gets the budget in bytes.</summary>
    public long Limit { get; }

    /// <summary>Gets the size of each floor.</summary>
    public long FloorBytes { get; }

    /// <summary>Gets the number of floors.</summary>
    public int Floors { get; }

    /// <summary>Gets the shared pool's size: <see cref="Limit"/> less the floors.</summary>
    public long SharedLimit { get; }

    /// <summary>Gets a value indicating whether the shared pool is full.</summary>
    public bool SharedExhausted => Interlocked.Read(ref _shared) >= SharedLimit;

    /// <summary>Reserves shared bytes if the shared pool allows.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns><see langword="true"/> if reserved.</returns>
    public bool TryReserve(long bytes)
    {
        var shared = Interlocked.Read(ref _shared);
        while (true)
        {
            if (shared + bytes > SharedLimit)
            {
                return false;
            }

            var observed = Interlocked.CompareExchange(ref _shared, shared + bytes, shared);
            if (observed == shared)
            {
                Add(bytes);
                return true;
            }

            shared = observed;
        }
    }

    /// <summary>Releases shared bytes and wakes producers waiting for them.</summary>
    /// <param name="bytes">The bytes.</param>
    public void Release(long bytes)
    {
        Interlocked.Add(ref _shared, -bytes);
        Interlocked.Add(ref _used, -bytes);
        if (Volatile.Read(ref _waiterCount) > 0)
        {
            lock (_gate)
            {
                foreach (var waiter in _waiters)
                {
                    waiter.Set();
                }
            }
        }
    }

    /// <summary>Takes a floor for up to <see cref="FloorBytes"/> bytes, if one is free.</summary>
    /// <param name="bytes">The bytes held on it, at most <see cref="FloorBytes"/>.</param>
    /// <returns><see langword="true"/> if taken.</returns>
    public bool TryUseFloor(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, FloorBytes);
        var used = Volatile.Read(ref _floorsInUse);
        while (true)
        {
            if (used >= Floors)
            {
                return false;
            }

            var observed = Interlocked.CompareExchange(ref _floorsInUse, used + 1, used);
            if (observed == used)
            {
                Add(bytes);
                return true;
            }

            used = observed;
        }
    }

    /// <summary>Returns a floor taken by <see cref="TryUseFloor"/>.</summary>
    /// <param name="bytes">The bytes held on it.</param>
    public void ReturnFloor(long bytes)
    {
        Interlocked.Decrement(ref _floorsInUse);
        Interlocked.Add(ref _used, -bytes);
    }

    /// <summary>Registers a signal set whenever shared bytes are released.</summary>
    /// <param name="signal">The signal.</param>
    public void AddWaiter(RtpSenderSignal signal)
    {
        lock (_gate)
        {
            _waiters.Add(signal);
            _waiterCount = _waiters.Count;
        }
    }

    /// <summary>Removes a signal registered by <see cref="AddWaiter"/>.</summary>
    /// <param name="signal">The signal.</param>
    public void RemoveWaiter(RtpSenderSignal signal)
    {
        lock (_gate)
        {
            _waiters.Remove(signal);
            _waiterCount = _waiters.Count;
        }
    }

    /// <summary>Adds held bytes and records the peak.</summary>
    /// <param name="bytes">The bytes.</param>
    private void Add(long bytes)
    {
        var used = Interlocked.Add(ref _used, bytes);
        var peak = Interlocked.Read(ref _peak);
        while (used > peak)
        {
            var observed = Interlocked.CompareExchange(ref _peak, used, peak);
            if (observed == peak)
            {
                break;
            }

            peak = observed;
        }
    }
}

/// <summary>The transcoding pipeline of one publishing session.</summary>
/// <remarks>
/// <see cref="SubmitAsync"/>, <see cref="RouteVideo"/>, <see cref="RouteAudio"/>, <see cref="MarkVideoBroken"/>,
/// <see cref="MarkAudioLost"/> and <see cref="NotifyMetadata"/> are called only from the session's receive loop;
/// <see cref="StopAsync"/> once, after the receive loop has stopped.
/// </remarks>
internal sealed partial class TranscodingSession : IDisposable
{
    /// <summary>How much media, in milliseconds, one track is sent before the other, unconfigured track is declared absent.</summary>
    private const long StartupHold = 1000;

    /// <summary>The publishing session.</summary>
    private readonly PublisherSession _publisher;

    /// <summary>The transcoding options.</summary>
    private readonly TranscodingOptions _options;

    /// <summary>The managed video stage's settings.</summary>
    private readonly ManagedVideoSettings _videoSettings;

    /// <summary>The service-wide buffer budget.</summary>
    private readonly TranscodingBufferBudget _budget;

    /// <summary>Metrics.</summary>
    private readonly TranscodingMetrics _metrics;

    /// <summary>Logger.</summary>
    private readonly ILogger _logger;

    /// <summary>Guards the queue accounting, the stage fields and <see cref="_spaceWaiter"/>.</summary>
    private readonly Lock _gate = new();

    /// <summary>Cancelled to abort the pipeline.</summary>
    private readonly CancellationTokenSource _abort = new();

    /// <summary>Tracks whose first drop was logged at Warning.</summary>
    private readonly HashSet<(MediaTrack, TranscodingDropReason)> _loggedDrops = [];

    /// <summary>The multiplexer between the managed stages and the downstream stage.</summary>
    private readonly MediaMultiplexer _mux;

    /// <summary>Queued video frames.</summary>
    private int _queuedVideo;

    /// <summary>Queued audio frames.</summary>
    private int _queuedAudio;

    /// <summary>Queued bytes.</summary>
    private long _queuedBytes;

    /// <summary>Completed when queue space is released, for the backpressure policy.</summary>
    private TaskCompletionSource? _spaceWaiter;

    /// <summary>Whether video must be dropped until the next keyframe (receive-loop state).</summary>
    private bool _videoBroken;

    /// <summary>The managed video stage, once the source has configured video.</summary>
    private ManagedVideoStage? _videoStage;

    /// <summary>The managed audio stage, once the source has configured audio.</summary>
    private ManagedAudioStage? _audioStage;

    /// <summary>The last video configuration accepted by <see cref="RouteVideo"/> (receive-loop state).</summary>
    private SourceVideoConfiguration? _routedVideo;

    /// <summary>The last audio configuration accepted by <see cref="RouteAudio"/> (receive-loop state).</summary>
    private SourceAudioConfiguration? _routedAudio;

    /// <summary>Audio units lost since the last one queued (receive-loop state).</summary>
    private int _audioLost;

    /// <summary>The first audio timestamp while the source has no video (receive-loop state).</summary>
    private long? _firstAudioWithoutVideo;

    /// <summary>Whether video was declared absent to the multiplexer for an audio-only stream (receive-loop state).</summary>
    private bool _videoDeclaredAbsent;

    /// <summary>The first video timestamp while the source has no audio (receive-loop state).</summary>
    private long? _firstVideoWithoutAudio;

    /// <summary>Whether audio was declared absent to the multiplexer for a video-only stream (receive-loop state).</summary>
    private bool _audioDeclaredAbsent;

    /// <summary>The first failure, once failed.</summary>
    private Exception? _fault;

    /// <summary>The stop task, once stopping.</summary>
    private Task? _stopped;

    /// <summary>Source frames dropped before reaching a stage.</summary>
    private long _droppedFrames;

    /// <summary>Initializes a new instance of the <see cref="TranscodingSession"/> class.</summary>
    /// <param name="publisher">The publishing session.</param>
    /// <param name="downstream">The downstream media stage.</param>
    /// <param name="options">The transcoding options.</param>
    /// <param name="media">The media limits.</param>
    /// <param name="videoSettings">The managed video stage's settings.</param>
    /// <param name="budget">The service-wide buffer budget.</param>
    /// <param name="metrics">Metrics.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="limits">The multiplexer bounds.</param>
    /// <param name="time">The clock for multiplexer hold deadlines.</param>
    public TranscodingSession(PublisherSession publisher, IPublishSessionHandler downstream, TranscodingOptions options, MediaOptions media, ManagedVideoSettings videoSettings, TranscodingBufferBudget budget, TranscodingMetrics metrics, ILogger logger, MultiplexerLimits limits, TimeProvider time)
    {
        _publisher = publisher;
        _options = options;
        _videoSettings = videoSettings;
        _budget = budget;
        _metrics = metrics;
        _logger = logger;
        Parser = new SourceMediaParser(options, media);
        _mux = new MediaMultiplexer(publisher, downstream, limits, budget, metrics, logger, time, SetFault, _abort.Token);
    }

    /// <summary>Gets the session's source parser.</summary>
    public SourceMediaParser Parser { get; }

    /// <summary>Gets the first failure, if the pipeline failed.</summary>
    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>Gets the managed video stage, once video is configured; for tests.</summary>
    internal ManagedVideoStage? VideoStage => Volatile.Read(ref _videoStage);

    /// <summary>Gets the managed audio stage, once audio is configured; for tests.</summary>
    internal ManagedAudioStage? AudioStage => Volatile.Read(ref _audioStage);

    /// <summary>Gets the bytes currently queued; for tests.</summary>
    internal long QueuedBytes
    {
        get
        {
            lock (_gate)
            {
                return _queuedBytes;
            }
        }
    }

    /// <summary>Starts the multiplexer; the managed stages start when their track is first configured.</summary>
    public void Start() => _mux.Start();

    /// <summary>Throws the pipeline's failure, if any.</summary>
    /// <exception cref="TranscodingFailedException">The pipeline failed.</exception>
    public void ThrowIfFaulted()
    {
        if (Fault is { } fault)
        {
            throw fault as TranscodingFailedException ?? new TranscodingFailedException(TranscodingStage.Pipeline, false, "The transcoding pipeline failed.", fault);
        }
    }

    /// <summary>Records that publisher metadata changed; the downstream stage is told before the next output.</summary>
    public void NotifyMetadata() => _mux.NotifyMetadata();

    /// <summary>Records that a video packet was lost, so video waits for the next keyframe.</summary>
    public void MarkVideoBroken() => _videoBroken = true;

    /// <summary>Records that a source audio packet was lost, so managed audio conceals its duration.</summary>
    public void MarkAudioLost() => _audioLost++;

    /// <summary>
    /// Routes the session's video to the managed video stage when its configuration appears or changes; call before
    /// any sample under the configuration is submitted.
    /// </summary>
    /// <param name="configuration">The video configuration.</param>
    /// <returns><see langword="null"/> if accepted; otherwise why the configuration cannot be transcoded, which ends the publish.</returns>
    public (TranscodingRejectionReason Reason, string Detail)? RouteVideo(SourceVideoConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (ReferenceEquals(configuration, _routedVideo))
        {
            return null;
        }

        if (ManagedVideoStage.CheckEligibility(configuration, _videoSettings) is { } rejection)
        {
            return rejection;
        }

        if (VideoStage is null)
        {
            var stage = new ManagedVideoStage(_publisher, _mux.Video, _videoSettings, _metrics, _logger, Release, FailVideo, System.Buffers.ArrayPool<byte>.Shared, _abort.Token);
            lock (_gate)
            {
                Volatile.Write(ref _videoStage, stage);
            }

            stage.Start();
        }

        _routedVideo = configuration;
        return null;
    }

    /// <summary>
    /// Routes the session's audio to the managed audio stage when its configuration appears or changes; call before
    /// any sample under the configuration is submitted.
    /// </summary>
    /// <param name="configuration">The audio configuration.</param>
    /// <returns><see langword="null"/> if accepted; otherwise why the configuration cannot be transcoded, which ends the publish.</returns>
    public ManagedAudioIneligibility? RouteAudio(SourceAudioConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (ReferenceEquals(configuration, _routedAudio))
        {
            return null;
        }

        if (!ManagedAudioStage.IsEligible(configuration, out var reason))
        {
            return reason;
        }

        if (AudioStage is null)
        {
            var stage = new ManagedAudioStage(_publisher, _mux.Audio, _options.Audio.BitrateKilobits * 1000, _metrics, _logger, Release, FailAudio, System.Buffers.ArrayPool<byte>.Shared, _abort.Token);
            lock (_gate)
            {
                Volatile.Write(ref _audioStage, stage);
            }

            stage.Start();
        }

        _routedAudio = configuration;
        return null;
    }

    /// <summary>Queues a sample for its track's managed stage, applying the overload policy.</summary>
    /// <param name="sample">The sample; ownership transfers to this method.</param>
    /// <param name="cancellationToken">Cancelled when the connection closes.</param>
    /// <returns>A task that completes when the sample was queued or dropped.</returns>
    /// <exception cref="TranscodingFailedException">The pipeline failed.</exception>
    /// <exception cref="InvalidOperationException">The sample's track was not routed.</exception>
    public async ValueTask SubmitAsync(SourceSample sample, CancellationToken cancellationToken)
    {
        var owned = true;
        try
        {
            ThrowIfFaulted();
            var video = sample.Track == MediaTrack.Video;
            if (video && _videoBroken && !sample.IsKeyframe)
            {
                Drop(sample.Track, TranscodingDropReason.AwaitingKeyframe);
                return;
            }

            var videoStage = video ? VideoStage ?? throw new InvalidOperationException("Video was submitted before it was routed.") : null;
            var audioStage = video ? null : AudioStage ?? throw new InvalidOperationException("Audio was submitted before it was routed.");
            if (video)
            {
                DeclareAudioAbsentIfVideoOnly(sample);
            }
            else
            {
                DeclareVideoAbsentIfAudioOnly(sample);
            }

            while (true)
            {
                Task? wait;
                TranscodingDropReason? reason;
                lock (_gate)
                {
                    reason = TryAccount(sample);
                    if (reason is null)
                    {
                        if (!(videoStage?.TryWrite(sample) ?? audioStage!.TryWrite(sample, _audioLost)))
                        {
                            Unaccount(sample.Track, sample.Message.Length);
                            ThrowIfFaulted();
                            return;
                        }

                        owned = false;
                        if (video)
                        {
                            _videoBroken = false;
                        }
                        else
                        {
                            _audioLost = 0;
                        }

                        return;
                    }

                    wait = null;
                    if (reason == TranscodingDropReason.QueueFull && _options.OverloadPolicy == TranscodingOverloadPolicy.Backpressure)
                    {
                        _spaceWaiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        wait = _spaceWaiter.Task;
                    }
                }

                _metrics.Saturated(sample.Track);
                if (wait is null)
                {
                    Drop(sample.Track, reason.Value);
                    if (video)
                    {
                        _videoBroken = true;
                    }
                    else
                    {
                        _audioLost++;
                    }

                    return;
                }

                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
                ThrowIfFaulted();
            }
        }
        finally
        {
            if (owned)
            {
                sample.Dispose();
            }
        }
    }

    /// <summary>
    /// Declares video absent once a source without video has sent <see cref="StartupHold"/> of audio, so the
    /// multiplexer stops holding audio for a first video keyframe that will not come. A video stream started later
    /// declares video again.
    /// </summary>
    /// <param name="sample">An audio sample.</param>
    private void DeclareVideoAbsentIfAudioOnly(SourceSample sample)
    {
        if (_videoDeclaredAbsent || sample.Configuration.Video is not null)
        {
            return;
        }

        _firstAudioWithoutVideo ??= sample.DecodeTimestamp;
        if (sample.DecodeTimestamp - _firstAudioWithoutVideo.Value >= StartupHold || sample.DecodeTimestamp < _firstAudioWithoutVideo.Value)
        {
            _videoDeclaredAbsent = true;
            _mux.Video.Declare(false);
        }
    }

    /// <summary>
    /// Declares audio absent once a source without audio has sent <see cref="StartupHold"/> of video, so the
    /// multiplexer stops holding video for audio that will not come. An audio stream started later declares audio again.
    /// </summary>
    /// <param name="sample">A video sample.</param>
    private void DeclareAudioAbsentIfVideoOnly(SourceSample sample)
    {
        if (_audioDeclaredAbsent || sample.Configuration.Audio is not null)
        {
            return;
        }

        _firstVideoWithoutAudio ??= sample.DecodeTimestamp;
        if (sample.DecodeTimestamp - _firstVideoWithoutAudio.Value >= StartupHold || sample.DecodeTimestamp < _firstVideoWithoutAudio.Value)
        {
            _audioDeclaredAbsent = true;
            _mux.Audio.Declare(false);
        }
    }

    /// <summary>Ends the publish after a managed video failure.</summary>
    /// <param name="failure">The failure.</param>
    private void FailVideo(TranscodingFailedException failure) => Fail(MediaTrack.Video, _mux.Video, failure);

    /// <summary>Ends the publish after a managed audio failure.</summary>
    /// <param name="failure">The failure.</param>
    private void FailAudio(TranscodingFailedException failure) => Fail(MediaTrack.Audio, _mux.Audio, failure);

    /// <summary>Records a managed stage's failure, fails its multiplexer track and aborts the pipeline.</summary>
    /// <param name="track">The failed track.</param>
    /// <param name="input">The track's multiplexer input.</param>
    /// <param name="failure">The failure.</param>
    private void Fail(MediaTrack track, MediaMultiplexer.TrackInput input, TranscodingFailedException failure)
    {
        if (Fault is null)
        {
            _metrics.Failure(failure.Stage, failure.Initialization);
            LogFailed(_publisher.SessionId, track, failure.Stage, failure.Initialization, failure.Message);
        }

        input.Fail(failure);
        SetFault(failure);
    }

    /// <summary>
    /// Stops the pipeline. When draining, queued samples are transcoded, the stages are flushed and their output
    /// delivered, all within <see cref="TranscodingOptions.DrainTimeout"/>; otherwise queued work is discarded.
    /// </summary>
    /// <param name="drain">Whether to drain.</param>
    /// <returns>A task that completes when both stages and the multiplexer have finished.</returns>
    /// <remarks>Later calls return the first call's task.</remarks>
    public Task StopAsync(bool drain)
    {
        lock (_gate)
        {
            return _stopped ??= StopCoreAsync(drain);
        }
    }

    /// <summary>Stops the pipeline once.</summary>
    /// <param name="drain">Whether to drain.</param>
    /// <returns>A task that completes when stopped.</returns>
    private async Task StopCoreAsync(bool drain)
    {
        await Task.Yield();
        ManagedVideoStage? video;
        ManagedAudioStage? audio;
        lock (_gate)
        {
            video = _videoStage;
            audio = _audioStage;
        }

        var draining = drain && Fault is null;
        if (draining)
        {
            // A track with no stage has nothing to deliver, so the other track's flush does not wait for it.
            if (video is null)
            {
                _mux.Video.Complete();
            }

            if (audio is null)
            {
                _mux.Audio.Complete();
            }

            video?.Complete(drain: true);
            audio?.Complete(drain: true);
            _abort.CancelAfter(_options.DrainTimeout);
        }
        else
        {
            // Cancel before completing the stages: an idle stage woken by the completion must find the pipeline
            // already aborted rather than take the end of its queue as the end of a normal publish.
            await _abort.CancelAsync().ConfigureAwait(false);
            video?.Complete(drain: false);
            audio?.Complete(drain: false);
        }

        WakeWaiter();
        await Task.WhenAll(video?.Completion ?? Task.CompletedTask, audio?.Completion ?? Task.CompletedTask).ConfigureAwait(false);

        // Only a drain that finished cleanly delivers what the multiplexer still holds; otherwise it is discarded.
        if (draining && Fault is null && !_abort.IsCancellationRequested)
        {
            await _mux.CompleteAsync().ConfigureAwait(false);
        }

        if (draining && Fault is null && _abort.IsCancellationRequested)
        {
            LogDrainTimeout(_publisher.SessionId);
        }

        await _mux.DisposeAsync().ConfigureAwait(false);
        var input = (video?.DecodedUnits ?? 0) + (audio?.DecodedUnits ?? 0);
        var output = (video?.EncodedUnits ?? 0) + (audio?.EncodedUnits ?? 0);
        var dropped = Interlocked.Read(ref _droppedFrames) + (video?.DroppedPictures ?? 0);
        LogSummary(_publisher.SessionId, input, output, dropped, video?.Streams ?? 0);

        _abort.Dispose();
    }

    /// <summary>Releases the cancellation source and the video codecs; call after <see cref="StopAsync"/> has completed.</summary>
    public void Dispose()
    {
        VideoStage?.Dispose();
        _abort.Dispose();
    }

    /// <summary>Accounts for a sample if there is room; called under <see cref="_gate"/>.</summary>
    /// <param name="sample">The sample.</param>
    /// <returns><see langword="null"/> if accounted; otherwise why not.</returns>
    private TranscodingDropReason? TryAccount(SourceSample sample)
    {
        var length = sample.Message.Length;
        var full = sample.Track == MediaTrack.Video ? _queuedVideo >= _options.MaxBufferedVideoFrames : _queuedAudio >= _options.MaxBufferedAudioFrames;
        if (full || _queuedBytes + length > _options.MaxSessionBufferBytes)
        {
            return TranscodingDropReason.QueueFull;
        }

        if (!_budget.TryReserve(length))
        {
            return TranscodingDropReason.BudgetExhausted;
        }

        if (sample.Track == MediaTrack.Video)
        {
            _queuedVideo++;
        }
        else
        {
            _queuedAudio++;
        }

        _queuedBytes += length;
        _metrics.Buffered(length);
        return null;
    }

    /// <summary>Releases a sample's accounting; called under <see cref="_gate"/>.</summary>
    /// <param name="track">The track.</param>
    /// <param name="length">The bytes.</param>
    private void Unaccount(MediaTrack track, int length)
    {
        if (track == MediaTrack.Video)
        {
            _queuedVideo--;
        }
        else
        {
            _queuedAudio--;
        }

        _queuedBytes -= length;
        _budget.Release(length);
        _metrics.Buffered(-length);
    }

    /// <summary>Releases a dequeued sample's accounting and wakes a waiting submitter.</summary>
    /// <param name="sample">The sample.</param>
    private void Release(SourceSample sample)
    {
        lock (_gate)
        {
            Unaccount(sample.Track, sample.Message.Length);
        }

        WakeWaiter();
    }

    /// <summary>Completes the backpressure waiter, if any.</summary>
    private void WakeWaiter()
    {
        TaskCompletionSource? waiter;
        lock (_gate)
        {
            waiter = _spaceWaiter;
            _spaceWaiter = null;
        }

        waiter?.TrySetResult();
    }

    /// <summary>Counts and logs a dropped frame.</summary>
    /// <param name="track">The track.</param>
    /// <param name="reason">Why.</param>
    private void Drop(MediaTrack track, TranscodingDropReason reason)
    {
        Interlocked.Increment(ref _droppedFrames);
        _metrics.Dropped(track, reason);
        bool first;
        lock (_loggedDrops)
        {
            first = _loggedDrops.Add((track, reason));
        }

        if (first)
        {
            LogFirstDrop(_publisher.SessionId, track, reason);
        }
        else
        {
            LogDrop(_publisher.SessionId, track, reason);
        }
    }

    /// <summary>Records the first failure and aborts the pipeline.</summary>
    /// <param name="exception">The failure.</param>
    private void SetFault(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _fault, exception, null) is null)
        {
            try
            {
                _abort.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already stopped.
            }

            WakeWaiter();
        }
    }


    /// <summary>Logs a failed pipeline.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="track">The failed track.</param>
    /// <param name="stage">The failing stage.</param>
    /// <param name="initialization">Whether it failed before producing output.</param>
    /// <param name="message">The description.</param>
    [LoggerMessage(EventId = 10001, Level = LogLevel.Error, Message = "Session {SessionId} {Track} transcoding failed ({Stage}, during initialization {Initialization}): {Message}")]
    private partial void LogFailed(Guid sessionId, MediaTrack track, TranscodingStage stage, bool initialization, string message);

    /// <summary>Logs a drain that ran out of time.</summary>
    /// <param name="sessionId">The session.</param>
    [LoggerMessage(EventId = 10002, Level = LogLevel.Warning, Message = "Session {SessionId} transcoding did not finish within the drain timeout; undelivered output was discarded")]
    private partial void LogDrainTimeout(Guid sessionId);

    /// <summary>Logs the first drop of a track and reason.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="track">The track.</param>
    /// <param name="reason">Why.</param>
    [LoggerMessage(EventId = 10003, Level = LogLevel.Warning, Message = "Session {SessionId} {Track} frames dropped ({Reason}); further drops for this reason are logged at Debug")]
    private partial void LogFirstDrop(Guid sessionId, MediaTrack track, TranscodingDropReason reason);

    /// <summary>Logs a repeated drop.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="track">The track.</param>
    /// <param name="reason">Why.</param>
    [LoggerMessage(EventId = 10004, Level = LogLevel.Debug, Message = "Session {SessionId} {Track} frame dropped ({Reason})")]
    private partial void LogDrop(Guid sessionId, MediaTrack track, TranscodingDropReason reason);

    /// <summary>Logs the session summary.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="input">Source frames decoded.</param>
    /// <param name="output">Canonical frames produced.</param>
    /// <param name="dropped">Frames dropped.</param>
    /// <param name="videoStreams">Canonical video streams started.</param>
    [LoggerMessage(EventId = 10005, Level = LogLevel.Information, Message = "Transcoding ended for session {SessionId}: {Input} source frames, {Output} canonical frames, {Dropped} dropped, {VideoStreams} video streams")]
    private partial void LogSummary(Guid sessionId, long input, long output, long dropped, int videoStreams);
}
