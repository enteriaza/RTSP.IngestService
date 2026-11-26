// The encoded-media multiplexer of one publisher incarnation: the single caller of the downstream media stage. Audio
// and video producers submit encoded access units to independent bounded per-track queues, taking turns with the
// submitter's ownership of each payload; one output task releases them merged by timestamp. A unit is released as
// soon as no earlier unit can still arrive on the other track: when both tracks have a unit queued the earlier goes
// first (ties: sequence starts before samples, then video), and a lone track waits only while the other is expected
// and may still produce: for the other track's first video keyframe (HLS discards audio before it), for up to the
// startup hold while the other track's first audio is late, and for up to the idle release once the other track has
// gone quiet. A queue that reaches its unit, byte or media-duration bound, a producer waiting for room, or the shared
// buffer budget running out releases the queue's oldest unit regardless, so a single producer feeding both tracks
// can never deadlock and nothing is dropped. Within a track units leave in submission order and timestamps are never
// changed. It has no codec or process dependency; the transcoding session owns it and its lifecycle.

using System.Buffers;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;
using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Rtp;

namespace RTSP.IngestService.Transcoding;

/// <summary>What an <see cref="EncodedAccessUnit"/> carries.</summary>
internal enum EncodedUnitKind
{
    /// <summary>Codec configuration that precedes the samples at its timestamp, such as an AVC or AAC sequence header.</summary>
    SequenceStart,

    /// <summary>A coded frame.</summary>
    Sample,

    /// <summary>A marker that follows every sample queued before it on either track, such as an AVC end of sequence.</summary>
    SequenceEnd,
}

/// <summary>Why the multiplexer released a unit before ordering was known to be safe.</summary>
internal enum MultiplexerEarlyRelease
{
    /// <summary>The other track's first unit did not arrive within the startup hold.</summary>
    Startup,

    /// <summary>The other track submitted nothing for the idle release.</summary>
    Idle,

    /// <summary>The queue reached its unit bound or its producer waited for room.</summary>
    Units,

    /// <summary>The queue reached its byte bound.</summary>
    Bytes,

    /// <summary>The queue reached its media-duration bound.</summary>
    Duration,

    /// <summary>The shared buffer budget was exhausted.</summary>
    Budget,
}

/// <summary>How a unit left the multiplexer.</summary>
internal enum MultiplexerUnitOutcome
{
    /// <summary>Queued.</summary>
    Accepted,

    /// <summary>Forwarded to the downstream stage.</summary>
    Forwarded,

    /// <summary>Queued, then released unused because the multiplexer was cancelled, failed or disposed.</summary>
    Discarded,

    /// <summary>Refused at submission and released.</summary>
    Rejected,
}

/// <summary>
/// One encoded access unit. <see cref="Payload"/> is the complete FLV tag body the downstream stage receives; the
/// submitter's ownership of it passes to <see cref="MediaMultiplexer.TrackInput.SubmitAsync"/>.
/// </summary>
/// <param name="Kind">What it carries.</param>
/// <param name="Timestamp">Its decode timestamp in <paramref name="TimeBase"/> ticks on the session timeline.</param>
/// <param name="TimeBase">The time base of <paramref name="Timestamp"/>.</param>
/// <param name="IsSyncPoint">Whether decoding can start at it: a video keyframe; every audio unit.</param>
/// <param name="Payload">The payload.</param>
internal readonly record struct EncodedAccessUnit(EncodedUnitKind Kind, long Timestamp, MediaTimeBase TimeBase, bool IsSyncPoint, PooledBuffer Payload);

/// <summary>The bounds of one multiplexer track queue.</summary>
/// <param name="MaxUnits">The most queued units.</param>
/// <param name="MaxBytes">The most queued payload bytes; an empty queue admits one unit whatever its size, within the buffer budget.</param>
/// <param name="MaxDuration">The most queued media, from the oldest to the newest unit's timestamp.</param>
internal sealed record MultiplexerTrackLimits(int MaxUnits, long MaxBytes, TimeSpan MaxDuration);

/// <summary>The bounds and hold times of a <see cref="MediaMultiplexer"/>.</summary>
/// <param name="Video">The video queue bounds.</param>
/// <param name="Audio">The audio queue bounds.</param>
/// <param name="StartupHold">How long a track waits for the other track's first audio.</param>
/// <param name="IdleRelease">How long a track waits for a started track that has gone quiet.</param>
internal sealed record MultiplexerLimits(MultiplexerTrackLimits Video, MultiplexerTrackLimits Audio, TimeSpan StartupHold, TimeSpan IdleRelease)
{
    /// <summary>The media-duration bound of each queue, and the longest video is retained.</summary>
    public static readonly TimeSpan RetentionWindow = TimeSpan.FromSeconds(5);

    /// <summary>The default startup hold.</summary>
    public static readonly TimeSpan DefaultStartupHold = TimeSpan.FromSeconds(1);

    /// <summary>The default idle release.</summary>
    public static readonly TimeSpan DefaultIdleRelease = TimeSpan.FromSeconds(1);

    /// <summary>The audio unit bound.</summary>
    public const int AudioUnits = 256;

