// One publisher's transcoding pipeline. The RTMP receive loop submits validated source samples into a bounded queue
// (frame counts per track, bytes per session and a service-wide byte budget); a writer task feeds them to the
// session's transcoder process as FLV, and a reader task turns the canonical FLV output back into RTMP messages for
// the existing media/HLS stage, which therefore only ever sees canonical H.264/AAC. The reader is the only caller of
// the downstream handler while the session runs, so its calls stay sequential. The process is restarted when the
// source configuration changes or the timeline jumps; a failure ends only this session.

using System.Diagnostics;
using System.Threading.Channels;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Rtmp.Publishing;

namespace RTSP.IngestService.Transcoding;

/// <summary>A service-wide budget of bytes held in transcoding queues.</summary>
/// <param name="limit">The budget in bytes.</param>
internal sealed class TranscodingBufferBudget(long limit)
{
    /// <summary>Bytes reserved.</summary>
    private long _used;

    /// <summary>Gets the bytes reserved.</summary>
    public long Used => Interlocked.Read(ref _used);

    /// <summary>Gets the budget in bytes.</summary>
    public long Limit { get; } = limit;

    /// <summary>Reserves bytes if the budget allows.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns><see langword="true"/> if reserved.</returns>
    public bool TryReserve(long bytes)
    {
        var used = Interlocked.Read(ref _used);
        while (true)
        {
            if (used + bytes > Limit)
            {
                return false;
            }

            var observed = Interlocked.CompareExchange(ref _used, used + bytes, used);
            if (observed == used)
            {
                return true;
            }

            used = observed;
        }
    }

    /// <summary>Releases reserved bytes.</summary>
    /// <param name="bytes">The bytes.</param>
    public void Release(long bytes) => Interlocked.Add(ref _used, -bytes);
}

/// <summary>The transcoding pipeline of one publishing session.</summary>
/// <remarks>
/// <see cref="SubmitAsync"/>, <see cref="MarkVideoBroken"/> and <see cref="NotifyMetadata"/> are called only from the
/// session's receive loop; <see cref="StopAsync"/> once, after the receive loop has stopped.
/// </remarks>
internal sealed partial class TranscodingSession : IDisposable
{
    /// <summary>The largest decode-time span one process covers before it is restarted, in milliseconds.</summary>
    internal const long MaxProcessTimeline = int.MaxValue - 3_600_000L;

    /// <summary>How long one write to the transcoder may block before the transcoder is declared stalled.</summary>
    internal static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How much media, in milliseconds, is held before the first process starts while a track is unconfigured.</summary>
    private const long StartupHold = 1000;

    /// <summary>Entries in the latency ring.</summary>
    private const int LatencyEntries = 128;

    /// <summary>The publishing session.</summary>
    private readonly PublisherSession _publisher;

    /// <summary>The downstream media stage.</summary>
    private readonly IPublishSessionHandler _downstream;

    /// <summary>The transcoding options.</summary>
    private readonly TranscodingOptions _options;

    /// <summary>The media limits.</summary>
    private readonly MediaOptions _media;

    /// <summary>Starts transcoders.</summary>
    private readonly ITranscoderProcessFactory _factory;

    /// <summary>The service-wide buffer budget.</summary>
    private readonly TranscodingBufferBudget _budget;

    /// <summary>Metrics.</summary>
    private readonly TranscodingMetrics _metrics;

    /// <summary>Logger.</summary>
    private readonly ILogger _logger;

    /// <summary>The canonical IDR interval.</summary>
    private readonly TimeSpan _keyframeInterval;

    /// <summary>The queue between the receive loop and the writer task.</summary>
    private readonly Channel<SourceSample> _queue = Channel.CreateUnbounded<SourceSample>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    /// <summary>Guards the queue accounting and <see cref="_spaceWaiter"/>.</summary>
    private readonly Lock _gate = new();

