// Read-only view of the application lifecycle and readiness, intended for injection into any component that needs
// to know whether the application has completed initialisation or has begun shutting down.

namespace RTSP.IngestService.Lifecycle;

/// <summary>
/// Exposes the current application lifecycle state and readiness. All members are thread-safe.
/// </summary>
public interface IApplicationLifecycle
{
    /// <summary>Gets the current lifecycle state.</summary>
    ApplicationState State { get; }

    /// <summary>
    /// Gets a value indicating whether the application is ready, which is <see langword="true"/> only while
    /// <see cref="State"/> is <see cref="ApplicationState.Running"/>. Readiness is never reported before every
    /// hosted service has started and is withdrawn as soon as shutdown begins.
    /// </summary>
    bool IsReady { get; }

    /// <summary>Gets the time, from the registered <see cref="TimeProvider"/>, of the most recent state change.</summary>
    DateTimeOffset StateChangedAt { get; }

    /// <summary>
    /// Gets the exception recorded when the application entered <see cref="ApplicationState.Failed"/>, or
    /// <see langword="null"/> in every other state.
    /// </summary>
    Exception? Failure { get; }
}
