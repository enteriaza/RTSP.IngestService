// The bound on segment memory held by readers. A segment's bytes stay allocated while any reader (an HTTP response)
// holds a lease, even after the store evicts it, so readers are admitted against a fixed budget of distinct leased
// segment bytes instead of letting slow viewers grow memory beyond the store's own limit.

namespace RTSP.IngestService.Hls;

/// <summary>Accounts the footprint of distinct segments that have at least one lease, against a fixed limit.</summary>
/// <remarks>
/// A segment's footprint is reserved when its first lease is taken and returned when its last lease ends, so readers
/// sharing a segment cost it once, and a segment that is already leased never needs more budget. Thread-safe and
/// lock-free; <see cref="Held"/> never exceeds <see cref="Limit"/>.
/// </remarks>
/// <param name="limit">The largest number of leased bytes.</param>
/// <param name="metrics">Metrics to report held bytes to, or <see langword="null"/>.</param>
internal sealed class HlsReaderMemoryBudget(long limit, HlsMetrics? metrics = null)
{
    /// <summary>Bytes reserved by leased segments.</summary>
    private long _held;

    /// <summary>Gets the largest number of leased bytes.</summary>
    public long Limit { get; } = limit;

    /// <summary>Gets the bytes reserved by leased segments.</summary>
    public long Held => Volatile.Read(ref _held);

    /// <summary>Reserves bytes for a segment's first lease, unless that would exceed the limit.</summary>
    /// <param name="bytes">The segment's footprint.</param>
    /// <returns><see langword="false"/> if the budget cannot cover them; nothing is reserved then.</returns>
    public bool TryReserve(long bytes)
    {
        var held = Volatile.Read(ref _held);
        while (held + bytes <= Limit)
        {
            var seen = Interlocked.CompareExchange(ref _held, held + bytes, held);
            if (seen == held)
            {
                metrics?.ReaderMemoryChanged(bytes);
                return true;
            }

            held = seen;
        }

        return false;
    }

    /// <summary>Returns bytes reserved by <see cref="TryReserve"/>.</summary>
    /// <param name="bytes">The segment's footprint.</param>
    public void Return(long bytes)
    {
        Interlocked.Add(ref _held, -bytes);
        metrics?.ReaderMemoryChanged(-bytes);
    }
}
