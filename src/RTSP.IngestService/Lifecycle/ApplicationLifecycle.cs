// Thread-safe application lifecycle state machine. It enforces the documented transition table, rejects invalid
// transitions without throwing, and logs every accepted transition while holding its lock so that log order matches
// transition order.


namespace RTSP.IngestService.Lifecycle;

/// <summary>
/// Holds the application lifecycle state. Transitions are requested by <see cref="ApplicationLifecycleCoordinator"/>
/// and <see cref="Hosting.ServiceRunner"/>; other components should depend on <see cref="IApplicationLifecycle"/>.
/// </summary>
/// <remarks>
/// All members are thread-safe. Concurrent transition requests are serialised and the transition table is evaluated
/// against the state current at the time each request acquires the lock, so at most one of several competing
/// requests for the same transition succeeds.
/// </remarks>
public sealed partial class ApplicationLifecycle : IApplicationLifecycle
{
    /// <summary>Serialises state reads and transitions.</summary>
    private readonly Lock _gate = new();

    /// <summary>Logger for transition and rejection events.</summary>
    private readonly ILogger<ApplicationLifecycle> _logger;

    /// <summary>Clock used to timestamp transitions.</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>Current state; guarded by <see cref="_gate"/>.</summary>
    private ApplicationState _state = ApplicationState.NotStarted;

    /// <summary>Time of the most recent transition; guarded by <see cref="_gate"/>.</summary>
    private DateTimeOffset _stateChangedAt;

    /// <summary>Exception recorded on entry to <see cref="ApplicationState.Failed"/>; guarded by <see cref="_gate"/>.</summary>
    private Exception? _failure;

    /// <summary>
    /// Initializes a new instance in the <see cref="ApplicationState.NotStarted"/> state.
    /// </summary>
    /// <param name="logger">Logger for transition events.</param>
    /// <param name="timeProvider">Clock used to timestamp transitions.</param>
    public ApplicationLifecycle(ILogger<ApplicationLifecycle> logger, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _logger = logger;
        _timeProvider = timeProvider;
        _stateChangedAt = timeProvider.GetUtcNow();
    }

    /// <inheritdoc />
    public ApplicationState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public bool IsReady => State == ApplicationState.Running;

    /// <inheritdoc />
    public DateTimeOffset StateChangedAt
    {
        get
        {
            lock (_gate)
            {
                return _stateChangedAt;
            }
        }
    }

    /// <inheritdoc />
    public Exception? Failure
    {
        get
        {
            lock (_gate)
            {
                return _failure;
            }
        }
    }

    /// <summary>
    /// Determines whether the transition table permits moving from <paramref name="current"/> to
    /// <paramref name="next"/>. A transition to the current state is never valid.
    /// </summary>
    /// <param name="current">The state being left.</param>
    /// <param name="next">The requested state.</param>
    /// <returns><see langword="true"/> if the transition is permitted.</returns>
    public static bool IsValidTransition(ApplicationState current, ApplicationState next) => (current, next) switch
    {
        (ApplicationState.NotStarted, ApplicationState.Starting or ApplicationState.Stopped or ApplicationState.Failed) => true,
        (ApplicationState.Starting, ApplicationState.Running or ApplicationState.Stopping or ApplicationState.Failed) => true,
        (ApplicationState.Running, ApplicationState.Stopping or ApplicationState.Failed) => true,
        (ApplicationState.Stopping, ApplicationState.Stopped or ApplicationState.Failed) => true,
        _ => false,
    };

    /// <summary>
    /// Attempts to move to <paramref name="next"/>. Use <see cref="TryFail(Exception)"/> to enter
    /// <see cref="ApplicationState.Failed"/>.
    /// </summary>
    /// <param name="next">The requested state.</param>
    /// <returns>
    /// <see langword="true"/> if the transition was applied; <see langword="false"/> if it is not permitted from the
    /// current state, in which case the state is unchanged and the rejection is logged at debug level.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="next"/> is <see cref="ApplicationState.Failed"/>.</exception>
    public bool TryTransitionTo(ApplicationState next)
    {
        if (next == ApplicationState.Failed)
        {
            throw new ArgumentException($"Use {nameof(TryFail)} to enter the {nameof(ApplicationState.Failed)} state.", nameof(next));
        }

        return TryTransition(next, failure: null);
    }

    /// <summary>
    /// Attempts to move to <see cref="ApplicationState.Failed"/>, recording <paramref name="failure"/>.
    /// </summary>
    /// <param name="failure">The exception that caused the failure.</param>
    /// <returns>
    /// <see langword="true"/> if the transition was applied; <see langword="false"/> if the current state is already
    /// terminal, in which case the original state and any earlier failure are preserved.
    /// </returns>
    public bool TryFail(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return TryTransition(ApplicationState.Failed, failure);
    }

    /// <summary>
    /// Applies a validated transition under the lock and logs it.
    /// </summary>
    /// <param name="next">The requested state.</param>
    /// <param name="failure">The failure to record when <paramref name="next"/> is <see cref="ApplicationState.Failed"/>.</param>
    /// <returns><see langword="true"/> if the transition was applied.</returns>
    private bool TryTransition(ApplicationState next, Exception? failure)
    {
        lock (_gate)
        {
            var current = _state;
            if (!IsValidTransition(current, next))
            {
                LogTransitionRejected(current, next);
                return false;
            }

            _state = next;
            _stateChangedAt = _timeProvider.GetUtcNow();
            _failure = failure;

            if (failure is null)
            {
                LogTransition(current, next);
            }
            else
            {
                LogFailedTransition(current, next, failure.GetType().FullName, failure.Message);
            }

            return true;
        }
    }

    /// <summary>Logs an accepted transition.</summary>
    /// <param name="previousState">The state that was left.</param>
    /// <param name="state">The state that was entered.</param>
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "Application lifecycle state changed from {PreviousState} to {State}")]
    private partial void LogTransition(ApplicationState previousState, ApplicationState state);

    /// <summary>
    /// Logs entry to <see cref="ApplicationState.Failed"/>. Only the exception type and message are included; the
    /// component that observed the exception is responsible for logging the full exception.
    /// </summary>
    /// <param name="previousState">The state that was left.</param>
    /// <param name="state">The failed state.</param>
    /// <param name="failureType">Full type name of the recorded exception.</param>
    /// <param name="failureMessage">Message of the recorded exception.</param>
    [LoggerMessage(EventId = 1001, Level = LogLevel.Error, Message = "Application lifecycle state changed from {PreviousState} to {State}: {FailureType}: {FailureMessage}")]
    private partial void LogFailedTransition(ApplicationState previousState, ApplicationState state, string? failureType, string failureMessage);

    /// <summary>Logs a rejected transition request.</summary>
    /// <param name="state">The current, unchanged state.</param>
    /// <param name="requestedState">The rejected target state.</param>
    [LoggerMessage(EventId = 1002, Level = LogLevel.Debug, Message = "Ignored application lifecycle transition from {State} to {RequestedState}")]
    private partial void LogTransitionRejected(ApplicationState state, ApplicationState requestedState);
}
