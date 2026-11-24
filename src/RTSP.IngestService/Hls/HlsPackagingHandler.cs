// The IMediaSessionHandler that packages every published stream as live fragmented-MP4 HLS. It keeps one
// HlsPackager per media session, feeds it the session's samples in order and closes the stream in the segment
// store when the session ends. All work is synchronous and bounded, so Phase 2/3 backpressure is unchanged.

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtmp.Publishing;

namespace RTSP.IngestService.Hls;

/// <summary>
/// Packages published H.264/AAC media into an <see cref="IHlsSegmentStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle: a stream is opened in the store when the session starts; if the store refuses (stream limit reached,
/// or the stream is still being written) the publish is rejected. When the session ends after
/// <see cref="PublishEndReason.Unpublished"/>, <see cref="PublishEndReason.StreamDeleted"/> or
/// <see cref="PublishEndReason.ClientDisconnected"/>, the pending samples become a final segment and the playlist is
/// ended with <c>#EXT-X-ENDLIST</c> and retained for <see cref="HlsOptions.EndedStreamRetention"/>. For every other
/// reason (timeouts, protocol or handler failures, shutdown) pending samples are discarded and the stream is removed.
/// </para>
/// <para>Thread-safe across sessions; calls for one session are sequential (guaranteed by media processing).</para>
/// </remarks>
public sealed partial class HlsPackagingHandler : IMediaSessionHandler
{
    /// <summary>The settings.</summary>
    private readonly HlsOptions _options;

    /// <summary>The store.</summary>
    private readonly IHlsSegmentStore _store;

    /// <summary>Metrics.</summary>
    private readonly HlsMetrics _metrics;

    /// <summary>Logger.</summary>
    private readonly ILogger<HlsPackagingHandler> _logger;

    /// <summary>Packagers by session.</summary>
    private readonly ConcurrentDictionary<Guid, HlsPackager> _packagers = new();

    /// <summary>Initializes a new instance of the <see cref="HlsPackagingHandler"/> class.</summary>
    /// <param name="options">The settings.</param>
    /// <param name="store">The segment store.</param>
    /// <param name="metrics">Metrics.</param>
    /// <param name="logger">Logger.</param>
    internal HlsPackagingHandler(IOptions<HlsOptions> options, IHlsSegmentStore store, HlsMetrics metrics, ILogger<HlsPackagingHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _store = store;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>Gets the number of streams being packaged; for diagnostics and tests.</summary>
    internal int ActiveSessionCount => _packagers.Count;

    /// <inheritdoc />
    public ValueTask OnSessionStartedAsync(MediaSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var publisher = session.Publisher;
        if (!_store.TryOpenStream(session.SessionId, publisher.Application, publisher.StreamIdentity, HlsPackager.TargetDurationSeconds(_options), out var numbering))
        {
            LogRejected(session.SessionId);
            throw new InvalidOperationException("The HLS segment store refused the stream: the stream limit is reached or the stream is already being packaged.");
        }

        var packager = new HlsPackager(session.SessionId, _options, _store, numbering, _metrics, _logger);
        if (!_packagers.TryAdd(session.SessionId, packager))
        {
            _store.CloseStream(session.SessionId, completed: false);
            throw new InvalidOperationException($"Session {session.SessionId} is already being packaged.");
        }

        _metrics.SessionStarted();
        LogStarted(session.SessionId);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnStreamInfoChangedAsync(MediaSession session, MediaStreamInfo streamInfo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(streamInfo);
        Packager(session).OnStreamInfo(streamInfo);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnVideoSampleAsync(MediaSession session, EncodedVideoSample sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sample);
        Packager(session).AddVideo(sample);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnAudioSampleAsync(MediaSession session, EncodedAudioSample sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sample);
        Packager(session).AddAudio(sample);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnMediaErrorAsync(MediaSession session, MediaProcessingError mediaError, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask OnSessionEndedAsync(MediaSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_packagers.TryRemove(session.SessionId, out var packager))
        {
            return ValueTask.CompletedTask;
        }

        var completed = session.Publisher.EndReason is PublishEndReason.Unpublished or PublishEndReason.StreamDeleted or PublishEndReason.ClientDisconnected;
        try
        {
            packager.Finish(completed);
        }
        finally
        {
            packager.Dispose();
            _metrics.SessionEnded();
            LogEnded(session.SessionId, completed, packager.SegmentCount, packager.SegmentBytes, packager.DroppedSamples);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Returns the packager of a started session.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The packager.</returns>
    private HlsPackager Packager(MediaSession session) =>
        _packagers.TryGetValue(session.SessionId, out var packager) ? packager : throw new InvalidOperationException($"Session {session.SessionId} is not being packaged.");

    /// <summary>Logs a started stream.</summary>
    /// <param name="sessionId">The session.</param>
    [LoggerMessage(EventId = 7000, Level = LogLevel.Debug, Message = "HLS packaging started for session {SessionId}")]
    private partial void LogStarted(Guid sessionId);

    /// <summary>Logs a refused stream.</summary>
    /// <param name="sessionId">The session.</param>
    [LoggerMessage(EventId = 7010, Level = LogLevel.Warning, Message = "HLS packaging refused session {SessionId}: the stream limit is reached or the stream is still being packaged; the publish is rejected")]
    private partial void LogRejected(Guid sessionId);

    /// <summary>Logs an ended stream.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="completed">Whether it ended normally.</param>
    /// <param name="segments">Segments published.</param>
    /// <param name="bytes">Bytes published.</param>
    /// <param name="dropped">Samples not packaged.</param>
    [LoggerMessage(EventId = 7011, Level = LogLevel.Information, Message = "HLS packaging ended for session {SessionId} (completed {Completed}): {Segments} segments, {Bytes} bytes, {Dropped} samples not packaged")]
    private partial void LogEnded(Guid sessionId, bool completed, long segments, long bytes, long dropped);
}
