// Unit tests for the ApplicationLifecycle state machine: the transition table, readiness, failure recording,
// transition logging and thread safety under concurrent transition requests.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Lifecycle;

namespace RTSP.IngestService.Tests.Lifecycle;

/// <summary>
/// Tests for <see cref="ApplicationLifecycle"/>.
/// </summary>
public sealed class ApplicationLifecycleTests
{
    /// <summary>Clock used to timestamp transitions.</summary>
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>Logger capturing transition events.</summary>
    private readonly FakeLogger<ApplicationLifecycle> _logger = new();

    /// <summary>
    /// Every (current, next) pair with the expected validity from the documented transition table.
    /// </summary>
    /// <returns>Theory data covering all 36 state pairs.</returns>
    public static TheoryData<ApplicationState, ApplicationState, bool> AllTransitions()
    {
        HashSet<(ApplicationState, ApplicationState)> valid =
        [
            (ApplicationState.NotStarted, ApplicationState.Starting),
            (ApplicationState.NotStarted, ApplicationState.Stopped),
            (ApplicationState.NotStarted, ApplicationState.Failed),
            (ApplicationState.Starting, ApplicationState.Running),
            (ApplicationState.Starting, ApplicationState.Stopping),
            (ApplicationState.Starting, ApplicationState.Failed),
            (ApplicationState.Running, ApplicationState.Stopping),
            (ApplicationState.Running, ApplicationState.Failed),
            (ApplicationState.Stopping, ApplicationState.Stopped),
            (ApplicationState.Stopping, ApplicationState.Failed),
        ];

        var data = new TheoryData<ApplicationState, ApplicationState, bool>();
        foreach (var current in Enum.GetValues<ApplicationState>())
        {
            foreach (var next in Enum.GetValues<ApplicationState>())
            {
                data.Add(current, next, valid.Contains((current, next)));
            }
        }

        return data;
    }

    /// <summary>
    /// Verifies that <see cref="ApplicationLifecycle.IsValidTransition"/> matches the documented table for every
    /// state pair, including that self-transitions and transitions out of terminal states are invalid.
    /// </summary>
    /// <param name="current">The state being left.</param>
    /// <param name="next">The requested state.</param>
    /// <param name="expected">Whether the table permits the transition.</param>
    [Theory]
    [MemberData(nameof(AllTransitions))]
    public void IsValidTransition_MatchesDocumentedTable(ApplicationState current, ApplicationState next, bool expected) =>
        Assert.Equal(expected, ApplicationLifecycle.IsValidTransition(current, next));

    /// <summary>Verifies that a new lifecycle is NotStarted, not ready, has no failure and is stamped with the current time.</summary>
    [Fact]
    public void NewLifecycle_IsNotStartedAndNotReady()
    {
        var lifecycle = CreateLifecycle();

        Assert.Equal(ApplicationState.NotStarted, lifecycle.State);
        Assert.False(lifecycle.IsReady);
        Assert.Null(lifecycle.Failure);
        Assert.Equal(_time.GetUtcNow(), lifecycle.StateChangedAt);
    }

    /// <summary>
    /// Verifies the normal path NotStarted → Starting → Running → Stopping → Stopped and that readiness is reported
    /// only in Running.
    /// </summary>
    [Fact]
    public void NormalPath_IsAcceptedAndReadyOnlyWhileRunning()
    {
        var lifecycle = CreateLifecycle();

        Assert.True(lifecycle.TryTransitionTo(ApplicationState.Starting));
        Assert.False(lifecycle.IsReady);

        Assert.True(lifecycle.TryTransitionTo(ApplicationState.Running));
        Assert.True(lifecycle.IsReady);

        Assert.True(lifecycle.TryTransitionTo(ApplicationState.Stopping));
        Assert.False(lifecycle.IsReady);

        Assert.True(lifecycle.TryTransitionTo(ApplicationState.Stopped));
        Assert.False(lifecycle.IsReady);
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
    }

