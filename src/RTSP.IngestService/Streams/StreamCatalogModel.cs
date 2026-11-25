// The stream catalog's immutable read model: one entry per stream incarnation that is publishing or still has HLS
// resources, and the snapshot that groups them. A snapshot is built completely before it is published and never
// changes afterwards, so readers can enumerate and serialize it without locks.

using System.Text.Json.Serialization;

namespace RTSP.IngestService.Streams;

/// <summary>The RTMP publishing state of an incarnation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StreamPublishingState>))]
public enum StreamPublishingState
{
    /// <summary>The RTMP session is registered and publishing.</summary>
    [JsonStringEnumMemberName("publishing")]
    Publishing,

    /// <summary>The RTMP session has ended; HLS resources may remain.</summary>
    [JsonStringEnumMemberName("ended")]
    Ended,
}

/// <summary>The HLS state of an incarnation, in lifecycle order.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StreamHlsState>))]
public enum StreamHlsState
{
    /// <summary>Publishing, but the HLS store holds nothing for the incarnation yet (or there is no HLS stage).</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,

    /// <summary>
    /// The HLS stream is open and being packaged. The playlist may not be served yet (before the first segment); see
    /// <see cref="StreamHlsInfo.PlaylistAvailable"/>.
    /// </summary>
    [JsonStringEnumMemberName("live")]
    Live,

    /// <summary>The stream ended normally and its ended playlist is still served.</summary>
    [JsonStringEnumMemberName("ended")]
    Ended,

    /// <summary>The playlist is no longer served; retained segments are draining.</summary>
    [JsonStringEnumMemberName("withdrawn")]
    Withdrawn,
}

/// <summary>RTMP publishing information of an incarnation.</summary>
public sealed record StreamPublishingInfo
{
    /// <summary>Gets the publishing state.</summary>
    public required StreamPublishingState State { get; init; }

    /// <summary>Gets when publishing started (UTC), if observed.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Gets when publishing ended (UTC), if observed.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>Gets why publishing ended, from the <see cref="StreamEventReasons"/> vocabulary, if observed.</summary>
    public string? EndReason { get; init; }
}

/// <summary>HLS information of an incarnation.</summary>
public sealed record StreamHlsInfo
{
    /// <summary>Gets the HLS state.</summary>
    public required StreamHlsState State { get; init; }

    /// <summary>
    /// Gets a value indicating whether the playlist route served this incarnation's playlist when the HLS store was
    /// last read, as reported by the store. Independent of <see cref="State"/>: a <see cref="StreamHlsState.Live"/>
    /// stream has no playlist until its first segment is published, and the value is never true for
    /// <see cref="StreamHlsState.Pending"/> or <see cref="StreamHlsState.Withdrawn"/>.
    /// </summary>
    public bool PlaylistAvailable { get; init; }
}

/// <summary>The catalog entry of one stream incarnation.</summary>
public sealed record StreamCatalogEntry
{
    /// <summary>Gets the RTMP application.</summary>
    public required string Application { get; init; }

    /// <summary>Gets the non-secret stream identity.</summary>
    public required string Stream { get; init; }

    /// <summary>Gets the public incarnation identifier.</summary>
    public required string IncarnationId { get; init; }

    /// <summary>Gets a value indicating whether this is the newest incarnation of its application and stream.</summary>
    public bool Current { get; init; }

    /// <summary>Gets the RTMP publishing information.</summary>
    public required StreamPublishingInfo Publishing { get; init; }

    /// <summary>Gets the HLS information.</summary>
    public required StreamHlsInfo Hls { get; init; }

    /// <summary>Gets the latest complete known media description, or <see langword="null"/> if none has been established.</summary>
    public StreamMediaDescription? Media { get; init; }

    /// <summary>Gets the registration generation that orders incarnations of one stream; internal, never serialized.</summary>
    [JsonIgnore]
    public long Generation { get; init; }
}

/// <summary>An immutable, complete catalog state.</summary>
public sealed class StreamCatalogSnapshot
{
    /// <summary>Entries by incarnation identifier.</summary>
    private readonly Dictionary<string, StreamCatalogEntry> _byIncarnation;

    /// <summary>The current (newest) entry of each application and stream.</summary>
    private readonly Dictionary<(string Application, string Stream), StreamCatalogEntry> _current;

    /// <summary>Initializes a new instance of the <see cref="StreamCatalogSnapshot"/> class, deriving <see cref="StreamCatalogEntry.Current"/>.</summary>
    /// <param name="version">The publication version.</param>
    /// <param name="initialized">Whether a reconciliation has completed.</param>
    /// <param name="reconciledAt">When the last successful reconciliation completed.</param>
    /// <param name="entries">The entries; each incarnation at most once.</param>
    internal StreamCatalogSnapshot(long version, bool initialized, DateTimeOffset? reconciledAt, IEnumerable<StreamCatalogEntry> entries)
    {
        Version = version;
        IsInitialized = initialized;
        ReconciledAt = reconciledAt;
        var newest = new Dictionary<(string, string), StreamCatalogEntry>();
        foreach (var entry in entries)
        {
            var key = (entry.Application, entry.Stream);
            if (!newest.TryGetValue(key, out var other) || entry.Generation > other.Generation)
            {
                newest[key] = entry;
            }
        }

        var streams = entries
            .Select(e => e with { Current = ReferenceEquals(newest[(e.Application, e.Stream)], e) })
            .OrderBy(static e => e.Application, StringComparer.Ordinal)
            .ThenBy(static e => e.Stream, StringComparer.Ordinal)
            .ThenByDescending(static e => e.Generation)
            .ToArray();
        Streams = streams.AsReadOnly();
        _byIncarnation = streams.ToDictionary(static e => e.IncarnationId, StringComparer.Ordinal);
        _current = streams.Where(static e => e.Current).ToDictionary(static e => (e.Application, e.Stream));
    }

    /// <summary>Gets the empty, uninitialized catalog.</summary>
    public static StreamCatalogSnapshot Empty { get; } = new(0, false, null, []);

    /// <summary>Gets the publication version, increasing with every published state.</summary>
    public long Version { get; }

    /// <summary>Gets a value indicating whether at least one reconciliation has completed.</summary>
    public bool IsInitialized { get; }

    /// <summary>Gets when the last successful reconciliation completed, or <see langword="null"/> if none has.</summary>
    public DateTimeOffset? ReconciledAt { get; }

    /// <summary>Gets the entries, ordered by application, stream and newest incarnation first.</summary>
    public IReadOnlyList<StreamCatalogEntry> Streams { get; }

    /// <summary>Returns the current (newest) entry of a stream.</summary>
    /// <param name="application">The application.</param>
    /// <param name="stream">The stream identity.</param>
    /// <returns>The entry, or <see langword="null"/>.</returns>
    public StreamCatalogEntry? Find(string application, string stream) => _current.GetValueOrDefault((application, stream));

    /// <summary>Returns the entry of an incarnation.</summary>
    /// <param name="incarnationId">The incarnation identifier.</param>
    /// <returns>The entry, or <see langword="null"/>.</returns>
    internal StreamCatalogEntry? FindIncarnation(string incarnationId) => _byIncarnation.GetValueOrDefault(incarnationId);
}
