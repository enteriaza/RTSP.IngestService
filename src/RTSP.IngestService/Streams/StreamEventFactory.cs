// Builds stream events from the authoritative session and media models, so every producer fills the envelope the same
// way and only ever copies public, non-secret fields.

using RTSP.IngestService.Media;
using RTSP.IngestService.Rtmp.Publishing;

namespace RTSP.IngestService.Streams;

/// <summary>Creates <see cref="StreamEvent"/> instances.</summary>
internal static class StreamEventFactory
{
    /// <summary>Creates an event for a publication.</summary>
    /// <param name="type">The transition.</param>
    /// <param name="session">The publication.</param>
    /// <param name="timestamp">When the transition happened.</param>
    /// <param name="reason">The reason, if the transition has one.</param>
    /// <param name="media">The media description, for <see cref="StreamEventType.MediaDescribed"/>.</param>
    /// <returns>The event.</returns>
    public static StreamEvent Create(StreamEventType type, PublisherSession session, DateTimeOffset timestamp, string? reason = null, StreamMediaDescription? media = null) =>
        Create(type, session.Application, session.StreamIdentity, session.IncarnationId, timestamp, reason, media);

    /// <summary>Creates an event.</summary>
    /// <param name="type">The transition.</param>
    /// <param name="application">The application.</param>
    /// <param name="stream">The stream identity.</param>
    /// <param name="incarnationId">The publication's incarnation identifier.</param>
    /// <param name="timestamp">When the transition happened.</param>
    /// <param name="reason">The reason, if the transition has one.</param>
    /// <param name="media">The media description, for <see cref="StreamEventType.MediaDescribed"/>.</param>
    /// <returns>The event.</returns>
    public static StreamEvent Create(StreamEventType type, string application, string stream, string incarnationId, DateTimeOffset timestamp, string? reason = null, StreamMediaDescription? media = null) => new()
    {
        Type = type,
        Timestamp = timestamp.ToUniversalTime(),
        Application = application,
        Stream = stream,
        IncarnationId = incarnationId,
        Reason = reason,
        Media = media,
    };

    /// <summary>Describes the configured tracks of a stream.</summary>
    /// <param name="info">The stream description.</param>
    /// <returns>The description.</returns>
    public static StreamMediaDescription Describe(MediaStreamInfo info) => new()
    {
        Video = info.Video is { } video
            ? new StreamVideoDescription
            {
                Codec = video.Configuration.CodecString,
                ConfigurationVersion = video.ConfigurationVersion,
                Width = video.Width,
                Height = video.Height,
                FrameRate = video.FrameRate,
            }
            : null,
        Audio = info.Audio is { } audio
            ? new StreamAudioDescription
            {
                Codec = audio.Configuration.CodecString,
                ConfigurationVersion = audio.ConfigurationVersion,
                SampleRate = audio.SampleRate,
                ChannelConfiguration = audio.ChannelConfiguration,
                Channels = audio.ChannelCount,
            }
            : null,
    };

    /// <summary>Returns the <see cref="StreamEvent.Reason"/> of a session end.</summary>
    /// <param name="reason">The end reason.</param>
    /// <returns>The reason value.</returns>
    public static string EndReason(PublishEndReason reason) => reason switch
    {
        PublishEndReason.Unpublished => StreamEventReasons.Unpublished,
        PublishEndReason.StreamDeleted => StreamEventReasons.StreamDeleted,
        PublishEndReason.ClientDisconnected => StreamEventReasons.ClientDisconnected,
        PublishEndReason.ConnectionTimeout => StreamEventReasons.ConnectionTimeout,
        PublishEndReason.ProtocolError => StreamEventReasons.ProtocolError,
        PublishEndReason.ConnectionError => StreamEventReasons.ConnectionError,
        PublishEndReason.ServerShutdown => StreamEventReasons.ServerShutdown,
        _ => StreamEventReasons.HandlerFailed,
    };
}
