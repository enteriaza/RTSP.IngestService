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
/// <para>
/// The bytes are exclusively owned by the instance and never modified after construction. They are only reachable
/// through an <see cref="HlsSegmentLease"/>, so any number of readers may use them concurrently, before or after the
/// store evicts the segment.
/// </para>
/// <para>
/// References: the instance starts with one reference, owned by its creator and handed to the store when the store
/// publishes it. Each lease holds one more. The bytes are returned to their pool exactly once, when the last reference
/// is released; no lease can be taken after that.
/// </para>
/// </remarks>
public sealed class HlsSegment
{
    /// <summary>The bytes: one array, or <see cref="HlsSegmentBufferPool.ChunkSize"/> chunks from <see cref="_pool"/>.</summary>
    private readonly byte[][] _chunks;

    /// <summary>The pool the chunks return to, or <see langword="null"/> for an unpooled array.</summary>
    private readonly HlsSegmentBufferPool? _pool;

    /// <summary>Outstanding references; 0 once released.</summary>
    private int _references = 1;

    /// <summary>Outstanding leases taken against a reader budget; the first reserves <see cref="Footprint"/> in it.</summary>
    private int _leases;

    /// <summary>Initializes a new instance of the <see cref="HlsSegment"/> class, taking ownership of <paramref name="data"/>.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="name">The URI relative to the playlist.</param>
    /// <param name="data">The complete segment; the caller must not modify it afterwards.</param>
    internal HlsSegment(HlsSegmentKind kind, string name, byte[] data)
    {
        Kind = kind;
        Name = name;
        _chunks = [data];
        Length = data.Length;
    }

    /// <summary>Initializes a new instance of the <see cref="HlsSegment"/> class, taking ownership of pooled chunks.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="name">The URI relative to the playlist.</param>
    /// <param name="chunks">The chunks rented from <paramref name="pool"/> for <paramref name="length"/> bytes.</param>
    /// <param name="length">The segment length.</param>
    /// <param name="pool">The pool the chunks return to.</param>
    internal HlsSegment(HlsSegmentKind kind, string name, byte[][] chunks, int length, HlsSegmentBufferPool pool)
    {
        Kind = kind;
        Name = name;
        _chunks = chunks;
        Length = length;
        _pool = pool;
    }

    /// <summary>Gets the kind.</summary>
    public HlsSegmentKind Kind { get; }

    /// <summary>Gets the URI relative to the playlist, for example <c>segment-12.m4s</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the size in bytes.</summary>
    public int Length { get; }

    /// <summary>Gets the memory held by the bytes: the chunk capacity for pooled segments, otherwise the length.</summary>
    public long Footprint => _pool is null ? Length : (long)_chunks.Length * HlsSegmentBufferPool.ChunkSize;

    /// <summary>Gets a value indicating whether the last reference has been released.</summary>
    internal bool IsReleased => Volatile.Read(ref _references) == 0;

    /// <summary>Gets the number of contiguous parts; see <see cref="GetPart"/>.</summary>
    internal int PartCount => _chunks.Length;

    /// <summary>Takes a reference for a reader, unless the segment has been released.</summary>
    /// <returns>The lease, or <see langword="null"/> if the last reference has been released.</returns>
    internal HlsSegmentLease? TryAcquire() => TryReference() ? new HlsSegmentLease(this, null) : null;

    /// <summary>
    /// Takes a reference for a reader, unless the segment has been released or, when it has no lease yet, its
    /// footprint does not fit in <paramref name="budget"/>.
    /// </summary>
    /// <param name="budget">The reader budget the footprint is reserved in while the segment has leases.</param>
    /// <param name="lease">The lease, when <see cref="HlsSegmentAccess.Leased"/>.</param>
    /// <returns>The outcome.</returns>
    internal HlsSegmentAccess TryAcquire(HlsReaderMemoryBudget budget, out HlsSegmentLease? lease)
    {
        lease = null;
        while (true)
        {
            var leases = Volatile.Read(ref _leases);
            if (leases > 0)
            {
                if (Interlocked.CompareExchange(ref _leases, leases + 1, leases) != leases)
                {
                    continue;
                }
            }
            else if (IsReleased)
            {
                return HlsSegmentAccess.NotStored;
            }
            else if (!budget.TryReserve(Footprint))
            {
                return HlsSegmentAccess.ReaderMemoryExhausted;
            }
            else if (Interlocked.CompareExchange(ref _leases, 1, 0) != 0)
            {
                budget.Return(Footprint);
                continue;
            }

            if (!TryReference())
            {
                EndLease(budget);
                return HlsSegmentAccess.NotStored;
            }

            lease = new HlsSegmentLease(this, budget);
            return HlsSegmentAccess.Leased;
        }
    }

