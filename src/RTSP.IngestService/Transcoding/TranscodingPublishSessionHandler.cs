// The transcoding stage: an IPublishSessionHandler placed in front of the media/HLS stage. It admits publishes
// against the configured session limit, parses each publisher's audio and video codec-aware (legacy FLV and
// Enhanced RTMP), queues validated source samples for that publisher's transcoder and lets the transcoder's canonical
// H.264/AAC output, not the publisher's media, reach the media stage. Disabled transcoding rejects every publish
// rather than passing source media through, and unsupported or out-of-policy media ends the publish with a reason.

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtmp.Publishing;

namespace RTSP.IngestService.Transcoding;

/// <summary>Transcodes every published stream to the canonical profile before media processing.</summary>
/// <remarks>Thread-safe across sessions; calls for one session are sequential, as the RTMP server guarantees.</remarks>
internal sealed partial class TranscodingPublishSessionHandler : IPublishSessionHandler
{
    /// <summary>The transcoding options.</summary>
    private readonly TranscodingOptions _options;

    /// <summary>The media limits.</summary>
    private readonly MediaOptions _media;

    /// <summary>The downstream media stage.</summary>
    private readonly IPublishSessionHandler _downstream;

    /// <summary>Starts transcoders.</summary>
    private readonly ITranscoderProcessFactory _factory;

    /// <summary>Metrics.</summary>
    private readonly TranscodingMetrics _metrics;

    /// <summary>Logger.</summary>
    private readonly ILogger<TranscodingPublishSessionHandler> _logger;

    /// <summary>The service-wide buffer budget.</summary>
    private readonly TranscodingBufferBudget _budget;

    /// <summary>The canonical IDR interval.</summary>
    private readonly TimeSpan _keyframeInterval;

    /// <summary>Active sessions by session ID.</summary>
    private readonly ConcurrentDictionary<Guid, SessionState> _sessions = new();

    /// <summary>Slots in use.</summary>
    private int _active;

    /// <summary>Initializes a new instance of the <see cref="TranscodingPublishSessionHandler"/> class.</summary>
    /// <param name="options">The transcoding options.</param>
    /// <param name="media">The media limits.</param>
    /// <param name="hls">The HLS settings, whose target segment duration is the default IDR interval.</param>
    /// <param name="downstream">The downstream media stage.</param>
    /// <param name="factory">Starts transcoders.</param>
    /// <param name="metrics">Metrics.</param>
    /// <param name="logger">Logger.</param>
    public TranscodingPublishSessionHandler(IOptions<TranscodingOptions> options, IOptions<MediaOptions> media, IOptions<HlsOptions> hls, IPublishSessionHandler downstream, ITranscoderProcessFactory factory, TranscodingMetrics metrics, ILogger<TranscodingPublishSessionHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(hls);
        _options = options.Value;
        _media = media.Value;
        _downstream = downstream;
        _factory = factory;
        _metrics = metrics;
        _logger = logger;
        _budget = new TranscodingBufferBudget(_options.MaxTotalBufferBytes);
        _keyframeInterval = _options.Video.KeyframeInterval ?? hls.Value.TargetSegmentDuration;
    }

    /// <summary>Gets the downstream media stage, when it is the built-in one.</summary>
    internal MediaPublishSessionHandler? MediaStage => _downstream as MediaPublishSessionHandler;

    /// <summary>Gets the number of sessions holding a slot; for tests.</summary>
    internal int ActiveSessionCount => Volatile.Read(ref _active);

    /// <summary>Gets the bytes held in all session queues; for tests.</summary>
    internal long BufferedBytes => _budget.Used;

    /// <summary>Returns a session's pipeline; for tests.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The pipeline, or <see langword="null"/>.</returns>
    internal TranscodingSession? FindSession(Guid sessionId) => _sessions.TryGetValue(sessionId, out var state) ? state.Pipeline : null;