    /// <summary>The largest canonical audio tag body: 6144 bits per channel for two AAC-LC channels, and the 2-byte FLV AAC header.</summary>
    public const int MaxAudioUnitBytes = 1538;

    /// <summary>The fewest video units a queue may hold, whatever the frame-rate limit.</summary>
    private const int MinVideoUnits = 16;

    /// <summary>Seconds of the encoder's VBV buffer, which the canonical arguments set to twice the maximum bitrate.</summary>
    private const int VbvSeconds = 2;

    /// <summary>
    /// Derives the bounds from the transcoding options: video holds the retention window at the frame-rate limit and
    /// at the canonical maximum bitrate plus its VBV buffer; audio holds <see cref="AudioUnits"/> maximum-size AAC
    /// units, more than the retention window at 48 kHz.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <returns>The limits.</returns>
    public static MultiplexerLimits FromOptions(TranscodingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var seconds = (long)RetentionWindow.TotalSeconds;
        var videoUnits = (int)Math.Max(MinVideoUnits, seconds * options.MaxInputFrameRate);
        var videoBytes = (seconds + VbvSeconds) * options.Video.MaxBitrateKilobits * 1000L / 8;
        return new MultiplexerLimits(
            new MultiplexerTrackLimits(videoUnits, videoBytes, RetentionWindow),
            new MultiplexerTrackLimits(AudioUnits, (long)AudioUnits * MaxAudioUnitBytes, RetentionWindow),
            DefaultStartupHold,
            DefaultIdleRelease);
    }
}

/// <summary>Merges one publisher's encoded audio and video into the single sequential downstream call stream.</summary>
/// <remarks>
/// <para>
/// Each track has one producer: calls on one <see cref="TrackInput"/> are sequential. The two inputs, <see cref="NotifyMetadata"/>,
/// <see cref="FlushAsync"/> and disposal may be used concurrently. <see cref="Start"/> starts the output task, which is
/// then the only caller of the downstream stage's <see cref="IPublishSessionHandler.OnMessageAsync"/>.
/// </para>
/// <para>
/// Every submitted payload is released exactly once: delivered (the downstream stage then owns it), discarded when the
/// multiplexer stops without delivering it, or released by the rejected submission.
/// </para>
/// </remarks>
internal sealed partial class MediaMultiplexer : IAsyncDisposable
{
    /// <summary>The publishing session.</summary>
    private readonly PublisherSession _publisher;

    /// <summary>The downstream stage.</summary>
    private readonly IPublishSessionHandler _downstream;

    /// <summary>The bounds and hold times.</summary>
    private readonly MultiplexerLimits _limits;

    /// <summary>The service-wide buffer budget.</summary>
    private readonly TranscodingBufferBudget _budget;

    /// <summary>Metrics.</summary>
    private readonly TranscodingMetrics _metrics;

    /// <summary>Logger.</summary>
    private readonly ILogger _logger;

    /// <summary>The clock for hold deadlines.</summary>
    private readonly TimeProvider _time;

    /// <summary>Told about a failure, once.</summary>
    private readonly Action<Exception>? _onFault;

    /// <summary>Cancelled by the owner's token or by disposal.</summary>
    private readonly CancellationTokenSource _stop;

    /// <summary>Guards all queue and lifecycle state.</summary>
    private readonly Lock _gate = new();

    /// <summary>Wakes the output task.</summary>
    private readonly RtpSenderSignal _wake = new();

    /// <summary>The video queue.</summary>
    private readonly TrackQueue _video;

    /// <summary>The audio queue.</summary>
    private readonly TrackQueue _audio;

    /// <summary>Wakes the output task at a hold deadline; created by <see cref="Start"/>.</summary>
    private ITimer? _timer;

    /// <summary>The timestamp the timer is armed for, or <see cref="long.MaxValue"/>.</summary>
    private long _armed = long.MaxValue;

    /// <summary>Wakes the output task on cancellation.</summary>
    private CancellationTokenRegistration _registration;

    /// <summary>The output task.</summary>
    private Task? _loop;

    /// <summary>Whether units are no longer accepted or delivered.</summary>
    private bool _closed;

    /// <summary>The first failure.</summary>
    private Exception? _fault;

    /// <summary>Whether a dequeued unit is being delivered.</summary>
    private bool _delivering;

    /// <summary>Whether publisher metadata changed and the next submitted unit must be preceded by a data message.</summary>
    private bool _metadataPending;

    /// <summary>The pending flush, completed when every unit queued before it has been delivered.</summary>
    private TaskCompletionSource? _flush;

    /// <summary>When the first unit was accepted.</summary>
    private long? _firstAccepted;

    /// <summary>Whether a unit was delivered.</summary>
    private bool _firstDelivered;

    /// <summary>Units released early, for the summary.</summary>
    private long _earlyReleases;

    /// <summary>Units discarded, for the summary.</summary>
    private long _discarded;