    /// <summary>Ends a lease taken against a reader budget; the last returns the footprint to it.</summary>
    /// <param name="budget">The budget.</param>
    internal void EndLease(HlsReaderMemoryBudget budget)
    {
        if (Interlocked.Decrement(ref _leases) == 0)
        {
            budget.Return(Footprint);
        }
    }

    /// <summary>Takes a reference, unless the last one has been released.</summary>
    /// <returns>Whether a reference was taken.</returns>
    private bool TryReference()
    {
        var references = Volatile.Read(ref _references);
        while (references > 0)
        {
            var seen = Interlocked.CompareExchange(ref _references, references + 1, references);
            if (seen == references)
            {
                return true;
            }

            references = seen;
        }

        return false;
    }

    /// <summary>Releases one reference; the last returns the bytes to their pool.</summary>
    /// <exception cref="InvalidOperationException">Every reference has already been released.</exception>
    internal void Release()
    {
        var references = Interlocked.Decrement(ref _references);
        if (references < 0)
        {
            Interlocked.Increment(ref _references);
            throw new InvalidOperationException($"Segment {Name} was released more often than it was referenced.");
        }

        if (references == 0)
        {
            _pool?.Return(_chunks);
        }
    }

    /// <summary>Returns one contiguous part of the bytes; only valid while a reference is held.</summary>
    /// <param name="index">The part, from 0 to <see cref="PartCount"/> - 1.</param>
    /// <returns>The bytes of the part.</returns>
    internal ReadOnlyMemory<byte> GetPart(int index)
    {
        if (_pool is null)
        {
            return _chunks[0];
        }

        var start = (long)index * HlsSegmentBufferPool.ChunkSize;
        return _chunks[index].AsMemory(0, (int)Math.Min(HlsSegmentBufferPool.ChunkSize, Length - start));
    }
}

/// <summary>A reader's reference to an <see cref="HlsSegment"/>; its bytes stay valid and unchanged until it is disposed.</summary>
/// <remarks>
/// Dispose exactly when the bytes are no longer used, including on error and cancellation paths; memory obtained from
/// <see cref="GetPart"/> must not be used afterwards. Disposing more than once has no further effect. Not thread-safe.
/// </remarks>
public sealed class HlsSegmentLease : IDisposable
{
    /// <summary>The reader budget the lease was taken against, if any.</summary>
    private readonly HlsReaderMemoryBudget? _budget;

    /// <summary>The segment, until disposed.</summary>
    private HlsSegment? _segment;

    /// <summary>Initializes a new instance of the <see cref="HlsSegmentLease"/> class for a reference already taken.</summary>
    /// <param name="segment">The segment.</param>
    /// <param name="budget">The reader budget the lease was taken against, or <see langword="null"/>.</param>
    internal HlsSegmentLease(HlsSegment segment, HlsReaderMemoryBudget? budget)
    {
        _segment = segment;
        _budget = budget;
        Kind = segment.Kind;
        Name = segment.Name;
        Length = segment.Length;
    }

    /// <summary>Gets the kind.</summary>
    public HlsSegmentKind Kind { get; }

    /// <summary>Gets the URI relative to the playlist.</summary>
    public string Name { get; }

    /// <summary>Gets the size in bytes.</summary>
    public int Length { get; }

    /// <summary>Gets the number of contiguous parts that make up the bytes, in order.</summary>
    public int PartCount => Segment.PartCount;

    /// <summary>Gets the leased segment.</summary>
    private HlsSegment Segment => _segment ?? throw new ObjectDisposedException(nameof(HlsSegmentLease));