    /// <inheritdoc />
    public async ValueTask OnSessionStartedAsync(PublisherSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_options.Enabled)
        {
            _metrics.Rejection(TranscodingRejectionReason.Disabled);
            LogRejected(session.SessionId, TranscodingRejectionReason.Disabled);
            throw new TranscodingRejectedException(TranscodingRejectionReason.Disabled, "Transcoding is disabled, so the stream cannot be published.");
        }

        if (Interlocked.Increment(ref _active) > _options.MaxConcurrentSessions)
        {
            Interlocked.Decrement(ref _active);
            _metrics.Rejection(TranscodingRejectionReason.Capacity);
            LogRejected(session.SessionId, TranscodingRejectionReason.Capacity);
            throw new TranscodingRejectedException(TranscodingRejectionReason.Capacity, "All transcoding sessions are in use.");
        }

        var state = new SessionState(new TranscodingSession(session, _downstream, _options, _media, _factory, _budget, _metrics, _logger, _keyframeInterval));
        if (!_sessions.TryAdd(session.SessionId, state))
        {
            Interlocked.Decrement(ref _active);
            throw new InvalidOperationException($"Session {session.SessionId} has already started.");
        }

        _metrics.SessionStarted();
        try
        {
            await _downstream.OnSessionStartedAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Release(session.SessionId);
            throw;
        }