    /// <summary>Initializes a new instance of the <see cref="MediaMultiplexer"/> class.</summary>
    /// <param name="publisher">The publishing session.</param>
    /// <param name="downstream">The downstream stage.</param>
    /// <param name="limits">The bounds and hold times.</param>
    /// <param name="budget">The service-wide buffer budget queued payload bytes are reserved from.</param>
    /// <param name="metrics">Metrics.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="time">The clock for hold deadlines.</param>
    /// <param name="onFault">Told about the first failure of the downstream stage or a producer.</param>
    /// <param name="cancellationToken">Stops the multiplexer without delivering what is queued.</param>
    public MediaMultiplexer(PublisherSession publisher, IPublishSessionHandler downstream, MultiplexerLimits limits, TranscodingBufferBudget budget, TranscodingMetrics metrics, ILogger logger, TimeProvider time, Action<Exception>? onFault, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(downstream);
        ArgumentNullException.ThrowIfNull(limits);
        _publisher = publisher;
        _downstream = downstream;
        _limits = limits;
        _budget = budget;
        _metrics = metrics;
        _logger = logger;
        _time = time;
        _onFault = onFault;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _video = new TrackQueue(MediaTrack.Video, limits.Video);
        _audio = new TrackQueue(MediaTrack.Audio, limits.Audio);
        Video = new TrackInput(this, _video);
        Audio = new TrackInput(this, _audio);
    }

    /// <summary>Gets the video input.</summary>
    public TrackInput Video { get; }

    /// <summary>Gets the audio input.</summary>
    public TrackInput Audio { get; }

    /// <summary>Gets the first failure, if any.</summary>
    public Exception? Fault
    {
        get
        {
            lock (_gate)
            {
                return _fault;
            }
        }
    }

    /// <summary>Gets a task that completes when the output task has stopped; it never faults.</summary>
    public Task Completion => _loop ?? Task.CompletedTask;

