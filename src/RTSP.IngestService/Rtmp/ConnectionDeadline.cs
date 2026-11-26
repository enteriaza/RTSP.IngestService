// The handshake and inactivity deadline of one RTMP connection. Arming and disarming only record a timestamp; a
// single timer, rescheduled only when it fires or when a deadline earlier than its due time is armed, cancels the
// token once an armed deadline has passed. This replaces re-arming a CancellationTokenSource timer on every read.

namespace RTSP.IngestService.Rtmp;

/// <summary>
/// A re-armable deadline whose <see cref="Token"/> is cancelled when an armed deadline passes.
/// </summary>
/// <remarks>
/// <para>
/// The timer's due time never lies after the armed deadline: an armed deadline only moves later while armed, and
/// arming a deadline earlier than the due time reschedules the timer. When the timer fires before the deadline (it was
/// armed again since), it is rescheduled for the remainder; when it fires while disarmed, it stops until the next
/// <see cref="Arm"/>. The timer therefore fires about once per timeout period, not once per read, and the deadline
/// expires when it is due, as precisely as the <see cref="TimeProvider"/>'s timers allow.
/// </para>
/// <para>
/// <see cref="Arm"/> and <see cref="Disarm"/> are called by the connection's receive loop only; the timer callback may
/// run concurrently with them. A deadline that passes at the same moment as it is disarmed may still expire, as with a
/// timer-based <see cref="CancellationTokenSource"/>. Once expired the token stays cancelled.
/// </para>
/// </remarks>
internal sealed class ConnectionDeadline : IDisposable
{
    /// <summary>The timestamp value of a disarmed deadline or a stopped timer.</summary>
    private const long Never = long.MaxValue;

    /// <summary>The clock.</summary>
    private readonly TimeProvider _time;

    /// <summary>Cancelled when an armed deadline passes.</summary>
    private readonly CancellationTokenSource _expired = new();

    /// <summary>The timer that checks the deadline.</summary>
    private readonly ITimer _timer;

    /// <summary>Serializes rescheduling the timer and disposal.</summary>
    private readonly Lock _gate = new();

    /// <summary>The armed deadline as a timestamp, or <see cref="Never"/>.</summary>
    private long _deadline = Never;

    /// <summary>When the timer is next due as a timestamp, or <see cref="Never"/> while it is stopped; written under <see cref="_gate"/>.</summary>
    private long _due = Never;

    /// <summary>Whether <see cref="Dispose"/> has been called; written under <see cref="_gate"/>.</summary>
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ConnectionDeadline"/> class, disarmed.</summary>
    /// <param name="time">The clock.</param>
    public ConnectionDeadline(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _timer = time.CreateTimer(static state => ((ConnectionDeadline)state!).OnTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Gets a token that is cancelled when an armed deadline passes.</summary>
    public CancellationToken Token => _expired.Token;

    /// <summary>Gets a value indicating whether an armed deadline has passed.</summary>
    public bool HasExpired => _expired.IsCancellationRequested;

    /// <summary>Arms the deadline <paramref name="timeout"/> from now, replacing any armed deadline.</summary>
    /// <param name="timeout">The time until the deadline; positive and at most a day.</param>
    public void Arm(TimeSpan timeout)
    {
        var now = _time.GetTimestamp();
        var deadline = now + (long)Math.Ceiling(timeout.Ticks * ((double)_time.TimestampFrequency / TimeSpan.TicksPerSecond));

        // The exchange orders this write before the read of _due, pairing with OnTimer's write of _due before its
        // read of _deadline: either this call sees the stopped timer, or the callback sees the new deadline.
        Interlocked.Exchange(ref _deadline, deadline);
        if (deadline < Volatile.Read(ref _due))
        {
            lock (_gate)
            {
                if (!_disposed && deadline < _due)
                {
                    _due = deadline;
                    _timer.Change(_time.GetElapsedTime(now, deadline), Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    /// <summary>Disarms the deadline.</summary>
    public void Disarm() => Volatile.Write(ref _deadline, Never);

    /// <summary>Stops the timer. The token is not cancelled.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Dispose();
        }

        _expired.Dispose();
    }

    /// <summary>Cancels the token if the armed deadline has passed, or reschedules the timer for it.</summary>
    private void OnTimer()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Interlocked.Exchange(ref _due, Never);
            var deadline = Volatile.Read(ref _deadline);
            if (deadline == Never)
            {
                return;
            }

            var now = _time.GetTimestamp();
            if (deadline > now)
            {
                _due = deadline;
                _timer.Change(_time.GetElapsedTime(now, deadline), Timeout.InfiniteTimeSpan);
                return;
            }

            _expired.Cancel();
        }
    }
}
