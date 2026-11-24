// The public, versioned stream lifecycle event envelope. Events are notifications that an authoritative transition
// happened, not authoritative state: they are best-effort, may be lost, and consumers must reconcile against the
// authoritative state. Version 1 evolves additively only; see docs/architecture.md, "Stream lifecycle events".

using System.Text.Json.Serialization;

namespace RTSP.IngestService.Streams;

/// <summary>The lifecycle transition a <see cref="StreamEvent"/> reports.</summary>
/// <remarks>Serialized by name. New values may be added within a schema version; consumers must ignore unknown values.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<StreamEventType>))]
public enum StreamEventType
{
    /// <summary>A publication was accepted and registered: authenticated, unique for its key, and accepted by the media and HLS stages.</summary>
    Published,

    /// <summary>The publication's codec configuration changed; the first is sent when its first track is configured.</summary>
    MediaDescribed,

    /// <summary>The publication ended, for the reason in <see cref="StreamEvent.Reason"/>.</summary>
    Ended,

    /// <summary>The publication's HLS playlist is no longer served, for the reason in <see cref="StreamEvent.Reason"/>.</summary>
    PlaylistWithdrawn,

    /// <summary>Every HLS segment of the publication has been released; nothing of it remains and its lifecycle is complete.</summary>
    Drained,
}

/// <summary>
/// A stream lifecycle notification. Immutable metadata only: it never carries media, credentials, internal session
/// identifiers or connection details.
/// </summary>
/// <remarks>
/// <para>
/// Not authoritative state: delivery is best-effort and in-process, events may be dropped when the bounded channel
/// overflows or discarded at shutdown, and nothing is persisted or replayed. A consumer that needs the current state
/// must reconcile against the authoritative source.
/// </para>
/// <para>
/// Versioning: <see cref="SchemaVersion"/> is <see cref="CurrentSchemaVersion"/>. Within a version, changes are
/// additive only: fields and enumeration values may be added, but none is renamed, removed, retyped or given a new
/// meaning, and a removed field's name is never reused. Any other change requires a new schema version.
/// </para>
/// </remarks>
public sealed record StreamEvent
{
    /// <summary>The schema version of this contract.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the envelope's schema version; always <see cref="CurrentSchemaVersion"/> for events created by this service.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Gets the transition reported.</summary>
    public required StreamEventType Type { get; init; }

    /// <summary>Gets when the transition happened, in UTC.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Gets the RTMP application name.</summary>
    public required string Application { get; init; }

    /// <summary>Gets the non-secret stream identity assigned by authentication; never the publisher's stream key.</summary>
    public required string Stream { get; init; }

    /// <summary>
    /// Gets the public identifier of this publication: random, new for every accepted publication, the same on every
    /// event of it, and unrelated to the application, the stream name and any internal identifier.
    /// </summary>
    public required string IncarnationId { get; init; }

    /// <summary>
    /// Gets why the transition happened, from a fixed snake-case vocabulary (see <see cref="StreamEventReasons"/>),
    /// or <see langword="null"/> for transitions that have no reason.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>Gets the codec configuration, on <see cref="StreamEventType.MediaDescribed"/> events only.</summary>
    public StreamMediaDescription? Media { get; init; }
}

/// <summary>The configured tracks of a publication.</summary>
public sealed record StreamMediaDescription
{
    /// <summary>Gets the video track, or <see langword="null"/> if none is configured.</summary>
    public StreamVideoDescription? Video { get; init; }

    /// <summary>Gets the audio track, or <see langword="null"/> if none is configured.</summary>
    public StreamAudioDescription? Audio { get; init; }
}

/// <summary>A configured video track.</summary>
public sealed record StreamVideoDescription
{
    /// <summary>Gets the RFC 6381 codec string from the decoder configuration, for example <c>avc1.64001F</c>.</summary>
    public required string Codec { get; init; }

