// Default publish session handler used until a media pipeline exists: it accepts every session and releases each
// message's pooled payload immediately. Registered with TryAdd so that a real consumer registered earlier wins.

namespace RTSP.IngestService.Rtmp.Publishing;

/// <summary>
/// An <see cref="IPublishSessionHandler"/> that accepts every session and discards every message.
/// </summary>
/// <remarks>
/// Received messages remain visible through <see cref="PublisherSession"/> counters, metrics and logs. Thread-safe.
/// </remarks>
public sealed class DiscardingPublishSessionHandler : IPublishSessionHandler
{
    /// <summary>Accepts the session.</summary>
    /// <param name="session">Not used.</param>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>A completed task.</returns>
    public ValueTask OnSessionStartedAsync(PublisherSession session, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Disposes <paramref name="message"/>, returning its payload to the pool.</summary>
    /// <param name="session">Not used.</param>
    /// <param name="message">The message to discard.</param>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>A completed task.</returns>
    public ValueTask OnMessageAsync(PublisherSession session, PublishedMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        message.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Performs no work.</summary>
    /// <param name="session">Not used.</param>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>A completed task.</returns>
    public ValueTask OnSessionEndedAsync(PublisherSession session, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
