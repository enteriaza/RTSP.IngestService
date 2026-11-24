// The storage boundary of HLS packaging. The packager writes immutable initialization and media segments and closes
// streams; the store owns each stream's live playlist, decides retention and eviction within its resource limits and
// serves playlist snapshots and segments to readers. Packaging never depends on a particular implementation.

namespace RTSP.IngestService.Hls;

/// <summary>The kind of an <see cref="HlsSegment"/>.</summary>
public enum HlsSegmentKind
{
    /// <summary>A fragmented MP4 initialization segment (<c>ftyp</c> + <c>moov</c>).</summary>
    Initialization,

    /// <summary>A fragmented MP4 media segment (<c>moof</c> + <c>mdat</c>).</summary>
    Media,
}

/// <summary>An immutable, complete segment.</summary>
/// <remarks>
/// The bytes are exclusively owned by the instance and never modified after construction, so any number of readers
/// may use <see cref="Data"/> concurrently, before or after the store evicts it.
/// </remarks>
public sealed class HlsSegment
{
    /// <summary>The bytes.</summary>
    private readonly byte[] _data;

    /// <summary>Initializes a new instance of the <see cref="HlsSegment"/> class, taking ownership of <paramref name="data"/>.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="name">The URI relative to the playlist.</param>
    /// <param name="data">The complete segment; the caller must not modify it afterwards.</param>
    internal HlsSegment(HlsSegmentKind kind, string name, byte[] data)
    {
        Kind = kind;
        Name = name;
        _data = data;
    }

    /// <summary>Gets the kind.</summary>
    public HlsSegmentKind Kind { get; }

    /// <summary>Gets the URI relative to the playlist, for example <c>segment-12.m4s</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the segment bytes.</summary>
    public ReadOnlyMemory<byte> Data => _data;

    /// <summary>Gets the size in bytes.</summary>
    public int Length => _data.Length;
}

/// <summary>A published playlist: the text and the state it was rendered from.</summary>
/// <param name="Text">The playlist text.</param>
/// <param name="MediaSequence">The sequence number of the first listed segment.</param>
/// <param name="Segments">The listed segments.</param>
/// <param name="Ended">Whether the playlist ends with <c>#EXT-X-ENDLIST</c>.</param>
public sealed record HlsPlaylistSnapshot(string Text, long MediaSequence, IReadOnlyList<HlsSegmentEntry> Segments, bool Ended)
{
    /// <summary>
    /// Gets the snapshot's version: unique among the store's snapshots and increasing with each publication, so it
    /// identifies the exact playlist text (for example as an HTTP entity tag).
    /// </summary>
    public long Version { get; init; }
}

/// <summary>
/// Numbering for a newly opened stream. It continues after earlier instances of the same key whose segments are still
/// available, so segment URIs stay unique per key and the media sequence of a key never decreases.
/// </summary>
/// <param name="FirstSequence">The media sequence number of the stream's first segment.</param>
/// <param name="FirstInitialization">The index of the stream's first initialization segment.</param>
public readonly record struct HlsStreamNumbering(long FirstSequence, int FirstInitialization);

/// <summary>Why <see cref="IHlsSegmentStore.AppendSegment"/> did not publish a segment.</summary>
public enum HlsAppendResult
{
    /// <summary>The segment was stored and the playlist updated.</summary>
    Published,

    /// <summary>
    /// Retaining the segment would exceed the per-stream or global storage limit, and no segment could be released
    /// without breaking HLS availability; nothing was changed. Space returns as availability deadlines pass.
    /// </summary>
    StorageFull,

    /// <summary>
    /// The stream's listed segments and current initialization segment plus this segment exceed the per-stream limit
    /// and the stream holds no retired segments that will expire, so no space can return while the stream is live;
    /// nothing was changed.
    /// </summary>
    StreamLimitExceeded,

    /// <summary>
    /// The live playlist already lists <see cref="HlsPlaylist.MaxEntries"/> segments and removing its oldest would
    /// leave it shorter than three target durations (RFC 8216, section 6.2.2); nothing was changed.
    /// </summary>
    PlaylistFull,
}