        state.Pipeline.Start();
    }

    /// <inheritdoc />
    public async ValueTask OnMessageAsync(PublisherSession session, PublishedMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(message);
        if (!_sessions.TryGetValue(session.SessionId, out var state))
        {
            message.Dispose();
            throw new InvalidOperationException($"Session {session.SessionId} has not started.");
        }

        var pipeline = state.Pipeline;
        try
        {
            pipeline.ThrowIfFaulted();
        }
        catch
        {
            message.Dispose();
            throw;
        }

        if (message.Kind == PublishedMessageKind.Data)
        {
            pipeline.NotifyMetadata();
            message.Dispose();
            return;
        }

        var result = pipeline.Parser.Parse(message);
        if (result.Outcome != SourceParseOutcome.Sample)
        {
            message.Dispose();
        }

        if (result.ConfigurationChanged)
        {
            var configuration = pipeline.Parser.Configuration;
            LogConfiguration(session.SessionId, configuration.Video?.Description ?? "none", configuration.Audio?.Description ?? "none", configuration.Color?.IsHighDynamicRange == true);
        }

        switch (result.Outcome)
        {
            case SourceParseOutcome.Sample:
                await pipeline.SubmitAsync(result.Sample!, cancellationToken).ConfigureAwait(false);
                break;
            case SourceParseOutcome.Dropped:
                _metrics.SourceError(result.Track, result.ErrorKind);
                if (result.Track == MediaTrack.Video)
                {
                    pipeline.MarkVideoBroken();
                }

                if (state.LoggedErrors.Add((result.Track, result.ErrorKind)))
                {
                    LogSourceError(session.SessionId, result.Track, result.ErrorKind, result.Detail ?? string.Empty);
                }
                else
                {
                    LogRepeatedSourceError(session.SessionId, result.Track, result.ErrorKind, result.Detail ?? string.Empty);
                }

                break;
            case SourceParseOutcome.Rejected:
                var reason = result.Rejection switch
                {
                    TranscodingRejection.UnsupportedCodec => TranscodingRejectionReason.UnsupportedCodec,
                    TranscodingRejection.CodecNotAccepted => TranscodingRejectionReason.CodecNotAccepted,
                    TranscodingRejection.ResolutionLimit => TranscodingRejectionReason.ResolutionLimit,
                    _ => TranscodingRejectionReason.FrameRateLimit,
                };
                _metrics.Rejection(reason);
                LogMediaRejected(session.SessionId, result.Track, reason, result.Detail ?? string.Empty);
                throw new TranscodingRejectedException(reason, result.Detail ?? "The media is not accepted.");
        }
    }

    /// <inheritdoc />
    public async ValueTask OnSessionEndedAsync(PublisherSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_sessions.TryGetValue(session.SessionId, out var state))
        {
            return;
        }

        try
        {
            var drain = session.EndReason is PublishEndReason.Unpublished or PublishEndReason.StreamDeleted or PublishEndReason.ClientDisconnected;
            await state.Pipeline.StopAsync(drain).ConfigureAwait(false);
            state.Pipeline.Dispose();
            await _downstream.OnSessionEndedAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Release(session.SessionId);
        }
    }

    /// <summary>
    /// Stops every remaining pipeline without draining; used when the host stops. The sessions stay registered so
    /// their <see cref="OnSessionEndedAsync"/> still ends them downstream and releases their slots.
    /// </summary>
    /// <returns>A task that completes when every transcoder has exited.</returns>
    internal Task AbortAllAsync() => Task.WhenAll(_sessions.Values.Select(static state => state.Pipeline.StopAsync(drain: false)));

    /// <summary>Releases a session's slot.</summary>
    /// <param name="sessionId">The session.</param>
    private void Release(Guid sessionId)
    {
        if (_sessions.TryRemove(sessionId, out _))
        {
            Interlocked.Decrement(ref _active);
            _metrics.SessionEnded();
        }
    }

    /// <summary>Logs a refused publish.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="reason">Why.</param>
    [LoggerMessage(EventId = 10010, Level = LogLevel.Warning, Message = "Publish of session {SessionId} rejected by transcoding admission ({Reason})")]
    private partial void LogRejected(Guid sessionId, TranscodingRejectionReason reason);

    /// <summary>Logs media that ended a publish.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="track">The track.</param>
    /// <param name="reason">Why.</param>
    /// <param name="detail">A description without publisher data.</param>
    [LoggerMessage(EventId = 10011, Level = LogLevel.Warning, Message = "Session {SessionId} ended: its {Track} is not accepted ({Reason}): {Detail}")]
    private partial void LogMediaRejected(Guid sessionId, MediaTrack track, TranscodingRejectionReason reason, string detail);

    /// <summary>Logs the first dropped source packet of a kind and track.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="track">The track.</param>
    /// <param name="kind">Why.</param>
    /// <param name="detail">A description without payload bytes.</param>
    [LoggerMessage(EventId = 10012, Level = LogLevel.Warning, Message = "Session {SessionId} {Track} source packet dropped ({Kind}): {Detail}; further packets of this kind are logged at Debug")]
    private partial void LogSourceError(Guid sessionId, MediaTrack track, MediaErrorKind kind, string detail);

    /// <summary>Logs a repeated dropped source packet.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="track">The track.</param>
    /// <param name="kind">Why.</param>
    /// <param name="detail">A description without payload bytes.</param>
    [LoggerMessage(EventId = 10013, Level = LogLevel.Debug, Message = "Session {SessionId} {Track} source packet dropped ({Kind}): {Detail}")]
    private partial void LogRepeatedSourceError(Guid sessionId, MediaTrack track, MediaErrorKind kind, string detail);

    /// <summary>Logs a changed source configuration.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="video">The source video description.</param>
    /// <param name="audio">The source audio description.</param>
    /// <param name="hdr">Whether HDR metadata was declared.</param>
    [LoggerMessage(EventId = 10014, Level = LogLevel.Information, Message = "Session {SessionId} source media: video {Video}, audio {Audio}, declared HDR {Hdr}")]
    private partial void LogConfiguration(Guid sessionId, string video, string audio, bool hdr);

    /// <summary>Per-session state.</summary>
    /// <param name="pipeline">The pipeline.</param>
    private sealed class SessionState(TranscodingSession pipeline)
    {
        /// <summary>Gets the pipeline.</summary>
        public TranscodingSession Pipeline { get; } = pipeline;

        /// <summary>Gets the (track, kind) pairs already logged at Warning.</summary>
        public HashSet<(MediaTrack Track, MediaErrorKind Kind)> LoggedErrors { get; } = [];
    }
}
