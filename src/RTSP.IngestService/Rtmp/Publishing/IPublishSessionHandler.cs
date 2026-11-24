// The message handoff boundary between RTMP ingest and downstream media processing. The handler sees publishing
// sessions and their messages only; it never sees sockets, chunks or RTMP framing. Calls for one connection are
// sequential and awaited, which is how backpressure reaches the publisher.

namespace RTSP.IngestService.Rtmp.Publishing;

/// <summary>
/// Consumes publishing sessions and the audio, video and data messages they deliver.
/// </summary>
/// <remarks>
/// <para>
/// Ordering and concurrency: for a given connection, calls are made one at a time from the connection's receive loop,
/// in the order the messages arrived; the next call is not made until the previous returned task completes.
/// Different connections call concurrently, so implementations must be thread-safe across sessions.
/// </para>
/// <para>
/// Backpressure: while a returned task is pending the connection reads nothing more from its socket, so a slow
/// handler slows the publisher through TCP flow control instead of buffering in memory. Time spent waiting does not
/// count towards the connection inactivity timeout.
/// </para>
/// <para>
/// Failures: an exception from <see cref="OnSessionStartedAsync"/> rejects the publish; an exception from
/// <see cref="OnMessageAsync"/> ends the session with <see cref="PublishEndReason.HandlerFailed"/> and closes the
/// connection. Exceptions from <see cref="OnSessionEndedAsync"/> are logged and ignored.
/// </para>
/// </remarks>
public interface IPublishSessionHandler
{
    /// <summary>Called when a publish request has been authenticated, before the publisher is told publishing started.</summary>
    /// <param name="session">The new session.</param>
    /// <param name="cancellationToken">Cancelled when the connection closes or the service stops.</param>
    /// <returns>A task that completes when the handler is ready to receive messages.</returns>
    ValueTask OnSessionStartedAsync(PublisherSession session, CancellationToken cancellationToken);

    /// <summary>Called for each audio, video or data message of a session.</summary>
    /// <param name="session">The session the message belongs to.</param>
    /// <param name="message">
    /// The message. Ownership transfers to the handler, which must dispose it once it no longer needs the payload,
    /// whether it handles the message synchronously or later. If this method throws, the caller disposes it.
    /// </param>
    /// <param name="cancellationToken">Cancelled when the connection closes or the service stops.</param>
    /// <returns>A task whose completion allows the connection to read further data.</returns>
    ValueTask OnMessageAsync(PublisherSession session, PublishedMessage message, CancellationToken cancellationToken);

    /// <summary>Called once when a session that was started ends, with <see cref="PublisherSession.EndReason"/> set.</summary>
    /// <param name="session">The ended session.</param>
    /// <param name="cancellationToken">Not cancelled by the server; the call is bounded by the host shutdown timeout during shutdown.</param>
    /// <returns>A task that completes when the handler has released the session's resources.</returns>
    ValueTask OnSessionEndedAsync(PublisherSession session, CancellationToken cancellationToken);
}