/// <summary>
/// Stores the segments and live playlist of packaged streams.
/// </summary>
/// <remarks>
/// <para>
/// Writers: each stream has exactly one writer, identified by its session ID, whose calls are sequential. Writer calls
/// are synchronous and bounded; they never wait for readers.
/// </para>
/// <para>
/// Readers: <see cref="GetPlaylist"/> and <see cref="GetSegment"/> may be called from any thread at any time. A
/// published playlist only lists segments that are already stored. Segment availability follows RFC 8216: a listed
/// segment is never released; a segment removed from the playlist, or whose stream is removed, stays retrievable for
/// its duration plus the duration of the longest published playlist that listed it.
/// </para>
/// </remarks>
public interface IHlsSegmentStore
{
    /// <summary>Opens a stream for writing, replacing a previously ended stream with the same key.</summary>
    /// <param name="sessionId">The writer's session ID.</param>
    /// <param name="application">The RTMP application.</param>
    /// <param name="streamIdentity">The non-secret stream identity.</param>
    /// <param name="targetDurationSeconds">The playlist's <c>#EXT-X-TARGETDURATION</c>, fixed for its lifetime.</param>
    /// <param name="numbering">The numbering the writer must use for segment sequence numbers and URIs.</param>
    /// <returns><see langword="false"/> if the stream limit is reached or the key is being written by another session.</returns>
    bool TryOpenStream(Guid sessionId, string application, string streamIdentity, int targetDurationSeconds, out HlsStreamNumbering numbering);

    /// <summary>Stores an initialization segment, before any media segment that references it.</summary>
    /// <param name="sessionId">The writer's session ID.</param>
    /// <param name="segment">The segment.</param>
    /// <returns><see cref="HlsAppendResult.StorageFull"/> if it cannot be retained.</returns>
    HlsAppendResult AddInitialization(Guid sessionId, HlsSegment segment);

    /// <summary>Stores a completed media segment and publishes the playlist that lists it, atomically.</summary>
    /// <param name="sessionId">The writer's session ID.</param>
    /// <param name="segment">The segment.</param>
    /// <param name="entry">Its playlist entry; its initialization segment must already be stored.</param>
    /// <returns>Whether it was published.</returns>
    HlsAppendResult AppendSegment(Guid sessionId, HlsSegment segment, HlsSegmentEntry entry);

    /// <summary>Closes a stream.</summary>
    /// <param name="sessionId">The writer's session ID.</param>
    /// <param name="completed">
    /// <see langword="true"/> to end the playlist with <c>#EXT-X-ENDLIST</c> and keep it available for the configured
    /// retention; <see langword="false"/> to withdraw the playlist immediately. Either way the stream's segments stay
    /// available for their RFC 8216 period after the playlist is withdrawn.
    /// </param>
    void CloseStream(Guid sessionId, bool completed);

    /// <summary>Returns the current playlist of a stream.</summary>
    /// <param name="application">The RTMP application.</param>
    /// <param name="streamIdentity">The stream identity.</param>
    /// <returns>The playlist, or <see langword="null"/> if none has been published.</returns>
    HlsPlaylistSnapshot? GetPlaylist(string application, string streamIdentity);

    /// <summary>Returns a stored segment of a stream.</summary>
    /// <param name="application">The RTMP application.</param>
    /// <param name="streamIdentity">The stream identity.</param>
    /// <param name="name">The segment URI.</param>
    /// <returns>The segment, or <see langword="null"/> if it is not stored.</returns>
    HlsSegment? GetSegment(string application, string streamIdentity, string name);

    /// <summary>
    /// Returns whether anything is stored for a stream: a published playlist, or segments still within their
    /// availability period after the playlist was withdrawn.
    /// </summary>
    /// <param name="application">The application.</param>
    /// <param name="streamIdentity">The stream identity.</param>
    /// <returns><see langword="true"/> if the stream is known.</returns>
    bool ContainsStream(string application, string streamIdentity);

    /// <summary>
    /// Gets the store's generation: a non-secret path segment, unique per store instance and so per process, that
    /// published playlists put before every segment URI (<c>{generation}/{name}</c>). Segment names restart with the
    /// process; the generation keeps URIs from different processes distinct.
    /// </summary>
    string Generation { get; }
}
