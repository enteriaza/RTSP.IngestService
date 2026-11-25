// Limits failed publish authentication attempts per remote IP address with a token bucket. A token is reserved before
// the key is verified and returned when the attempt succeeds, so concurrent attempts cannot exceed the allowance and
// successful publishers are never charged. State is bounded and reclaimed lazily, without timers.

using System.Net;

namespace RTSP.IngestService.Rtmp.Publishing;

/// <summary>
/// Per-address limiter of failed publish authentication attempts.
/// </summary>
/// <remarks>
/// <para>
/// Each tracked address has a bucket of up to <c>failureLimit</c> tokens that refills continuously at
/// <c>failureLimit / window</c>. <see cref="TryAcquire"/> takes a token, or reports that the address is limited when
/// fewer than one remains; <see cref="Release"/> returns it after a successful attempt. A full bucket is
/// indistinguishable from an untracked address, so full buckets are removed: on release, during a sweep at most once
/// per window, and when the table reaches its capacity.
/// </para>
/// <para>
/// At most <c>maxTrackedAddresses</c> addresses are tracked. When the table is full and a sweep cannot make room, a new
/// address is limited (fail closed) until existing buckets refill; addresses already tracked are unaffected.
/// </para>
/// <para>
/// The key is the TCP peer address from the accepted socket, never a publisher-supplied value. IPv4-mapped IPv6
/// addresses are normalised to IPv4; connections without an IP peer address share one bucket. A single short lock
/// guards the table; it is taken once per publish command, never on the media path. Thread-safe.
/// </para>
/// </remarks>
internal sealed class PublisherAuthenticationRateLimiter
{
    /// <summary>The default capacity of the address table.</summary>
    public const int DefaultMaxTrackedAddresses = 10_000;

    /// <summary>The key used for connections without an IP peer address.</summary>
    private static readonly IPAddress UnknownAddress = IPAddress.None;

    /// <summary>Guards <see cref="_buckets"/> and <see cref="_lastSweep"/>.</summary>
    private readonly Lock _gate = new();

    /// <summary>Buckets by address.</summary>
    private readonly Dictionary<IPAddress, Bucket> _buckets = [];

    /// <summary>The bucket capacity.</summary>
    private readonly double _capacity;

    /// <summary>The refill rate in tokens per second.</summary>
    private readonly double _refillPerSecond;

    /// <summary>The full-refill window.</summary>
    private readonly TimeSpan _window;

    /// <summary>The time it takes to refill one token.</summary>
    private readonly TimeSpan _tokenInterval;

    /// <summary>The table capacity.</summary>
    private readonly int _maxTrackedAddresses;

    /// <summary>The clock.</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>Timestamp of the last sweep.</summary>
    private long _lastSweep;