    /// <summary>Gets how many distinct configurations the track has had; 1 for the first.</summary>
    public required int ConfigurationVersion { get; init; }

    /// <summary>Gets the width declared in publisher metadata, if any; not verified against the bitstream.</summary>
    public int? Width { get; init; }

    /// <summary>Gets the height declared in publisher metadata, if any; not verified against the bitstream.</summary>
    public int? Height { get; init; }

    /// <summary>Gets the frame rate declared in publisher metadata, if any; not verified against the bitstream.</summary>
    public double? FrameRate { get; init; }
}

/// <summary>A configured audio track.</summary>
public sealed record StreamAudioDescription
{
    /// <summary>Gets the RFC 6381 codec string from the decoder configuration, for example <c>mp4a.40.2</c>.</summary>
    public required string Codec { get; init; }

    /// <summary>Gets how many distinct configurations the track has had; 1 for the first.</summary>
    public required int ConfigurationVersion { get; init; }

    /// <summary>Gets the core sample rate in hertz, from the decoder configuration.</summary>
    public required int SampleRate { get; init; }

    /// <summary>Gets the AAC channel configuration (0 when it is carried in the bitstream).</summary>
    public required int ChannelConfiguration { get; init; }

    /// <summary>Gets the channel count, when derivable from the channel configuration.</summary>
    public int? Channels { get; init; }
}

/// <summary>The <see cref="StreamEvent.Reason"/> vocabulary. Values may be added within a schema version.</summary>
public static class StreamEventReasons
{
    /// <summary><see cref="StreamEventType.Ended"/>: the publisher sent <c>closeStream</c>.</summary>
    public const string Unpublished = "unpublished";

    /// <summary><see cref="StreamEventType.Ended"/>: the publisher sent <c>deleteStream</c>.</summary>
    public const string StreamDeleted = "stream_deleted";

    /// <summary><see cref="StreamEventType.Ended"/>: the publisher closed or reset the connection.</summary>
    public const string ClientDisconnected = "client_disconnected";

    /// <summary><see cref="StreamEventType.Ended"/>: the publisher was silent, or did not read, for the connection timeout.</summary>
    public const string ConnectionTimeout = "connection_timeout";

    /// <summary><see cref="StreamEventType.Ended"/>: the publisher violated the RTMP protocol or a protocol limit.</summary>
    public const string ProtocolError = "protocol_error";

    /// <summary><see cref="StreamEventType.Ended"/>: the connection failed for another reason.</summary>
    public const string ConnectionError = "connection_error";

    /// <summary><see cref="StreamEventType.Ended"/>: the service is shutting down.</summary>
    public const string ServerShutdown = "server_shutdown";

    /// <summary><see cref="StreamEventType.Ended"/>: a processing stage failed and ended the publication.</summary>
    public const string HandlerFailed = "handler_failed";

    /// <summary><see cref="StreamEventType.PlaylistWithdrawn"/>: the publication ended abnormally, so its playlist was withdrawn at once.</summary>
    public const string Aborted = "aborted";

    /// <summary><see cref="StreamEventType.PlaylistWithdrawn"/>: the publication ended normally but ended playlists are not retained.</summary>
    public const string RetentionDisabled = "retention_disabled";

    /// <summary><see cref="StreamEventType.PlaylistWithdrawn"/>: the publication ended normally without producing a segment.</summary>
    public const string Empty = "empty";

    /// <summary><see cref="StreamEventType.PlaylistWithdrawn"/>: the ended playlist's retention period passed.</summary>
    public const string RetentionExpired = "retention_expired";

    /// <summary><see cref="StreamEventType.PlaylistWithdrawn"/>: a new publication of the same stream replaced the ended playlist.</summary>
    public const string Replaced = "replaced";

    /// <summary><see cref="StreamEventType.PlaylistWithdrawn"/>: the ended playlist was withdrawn early to free storage.</summary>
    public const string StoragePressure = "storage_pressure";
}
