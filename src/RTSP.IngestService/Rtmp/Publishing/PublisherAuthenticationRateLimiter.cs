// Limits failed publish authentication attempts per remote IP address with a token bucket. A token is reserved before
// the key is verified and returned when the attempt succeeds, so concurrent attempts cannot exceed the allowance and
// successful publishers are never charged. An attempt that finds every token reserved by attempts still in flight
// waits, in a bounded queue and for a bounded time, for one of them to finish instead of being rejected: a returned
// token passes directly to the next waiter, and waiters are rejected once nothing is in flight and no token is left.
// State is bounded and reclaimed lazily, without timers.

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
/// <see cref="AcquireAsync"/> also counts the attempts in flight per address. When fewer than one token remains but
/// attempts are in flight, their outcomes are not known yet, so the attempt joins the address's FIFO queue (at most
/// <see cref="MaxWaitersPerAddress"/>) for at most <see cref="MaxContendedWait"/>. <see cref="Release"/> passes the
/// returned token to the first waiter; <see cref="RecordFailure"/> keeps the token spent and, once no attempt is in
/// flight and no token is left, rejects the waiters. Concurrency of verification per address therefore never exceeds
/// the tokens available, exactly as without waiting, and a burst of valid publishers is no longer rejected merely
/// because it is concurrent.
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

    /// <summary>The most attempts from one address that wait for in-flight attempts at a time.</summary>
    public const int MaxWaitersPerAddress = 1024;

    /// <summary>The longest an attempt waits for in-flight attempts from its address before it is rejected.</summary>
    public static readonly TimeSpan MaxContendedWait = TimeSpan.FromSeconds(2);

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
        lock (_gate)
        {
            return Reserve(address, _timeProvider.GetTimestamp(), out _) == Reservation.Acquired;
        }
    }

    /// <summary>
    /// Reserves one failure token for <paramref name="remoteEndPoint"/>, waiting for in-flight attempts from the same
    /// address when they hold every remaining token. Complete a successful reservation with <see cref="Release"/> or
    /// <see cref="RecordFailure"/>.
    /// </summary>
    /// <param name="remoteEndPoint">The TCP peer of the connection.</param>
    /// <param name="cancellationToken">Cancels the wait; no token is held afterwards.</param>
    /// <returns>The normalised address holding the reservation, or <see langword="null"/> if the address is limited.</returns>
    public async ValueTask<IPAddress?> AcquireAsync(EndPoint? remoteEndPoint, CancellationToken cancellationToken)
    {
        var address = Normalize(remoteEndPoint);
        var started = _timeProvider.GetTimestamp();
        TaskCompletionSource<bool> turn;
        lock (_gate)
        {
            switch (Reserve(address, started, out var bucket))
            {
                case Reservation.Acquired:
                    return address;
                case Reservation.Limited:
                    return null;
            }

            turn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bucket!.Waiters.Enqueue(turn);
        }

        try
        {
            if (await turn.Task.WaitAsync(MaxContendedWait, _timeProvider, cancellationToken).ConfigureAwait(false))
            {
                return address;
            }
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            if (turn.TrySetResult(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            else if (turn.Task.Result)
            {
                // The token was passed on just as the wait ended.
                if (cancellationToken.IsCancellationRequested)
                {
                    Release(address);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return address;
            }
        }

        // Rejected or timed out: tokens may have refilled meanwhile.
        cancellationToken.ThrowIfCancellationRequested();
        return TryAcquire(remoteEndPoint, out _) ? address : null;
    }

    /// <summary>Returns the token reserved by a successful attempt.</summary>
    /// <param name="address">The address returned by <see cref="TryAcquire"/> or <see cref="AcquireAsync"/>.</param>
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
            bucket.InFlight = Math.Max(0, bucket.InFlight - 1);
            Dispatch(bucket);
            if (bucket.Tokens >= _capacity && bucket.Waiters.Count == 0)
            {
                _buckets.Remove(address);
            }
        }
    }

    /// <summary>Completes a failed attempt: its token stays spent.</summary>
    /// <param name="address">The address returned by <see cref="AcquireAsync"/>.</param>
    public void RecordFailure(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var now = _timeProvider.GetTimestamp();
        lock (_gate)
        {
            if (_buckets.TryGetValue(address, out var bucket))
            {
                Refill(bucket, now);
                bucket.InFlight = Math.Max(0, bucket.InFlight - 1);
                Dispatch(bucket);
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

    /// <summary>Takes a token for <paramref name="address"/> if one is free. Called with the lock held.</summary>
    /// <param name="address">The normalised address.</param>
    /// <param name="now">The current timestamp.</param>
    /// <param name="bucket">The address's bucket when the result is <see cref="Reservation.Busy"/>.</param>
    /// <returns>The outcome.</returns>
    private Reservation Reserve(IPAddress address, long now, out Bucket? bucket)
    {
        if (_timeProvider.GetElapsedTime(_lastSweep, now) >= _window)
        {
            Sweep(now);
        }

        if (_buckets.TryGetValue(address, out bucket))
        {
            Refill(bucket, now);
            Dispatch(bucket);
            if (bucket.Tokens >= 1 && bucket.Waiters.Count == 0)
            {
                bucket.Tokens -= 1;
                bucket.InFlight++;
                return Reservation.Acquired;
            }

            if (bucket.InFlight == 0)
            {
                return Reservation.Limited;
            }

            if (bucket.Waiters.Count >= MaxWaitersPerAddress)
            {
                bucket.Waiters = new Queue<TaskCompletionSource<bool>>(bucket.Waiters.Where(static w => !w.Task.IsCompleted));
                if (bucket.Waiters.Count >= MaxWaitersPerAddress)
                {
                    return Reservation.Limited;
                }
            }

            return Reservation.Busy;
        }

        if (_buckets.Count >= _maxTrackedAddresses)
        {
            if (_timeProvider.GetElapsedTime(_lastSweep, now) >= _tokenInterval)
            {
                Sweep(now);
            }

            if (_buckets.Count >= _maxTrackedAddresses)
            {
                return Reservation.Limited;
            }
        }

        _buckets.Add(address, new Bucket { Tokens = _capacity - 1, Updated = now, InFlight = 1 });
        return Reservation.Acquired;
    }

    /// <summary>
    /// Passes free tokens to waiters in arrival order, then rejects the remaining waiters if no attempt is in flight to
    /// return one. Called with the lock held, after <see cref="Refill"/>.
    /// </summary>
    /// <param name="bucket">The bucket.</param>
    private static void Dispatch(Bucket bucket)
    {
        while (bucket.Tokens >= 1 && bucket.Waiters.TryDequeue(out var waiter))
        {
            if (waiter.TrySetResult(true))
            {
                bucket.Tokens -= 1;
                bucket.InFlight++;
            }
        }

        if (bucket.InFlight == 0)
        {
            while (bucket.Waiters.TryDequeue(out var waiter))
            {
                waiter.TrySetResult(false);
            }
        }
    }

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
            Dispatch(bucket);
            if (bucket.Tokens >= _capacity && bucket.Waiters.Count == 0)
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

        /// <summary>Gets or sets the number of reserved attempts not yet completed.</summary>
        public int InFlight { get; set; }

        /// <summary>Gets or sets the attempts waiting for a token, in arrival order; at most <see cref="MaxWaitersPerAddress"/>.</summary>
        public Queue<TaskCompletionSource<bool>> Waiters { get; set; } = new();
    }

    /// <summary>The outcome of a reservation.</summary>
    private enum Reservation
    {
        /// <summary>A token was reserved.</summary>
        Acquired,

        /// <summary>No token is left and none can be returned; reject.</summary>
        Limited,

        /// <summary>No token is free, but attempts in flight may return one.</summary>
        Busy,
    }
}
