// Process exit codes and their derivation from the application lifecycle. The same codes are returned from Main and
// reported to the Windows Service Control Manager, so service managers can distinguish configuration errors (which
// should not be retried) from other failures.

using Microsoft.Extensions.Options;
using RTSP.IngestService.Lifecycle;

namespace RTSP.IngestService.Hosting;

/// <summary>
/// Process exit codes returned by the service.
/// </summary>
public static class ExitCodes
{
    /// <summary>The host started and stopped gracefully, or startup was cancelled by a shutdown request.</summary>
    public const int Success = 0;

    /// <summary>Startup failed, startup timed out, or a hosted service faulted.</summary>
    public const int Failure = 1;

    /// <summary>
    /// Configuration was missing or invalid (<c>EX_CONFIG</c> from sysexits.h). The systemd unit uses
    /// <c>RestartPreventExitStatus=78</c> so that a misconfigured service is not restarted in a loop.
    /// </summary>
    public const int ConfigurationError = 78;

    /// <summary>
    /// Maps an exception that terminated startup or execution to an exit code.
    /// </summary>
    /// <param name="exception">The terminating exception.</param>
    /// <returns><see cref="ConfigurationError"/> for configuration failures; otherwise <see cref="Failure"/>.</returns>
    public static int FromException(Exception exception) =>
        IsConfigurationFailure(exception) ? ConfigurationError : Failure;

    /// <summary>
    /// Maps the final lifecycle state to an exit code.
    /// </summary>
    /// <param name="lifecycle">The application lifecycle.</param>
    /// <returns>
    /// <see cref="Success"/> unless the state is <see cref="ApplicationState.Failed"/>, in which case the recorded
    /// failure is mapped with <see cref="FromException(Exception)"/>.
    /// </returns>
    public static int FromLifecycle(IApplicationLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);

        if (lifecycle.State != ApplicationState.Failed)
        {
            return Success;
        }

        return lifecycle.Failure is { } failure ? FromException(failure) : Failure;
    }

    /// <summary>
    /// Determines whether <paramref name="exception"/> represents a configuration failure: an
    /// <see cref="OptionsValidationException"/>, or an <see cref="AggregateException"/> (as thrown by startup
    /// validation of several options types) whose inner exceptions are all configuration failures.
    /// </summary>
    /// <param name="exception">The exception to classify.</param>
    /// <returns><see langword="true"/> if the exception represents a configuration failure.</returns>
    public static bool IsConfigurationFailure(Exception exception) => exception switch
    {
        OptionsValidationException => true,
        AggregateException { InnerExceptions.Count: > 0 } aggregate => aggregate.InnerExceptions.All(IsConfigurationFailure),
        _ => false,
    };
}