    /// <summary>Cancelled to abort the pipeline.</summary>
    private readonly CancellationTokenSource _abort = new();

    /// <summary>Submission times of recent video frames by presentation time, for latency.</summary>
    private readonly (long Pts, long Ticks)[] _latency = new (long, long)[LatencyEntries];

    /// <summary>Tracks whose first drop was logged at Warning.</summary>
    private readonly HashSet<(MediaTrack, TranscodingDropReason)> _loggedDrops = [];

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

    /// <summary>1 when publisher metadata changed and the downstream stage has not been told.</summary>
    private int _metadataPending;

    /// <summary>The first failure, once failed.</summary>
    private Exception? _fault;

    /// <summary>The writer task.</summary>
    private Task? _worker;

    /// <summary>The stop task, once stopping.</summary>
    private Task? _stopped;

    /// <summary>The current transcoder (writer-task state).</summary>
    private Instance? _instance;

    /// <summary>Why the next process will start (writer-task state).</summary>
    private TranscoderStartReason _nextReason = TranscoderStartReason.Initial;

    /// <summary>The last delivered decode timestamp per track (reader-task state, sequential across processes).</summary>
    private long? _lastVideo;

    /// <summary>The last delivered audio decode timestamp.</summary>
    private long? _lastAudio;

    /// <summary>Next write index in <see cref="_latency"/>.</summary>
    private int _latencyNext;

    /// <summary>Counters for the summary log.</summary>
    private long _inputFrames;

    /// <summary>Output frames delivered.</summary>
    private long _outputFrames;

    /// <summary>Frames dropped.</summary>
    private long _droppedFrames;

    /// <summary>Processes started.</summary>
    private int _processes;

    /// <summary>Initializes a new instance of the <see cref="TranscodingSession"/> class.</summary>
    /// <param name="publisher">The publishing session.</param>
    /// <param name="downstream">The downstream media stage.</param>
    /// <param name="options">The transcoding options.</param>
    /// <param name="media">The media limits.</param>
    /// <param name="factory">Starts transcoders.</param>
    /// <param name="budget">The service-wide buffer budget.</param>
    /// <param name="metrics">Metrics.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="keyframeInterval">The canonical IDR interval.</param>
    public TranscodingSession(PublisherSession publisher, IPublishSessionHandler downstream, TranscodingOptions options, MediaOptions media, ITranscoderProcessFactory factory, TranscodingBufferBudget budget, TranscodingMetrics metrics, ILogger logger, TimeSpan keyframeInterval)
    {
        _keyframeInterval = keyframeInterval;
        _publisher = publisher;
        _downstream = downstream;
        _options = options;
        _media = media;
        _factory = factory;
        _budget = budget;
        _metrics = metrics;
        _logger = logger;
        Parser = new SourceMediaParser(options, media);
    }

    /// <summary>Gets the session's source parser.</summary>
    public SourceMediaParser Parser { get; }

    /// <summary>Gets the first failure, if the pipeline failed.</summary>
    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>Gets the number of transcoder processes started; for tests.</summary>
    internal int ProcessCount => Volatile.Read(ref _processes);

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

    /// <summary>Starts the writer task.</summary>
    public void Start() => _worker = Task.Run(RunAsync);

    /// <summary>Throws the pipeline's failure, if any.</summary>
    /// <exception cref="TranscodingFailedException">The pipeline failed.</exception>
    public void ThrowIfFaulted()
    {
        if (Fault is { } fault)
        {
            throw fault as TranscodingFailedException ?? new TranscodingFailedException(TranscodingStage.Process, false, "The transcoding pipeline failed.", fault);
        }
    }

    /// <summary>Records that publisher metadata changed; the downstream stage is told before the next output.</summary>
    public void NotifyMetadata() => Volatile.Write(ref _metadataPending, 1);

    /// <summary>Records that a video packet was lost, so video waits for the next keyframe.</summary>
    public void MarkVideoBroken() => _videoBroken = true;