    /// <summary>
    /// Verifies that an invalid transition is rejected without throwing, leaves state and timestamp unchanged, and is
    /// logged at debug level.
    /// </summary>
    [Fact]
    public void InvalidTransition_IsRejectedAndLoggedAtDebug()
    {
        var lifecycle = CreateLifecycle();
        var stampBefore = lifecycle.StateChangedAt;
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.False(lifecycle.TryTransitionTo(ApplicationState.Running));

        Assert.Equal(ApplicationState.NotStarted, lifecycle.State);
        Assert.Equal(stampBefore, lifecycle.StateChangedAt);
        var record = Assert.Single(_logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Debug, record.Level);
        Assert.Equal("NotStarted", record.GetStructuredStateValue("State"));
        Assert.Equal("Running", record.GetStructuredStateValue("RequestedState"));
    }

    /// <summary>Verifies that a repeated request for the current state is rejected.</summary>
    [Fact]
    public void SelfTransition_IsRejected()
    {
        var lifecycle = CreateLifecycle();
        lifecycle.TryTransitionTo(ApplicationState.Starting);

        Assert.False(lifecycle.TryTransitionTo(ApplicationState.Starting));
        Assert.Equal(ApplicationState.Starting, lifecycle.State);
    }

    /// <summary>Verifies that Stopped is terminal: no further transition or failure is accepted.</summary>
    [Fact]
    public void Stopped_IsTerminal()
    {
        var lifecycle = CreateLifecycle();
        lifecycle.TryTransitionTo(ApplicationState.Stopped);

        Assert.False(lifecycle.TryTransitionTo(ApplicationState.Starting));
        Assert.False(lifecycle.TryTransitionTo(ApplicationState.Running));
        Assert.False(lifecycle.TryTransitionTo(ApplicationState.Stopping));
        Assert.False(lifecycle.TryFail(new InvalidOperationException()));
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        Assert.Null(lifecycle.Failure);
    }

    /// <summary>
    /// Verifies that TryFail enters Failed, records the exception and timestamp, withdraws readiness, and logs an
    /// error that names the exception type and message.
    /// </summary>
    [Fact]
    public void TryFail_RecordsFailureAndLogsError()
    {
        var lifecycle = CreateLifecycle();
        lifecycle.TryTransitionTo(ApplicationState.Starting);
        lifecycle.TryTransitionTo(ApplicationState.Running);
        _time.Advance(TimeSpan.FromSeconds(5));
        var failure = new InvalidOperationException("boom");

        Assert.True(lifecycle.TryFail(failure));

        Assert.Equal(ApplicationState.Failed, lifecycle.State);
        Assert.False(lifecycle.IsReady);
        Assert.Same(failure, lifecycle.Failure);
        Assert.Equal(_time.GetUtcNow(), lifecycle.StateChangedAt);
        var record = _logger.LatestRecord;
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Equal(typeof(InvalidOperationException).FullName, record.GetStructuredStateValue("FailureType"));
        Assert.Equal("boom", record.GetStructuredStateValue("FailureMessage"));
    }

    /// <summary>Verifies that Failed is terminal and the first recorded failure is preserved.</summary>
    [Fact]
    public void Failed_IsTerminalAndKeepsFirstFailure()
    {
        var lifecycle = CreateLifecycle();
        var first = new InvalidOperationException("first");
        lifecycle.TryFail(first);

        Assert.False(lifecycle.TryFail(new InvalidOperationException("second")));
        Assert.False(lifecycle.TryTransitionTo(ApplicationState.Stopped));
        Assert.Same(first, lifecycle.Failure);
    }

    /// <summary>Verifies that Failed cannot be requested without an exception.</summary>
    [Fact]
    public void TryTransitionTo_Failed_Throws() =>
        Assert.Throws<ArgumentException>(() => CreateLifecycle().TryTransitionTo(ApplicationState.Failed));