    /// <summary>Starts the output task.</summary>
    /// <exception cref="InvalidOperationException">Already started.</exception>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                throw new InvalidOperationException("The multiplexer has already started.");
            }

            _timer = _time.CreateTimer(static state => ((MediaMultiplexer)state!).OnTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _registration = _stop.Token.UnsafeRegister(static state => ((MediaMultiplexer)state!).Wake(), this);
            _loop = Task.Run(RunAsync, CancellationToken.None);
        }
    }

    /// <summary>Records that publisher metadata changed; a data message precedes the next unit submitted on either track.</summary>
    public void NotifyMetadata()
    {
        lock (_gate)
        {
            _metadataPending = true;
        }
    }

    /// <summary>
    /// Releases every unit queued now, in merge order and without holding for either track, and waits until they have
    /// been delivered. Units submitted meanwhile wait for the normal rules.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes once delivered.</returns>
    /// <exception cref="InvalidOperationException">The multiplexer failed, or a flush is already pending.</exception>
    /// <exception cref="OperationCanceledException">The multiplexer was stopped, or <paramref name="cancellationToken"/> was cancelled.</exception>
    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        Task task;
        lock (_gate)
        {
            ThrowIfClosed();
            if (_flush is not null)
            {
                throw new InvalidOperationException("A flush is already pending.");
            }

            _video.FlushRemaining = _video.Units.Count;
            _audio.FlushRemaining = _audio.Units.Count;
            if (_video.FlushRemaining == 0 && _audio.FlushRemaining == 0 && !_delivering)
            {
                return;
            }

            _flush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            task = _flush.Task;
        }

        _wake.Set();
        await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Completes both inputs and waits until everything queued has been delivered or the multiplexer stopped.</summary>
    /// <returns>A task that completes when the output task has stopped.</returns>
    public Task CompleteAsync()
    {
        Video.Complete();
        Audio.Complete();
        return Completion;
    }

    /// <summary>Stops the multiplexer, discarding undelivered units, and waits for the output task.</summary>
    /// <returns>A task that completes when stopped and every queued payload has been released.</returns>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        Task? loop;
        lock (_gate)
        {
            loop = _loop;
        }

        if (loop is null)
        {
            Close(null);
        }
        else
        {
            await loop.ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    /// <summary>Gets the units queued on a track; for tests.</summary>
    /// <param name="track">The track.</param>
    /// <returns>The count.</returns>
    internal int QueuedUnits(MediaTrack track)
    {
        lock (_gate)
        {
            return Queue(track).Units.Count;
        }
    }

    /// <summary>Gets the payload bytes queued on a track; for tests.</summary>
    /// <param name="track">The track.</param>
    /// <returns>The bytes.</returns>
    internal long QueuedBytes(MediaTrack track)
    {
        lock (_gate)
        {
            return Queue(track).Bytes;
        }
    }

    /// <summary>Compares when two units must be released; negative when <paramref name="video"/> goes first.</summary>
    /// <param name="video">The video unit.</param>
    /// <param name="audio">The audio unit.</param>
    /// <returns>The order.</returns>
    internal static int CompareRelease(in EncodedAccessUnit video, in EncodedAccessUnit audio)
    {
        if (video.Kind == EncodedUnitKind.SequenceEnd || audio.Kind == EncodedUnitKind.SequenceEnd)
        {
            return audio.Kind == EncodedUnitKind.SequenceEnd ? -1 : 1;
        }

        var left = (Int128)video.Timestamp * video.TimeBase.Numerator * audio.TimeBase.Denominator;
        var right = (Int128)audio.Timestamp * audio.TimeBase.Numerator * video.TimeBase.Denominator;
        if (left != right)
        {
            return left < right ? -1 : 1;
        }

        return audio.Kind == EncodedUnitKind.SequenceStart && video.Kind != EncodedUnitKind.SequenceStart ? 1 : -1;
    }

    /// <summary>Returns a track's queue.</summary>
    /// <param name="track">The track.</param>
    /// <returns>The queue.</returns>
    private TrackQueue Queue(MediaTrack track) => track == MediaTrack.Video ? _video : _audio;

    /// <summary>Returns the other track's queue.</summary>
    /// <param name="queue">A queue.</param>
    /// <returns>The other.</returns>
    private TrackQueue Other(TrackQueue queue) => ReferenceEquals(queue, _video) ? _audio : _video;

    /// <summary>Throws if closed; called under <see cref="_gate"/>.</summary>
    private void ThrowIfClosed()
    {
        if (!_closed)
        {
            return;
        }

        if (_fault is { } fault)
        {
            throw new InvalidOperationException("The media multiplexer failed.", fault);
        }

        throw new OperationCanceledException("The media multiplexer has stopped.", _stop.Token);
    }

    /// <summary>Wakes the output task and every waiting producer.</summary>
    private void Wake()
    {
        _wake.Set();
        _video.Space.Set();
        _audio.Space.Set();
    }

    /// <summary>Queues a unit, waiting for room.</summary>
    /// <param name="queue">The track queue.</param>
    /// <param name="unit">The unit; ownership transfers.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when queued.</returns>
    private async ValueTask SubmitAsync(TrackQueue queue, EncodedAccessUnit unit, CancellationToken cancellationToken)
    {
        var owned = true;
        var budgetWaiter = false;
        long waitStarted = 0;
        try
        {
            ArgumentNullException.ThrowIfNull(unit.Payload);
            if (unit.TimeBase.Numerator <= 0 || unit.TimeBase.Denominator <= 0)
            {
                throw new ArgumentException("The unit has no time base.", nameof(unit));
            }

            var length = unit.Payload.Length;
            var milliseconds = unit.TimeBase.Rescale(unit.Timestamp, MediaTimeBase.Milliseconds);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MultiplexerEarlyRelease? full;
                lock (_gate)
                {
                    ThrowIfClosed();
                    if (queue.Completed)
                    {
                        throw new InvalidOperationException($"The {queue.Name} input has completed.");
                    }

                    full = Admit(queue, unit, milliseconds, length);
                    if (full is null)
                    {
                        owned = false;
                        queue.Waiting = false;
                        if (waitStarted != 0)
                        {
                            _metrics.MultiplexerBackpressure(queue.Track, _time.GetElapsedTime(waitStarted).TotalSeconds);
                        }
                    }
                    else
                    {
                        queue.Waiting = true;
                        queue.WaitLimit = full.Value;
                    }
                }

                if (full is null)
                {
                    _wake.Set();
                    return;
                }

                if (waitStarted == 0)
                {
                    waitStarted = _time.GetTimestamp();
                    _metrics.MultiplexerSaturated(queue.Track, full.Value);
                    if (queue.LoggedSaturations.Add(full.Value))
                    {
                        LogSaturated(_publisher.SessionId, queue.Track, full.Value);
                    }
                    else
                    {
                        LogRepeatedSaturation(_publisher.SessionId, queue.Track, full.Value);
                    }
                }

                _wake.Set();
                if (full == MultiplexerEarlyRelease.Budget && !budgetWaiter)
                {
                    // Retry once registered, so shared bytes released since the failed reservation are not missed.
                    budgetWaiter = true;
                    _budget.AddWaiter(queue.Space);
                    continue;
                }

                using (cancellationToken.CanBeCanceled ? cancellationToken.UnsafeRegister(static state => ((RtpSenderSignal)state!).Set(), queue.Space) : default)
                {
                    await queue.Space.WaitAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (budgetWaiter)
            {
                _budget.RemoveWaiter(queue.Space);
            }

            if (owned)
            {
                lock (_gate)
                {
                    queue.Waiting = false;
                }

                unit.Payload?.Dispose();
                _metrics.MultiplexerUnit(queue.Track, MultiplexerUnitOutcome.Rejected);
            }
        }
    }

    /// <summary>Queues a unit if its queue and the budget have room; called under <see cref="_gate"/>.</summary>
    /// <param name="queue">The queue.</param>
    /// <param name="unit">The unit.</param>
    /// <param name="milliseconds">Its timestamp in milliseconds.</param>
    /// <param name="length">Its payload length.</param>
    /// <returns><see langword="null"/> if queued; otherwise the bound that is full.</returns>
    private MultiplexerEarlyRelease? Admit(TrackQueue queue, in EncodedAccessUnit unit, long milliseconds, int length)
    {
        long floor = 0;
        if (queue.Units.Count == 0)
        {
            // An empty queue holds its head on a budget floor, so a producer progresses however full the shared pool is.
            floor = Math.Min(length, _budget.FloorBytes);
            if (!_budget.TryUseFloor(floor))
            {
                floor = 0;
            }

            if (length > floor && !_budget.TryReserve(length - floor))
            {
                if (floor > 0)
                {
                    _budget.ReturnFloor(floor);
                }

                return MultiplexerEarlyRelease.Budget;
            }
        }
        else
        {
            var limits = queue.Limits;
            if (queue.Units.Count >= limits.MaxUnits)
            {
                return MultiplexerEarlyRelease.Units;
            }

            if (queue.Bytes + length > limits.MaxBytes)
            {
                return MultiplexerEarlyRelease.Bytes;
            }

            if (milliseconds - queue.Units.Peek().Milliseconds > (long)limits.MaxDuration.TotalMilliseconds)
            {
                return MultiplexerEarlyRelease.Duration;
            }

            if (!_budget.TryReserve(length))
            {
                return MultiplexerEarlyRelease.Budget;
            }
        }

        var now = _time.GetTimestamp();
        queue.Units.Enqueue(new Entry(unit, milliseconds, now, _metadataPending, floor));
        _metadataPending = false;
        queue.Newest = milliseconds;
        queue.Bytes += length;
        queue.LastSubmit = now;
        queue.PeakUnits = Math.Max(queue.PeakUnits, queue.Units.Count);
        if (unit.Kind == EncodedUnitKind.Sample && (unit.IsSyncPoint || queue.Track == MediaTrack.Audio))
        {
            queue.Started = true;
        }

        _firstAccepted ??= now;

        _metrics.MultiplexerUnit(queue.Track, MultiplexerUnitOutcome.Accepted);
        _metrics.MultiplexerQueued(queue.Track, 1, length);
        return null;
    }

    /// <summary>The output task.</summary>
    /// <returns>A task that completes when stopped; it never faults.</returns>
    private async Task RunAsync()
    {
        var token = _stop.Token;
        var outcome = "completed";
        try
        {
            while (true)
            {
                Entry entry = default;
                TrackQueue? queue;
                lock (_gate)
                {
                    if (_closed)
                    {
                        outcome = _fault is null ? "stopped" : "failed";
                        return;
                    }

                    if (token.IsCancellationRequested)
                    {
                        outcome = "stopped";
                        return;
                    }

                    var now = _time.GetTimestamp();
                    queue = Next(now, out var early, out var deadline);
                    if (queue is null)
                    {
                        if (_video.Completed && _audio.Completed && _video.Units.Count == 0 && _audio.Units.Count == 0)
                        {
                            return;
                        }

                        Arm(now, deadline);
                    }
                    else
                    {
                        entry = Dequeue(queue, now, early);
                        _delivering = true;
                    }
                }

                if (queue is null)
                {
                    await _wake.WaitAsync().ConfigureAwait(false);
                    continue;
                }

                await DeliverAsync(queue.Track, entry, token).ConfigureAwait(false);
                TaskCompletionSource? flushed = null;
                lock (_gate)
                {
                    _delivering = false;
                    if (_flush is not null && _video.FlushRemaining == 0 && _audio.FlushRemaining == 0)
                    {
                        flushed = _flush;
                        _flush = null;
                    }
                }

                flushed?.TrySetResult();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            outcome = "stopped";
        }
        catch (Exception ex)
        {
            outcome = "failed";
            Fail(ex, downstream: true);
        }
        finally
        {
            Close(null);
            _registration.Dispose();
            _timer?.Dispose();
            long discarded;
            long early;
            lock (_gate)
            {
                discarded = _discarded;
                early = _earlyReleases;
            }

            LogSummary(_publisher.SessionId, outcome, _video.Delivered, _audio.Delivered, discarded, _video.PeakUnits, _audio.PeakUnits, early);
        }
    }

    /// <summary>Chooses the queue whose head is released next; called under <see cref="_gate"/>.</summary>
    /// <param name="now">The current timestamp.</param>
    /// <param name="early">Set when the head is released before ordering is known to be safe.</param>
    /// <param name="deadline">Set to when the decision may change without a submission, or <see cref="long.MaxValue"/>.</param>
    /// <returns>The queue, or <see langword="null"/> to wait.</returns>
    private TrackQueue? Next(long now, out MultiplexerEarlyRelease? early, out long deadline)
    {
        early = null;
        deadline = long.MaxValue;
        var flushing = _flush is not null;
        var video = _video.Units.Count > 0 && (!flushing || _video.FlushRemaining > 0);
        var audio = _audio.Units.Count > 0 && (!flushing || _audio.FlushRemaining > 0);
        if (video && audio)
        {
            return CompareRelease(_video.Units.Peek().Unit, _audio.Units.Peek().Unit) <= 0 ? _video : _audio;
        }

        if (!video && !audio)
        {
            return null;
        }

        var queue = video ? _video : _audio;
        var other = Other(queue);
        if (flushing || queue.Units.Peek().Unit.Kind == EncodedUnitKind.SequenceStart || !other.Expected || other.Completed)
        {
            return queue;
        }

        if (Saturated(queue) is { } limit)
        {
            early = limit;
            return queue;
        }

        if (!other.Started)
        {
            if (other.Track == MediaTrack.Video)
            {
                // HLS discards audio before the first video keyframe; keep it until the keyframe orders it.
                return null;
            }

            if (other.StartupExpired)
            {
                return queue;
            }

            var startup = other.StartupDeadline ??= now + Ticks(_limits.StartupHold);
            if (now >= startup || queue.Span >= (long)_limits.StartupHold.TotalMilliseconds)
            {
                other.StartupExpired = true;
                early = MultiplexerEarlyRelease.Startup;
                return queue;
            }

            deadline = startup;
            return null;
        }

        var idle = other.LastSubmit + Ticks(_limits.IdleRelease);
        if (now >= idle)
        {
            early = MultiplexerEarlyRelease.Idle;
            return queue;
        }

        deadline = idle;
        return null;
    }

    /// <summary>Returns the bound a queue has reached, if any; called under <see cref="_gate"/>.</summary>
    /// <param name="queue">The queue.</param>
    /// <returns>The bound, or <see langword="null"/>.</returns>
    private MultiplexerEarlyRelease? Saturated(TrackQueue queue)
    {
        var limits = queue.Limits;
        return queue.Waiting ? queue.WaitLimit
            : queue.Units.Count >= limits.MaxUnits ? MultiplexerEarlyRelease.Units
            : queue.Bytes >= limits.MaxBytes ? MultiplexerEarlyRelease.Bytes
            : queue.Span >= (long)limits.MaxDuration.TotalMilliseconds ? MultiplexerEarlyRelease.Duration
            : _budget.SharedExhausted ? MultiplexerEarlyRelease.Budget
            : null;
    }

    /// <summary>Converts a duration to timestamp ticks.</summary>
    /// <param name="duration">The duration.</param>
    /// <returns>The ticks.</returns>
    private long Ticks(TimeSpan duration) => (long)(duration.TotalSeconds * _time.TimestampFrequency);

    /// <summary>Arms the timer for a deadline; called under <see cref="_gate"/>.</summary>
    /// <param name="now">The current timestamp.</param>
    /// <param name="deadline">The deadline, or <see cref="long.MaxValue"/> for none.</param>
    private void Arm(long now, long deadline)
    {
        if (deadline == long.MaxValue || Interlocked.Exchange(ref _armed, deadline) == deadline)
        {
            return;
        }

        var due = TimeSpan.FromSeconds((double)(deadline - now) / _time.TimestampFrequency);
        _timer!.Change(due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Wakes the output task at a deadline; a later wait arms the timer again.</summary>
    private void OnTimer()
    {
        Interlocked.Exchange(ref _armed, long.MaxValue);
        _wake.Set();
    }

    /// <summary>Removes a queue's head; called under <see cref="_gate"/>.</summary>
    /// <param name="queue">The queue.</param>
    /// <param name="now">The current timestamp.</param>
    /// <param name="early">Why it is released early, if it is.</param>
    /// <returns>The entry.</returns>
    private Entry Dequeue(TrackQueue queue, long now, MultiplexerEarlyRelease? early)
    {
        var entry = queue.Units.Dequeue();
        var length = entry.Unit.Payload.Length;
        queue.Bytes -= length;
        ReleaseBudget(entry, length);
        if (queue.FlushRemaining > 0)
        {
            queue.FlushRemaining--;
        }

        _metrics.MultiplexerQueued(queue.Track, -1, -length);
        _metrics.MultiplexerHeld(queue.Track, _time.GetElapsedTime(entry.Enqueued, now).TotalSeconds);
        if (early is { } reason)
        {
            _earlyReleases++;
            _metrics.MultiplexerReleasedEarly(queue.Track, reason);
        }

        if (queue.Waiting)
        {
            // One release makes room; the producer marks the queue again if it is still full.
            queue.Waiting = false;
            queue.Space.Set();
        }

        return entry;
    }

    /// <summary>Returns a removed entry's floor and shared bytes to the budget.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="length">Its payload length.</param>
    private void ReleaseBudget(in Entry entry, int length)
    {
        if (entry.FloorBytes > 0)
        {
            _budget.ReturnFloor(entry.FloorBytes);
        }

        if (length > entry.FloorBytes)
        {
            _budget.Release(length - entry.FloorBytes);
        }
    }

    /// <summary>Delivers a dequeued unit, preceded by a data message if metadata changed before it was submitted.</summary>
    /// <param name="track">Its track.</param>
    /// <param name="entry">The entry; its payload is released or handed over.</param>
    /// <param name="token">Cancellation.</param>
    /// <returns>A task that completes when the downstream stage has accepted it.</returns>
    private async ValueTask DeliverAsync(MediaTrack track, Entry entry, CancellationToken token)
    {
        var unit = entry.Unit;
        var timestamp = (uint)entry.Milliseconds;
        PublishedMessage? message = null;
        try
        {
            if (entry.Metadata)
            {
                var data = new PublishedMessage(PublishedMessageKind.Data, 18, timestamp, new PooledBuffer(ArrayPool<byte>.Shared));
                try
                {
                    await _downstream.OnMessageAsync(_publisher, data, token).ConfigureAwait(false);
                }
                catch
                {
                    data.Dispose();
                    throw;
                }
            }

            message = track == MediaTrack.Video
                ? new PublishedMessage(PublishedMessageKind.Video, 9, timestamp, unit.Payload)
                : new PublishedMessage(PublishedMessageKind.Audio, 8, timestamp, unit.Payload);
            await _downstream.OnMessageAsync(_publisher, message, token).ConfigureAwait(false);
        }
        catch
        {
            if (message is null)
            {
                unit.Payload.Dispose();
            }
            else
            {
                message.Dispose();
            }

            lock (_gate)
            {
                _discarded++;
            }

            _metrics.MultiplexerUnit(track, MultiplexerUnitOutcome.Discarded);
            throw;
        }

        Queue(track).Delivered++;
        _metrics.MultiplexerUnit(track, MultiplexerUnitOutcome.Forwarded);
        if (!_firstDelivered && _firstAccepted is { } first)
        {
            _firstDelivered = true;
            _metrics.MultiplexerStartup(_time.GetElapsedTime(first).TotalSeconds);
        }
    }

    /// <summary>Records the first failure, stops accepting units and tells the owner.</summary>
    /// <param name="exception">The failure.</param>
    /// <param name="downstream">Whether the downstream stage failed, rather than a producer.</param>
    private void Fail(Exception exception, bool downstream)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _fault = exception;
        }

        _metrics.MultiplexerFailure(downstream);
        LogFailed(_publisher.SessionId, downstream ? "downstream" : "producer", exception.GetType().Name);
        Close(exception);
        _onFault?.Invoke(exception);
    }

    /// <summary>Stops accepting units, releases every queued payload and wakes everyone waiting.</summary>
    /// <param name="fault">The failure, if closing because of one.</param>
    private void Close(Exception? fault)
    {
        List<Entry>? discarded = null;
        TaskCompletionSource? flush;
        lock (_gate)
        {
            _closed = true;
            flush = _flush;
            _flush = null;
            foreach (var queue in (ReadOnlySpan<TrackQueue>)[_video, _audio])
            {
                while (queue.Units.TryDequeue(out var entry))
                {
                    (discarded ??= []).Add(entry);
                    _discarded++;
                    var length = entry.Unit.Payload.Length;
                    queue.Bytes -= length;
                    ReleaseBudget(entry, length);
                    _metrics.MultiplexerQueued(queue.Track, -1, -length);
                    _metrics.MultiplexerUnit(queue.Track, MultiplexerUnitOutcome.Discarded);
                }

                queue.FlushRemaining = 0;
            }
        }

        if (discarded is not null)
        {
            foreach (var entry in discarded)
            {
                entry.Unit.Payload.Dispose();
            }
        }

        if (flush is not null)
        {
            if ((fault ?? Fault) is { } failure)
            {
                flush.TrySetException(new InvalidOperationException("The media multiplexer failed.", failure));
            }
            else
            {
                flush.TrySetCanceled(_stop.Token);
            }
        }

        Wake();
    }

    /// <summary>Logs the first time a track's producer waited for a bound.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="track">The track.</param>
    /// <param name="limit">The bound.</param>
    [LoggerMessage(EventId = 10020, Level = LogLevel.Warning, Message = "Session {SessionId} {Track} multiplexer queue is full ({Limit}); its producer waits while queued media is released early. Further waits are logged at Debug")]
    private partial void LogSaturated(Guid sessionId, MediaTrack track, MultiplexerEarlyRelease limit);

    /// <summary>Logs a repeated wait.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="track">The track.</param>
    /// <param name="limit">The bound.</param>
    [LoggerMessage(EventId = 10021, Level = LogLevel.Debug, Message = "Session {SessionId} {Track} multiplexer queue is full ({Limit})")]
    private partial void LogRepeatedSaturation(Guid sessionId, MediaTrack track, MultiplexerEarlyRelease limit);

    /// <summary>Logs a failure.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="source">What failed.</param>
    /// <param name="error">The exception type.</param>
    [LoggerMessage(EventId = 10022, Level = LogLevel.Warning, Message = "Session {SessionId} media multiplexer stopped: the {Source} failed ({Error})")]
    private partial void LogFailed(Guid sessionId, string source, string error);

    /// <summary>Logs the summary.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="outcome">How it stopped.</param>
    /// <param name="video">Video units delivered.</param>
    /// <param name="audio">Audio units delivered.</param>
    /// <param name="discarded">Units discarded.</param>
    /// <param name="videoPeak">The most video units queued.</param>
    /// <param name="audioPeak">The most audio units queued.</param>
    /// <param name="early">Units released early.</param>
    [LoggerMessage(EventId = 10023, Level = LogLevel.Debug, Message = "Media multiplexer for session {SessionId} {Outcome}: {Video} video and {Audio} audio units delivered, {Discarded} discarded, at most {VideoPeak} video and {AudioPeak} audio units queued, {Early} released early")]
    private partial void LogSummary(Guid sessionId, string outcome, long video, long audio, long discarded, int videoPeak, int audioPeak, long early);

    /// <summary>One track's producer side.</summary>
    internal sealed class TrackInput
    {
        /// <summary>The multiplexer.</summary>
        private readonly MediaMultiplexer _owner;

        /// <summary>The queue.</summary>
        private readonly TrackQueue _queue;

        /// <summary>Initializes a new instance of the <see cref="TrackInput"/> class.</summary>
        /// <param name="owner">The multiplexer.</param>
        /// <param name="queue">The queue.</param>
        internal TrackInput(MediaMultiplexer owner, TrackQueue queue)
        {
            _owner = owner;
            _queue = queue;
        }

        /// <summary>Gets the track.</summary>
        public MediaTrack Track => _queue.Track;

        /// <summary>
        /// Queues a unit, waiting while the track's queue is full; the multiplexer then releases that queue's oldest
        /// units without waiting for the other track, so the wait ends as soon as the downstream stage accepts them.
        /// </summary>
        /// <param name="unit">The unit. Ownership of its payload transfers to this method whether it succeeds or throws.</param>
        /// <param name="cancellationToken">Cancellation; ends a wait.</param>
        /// <returns>A task that completes when queued.</returns>
        /// <exception cref="InvalidOperationException">The input completed or the multiplexer failed.</exception>
        /// <exception cref="OperationCanceledException">Cancelled, or the multiplexer stopped.</exception>
        public ValueTask SubmitAsync(EncodedAccessUnit unit, CancellationToken cancellationToken) => _owner.SubmitAsync(_queue, unit, cancellationToken);

        /// <summary>
        /// Declares whether the track is expected to produce, starting a new epoch: until its first sync sample, the
        /// other track waits for it as for a track that has not started. An absent track is never waited for.
        /// </summary>
        /// <param name="expected">Whether the track will produce.</param>
        public void Declare(bool expected)
        {
            lock (_owner._gate)
            {
                _queue.Expected = expected;
                _queue.Started = false;
                _queue.StartupDeadline = null;
                _queue.StartupExpired = false;
            }

            _owner._wake.Set();
        }

        /// <summary>Completes the track: it submits nothing more, and the other track no longer waits for it.</summary>
        public void Complete()
        {
            lock (_owner._gate)
            {
                _queue.Completed = true;
            }

            _owner._wake.Set();
        }

        /// <summary>Reports that the producer failed: the multiplexer stops, discarding what is queued, and its owner is told.</summary>
        /// <param name="exception">The failure.</param>
        public void Fail(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            _owner.Fail(exception, downstream: false);
        }
    }

    /// <summary>A queued unit.</summary>
    /// <param name="Unit">The unit.</param>
    /// <param name="Milliseconds">Its timestamp in milliseconds.</param>
    /// <param name="Enqueued">When it was queued.</param>
    /// <param name="Metadata">Whether a data message precedes it.</param>
    /// <param name="FloorBytes">The bytes held on a budget floor; the rest are shared.</param>
    internal readonly record struct Entry(EncodedAccessUnit Unit, long Milliseconds, long Enqueued, bool Metadata, long FloorBytes);

    /// <summary>One track's queue and state; guarded by the multiplexer's lock except where noted.</summary>
    /// <param name="track">The track.</param>
    /// <param name="limits">The bounds.</param>
    internal sealed class TrackQueue(MediaTrack track, MultiplexerTrackLimits limits)
    {
        /// <summary>Gets the track.</summary>
        public MediaTrack Track { get; } = track;

        /// <summary>Gets the track name.</summary>
        public string Name => Track == MediaTrack.Video ? "video" : "audio";

        /// <summary>Gets the bounds.</summary>
        public MultiplexerTrackLimits Limits { get; } = limits;

        /// <summary>Gets the units, oldest first.</summary>
        public Queue<Entry> Units { get; } = new();

        /// <summary>Gets the signal its producer waits on for room; thread-safe.</summary>
        public RtpSenderSignal Space { get; } = new();

        /// <summary>Gets the bounds whose first wait was logged; touched only by the producer.</summary>
        public HashSet<MultiplexerEarlyRelease> LoggedSaturations { get; } = [];

        /// <summary>Gets or sets the queued payload bytes.</summary>
        public long Bytes { get; set; }

        /// <summary>Gets or sets a value indicating whether the track is expected to produce.</summary>
        public bool Expected { get; set; } = true;

        /// <summary>Gets or sets a value indicating whether the track submitted a sync sample in this epoch.</summary>
        public bool Started { get; set; }

        /// <summary>Gets or sets a value indicating whether the track was completed.</summary>
        public bool Completed { get; set; }

        /// <summary>Gets or sets when the other track stops waiting for this track's first unit, once it started waiting.</summary>
        public long? StartupDeadline { get; set; }

        /// <summary>Gets or sets a value indicating whether the other track stopped waiting for this track's first unit.</summary>
        public bool StartupExpired { get; set; }

        /// <summary>Gets or sets when the track last queued a unit.</summary>
        public long LastSubmit { get; set; }

        /// <summary>Gets or sets a value indicating whether its producer waits for room.</summary>
        public bool Waiting { get; set; }

        /// <summary>Gets or sets the bound its producer waits for.</summary>
        public MultiplexerEarlyRelease WaitLimit { get; set; }

        /// <summary>Gets or sets the units still to release for the pending flush.</summary>
        public int FlushRemaining { get; set; }

        /// <summary>Gets or sets the most units queued at once.</summary>
        public int PeakUnits { get; set; }

        /// <summary>Gets or sets the units delivered; touched only by the output task.</summary>
        public long Delivered { get; set; }

        /// <summary>Gets or sets the timestamp in milliseconds of the newest queued unit.</summary>
        public long Newest { get; set; }

        /// <summary>Gets the queued media in milliseconds, from the oldest to the newest unit.</summary>
        public long Span => Units.Count < 2 ? 0 : Math.Max(0, Newest - Units.Peek().Milliseconds);
    }
}
