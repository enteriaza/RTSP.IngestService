// The producer-side abstraction of stream lifecycle events and the optional consumer. Producers only publish; they
// never wait for capacity or for the consumer, which runs on its own task.

namespace RTSP.IngestService.Streams;

/// <summary>Publishes stream lifecycle events.</summary>
/// <remarks>
/// <see cref="Publish"/> never blocks, never waits for a consumer and never throws for a full or closed channel, so it
/// may be called on the media path. Delivery is best-effort: an event may be discarded (see
/// <see cref="StreamEventChannel"/>). Thread-safe.
/// </remarks>
public interface IStreamEventPublisher
{
    /// <summary>Offers an event for delivery.</summary>
    /// <param name="streamEvent">The event.</param>
    void Publish(StreamEvent streamEvent);
}

/// <summary>Receives stream lifecycle events, one at a time and in publication order, on a dedicated task.</summary>
/// <remarks>
/// At most one consumer is used. It is called from <see cref="StreamEventDispatcher"/>, never from a producer, so a
/// slow consumer only delays later events (and may cause older undelivered ones to be dropped). An exception is
/// logged and the next event is delivered. The token is cancelled when the service stops; events still buffered then
/// are discarded.
/// </remarks>
public interface IStreamEventConsumer
{
    /// <summary>Handles one event.</summary>
    /// <param name="streamEvent">The event.</param>
    /// <param name="cancellationToken">Cancelled when the service stops.</param>
    /// <returns>A task that completes when the event is handled.</returns>
    ValueTask ConsumeAsync(StreamEvent streamEvent, CancellationToken cancellationToken);
}