    /// <summary>Initializes a new instance of the <see cref="PublisherAuthenticationRateLimiter"/> class.</summary>
    /// <param name="failureLimit">The burst of failures an address may make; at least 1.</param>
    /// <param name="window">The time in which an empty bucket fully refills; positive.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="maxTrackedAddresses">The table capacity; at least 1.</param>
    public PublisherAuthenticationRateLimiter(int failureLimit, TimeSpan window, TimeProvider timeProvider, int maxTrackedAddresses = DefaultMaxTrackedAddresses)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failureLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTrackedAddresses, 1);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _capacity = failureLimit;
        _window = window;
        _tokenInterval = window / failureLimit;
        _refillPerSecond = failureLimit / window.TotalSeconds;
        _maxTrackedAddresses = maxTrackedAddresses;
        _timeProvider = timeProvider;
        _lastSweep = timeProvider.GetTimestamp();
    }

    /// <summary>Gets the number of addresses currently tracked.</summary>
    public int TrackedAddressCount
    {
        get
        {
            lock (_gate)
            {
                return _buckets.Count;
            }
        }
    }

    /// <summary>Reserves one failure token for <paramref name="remoteEndPoint"/>.</summary>
    /// <param name="remoteEndPoint">The TCP peer of the connection.</param>
    /// <param name="address">The normalised address, to pass to <see cref="Release"/> if the attempt succeeds.</param>
    /// <returns><see langword="false"/> if the address is limited and the attempt must be rejected without verification.</returns>
    public bool TryAcquire(EndPoint? remoteEndPoint, out IPAddress address)
    {
        address = Normalize(remoteEndPoint);
        var now = _timeProvider.GetTimestamp();
        lock (_gate)
        {
            if (_timeProvider.GetElapsedTime(_lastSweep, now) >= _window)
            {
                Sweep(now);
            }

            if (_buckets.TryGetValue(address, out var bucket))
            {
                Refill(bucket, now);
                if (bucket.Tokens < 1)
                {
                    return false;
                }

                bucket.Tokens -= 1;
                return true;
            }

            if (_buckets.Count >= _maxTrackedAddresses)
            {
                if (_timeProvider.GetElapsedTime(_lastSweep, now) >= _tokenInterval)
                {
                    Sweep(now);
                }

                if (_buckets.Count >= _maxTrackedAddresses)
                {
                    return false;
                }
            }

            _buckets.Add(address, new Bucket { Tokens = _capacity - 1, Updated = now });
            return true;
        }
    }

    /// <summary>Returns the token reserved by a successful attempt.</summary>
    /// <param name="address">The address returned by <see cref="TryAcquire"/>.</param>
    public void Release(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var now = _timeProvider.GetTimestamp();
        lock (_gate)
        {
            if (!_buckets.TryGetValue(address, out var bucket))
            {
                return;
            }

            Refill(bucket, now);
            bucket.Tokens = Math.Min(_capacity, bucket.Tokens + 1);
            if (bucket.Tokens >= _capacity)
            {
                _buckets.Remove(address);
            }
        }
    }

    /// <summary>Returns the limiter key for <paramref name="remoteEndPoint"/>.</summary>
    /// <param name="remoteEndPoint">The TCP peer.</param>
    /// <returns>The IP address, with IPv4-mapped addresses converted to IPv4.</returns>
    private static IPAddress Normalize(EndPoint? remoteEndPoint) => remoteEndPoint switch
    {
        IPEndPoint { Address: var ip } when ip.IsIPv4MappedToIPv6 => ip.MapToIPv4(),
        IPEndPoint { Address: var ip } => ip,
        _ => UnknownAddress,
    };

    /// <summary>Adds the tokens accrued since the bucket was last updated.</summary>
    /// <param name="bucket">The bucket.</param>
    /// <param name="now">The current timestamp.</param>
    private void Refill(Bucket bucket, long now)
    {
        var elapsed = _timeProvider.GetElapsedTime(bucket.Updated, now);
        if (elapsed > TimeSpan.Zero)
        {
            bucket.Tokens = Math.Min(_capacity, bucket.Tokens + (elapsed.TotalSeconds * _refillPerSecond));
            bucket.Updated = now;
        }
    }

    /// <summary>Removes every bucket that has refilled completely. Called with the lock held.</summary>
    /// <param name="now">The current timestamp.</param>
    private void Sweep(long now)
    {
        _lastSweep = now;
        List<IPAddress>? full = null;
        foreach (var (address, bucket) in _buckets)
        {
            Refill(bucket, now);
            if (bucket.Tokens >= _capacity)
            {
                (full ??= []).Add(address);
            }
        }

        if (full is not null)
        {
            foreach (var address in full)
            {
                _buckets.Remove(address);
            }
        }
    }

    /// <summary>The mutable state of one address. Accessed only with the lock held.</summary>
    private sealed class Bucket
    {
        /// <summary>Gets or sets the available tokens.</summary>
        public double Tokens { get; set; }

        /// <summary>Gets or sets the timestamp of the last refill.</summary>
        public long Updated { get; set; }
    }
}
