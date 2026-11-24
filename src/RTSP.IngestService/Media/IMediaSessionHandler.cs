// The downstream boundary of media processing: typed, validated H.264 and AAC samples of each published stream,
// with stream-description changes and dropped-packet reports. Calls for one session are sequential and awaited, so
// the RTMP backpressure guarantees of IPublishSessionHandler carry through unchanged.

namespace RTSP.IngestService.Media;

/// <summary>
/// Consumes the typed media of published streams.
/// </summary>
/// <remarks>
/// <para>
/// Ordering and concurrency: for one session, calls are made one at a time in arrival order, and the next call waits
/// for the previous task to complete. While a task is pending the publisher's connection reads nothing more, so a
/// slow handler slows the publisher instead of buffering. Different sessions call concurrently.
/// </para>
/// <para>
/// Ownership: <see cref="OnVideoSampleAsync"/> and <see cref="OnAudioSampleAsync"/> transfer ownership of the sample,
/// which the handler must dispose. If the method throws, the caller disposes it.
/// </para>
/// <para>
/// Failures: an exception from <see cref="OnSessionStartedAsync"/> rejects the publish; an exception from any other
/// method except <see cref="OnSessionEndedAsync"/> ends the session and closes the publisher's connection.
/// Exceptions from <see cref="OnSessionEndedAsync"/> are logged and ignored.
/// </para>
/// </remarks>
public interface IMediaSessionHandler
{
    /// <summary>Called when publishing starts, before any media.</summary>
    /// <param name="session">The media session.</param>
    /// <param name="cancellationToken">Cancelled when the connection closes or the service stops.</param>
    /// <returns>A task that completes when the handler is ready.</returns>
    ValueTask OnSessionStartedAsync(MediaSession session, CancellationToken cancellationToken);

    /// <summary>
    /// Called when <see cref="MediaSession.StreamInfo"/> changes: a new or changed codec configuration, a track
    /// configuration cleared by an invalid sequence header, or new metadata. Samples delivered afterwards use it.
    /// </summary>
    /// <param name="session">The media session.</param>
    /// <param name="streamInfo">The new description (also available as <see cref="MediaSession.StreamInfo"/>).</param>
    /// <param name="cancellationToken">Cancelled when the connection closes or the service stops.</param>
    /// <returns>A task that completes when the change has been handled.</returns>
    ValueTask OnStreamInfoChangedAsync(MediaSession session, MediaStreamInfo streamInfo, CancellationToken cancellationToken);

    /// <summary>Called for each H.264 access unit.</summary>
    /// <param name="session">The media session.</param>
    /// <param name="sample">The sample; ownership transfers to the handler.</param>
    /// <param name="cancellationToken">Cancelled when the connection closes or the service stops.</param>
    /// <returns>A task whose completion allows the next message to be processed.</returns>
    ValueTask OnVideoSampleAsync(MediaSession session, EncodedVideoSample sample, CancellationToken cancellationToken);

    /// <summary>Called for each AAC access unit.</summary>
    /// <param name="session">The media session.</param>
    /// <param name="sample">The sample; ownership transfers to the handler.</param>
    /// <param name="cancellationToken">Cancelled when the connection closes or the service stops.</param>
    /// <returns>A task whose completion allows the next message to be processed.</returns>
    ValueTask OnAudioSampleAsync(MediaSession session, EncodedAudioSample sample, CancellationToken cancellationToken);

    /// <summary>Called for each media packet that was dropped instead of delivered.</summary>
    /// <param name="session">The media session.</param>
    /// <param name="mediaError">Why the packet was dropped.</param>
    /// <param name="cancellationToken">Cancelled when the connection closes or the service stops.</param>
    /// <returns>A task that completes when the report has been handled.</returns>
    ValueTask OnMediaErrorAsync(MediaSession session, MediaProcessingError mediaError, CancellationToken cancellationToken);

    /// <summary>Called once when a started session ends; <see cref="MediaSession.Publisher"/> has the end reason.</summary>
    /// <param name="session">The media session.</param>
    /// <param name="cancellationToken">Not cancelled by the server.</param>
    /// <returns>A task that completes when the handler has released the session's resources.</returns>
    ValueTask OnSessionEndedAsync(MediaSession session, CancellationToken cancellationToken);
}

/// <summary>
/// The default <see cref="IMediaSessionHandler"/>: disposes every sample. Media is parsed, validated and counted,
/// then released. Replace it to process media.
/// </summary>
public sealed class DiscardingMediaSessionHandler : IMediaSessionHandler
{
    /// <summary>Does nothing.</summary>
    /// <param name="session">The session.</param>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>A completed task.</returns>
    public ValueTask OnSessionStartedAsync(MediaSession session, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Does nothing.</summary>
    /// <param name="session">The session.</param>
    /// <param name="streamInfo">The description.</param>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>A completed task.</returns>
    public ValueTask OnStreamInfoChangedAsync(MediaSession session, MediaStreamInfo streamInfo, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Disposes the sample.</summary>
    /// <param name="session">The session.</param>
    /// <param name="sample">The sample.</param>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>A completed task.</returns>
    public ValueTask OnVideoSampleAsync(MediaSession session, EncodedVideoSample sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);
        sample.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Disposes the sample.</summary>
    /// <param name="session">The session.</param>
    /// <param name="sample">The sample.</param>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>A completed task.</returns>
    public ValueTask OnAudioSampleAsync(MediaSession session, EncodedAudioSample sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);
        sample.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Does nothing.</summary>
    /// <param name="session">The session.</param>
    /// <param name="mediaError">The error.</param>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>A completed task.</returns>
    public ValueTask OnMediaErrorAsync(MediaSession session, MediaProcessingError mediaError, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Does nothing.</summary>
    /// <param name="session">The session.</param>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>A completed task.</returns>
    public ValueTask OnSessionEndedAsync(MediaSession session, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
