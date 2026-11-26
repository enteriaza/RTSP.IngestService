// Tests for the RTMP connection deadline: expiry exactly when an armed deadline passes, re-arming and disarming,
// an earlier deadline than the timer's due time, how rarely the timer is rescheduled, disposal, and arming racing the
// timer callback on the real clock.

using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Rtmp;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Rtmp;

/// <summary>
/// Tests for <see cref="ConnectionDeadline"/>.
/// </summary>
public sealed class ConnectionDeadlineTests
{
    /// <summary>The fake clock.</summary>
    private readonly CountingTimeProvider _time = new();

    /// <summary>Verifies that the token is cancelled when the armed deadline passes and not a tick earlier.</summary>
    [Fact]
    public void ArmedDeadline_ExpiresWhenItPasses()
    {
        using var deadline = new ConnectionDeadline(_time);
        deadline.Arm(TimeSpan.FromSeconds(10));

        _time.Fake.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));
        Assert.False(deadline.HasExpired);

        _time.Fake.Advance(TimeSpan.FromTicks(1));
        Assert.True(deadline.HasExpired);
        Assert.True(deadline.Token.IsCancellationRequested);
    }

    /// <summary>Verifies that arming again moves the deadline to the new time.</summary>
    [Fact]
    public void Rearming_MovesTheDeadline()
    {
        using var deadline = new ConnectionDeadline(_time);
        deadline.Arm(TimeSpan.FromSeconds(10));
        _time.Fake.Advance(TimeSpan.FromSeconds(6));
        deadline.Disarm();
        deadline.Arm(TimeSpan.FromSeconds(10));

        _time.Fake.Advance(TimeSpan.FromSeconds(9));
        Assert.False(deadline.HasExpired);

        _time.Fake.Advance(TimeSpan.FromSeconds(1));
        Assert.True(deadline.HasExpired);
    }

    /// <summary>Verifies that a disarmed deadline never expires and that arming afterwards works again.</summary>
    [Fact]
    public void DisarmedDeadline_NeverExpires()
    {
        using var deadline = new ConnectionDeadline(_time);
        deadline.Arm(TimeSpan.FromSeconds(1));
        deadline.Disarm();

        _time.Fake.Advance(TimeSpan.FromHours(1));
        Assert.False(deadline.HasExpired);

        deadline.Arm(TimeSpan.FromSeconds(1));
        _time.Fake.Advance(TimeSpan.FromSeconds(1));
        Assert.True(deadline.HasExpired);
    }

    /// <summary>Verifies that a deadline earlier than the timer's due time expires on time.</summary>
    [Fact]
    public void EarlierDeadline_ExpiresOnTime()
    {
        using var deadline = new ConnectionDeadline(_time);
        deadline.Arm(TimeSpan.FromMinutes(5));
        deadline.Disarm();
        deadline.Arm(TimeSpan.FromSeconds(1));

        _time.Fake.Advance(TimeSpan.FromSeconds(1));
        Assert.True(deadline.HasExpired);
    }

    /// <summary>
    /// Verifies that re-arming on every read does not reschedule the timer: 10,000 arm/disarm cycles over 10 seconds
    /// with a 30-second timeout reschedule it once, and it then expires 30 seconds after the last arm.
    /// </summary>
    [Fact]
    public void FrequentRearming_ReschedulesTheTimerRarely()
    {
        using var deadline = new ConnectionDeadline(_time);
        for (var i = 0; i < 10_000; i++)
        {
            deadline.Arm(TimeSpan.FromSeconds(30));
            _time.Fake.Advance(TimeSpan.FromMilliseconds(1));
            deadline.Disarm();
        }

        Assert.Equal(1, _time.Changes);

        deadline.Arm(TimeSpan.FromSeconds(30));
        _time.Fake.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        Assert.False(deadline.HasExpired);
        _time.Fake.Advance(TimeSpan.FromTicks(1));
        Assert.True(deadline.HasExpired);
        Assert.True(_time.Changes <= 3, $"the timer was rescheduled {_time.Changes} times");
    }

    /// <summary>Verifies that disposal stops the timer and that arming afterwards is harmless.</summary>
    [Fact]
    public void Dispose_StopsTheTimer()
    {
        var deadline = new ConnectionDeadline(_time);
        deadline.Arm(TimeSpan.FromSeconds(1));
        deadline.Dispose();

        _time.Fake.Advance(TimeSpan.FromSeconds(2));
        deadline.Arm(TimeSpan.FromSeconds(1));
        deadline.Dispose();

        Assert.False(deadline.HasExpired);
    }

    /// <summary>
    /// Verifies on the real clock that arming and disarming racing the timer callback never loses the timer: after
    /// rapid cycles with a timeout of one millisecond, a final armed deadline always expires.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task RacingTheTimer_NeverLosesAnArmedDeadline()
    {
        for (var round = 0; round < 20; round++)
        {
            using var deadline = new ConnectionDeadline(TimeProvider.System);
            var end = Environment.TickCount64 + 25;
            while (Environment.TickCount64 < end && !deadline.HasExpired)
            {
                deadline.Arm(TimeSpan.FromMilliseconds(1));
                deadline.Disarm();
            }

            deadline.Arm(TimeSpan.FromMilliseconds(30));
            var expired = new TaskCompletionSource();
            using var registration = deadline.Token.Register(expired.SetResult);

            await expired.Task.WithinSafetyTimeout();
        }
    }

    /// <summary>Verifies on the real clock that an armed deadline expires no earlier than it is due.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task RealClock_ExpiresNoEarlierThanDue()
    {
        using var deadline = new ConnectionDeadline(TimeProvider.System);
        var armedAt = TimeProvider.System.GetTimestamp();
        deadline.Arm(TimeSpan.FromMilliseconds(100));
        var expired = new TaskCompletionSource<TimeSpan>();
        using var registration = deadline.Token.Register(() => expired.SetResult(TimeProvider.System.GetElapsedTime(armedAt)));

        var elapsed = await expired.Task.WithinSafetyTimeout();

        Assert.True(elapsed >= TimeSpan.FromMilliseconds(100), $"expired after {elapsed}");
    }

    /// <summary>A fake clock that counts how often its timers are rescheduled.</summary>
    private sealed class CountingTimeProvider : TimeProvider
    {
        /// <summary>The number of <see cref="ITimer.Change"/> calls.</summary>
        private int _changes;

        /// <summary>Gets the underlying fake clock.</summary>
        public FakeTimeProvider Fake { get; } = new();

        /// <summary>Gets the number of <see cref="ITimer.Change"/> calls on timers created by this clock.</summary>
        public int Changes => Volatile.Read(ref _changes);

        /// <inheritdoc />
        public override long TimestampFrequency => Fake.TimestampFrequency;

        /// <inheritdoc />
        public override long GetTimestamp() => Fake.GetTimestamp();

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => Fake.GetUtcNow();

        /// <inheritdoc />
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new CountingTimer(this, Fake.CreateTimer(callback, state, dueTime, period));

        /// <summary>A timer that counts reschedules.</summary>
        /// <param name="owner">The clock.</param>
        /// <param name="inner">The fake timer.</param>
        private sealed class CountingTimer(CountingTimeProvider owner, ITimer inner) : ITimer
        {
            /// <inheritdoc />
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Interlocked.Increment(ref owner._changes);
                return inner.Change(dueTime, period);
            }

            /// <inheritdoc />
            public void Dispose() => inner.Dispose();

            /// <inheritdoc />
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
