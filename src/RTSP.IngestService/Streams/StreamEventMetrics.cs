// Metrics for stream lifecycle event delivery, on their own meter. The only tag is the event type, a fixed vocabulary;
// applications, stream identities and incarnation identifiers are never tags.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RTSP.IngestService.Streams;

/// <summary>Records stream event metrics on the <see cref="MeterName"/> meter.</summary>
/// <remarks>
/// <para>Instruments and tags:</para>
/// <list type="bullet">
/// <item><c>stream_events.published</c>: events accepted into the channel; tag <c>type</c>.</item>
/// <item><c>stream_events.dropped</c>: accepted events discarded undelivered because the full channel overwrote them (drop-oldest); tag <c>type</c> of the discarded event.</item>
/// <item><c>stream_events.rejected</c>: events offered after the channel closed at shutdown; tag <c>type</c>.</item>
/// <item><c>stream_events.consumer_failures</c>: events whose consumer threw; tag <c>type</c>.</item>
/// </list>
/// <para>
/// <c>type</c> is one of <c>published</c>, <c>media_described</c>, <c>ended</c>, <c>playlist_withdrawn</c> and
/// <c>drained</c>. Thread-safe.
/// </para>
/// </remarks>
internal sealed class StreamEventMetrics
{
    /// <summary>Name of the meter.</summary>
    public const string MeterName = "RTSP.IngestService.Streams";

    /// <summary>Accepted events.</summary>
    private readonly Counter<long> _published;

    /// <summary>Overwritten events.</summary>
    private readonly Counter<long> _dropped;

    /// <summary>Events offered after completion.</summary>
    private readonly Counter<long> _rejected;

    /// <summary>Consumer failures.</summary>
    private readonly Counter<long> _consumerFailures;

    /// <summary>Initializes a new instance of the <see cref="StreamEventMetrics"/> class.</summary>
    /// <param name="meterFactory">Factory owning the meter's lifetime.</param>
    public StreamEventMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _published = meter.CreateCounter<long>("stream_events.published", "{event}", "Stream events accepted into the channel; tag type.");
        _dropped = meter.CreateCounter<long>("stream_events.dropped", "{event}", "Accepted stream events overwritten undelivered because the channel was full; tag type.");
        _rejected = meter.CreateCounter<long>("stream_events.rejected", "{event}", "Stream events offered after the channel closed at shutdown; tag type.");
        _consumerFailures = meter.CreateCounter<long>("stream_events.consumer_failures", "{event}", "Stream events whose consumer threw; tag type.");
    }

    /// <summary>Records an accepted event.</summary>
    /// <param name="type">The event type.</param>
    public void Published(StreamEventType type) => _published.Add(1, Tag(type));

    /// <summary>Records an overwritten event.</summary>
    /// <param name="type">The event type.</param>
    public void Dropped(StreamEventType type) => _dropped.Add(1, Tag(type));

    /// <summary>Records an event offered after completion.</summary>
    /// <param name="type">The event type.</param>
    public void Rejected(StreamEventType type) => _rejected.Add(1, Tag(type));

    /// <summary>Records a consumer failure.</summary>
    /// <param name="type">The event type.</param>
    public void ConsumerFailed(StreamEventType type) => _consumerFailures.Add(1, Tag(type));

    /// <summary>Returns the tag value of an event type.</summary>
    /// <param name="type">The event type.</param>
    /// <returns>The tag value.</returns>
    internal static string TypeTag(StreamEventType type) => type switch
    {
        StreamEventType.Published => "published",
        StreamEventType.MediaDescribed => "media_described",
        StreamEventType.Ended => "ended",
        StreamEventType.PlaylistWithdrawn => "playlist_withdrawn",
        StreamEventType.Drained => "drained",
        _ => throw new UnreachableException(),
    };

    /// <summary>Returns the type tag.</summary>
    /// <param name="type">The event type.</param>
    /// <returns>The tag.</returns>
    private static KeyValuePair<string, object?> Tag(StreamEventType type) => new("type", TypeTag(type));
}
