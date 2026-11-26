// Fixed-size chunk pool for media segment bytes. Media segments are written into 64 KiB chunks rented here instead
// of one contiguous array per segment, so segment memory stays out of the large object heap and is reused after a
// segment is released instead of being left to full (gen2) collections. The pool only retains a bounded number of
// free chunks; returns beyond that bound are left to the garbage collector.

using System.Collections.Concurrent;
using RTSP.IngestService.Configuration;

namespace RTSP.IngestService.Hls;

/// <summary>A bounded pool of fixed-size chunks for media segment bytes.</summary>
/// <remarks>
/// <para>
/// Ownership: a rented chunk belongs to exactly one <see cref="HlsSegment"/>, which returns it exactly once when its
/// last reference is released (see <see cref="HlsSegment"/>). Chunks are never cleared: a segment only exposes the
/// bytes it wrote.
/// </para>
/// <para>
/// Bounds: at most <see cref="MaxRetainedChunks"/> free chunks are kept. Chunks in use are bounded by their owners:
/// segments held by the store (within its storage limits), segments being written (one per packaging session, within
/// <see cref="HlsOptions.MaxSegmentSize"/>), and released segments still being sent to HTTP readers.
/// </para>
/// <para>Thread-safe.</para>
/// </remarks>
internal sealed class HlsSegmentBufferPool
{
    /// <summary>The size of every chunk: the HTTP origin's write size and below the large object heap threshold.</summary>
    public const int ChunkSize = 64 * 1024;

    /// <summary>Free chunks.</summary>
    private readonly ConcurrentQueue<byte[]> _free = new();

    /// <summary>The number of chunks in <see cref="_free"/>, or reserved to be added to it.</summary>
    private int _retained;

    /// <summary>Chunks rented and not yet returned.</summary>
    private long _outstanding;

    /// <summary>Metrics, if reported.</summary>
    private readonly HlsMetrics? _metrics;

    /// <summary>Initializes a new instance of the <see cref="HlsSegmentBufferPool"/> class.</summary>
    /// <param name="maxRetainedChunks">The most free chunks kept for reuse.</param>
    /// <param name="metrics">Metrics to report chunk use to, or <see langword="null"/>.</param>
    public HlsSegmentBufferPool(int maxRetainedChunks, HlsMetrics? metrics = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetainedChunks);
        MaxRetainedChunks = maxRetainedChunks;
        _metrics = metrics;
    }

    /// <summary>Gets the most free chunks kept for reuse.</summary>
    public int MaxRetainedChunks { get; }

    /// <summary>Gets the number of free chunks kept for reuse.</summary>
    public int RetainedChunks => Volatile.Read(ref _retained);

    /// <summary>Gets the number of chunks rented and not yet returned.</summary>
    public long OutstandingChunks => Interlocked.Read(ref _outstanding);

    /// <summary>Creates the pool for the configured storage: free chunks are kept up to one eighth of the global limit.</summary>
    /// <param name="options">The settings.</param>
    /// <param name="metrics">Metrics to report chunk use to, or <see langword="null"/>.</param>
    /// <returns>The pool.</returns>
    public static HlsSegmentBufferPool Create(HlsOptions options, HlsMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new HlsSegmentBufferPool((int)Math.Min(int.MaxValue, options.MaxTotalStorageBytes / 8 / ChunkSize), metrics);
    }

    /// <summary>Returns the number of chunks that hold <paramref name="length"/> bytes.</summary>
    /// <param name="length">The length.</param>
    /// <returns>The chunk count.</returns>
    public static int ChunksFor(int length) => (int)(((long)length + ChunkSize - 1) / ChunkSize);

    /// <summary>Rents enough chunks for <paramref name="length"/> bytes.</summary>
    /// <param name="length">The length; at least 1.</param>
    /// <returns>The chunks, each <see cref="ChunkSize"/> bytes.</returns>
    public byte[][] Rent(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        var chunks = new byte[ChunksFor(length)][];
        var reused = 0;
        for (var i = 0; i < chunks.Length; i++)
        {
            if (_free.TryDequeue(out var chunk))
            {
                Interlocked.Decrement(ref _retained);
                reused++;
            }
            else
            {
                chunk = GC.AllocateUninitializedArray<byte>(ChunkSize);
            }

            chunks[i] = chunk;
        }

        Interlocked.Add(ref _outstanding, chunks.Length);
        _metrics?.PoolChanged((long)chunks.Length * ChunkSize, -(long)reused * ChunkSize);
        return chunks;
    }

    /// <summary>Returns chunks rented from this pool; the caller must not use them afterwards.</summary>
    /// <param name="chunks">The chunks.</param>
    public void Return(byte[][] chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        Interlocked.Add(ref _outstanding, -chunks.Length);
        var kept = 0;
        foreach (var chunk in chunks)
        {
            if (Interlocked.Increment(ref _retained) <= MaxRetainedChunks)
            {
                _free.Enqueue(chunk);
                kept++;
            }
            else
            {
                Interlocked.Decrement(ref _retained);
            }
        }

        _metrics?.PoolChanged(-(long)chunks.Length * ChunkSize, (long)kept * ChunkSize);
    }
}