    /// <summary>Returns one contiguous part of the bytes; valid until the lease is disposed.</summary>
    /// <param name="index">The part, from 0 to <see cref="PartCount"/> - 1.</param>
    /// <returns>The bytes of the part.</returns>
    public ReadOnlyMemory<byte> GetPart(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, PartCount);
        return Segment.GetPart(index);
    }

    /// <summary>Copies the bytes into a new array.</summary>
    /// <returns>The segment bytes.</returns>
    public byte[] ToArray()
    {
        var segment = Segment;
        var bytes = GC.AllocateUninitializedArray<byte>(Length);
        var offset = 0;
        for (var i = 0; i < segment.PartCount; i++)
        {
            var part = segment.GetPart(i).Span;
            part.CopyTo(bytes.AsSpan(offset));
            offset += part.Length;
        }

        return bytes;
    }

    /// <summary>Releases the reference.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _segment, null) is not { } segment)
        {
            return;
        }

        if (_budget is not null)
        {
            segment.EndLease(_budget);
        }

        segment.Release();
    }
}

/// <summary>The outcome of <see cref="IHlsSegmentStore.TryAcquireSegment"/>.</summary>
public enum HlsSegmentAccess
{
    /// <summary>A lease was taken.</summary>
    Leased,

    /// <summary>The segment is not stored.</summary>
    NotStored,

    /// <summary>
    /// The segment is stored but no reader holds it, and the reader memory budget cannot cover it until other readers'
    /// leases end; nothing was taken.
    /// </summary>
    ReaderMemoryExhausted,
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
/// Readers: <see cref="GetPlaylist"/>, <see cref="GetSegment"/> and <see cref="AcquireSegment"/> may be called from any
/// thread at any time. A published playlist only lists segments that are already stored. Segment availability follows
/// RFC 8216: a listed segment is never released; a segment removed from the playlist, or whose stream is removed,
/// stays retrievable for its duration plus the duration of the longest published playlist that listed it.
/// </para>
/// <para>
/// Segment ownership: <see cref="AddInitialization"/> and <see cref="AppendSegment"/> take over the creator's
/// reference to the segment only when they return <see cref="HlsAppendResult.Published"/>; otherwise, including when
/// they throw, the caller keeps it and must release it. The store releases its reference when the segment leaves it;
/// the bytes stay valid for readers holding a lease until the lease is disposed.
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
    /// <param name="incarnationId">
    /// The publication's public incarnation identifier, reported on the stream's playlist-withdrawn and drained
    /// lifecycle events; <see langword="null"/> to report none.
    /// </param>
    /// <param name="generation">The publication's registration generation, reported by stream inventories; 0 if unknown.</param>
    /// <returns><see langword="false"/> if the stream limit is reached or the key is being written by another session.</returns>
    bool TryOpenStream(Guid sessionId, string application, string streamIdentity, int targetDurationSeconds, out HlsStreamNumbering numbering, string? incarnationId = null, long generation = 0);

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

    /// <summary>Returns a stored segment of a stream, without access to its bytes.</summary>
    /// <param name="application">The RTMP application.</param>
    /// <param name="streamIdentity">The stream identity.</param>
    /// <param name="name">The segment URI.</param>
    /// <returns>The segment, or <see langword="null"/> if it is not stored.</returns>
    HlsSegment? GetSegment(string application, string streamIdentity, string name);

    /// <summary>Returns a lease on a stored segment of a stream, which keeps its bytes valid until it is disposed.</summary>
    /// <param name="application">The RTMP application.</param>
    /// <param name="streamIdentity">The stream identity.</param>
    /// <param name="name">The segment URI.</param>
    /// <returns>The lease, which the caller must dispose, or <see langword="null"/> if the segment is not stored.</returns>
    /// <remarks>Not limited by the reader memory budget; for readers whose memory the store does not bound.</remarks>
    HlsSegmentLease? AcquireSegment(string application, string streamIdentity, string name);

    /// <summary>
    /// Takes a lease on a stored segment for a reader whose memory is bounded: the distinct segments leased this way
    /// stay within the store's reader memory budget, so evicted segments still held by slow readers cannot grow memory
    /// without bound. A segment another reader already holds is always leased, since it costs no further memory.
    /// </summary>
    /// <param name="application">The RTMP application.</param>
    /// <param name="streamIdentity">The stream identity.</param>
    /// <param name="name">The segment URI.</param>
    /// <param name="lease">The lease, which the caller must dispose, when the result is <see cref="HlsSegmentAccess.Leased"/>.</param>
    /// <returns>The outcome.</returns>
    HlsSegmentAccess TryAcquireSegment(string application, string streamIdentity, string name, out HlsSegmentLease? lease);

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
