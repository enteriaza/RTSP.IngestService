// Application-level lifecycle states observed by ApplicationLifecycle. The states mirror, and never drive, the
// Generic Host lifecycle.

namespace RTSP.IngestService.Lifecycle;

/// <summary>
/// Lifecycle states of the application. Valid transitions are:
/// <list type="bullet">
///   <item><description><see cref="NotStarted"/> → <see cref="Starting"/>, <see cref="Stopped"/> or <see cref="Failed"/></description></item>
///   <item><description><see cref="Starting"/> → <see cref="Running"/>, <see cref="Stopping"/> or <see cref="Failed"/></description></item>
///   <item><description><see cref="Running"/> → <see cref="Stopping"/> or <see cref="Failed"/></description></item>
///   <item><description><see cref="Stopping"/> → <see cref="Stopped"/> or <see cref="Failed"/></description></item>
/// </list>
/// <see cref="Stopped"/> and <see cref="Failed"/> are terminal.
/// </summary>
public enum ApplicationState
{
    /// <summary>The host has been built but hosted-service startup has not begun.</summary>
    NotStarted = 0,

    /// <summary>Hosted services are starting; the application is not ready.</summary>
    Starting = 1,

    /// <summary>All hosted services started and the host raised <c>ApplicationStarted</c>; the application is ready.</summary>
    Running = 2,

    /// <summary>Shutdown has been requested; hosted services are being stopped and the application is not ready.</summary>
    Stopping = 3,

    /// <summary>All hosted services have been stopped, or startup was cancelled before it began.</summary>
    Stopped = 4,

    /// <summary>Startup failed or a hosted service faulted. The process exits with a non-zero exit code.</summary>
    Failed = 5,
}