    /// <summary>
    /// Verifies that each accepted transition is logged at information level with PreviousState and State
    /// structured properties, in transition order.
    /// </summary>
    [Fact]
    public void AcceptedTransitions_AreLoggedInOrder()
    {
        var lifecycle = CreateLifecycle();

        lifecycle.TryTransitionTo(ApplicationState.Starting);
        lifecycle.TryTransitionTo(ApplicationState.Running);

        var records = _logger.Collector.GetSnapshot();
        Assert.Collection(
            records,
            r =>
            {
                Assert.Equal(LogLevel.Information, r.Level);
                Assert.Equal("NotStarted", r.GetStructuredStateValue("PreviousState"));
                Assert.Equal("Starting", r.GetStructuredStateValue("State"));
            },
            r =>
            {
                Assert.Equal("Starting", r.GetStructuredStateValue("PreviousState"));
                Assert.Equal("Running", r.GetStructuredStateValue("State"));
            });
    }

    /// <summary>
    /// Verifies that when many threads request the same transition simultaneously exactly one succeeds.
    /// </summary>
    [Fact]
    public async Task ConcurrentIdenticalRequests_ExactlyOneSucceeds()
    {
        var lifecycle = CreateLifecycle();
        const int Contenders = 32;
        using var barrier = new Barrier(Contenders);

        var results = await Task.WhenAll(Enumerable.Range(0, Contenders).Select(_ => RunOnDedicatedThread(() =>
        {
            barrier.SignalAndWait(TestContext.Current.CancellationToken);
            return lifecycle.TryTransitionTo(ApplicationState.Starting);
        })));

        Assert.Single(results, static succeeded => succeeded);
        Assert.Equal(ApplicationState.Starting, lifecycle.State);
    }

    /// <summary>
    /// Verifies that concurrent threads each driving the full normal path produce exactly four accepted transitions,
    /// logged in valid order, ending in Stopped, while concurrent readers only ever observe defined states.
    /// </summary>
    [Fact]
    public async Task ConcurrentFullSequences_ProduceSingleValidHistory()
    {
        var lifecycle = CreateLifecycle();
        const int Writers = 16;
        using var barrier = new Barrier(Writers + 1);
        var observed = new System.Collections.Concurrent.ConcurrentBag<ApplicationState>();
        using var stopReading = new CancellationTokenSource();

        var reader = RunOnDedicatedThread(() =>
        {
            barrier.SignalAndWait(TestContext.Current.CancellationToken);
            while (!stopReading.IsCancellationRequested)
            {
                observed.Add(lifecycle.State);
            }
        });

        var writers = Enumerable.Range(0, Writers).Select(_ => RunOnDedicatedThread(() =>
        {
            barrier.SignalAndWait(TestContext.Current.CancellationToken);
            var accepted = 0;
            foreach (var state in new[] { ApplicationState.Starting, ApplicationState.Running, ApplicationState.Stopping, ApplicationState.Stopped })
            {
                accepted += lifecycle.TryTransitionTo(state) ? 1 : 0;
            }

            return accepted;
        })).ToArray();

        var acceptedCounts = await Task.WhenAll(writers);
        await stopReading.CancelAsync();
        await reader;

        Assert.Equal(4, acceptedCounts.Sum());
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        Assert.All(observed, static state => Assert.True(Enum.IsDefined(state)));

        var history = _logger.Collector.GetSnapshot()
            .Where(static r => r.Level == LogLevel.Information)
            .Select(static r => r.GetStructuredStateValue("State"))
            .ToArray();
        Assert.Equal(["Starting", "Running", "Stopping", "Stopped"], history);
    }

    /// <summary>Runs <paramref name="work"/> on its own thread so barrier participants never wait for thread-pool growth.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="work">The work to run.</param>
    /// <returns>A task producing the result of <paramref name="work"/>.</returns>
    private static Task<T> RunOnDedicatedThread<T>(Func<T> work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Runs <paramref name="work"/> on its own thread so barrier participants never wait for thread-pool growth.</summary>
    /// <param name="work">The work to run.</param>
    /// <returns>A task that completes when <paramref name="work"/> returns.</returns>
    private static Task RunOnDedicatedThread(Action work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Creates a lifecycle wired to the fake clock and logger.</summary>
    /// <returns>A new lifecycle.</returns>
    private ApplicationLifecycle CreateLifecycle() => new(_logger, _time);
}