    /// <summary>Queues a sample for the transcoder, applying the overload policy.</summary>
    /// <param name="sample">The sample; ownership transfers to this method.</param>
    /// <param name="cancellationToken">Cancelled when the connection closes.</param>
    /// <returns>A task that completes when the sample was queued or dropped.</returns>
    /// <exception cref="TranscodingFailedException">The pipeline failed.</exception>
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

            while (true)
            {
                Task? wait;
                TranscodingDropReason? reason;
                lock (_gate)
                {
                    reason = TryAccount(sample);
                    if (reason is null)
                    {
                        if (!_queue.Writer.TryWrite(sample))
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
    /// Stops the pipeline. When draining, queued samples are transcoded, the transcoder is flushed and its output
    /// delivered, all within <see cref="TranscodingOptions.DrainTimeout"/>; otherwise the transcoder is killed.
    /// </summary>
    /// <param name="drain">Whether to drain.</param>
    /// <returns>A task that completes when the writer and reader tasks have finished and the process has exited.</returns>
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
        _queue.Writer.TryComplete();
        if (drain && Fault is null)
        {
            _abort.CancelAfter(_options.DrainTimeout);
        }
        else
        {
            await _abort.CancelAsync().ConfigureAwait(false);
        }

        WakeWaiter();
        if (_worker is { } worker)
        {
            await worker.ConfigureAwait(false);
        }

        var input = Interlocked.Read(ref _inputFrames);
        var output = Interlocked.Read(ref _outputFrames);
        var dropped = Interlocked.Read(ref _droppedFrames);
        LogSummary(_publisher.SessionId, input, output, dropped, ProcessCount);

        _abort.Dispose();
    }

    /// <summary>Releases the cancellation source; call after <see cref="StopAsync"/> has completed.</summary>
    public void Dispose() => _abort.Dispose();

    /// <summary>Classifies a failed process from its diagnostics.</summary>
    /// <param name="diagnostics">Recent standard error.</param>
    /// <returns>The failing stage.</returns>
    internal static TranscodingStage Classify(string diagnostics)
    {
        if (diagnostics.Contains("encod", StringComparison.OrdinalIgnoreCase) || diagnostics.Contains("libx264", StringComparison.OrdinalIgnoreCase) || diagnostics.Contains("filter", StringComparison.OrdinalIgnoreCase))
        {
            return TranscodingStage.Encoder;
        }

        return diagnostics.Contains("decod", StringComparison.OrdinalIgnoreCase) || diagnostics.Contains("Invalid data", StringComparison.OrdinalIgnoreCase) || diagnostics.Contains("demux", StringComparison.OrdinalIgnoreCase)
            ? TranscodingStage.Decoder
            : TranscodingStage.Process;
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

    /// <summary>The writer task: feeds queued samples to the transcoder and restarts it when needed.</summary>
    /// <returns>A task that completes when the pipeline has stopped.</returns>
    private async Task RunAsync()
    {
        var token = _abort.Token;
        var held = new List<SourceSample>();
        try
        {
            await foreach (var sample in _queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (ShouldHold(sample, held))
                {
                    held.Add(sample);
                    continue;
                }

                if (held.Count > 0)
                {
                    await FlushHeldAsync(held, sample.Configuration, token).ConfigureAwait(false);
                }

                try
                {
                    await ProcessAsync(sample, sample.Configuration, token).ConfigureAwait(false);
                }
                finally
                {
                    Release(sample);
                    sample.Dispose();
                }
            }

            if (held.Count > 0)
            {
                await FlushHeldAsync(held, held[^1].Configuration, token).ConfigureAwait(false);
            }

            if (_instance is { } last)
            {
                _instance = null;
                await StopInstanceAsync(last, drain: true).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Stopped, failed or out of drain time.
        }
        catch (Exception ex)
        {
            if (ex is TranscodingFailedException failure && Fault is null)
            {
                _metrics.Failure(failure.Stage, failure.Initialization);
                LogFailed(_publisher.SessionId, failure.Stage, failure.Initialization, -1, failure.Message, _instance?.Process.Diagnostics ?? string.Empty);
            }

            SetFault(ex);
        }
        finally
        {
            if (_instance is { } current)
            {
                _instance = null;
                await StopInstanceAsync(current, drain: false).ConfigureAwait(false);
            }

            foreach (var sample in held)
            {
                Release(sample);
                sample.Dispose();
            }

            while (_queue.Reader.TryRead(out var remaining))
            {
                Release(remaining);
                remaining.Dispose();
            }
        }
    }

    /// <summary>
    /// Whether to hold a sample back before the first process starts: while one track is still unconfigured, samples
    /// wait up to <see cref="StartupHold"/> of media for it, so the first process starts with every track instead of
    /// being restarted (and losing video until the next keyframe) when the second track's configuration arrives.
    /// Held samples stay counted against the queue limits.
    /// </summary>
    /// <param name="sample">The sample.</param>
    /// <param name="held">The samples held so far.</param>
    /// <returns><see langword="true"/> to hold it.</returns>
    private bool ShouldHold(SourceSample sample, List<SourceSample> held)
    {
        var configuration = sample.Configuration;
        if (_instance is not null || _processes > 0 || (configuration.Video is not null && configuration.Audio is not null))
        {
            return false;
        }

        if (held.Count == 0)
        {
            return true;
        }

        var limit = Math.Min(_options.MaxBufferedVideoFrames, _options.MaxBufferedAudioFrames) / 2;
        return held.Count < limit
            && sample.DecodeTimestamp >= held[0].DecodeTimestamp
            && sample.DecodeTimestamp - held[0].DecodeTimestamp < StartupHold;
    }

    /// <summary>
    /// Writes held samples. Those whose configuration differs from <paramref name="latest"/> only by a track that was
    /// not yet configured are written as part of <paramref name="latest"/>, so one process takes them all.
    /// </summary>
    /// <param name="held">The held samples; emptied.</param>
    /// <param name="latest">The configuration to start with.</param>
    /// <param name="token">Cancellation.</param>
    /// <returns>A task that completes when written.</returns>
    private async ValueTask FlushHeldAsync(List<SourceSample> held, SourceStreamConfiguration latest, CancellationToken token)
    {
        var baseTimestamp = held.Min(static s => s.DecodeTimestamp);
        try
        {
            for (var i = 0; i < held.Count; i++)
            {
                var sample = held[i];
                var compatible = (sample.Configuration.Video is null || ReferenceEquals(sample.Configuration.Video, latest.Video))
                    && (sample.Configuration.Audio is null || ReferenceEquals(sample.Configuration.Audio, latest.Audio))
                    && Equals(sample.Configuration.Color, latest.Color);
                try
                {
                    await ProcessAsync(sample, compatible ? latest : sample.Configuration, token, i == 0 ? baseTimestamp : null).ConfigureAwait(false);
                }
                finally
                {
                    held[i] = null!;
                    Release(sample);
                    sample.Dispose();
                }
            }
        }
        finally
        {
            held.RemoveAll(static s => s is null);
        }
    }

    /// <summary>Writes one sample, starting or restarting the transcoder first if needed.</summary>
    /// <param name="sample">The sample.</param>
    /// <param name="configuration">The configuration to write it under.</param>
    /// <param name="token">Cancellation.</param>
    /// <param name="baseTimestamp">The base for a process started for it, when not its own decode time.</param>
    /// <returns>A task that completes when written or dropped.</returns>
    private async ValueTask ProcessAsync(SourceSample sample, SourceStreamConfiguration configuration, CancellationToken token, long? baseTimestamp = null)
    {
        var instance = _instance;
        TranscoderStartReason? reason = instance is null ? _nextReason
            : !ReferenceEquals(configuration, instance.Configuration) ? TranscoderStartReason.Configuration
            : sample.Discontinuity != TimestampDiscontinuity.None && instance.HasReceived(sample.Track) ? TranscoderStartReason.Discontinuity
            : sample.DecodeTimestamp - instance.Base > MaxProcessTimeline ? TranscoderStartReason.Timeline
            : null;
        if (reason is { } why)
        {
            if (instance is not null)
            {
                _instance = null;
                await StopInstanceAsync(instance, drain: true).ConfigureAwait(false);
                ThrowIfFaulted();
                _nextReason = why;
            }

            if (sample.Track == MediaTrack.Video && !sample.IsKeyframe)
            {
                Drop(MediaTrack.Video, TranscodingDropReason.AwaitingKeyframe);
                return;
            }

            if (why == TranscoderStartReason.Discontinuity)
            {
                _lastVideo = null;
                _lastAudio = null;
            }

            instance = await StartInstanceAsync(configuration, baseTimestamp ?? sample.DecodeTimestamp, why, token).ConfigureAwait(false);
            _instance = instance;
        }

        if (sample.Track == MediaTrack.Video && instance!.AwaitingKeyframe)
        {
            if (!sample.IsKeyframe)
            {
                Drop(MediaTrack.Video, TranscodingDropReason.AwaitingKeyframe);
                return;
            }

            instance.AwaitingKeyframe = false;
        }

        var timestamp = sample.DecodeTimestamp - instance!.Base + FfmpegArguments.TimestampOffset;
        if (timestamp < 0 || timestamp + sample.CompositionTimeOffset < 0 || timestamp > int.MaxValue)
        {
            Drop(sample.Track, TranscodingDropReason.Timestamp);
            if (sample.Track == MediaTrack.Video)
            {
                instance.AwaitingKeyframe = true;
            }

            return;
        }

        if (sample.Track == MediaTrack.Video)
        {
            RecordSubmission(sample.DecodeTimestamp + sample.CompositionTimeOffset);
        }

        try
        {
            await instance.Writer.WriteSampleAsync(sample, (int)timestamp, token).AsTask().WaitAsync(StallTimeout, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TranscodingFailedException(TranscodingStage.Process, instance.Produced == 0, "The transcoder stopped accepting input.");
        }
        catch (IOException ex)
        {
            await WaitForReaderAsync(instance).ConfigureAwait(false);
            ThrowIfFaulted();
            throw new TranscodingFailedException(TranscodingStage.Process, instance.Produced == 0, "The transcoder closed its input.", ex);
        }

        instance.MarkReceived(sample.Track);
        Interlocked.Increment(ref _inputFrames);
        _metrics.InputFrame(sample.Track, sample.Track == MediaTrack.Video
            ? TranscodingMetrics.CodecName(sample.Configuration.Video!.Codec)
            : TranscodingMetrics.CodecName(sample.Configuration.Audio!.Codec));
    }

    /// <summary>Starts a transcoder process for a configuration.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="baseTimestamp">The session decode time mapped to <see cref="FfmpegArguments.TimestampOffset"/>.</param>
    /// <param name="reason">Why.</param>
    /// <param name="token">Cancellation.</param>
    /// <returns>The instance.</returns>
    private async ValueTask<Instance> StartInstanceAsync(SourceStreamConfiguration configuration, long baseTimestamp, TranscoderStartReason reason, CancellationToken token)
    {
        var arguments = FfmpegArguments.Build(configuration, _options, _keyframeInterval);
        var process = _factory.Start(arguments);
        var instance = new Instance(process, configuration, baseTimestamp, reason != TranscoderStartReason.Discontinuity);
        Interlocked.Increment(ref _processes);
        _metrics.ProcessStarted(reason);
        LogProcessStarted(_publisher.SessionId, reason, configuration.Video?.Description ?? "none", configuration.Audio?.Description ?? "none", configuration.Color?.IsHighDynamicRange == true);
        instance.Reader = Task.Run(() => ReadOutputAsync(instance, _abort.Token), CancellationToken.None);
        try
        {
            await instance.Writer.WriteHeaderAsync(configuration.Audio is not null, configuration.Video is not null, token).AsTask().WaitAsync(StallTimeout, token).ConfigureAwait(false);
            await instance.Writer.WriteSequenceStartsAsync(configuration, 0, token).AsTask().WaitAsync(StallTimeout, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            await StopInstanceAsync(instance, drain: false).ConfigureAwait(false);
            ThrowIfFaulted();
            throw new TranscodingFailedException(TranscodingStage.Decoder, true, "The transcoder did not accept the stream configuration.", ex);
        }

        return instance;
    }

    /// <summary>Stops a transcoder process, draining it first if requested.</summary>
    /// <param name="instance">The instance.</param>
    /// <param name="drain">Whether to flush it and deliver its remaining output.</param>
    /// <returns>A task that completes when the process has exited and its reader has finished.</returns>
    private async ValueTask StopInstanceAsync(Instance instance, bool drain)
    {
        instance.Stopping = true;
        if (drain)
        {
            instance.Process.CompleteInput();
            try
            {
                await instance.Reader!.WaitAsync(_abort.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogDrainTimeout(_publisher.SessionId);
            }
        }

        instance.Process.Kill();
        await WaitForReaderAsync(instance).ConfigureAwait(false);
        await instance.Process.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Waits a bounded time for an instance's reader task.</summary>
    /// <param name="instance">The instance.</param>
    /// <returns>A task that completes when the reader finished or the wait timed out.</returns>
    private static async ValueTask WaitForReaderAsync(Instance instance)
    {
        if (instance.Reader is not { } reader)
        {
            return;
        }

        try
        {
            await reader.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The process has been killed; the reader ends when the pipe closes.
        }
    }

    /// <summary>The reader task of one process: delivers canonical output to the downstream stage.</summary>
    /// <param name="instance">The instance.</param>
    /// <param name="token">Cancellation.</param>
    /// <returns>A task that completes at the end of the output.</returns>
    private async Task ReadOutputAsync(Instance instance, CancellationToken token)
    {
        using var reader = new FlvTagReader(instance.Process.Output, _media.MaxSampleSize + 64);
        try
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false) is { } tag)
            {
                await DeliverAsync(instance, tag, token).ConfigureAwait(false);
            }

            int exitCode;
            try
            {
                exitCode = await instance.Process.Exited.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                exitCode = -1;
            }

            if (instance.Stopping && exitCode == 0)
            {
                return;
            }

            if (!instance.Stopping)
            {
                Fail(instance, exitCode, "The transcoder exited unexpectedly.");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            if (!instance.Stopping)
            {
                Fail(instance, instance.Process.Exited.IsCompleted ? instance.Process.Exited.Result : -1, ex is InvalidDataException ? ex.Message : "The transcoder output pipe failed.");
            }
        }
        catch (Exception ex)
        {
            SetFault(ex);
        }
    }

    /// <summary>Records a process failure.</summary>
    /// <param name="instance">The instance.</param>
    /// <param name="exitCode">Its exit code, or -1.</param>
    /// <param name="message">A description.</param>
    private void Fail(Instance instance, int exitCode, string message)
    {
        var diagnostics = instance.Process.Diagnostics;
        var stage = Classify(diagnostics);
        var initialization = instance.Produced == 0;
        _metrics.Failure(stage, initialization);
        LogFailed(_publisher.SessionId, stage, initialization, exitCode, message, diagnostics);
        SetFault(new TranscodingFailedException(stage, initialization, message));
    }

    /// <summary>Delivers one output tag downstream.</summary>
    /// <param name="instance">The producing instance.</param>
    /// <param name="tag">The tag; ownership transfers to this method.</param>
    /// <param name="token">Cancellation.</param>
    /// <returns>A task that completes when downstream accepted it.</returns>
    private async ValueTask DeliverAsync(Instance instance, FlvTag tag, CancellationToken token)
    {
        PublishedMessageKind kind;
        MediaTrack track;
        switch (tag.Type)
        {
            case 8:
                kind = PublishedMessageKind.Audio;
                track = MediaTrack.Audio;
                break;
            case 9:
                kind = PublishedMessageKind.Video;
                track = MediaTrack.Video;
                break;
            default:
                tag.Body.Dispose();
                return;
        }

        var body = tag.Body.Memory.Span;
        var header = body.Length < 2 || body[1] != 1;
        var decodeTimestamp = (long)tag.Timestamp - FfmpegArguments.TimestampOffset + instance.Base;
        if (header)
        {
            // Sequence headers carry timestamp 0, before the offset; the downstream timeline starts at its first message.
            decodeTimestamp = Math.Max(decodeTimestamp, instance.Base);
        }
        else
        {
            ref var last = ref track == MediaTrack.Video ? ref _lastVideo : ref _lastAudio;
            if (instance.Continuous && last is { } previous && (track == MediaTrack.Video ? decodeTimestamp <= previous : decodeTimestamp < previous))
            {
                tag.Body.Dispose();
                Drop(track, TranscodingDropReason.Overlap);
                return;
            }

            last = decodeTimestamp;
            Interlocked.Increment(ref instance.ProducedField);
            Interlocked.Increment(ref _outputFrames);
            _metrics.OutputFrame(track);
            if (track == MediaTrack.Video && body.Length >= 5)
            {
                var composition = ((body[2] << 16) | (body[3] << 8) | body[4]) << 8 >> 8;
                RecordLatency(decodeTimestamp + composition);
            }
        }

        if (Interlocked.Exchange(ref _metadataPending, 0) == 1)
        {
            var data = new PooledBuffer(System.Buffers.ArrayPool<byte>.Shared);
            await _downstream.OnMessageAsync(_publisher, new PublishedMessage(PublishedMessageKind.Data, 18, (uint)decodeTimestamp, data), token).ConfigureAwait(false);
        }

        var message = new PublishedMessage(kind, tag.Type, (uint)decodeTimestamp, tag.Body);
        try
        {
            await _downstream.OnMessageAsync(_publisher, message, token).ConfigureAwait(false);
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    /// <summary>Records when a video frame with a presentation time was submitted.</summary>
    /// <param name="pts">The presentation time.</param>
    private void RecordSubmission(long pts)
    {
        lock (_latency)
        {
            _latency[_latencyNext] = (pts, Stopwatch.GetTimestamp());
            _latencyNext = (_latencyNext + 1) % LatencyEntries;
        }
    }

    /// <summary>Records the latency of an encoded video frame, if its submission is still in the ring.</summary>
    /// <param name="pts">The presentation time.</param>
    private void RecordLatency(long pts)
    {
        long ticks = 0;
        lock (_latency)
        {
            for (var i = 0; i < LatencyEntries; i++)
            {
                if (_latency[i].Ticks != 0 && _latency[i].Pts == pts)
                {
                    ticks = _latency[i].Ticks;
                    _latency[i] = default;
                    break;
                }
            }
        }

        if (ticks != 0)
        {
            _metrics.Latency(Stopwatch.GetElapsedTime(ticks).TotalSeconds);
        }
    }

    /// <summary>Logs a started process.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="reason">Why it started.</param>
    /// <param name="video">The source video description.</param>
    /// <param name="audio">The source audio description.</param>
    /// <param name="hdr">Whether HDR metadata was declared.</param>
    [LoggerMessage(EventId = 10000, Level = LogLevel.Information, Message = "Session {SessionId} transcoder started ({Reason}): source video {Video}, source audio {Audio}, declared HDR {Hdr}")]
    private partial void LogProcessStarted(Guid sessionId, TranscoderStartReason reason, string video, string audio, bool hdr);

    /// <summary>Logs a failed process.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="stage">The failing stage.</param>
    /// <param name="initialization">Whether it failed before producing output.</param>
    /// <param name="exitCode">The exit code.</param>
    /// <param name="message">The description.</param>
    /// <param name="diagnostics">Recent transcoder diagnostics.</param>
    [LoggerMessage(EventId = 10001, Level = LogLevel.Error, Message = "Session {SessionId} transcoder failed ({Stage}, during initialization {Initialization}, exit code {ExitCode}): {Message} Diagnostics: {Diagnostics}")]
    private partial void LogFailed(Guid sessionId, TranscodingStage stage, bool initialization, int exitCode, string message, string diagnostics);

    /// <summary>Logs a drain that ran out of time.</summary>
    /// <param name="sessionId">The session.</param>
    [LoggerMessage(EventId = 10002, Level = LogLevel.Warning, Message = "Session {SessionId} transcoder did not finish within the drain timeout and was terminated")]
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
    /// <param name="input">Frames submitted.</param>
    /// <param name="output">Frames produced.</param>
    /// <param name="dropped">Frames dropped.</param>
    /// <param name="processes">Processes started.</param>
    [LoggerMessage(EventId = 10005, Level = LogLevel.Information, Message = "Transcoding ended for session {SessionId}: {Input} source frames, {Output} canonical frames, {Dropped} dropped, {Processes} transcoder processes")]
    private partial void LogSummary(Guid sessionId, long input, long output, long dropped, int processes);

    /// <summary>One transcoder process and its stream state.</summary>
    /// <param name="process">The process.</param>
    /// <param name="configuration">The configuration it was started with.</param>
    /// <param name="baseTimestamp">The session decode time mapped to <see cref="FfmpegArguments.TimestampOffset"/>.</param>
    /// <param name="continuous">Whether its output continues the previous process's timeline.</param>
    private sealed class Instance(ITranscoderProcess process, SourceStreamConfiguration configuration, long baseTimestamp, bool continuous)
    {
        /// <summary>Frames produced; incremented by the reader.</summary>
        public long ProducedField;

        /// <summary>Tracks written to this process, as <see cref="MediaTrack"/> bit flags (writer-task state).</summary>
        private int _received;

        /// <summary>Gets the process.</summary>
        public ITranscoderProcess Process { get; } = process;

        /// <summary>Gets the configuration.</summary>
        public SourceStreamConfiguration Configuration { get; } = configuration;

        /// <summary>Gets the base timestamp.</summary>
        public long Base { get; } = baseTimestamp;

        /// <summary>Gets a value indicating whether the output continues the previous timeline.</summary>
        public bool Continuous { get; } = continuous;

        /// <summary>Gets the input writer.</summary>
        public FlvTagWriter Writer { get; } = new(process.Input);

        /// <summary>Gets or sets the reader task.</summary>
        public Task? Reader { get; set; }

        /// <summary>Gets or sets a value indicating whether video waits for a keyframe.</summary>
        public bool AwaitingKeyframe { get; set; } = configuration.Video is not null;

        /// <summary>Gets or sets a value indicating whether the process is being stopped on purpose.</summary>
        public bool Stopping { get; set; }

        /// <summary>Gets the frames produced.</summary>
        public long Produced => Interlocked.Read(ref ProducedField);

        /// <summary>
        /// Whether a sample of a track was already written. The first sample of each track after a timeline jump
        /// carries the discontinuity; only one restart is wanted, so a track new to this process does not restart it.
        /// </summary>
        /// <param name="track">The track.</param>
        /// <returns><see langword="true"/> if written.</returns>
        public bool HasReceived(MediaTrack track) => (_received & (1 << (int)track)) != 0;

        /// <summary>Records that a sample of a track was written.</summary>
        /// <param name="track">The track.</param>
        public void MarkReceived(MediaTrack track) => _received |= 1 << (int)track;
    }
}
