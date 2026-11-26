// The stream catalog's immutable read model: one entry per stream incarnation that is publishing or still has HLS
// resources, and the snapshot that groups them. A snapshot is built completely before it is published and never
// changes afterwards, so readers can enumerate and serialize it without locks.

using System.Collections.Immutable;
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
/// <remarks>
/// Persistent: <see cref="With"/> derives a new snapshot that shares every unchanged entry and tree node with this
/// one, in O(changes × log n), so neither readers of this snapshot nor its contents are affected.
/// </remarks>
public sealed class StreamCatalogSnapshot
{
    /// <summary>Orders entries by application, stream, newest generation first, then incarnation identifier.</summary>
    private static readonly IComparer<StreamCatalogEntry> Order = Comparer<StreamCatalogEntry>.Create(static (x, y) =>
    {
        var order = string.CompareOrdinal(x.Application, y.Application);
        if (order == 0)
        {
            order = string.CompareOrdinal(x.Stream, y.Stream);
        }

        if (order == 0)
        {
            order = y.Generation.CompareTo(x.Generation);
        }

        return order != 0 ? order : string.CompareOrdinal(x.IncarnationId, y.IncarnationId);
    });

    /// <summary>Placeholder publishing information of search probes.</summary>
    private static readonly StreamPublishingInfo ProbePublishing = new() { State = StreamPublishingState.Publishing };

    /// <summary>Placeholder HLS information of search probes.</summary>
    private static readonly StreamHlsInfo ProbeHls = new() { State = StreamHlsState.Pending };

    /// <summary>Entries by incarnation identifier.</summary>
    private readonly ImmutableDictionary<string, StreamCatalogEntry> _byIncarnation;

    /// <summary>The current (newest) entry of each application and stream.</summary>
    private readonly ImmutableDictionary<(string Application, string Stream), StreamCatalogEntry> _current;

    /// <summary>The entries in <see cref="Order"/>.</summary>
    private readonly ImmutableSortedSet<StreamCatalogEntry> _streams;

    /// <summary>Initializes a new instance of the <see cref="StreamCatalogSnapshot"/> class, deriving <see cref="StreamCatalogEntry.Current"/>.</summary>
    /// <param name="version">The publication version.</param>
    /// <param name="initialized">Whether a reconciliation has completed.</param>
    /// <param name="reconciledAt">When the last successful reconciliation completed.</param>
    /// <param name="entries">The entries; each incarnation at most once.</param>
    internal StreamCatalogSnapshot(long version, bool initialized, DateTimeOffset? reconciledAt, IEnumerable<StreamCatalogEntry> entries)
        : this(
            version,
            initialized,
            reconciledAt,
            ImmutableDictionary.Create<string, StreamCatalogEntry>(StringComparer.Ordinal),
            ImmutableDictionary<(string, string), StreamCatalogEntry>.Empty,
            ImmutableSortedSet.Create(Order),
            entries,
            [])
    {
    }

    /// <summary>Initializes a new instance of the <see cref="StreamCatalogSnapshot"/> class from a base state and changes to it.</summary>
    /// <param name="version">The publication version.</param>
    /// <param name="initialized">Whether a reconciliation has completed.</param>
    /// <param name="reconciledAt">When the last successful reconciliation completed.</param>
    /// <param name="byIncarnation">The base entries by incarnation.</param>
    /// <param name="current">The base current entries.</param>
    /// <param name="streams">The base ordered entries.</param>
    /// <param name="upserts">Entries added or replacing the base entry of their incarnation; each incarnation at most once.</param>
    /// <param name="removals">Incarnations whose base entries are removed.</param>
    private StreamCatalogSnapshot(
        long version,
        bool initialized,
        DateTimeOffset? reconciledAt,
        ImmutableDictionary<string, StreamCatalogEntry> byIncarnation,
        ImmutableDictionary<(string, string), StreamCatalogEntry> current,
        ImmutableSortedSet<StreamCatalogEntry> streams,
        IEnumerable<StreamCatalogEntry> upserts,
        IEnumerable<string> removals)
    {
        Version = version;
        IsInitialized = initialized;
        ReconciledAt = reconciledAt;
        var entries = byIncarnation.ToBuilder();
        var newest = current.ToBuilder();
        var ordered = streams.ToBuilder();
        var changed = new HashSet<(string, string)>();
        foreach (var incarnation in removals)
        {
            if (entries.TryGetValue(incarnation, out var old))
            {
                entries.Remove(incarnation);
                ordered.Remove(old);
                changed.Add((old.Application, old.Stream));
            }
        }

        foreach (var entry in upserts)
        {
            if (entries.TryGetValue(entry.IncarnationId, out var old))
            {
                ordered.Remove(old);
                changed.Add((old.Application, old.Stream));
            }

            entries[entry.IncarnationId] = entry;
            ordered.Add(entry);
            changed.Add((entry.Application, entry.Stream));
        }

        // Only the changed streams' Current flags can change: the first entry of each in order is its newest.
        foreach (var (application, stream) in changed)
        {
            var probe = new StreamCatalogEntry { Application = application, Stream = stream, IncarnationId = string.Empty, Generation = long.MaxValue, Publishing = ProbePublishing, Hls = ProbeHls };
            var index = ordered.IndexOf(probe);
            var first = index < 0 ? ~index : index;
            newest.Remove((application, stream));
            for (var i = first; i < ordered.Count; i++)
            {
                var entry = ordered[i];
                if (!string.Equals(entry.Application, application, StringComparison.Ordinal) || !string.Equals(entry.Stream, stream, StringComparison.Ordinal))
                {
                    break;
                }

                if (entry.Current != (i == first))
                {
                    ordered.Remove(entry);
                    entry = entry with { Current = i == first };
                    ordered.Add(entry);
                    entries[entry.IncarnationId] = entry;
                }

                if (i == first)
                {
                    newest[(application, stream)] = entry;
                }
            }
        }

        _byIncarnation = entries.ToImmutable();
        _current = newest.ToImmutable();
        _streams = ordered.ToImmutable();
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
    public IReadOnlyList<StreamCatalogEntry> Streams => _streams;

    /// <summary>Returns the current (newest) entry of a stream.</summary>
    /// <param name="application">The application.</param>
    /// <param name="stream">The stream identity.</param>
    /// <returns>The entry, or <see langword="null"/>.</returns>
    public StreamCatalogEntry? Find(string application, string stream) => _current.GetValueOrDefault((application, stream));

    /// <summary>Returns the entry of an incarnation.</summary>
    /// <param name="incarnationId">The incarnation identifier.</param>
    /// <returns>The entry, or <see langword="null"/>.</returns>
    internal StreamCatalogEntry? FindIncarnation(string incarnationId) => _byIncarnation.GetValueOrDefault(incarnationId);

    /// <summary>Returns a new snapshot with entries added, replaced and removed; this snapshot is unchanged.</summary>
    /// <param name="version">The publication version.</param>
    /// <param name="initialized">Whether a reconciliation has completed.</param>
    /// <param name="reconciledAt">When the last successful reconciliation completed.</param>
    /// <param name="upserts">Entries added or replacing the entry of their incarnation; each incarnation at most once.</param>
    /// <param name="removals">Incarnations whose entries are removed.</param>
    /// <returns>The new snapshot, deriving <see cref="StreamCatalogEntry.Current"/>.</returns>
    internal StreamCatalogSnapshot With(long version, bool initialized, DateTimeOffset? reconciledAt, IEnumerable<StreamCatalogEntry> upserts, IEnumerable<string> removals) =>
        new(version, initialized, reconciledAt, _byIncarnation, _current, _streams, upserts, removals);
}
