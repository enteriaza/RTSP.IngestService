// Shared, immutable dependencies of every RTMP connection, created once by the listener when it starts. Grouping them
// keeps the connection constructor small and lets tests run connections over in-memory pipes without a listener.

using System.Buffers;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Streams;

namespace RTSP.IngestService.Rtmp;

/// <summary>
/// Dependencies shared by all connections of one listener.
/// </summary>
/// <param name="options">The validated RTMP options snapshot.</param>
/// <param name="authenticator">Authenticates publish requests.</param>
/// <param name="handler">Receives publishing sessions and their messages.</param>
/// <param name="registry">Tracks active publishing sessions.</param>
/// <param name="metrics">RTMP metrics.</param>
/// <param name="timeProvider">Clock for timeouts and timestamps.</param>
/// <param name="loggerFactory">Creates connection and session loggers.</param>
/// <param name="bufferPool">Pool for message payloads.</param>
/// <param name="events">Receives stream lifecycle events, or <see langword="null"/> for none.</param>
internal sealed class RtmpServerContext(
    RtmpOptions options,
    IPublisherAuthenticator authenticator,
    IPublishSessionHandler handler,
    PublishedStreamRegistry registry,
    RtmpMetrics metrics,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory,
    ArrayPool<byte> bufferPool,
    IStreamEventPublisher? events = null)
{
    /// <summary>Gets the stream lifecycle event publisher, if any.</summary>
    public IStreamEventPublisher? Events { get; } = events;

    /// <summary>Gets the RTMP options.</summary>
    public RtmpOptions Options { get; } = options;

    /// <summary>Gets the publisher authenticator.</summary>
    public IPublisherAuthenticator Authenticator { get; } = authenticator;

    /// <summary>Gets the publish session handler.</summary>
    public IPublishSessionHandler Handler { get; } = handler;

    /// <summary>Gets the registry of active sessions.</summary>
    public PublishedStreamRegistry Registry { get; } = registry;

    /// <summary>Gets the metrics.</summary>
    public RtmpMetrics Metrics { get; } = metrics;

    /// <summary>Gets the clock.</summary>
    public TimeProvider TimeProvider { get; } = timeProvider;

    /// <summary>Gets the logger factory.</summary>
    public ILoggerFactory LoggerFactory { get; } = loggerFactory;

    /// <summary>Gets the payload buffer pool.</summary>
    public ArrayPool<byte> BufferPool { get; } = bufferPool;
}
