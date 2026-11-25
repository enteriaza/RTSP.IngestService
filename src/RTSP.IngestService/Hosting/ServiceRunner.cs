// Owns the process-level lifecycle around the Generic Host: build, start, wait for a shutdown signal, stop, dispose,
// and compute the exit code. It classifies configuration failures, startup failures, startup cancellation and
// startup timeouts, and guarantees that the application lifecycle reaches a terminal state before the process exits.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Lifecycle;
using RTSP.IngestService.Logging;
using Serilog.Core;

namespace RTSP.IngestService.Hosting;

/// <summary>
/// Runs the service host and returns the process exit code (see <see cref="ExitCodes"/>).
/// </summary>
/// <remarks>
/// <para>Sequence:</para>
/// <list type="number">
///   <item><description>Create the bootstrap logger and build the host. Build resolves logging and host options, so
///   invalid logging or lifetime configuration fails here and is reported through the bootstrap logger.</description></item>
///   <item><description>Start the host: the platform lifetime waits for the service manager, startup validation runs
///   for every options type, then hosted services start in registration order. The runner enforces
///   <see cref="Configuration.LifetimeOptions.StartupTimeout"/> using the registered <see cref="TimeProvider"/>.</description></item>
///   <item><description>Wait until shutdown is requested (Ctrl+C, SIGTERM, SCM stop, systemd stop, a faulted
///   background service, or <c>cancellationToken</c>), then stop hosted services in reverse order within the
///   shutdown timeout.</description></item>
///   <item><description>Log the final state and exit code, then dispose the host, which flushes the log sinks.</description></item>
/// </list>
/// <para>
/// If startup fails or is cancelled, the host is still stopped so that services that did start can release resources
/// and the platform lifetime can report the stop to the service manager. <c>IHostedService.StopAsync</c> is then also
/// called on services whose <c>StartAsync</c> never ran, which <see cref="BackgroundService"/> tolerates.
/// </para>
/// <para>
/// Exceptions thrown while stopping are logged but do not change the exit code, because the service did stop.
/// </para>
/// </remarks>
public static partial class ServiceRunner
{
    /// <summary>Logger category used by the runner.</summary>
    private const string LoggerCategory = "RTSP.IngestService.Hosting.ServiceRunner";

    /// <summary>First recorded reason for startup cancellation; stored as <see cref="int"/> for atomic updates.</summary>
    private static class StartupAbortCause
    {
        /// <summary>Startup has not been cancelled.</summary>
        public const int None = 0;

        /// <summary>A shutdown was requested through the host lifetime or the caller's token.</summary>
        public const int ShutdownRequested = 1;

        /// <summary>The configured startup timeout elapsed.</summary>
        public const int TimedOut = 2;
    }

    /// <summary>
    /// Runs the <see cref="PublisherKeyCommand"/> when it is requested; otherwise detects the hosting mode and runs the
    /// service until it stops.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="cancellationToken">Requests a graceful shutdown when cancelled.</param>
    /// <returns>The process exit code.</returns>
    public static Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default) =>
        PublisherKeyCommand.TryRun(args, Console.In, Console.Out, Console.Error, out var exitCode)
            ? Task.FromResult(exitCode)
            : RunAsync(args, HostingModeDetector.Detect(), configure: null, cancellationToken);

    /// <summary>
    /// Runs the service in the specified hosting mode until it stops.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="mode">The hosting mode.</param>
    /// <param name="configure">
    /// Optional callback applied to the builder after the standard composition and before the host is built.
    /// </param>
    /// <param name="cancellationToken">
    /// Requests a graceful shutdown when cancelled. Cancellation during startup cancels startup and is treated as a
    /// graceful stop.
    /// </param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string[] args, HostingMode mode, Action<HostApplicationBuilder>? configure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);

        using var bootstrapLogger = SerilogPipeline.CreateBootstrapLogger(mode);

        IHost host;
        try
        {
            var builder = IngestServiceHost.CreateBuilder(args, mode);
            configure?.Invoke(builder);
            host = builder.Build();
        }
        catch (Exception ex)
        {
            var exitCode = ExitCodes.FromException(ex);
            bootstrapLogger.Fatal(
                ex,
                "Service host could not be built ({FailureKind}); exiting with code {ExitCode}",
                exitCode == ExitCodes.ConfigurationError ? "configuration error" : "startup error",
                exitCode);
            return exitCode;
        }

        try
        {
            return await RunHostAsync(host, mode, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DisposeHostAsync(host, bootstrapLogger).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts the host, waits for shutdown and stops it, handling every startup outcome.
    /// </summary>
    /// <param name="host">The built host.</param>
    /// <param name="mode">The hosting mode, for logging.</param>
    /// <param name="cancellationToken">Requests a graceful shutdown when cancelled.</param>
    /// <returns>The process exit code.</returns>
    private static async Task<int> RunHostAsync(IHost host, HostingMode mode, CancellationToken cancellationToken)
    {
        var services = host.Services;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);
        var lifecycle = services.GetRequiredService<ApplicationLifecycle>();
        var applicationLifetime = services.GetRequiredService<IHostApplicationLifetime>();
        var environment = services.GetRequiredService<IHostEnvironment>();
        var hostOptions = services.GetRequiredService<IOptions<HostOptions>>().Value;
        var startupTimeout = services.GetRequiredService<IOptions<LifetimeOptions>>().Value.StartupTimeout;
        var timeProvider = services.GetRequiredService<TimeProvider>();

        UnhandledExceptionEventHandler onUnhandledException = (_, e) =>
            LogUnhandledException(logger, e.ExceptionObject as Exception, e.IsTerminating);
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobservedTaskException = (_, e) =>
            LogUnobservedTaskException(logger, e.Exception);

        AppDomain.CurrentDomain.UnhandledException += onUnhandledException;
        TaskScheduler.UnobservedTaskException += onUnobservedTaskException;
        try
        {
            var framework = RuntimeInformation.FrameworkDescription;
            LogHostStarting(logger, mode, environment.EnvironmentName, environment.ContentRootPath, framework, Environment.ProcessId);
            LogHostTimeouts(logger, startupTimeout, hostOptions.ShutdownTimeout);

            // The runner enforces the startup timeout itself and records whichever cancellation cause happens first.
            // When startup is cancelled the host treats the resulting cancellation of already-started background
            // services as faults and calls StopApplication, so ApplicationStopping alone cannot distinguish a
            // shutdown request from a timeout.
            int abortCause = StartupAbortCause.None;
            using var timeoutSource = new CancellationTokenSource(startupTimeout, timeProvider);
            using var startupSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            using var stopRegistration = applicationLifetime.ApplicationStopping.Register(() => Interlocked.CompareExchange(ref abortCause, StartupAbortCause.ShutdownRequested, StartupAbortCause.None));
            using var cancelRegistration = cancellationToken.Register(() => Interlocked.CompareExchange(ref abortCause, StartupAbortCause.ShutdownRequested, StartupAbortCause.None));
            using var timeoutRegistration = timeoutSource.Token.Register(() => Interlocked.CompareExchange(ref abortCause, StartupAbortCause.TimedOut, StartupAbortCause.None));

            try
            {
                await host.StartAsync(startupSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (Volatile.Read(ref abortCause) == StartupAbortCause.ShutdownRequested)
            {
                LogStartupCancelled(logger);
                await StopAfterAbortedStartupAsync(host, logger).ConfigureAwait(false);
                return Complete(lifecycle, logger);
            }
            catch (Exception ex)
            {
                var failure = ex is OperationCanceledException && Volatile.Read(ref abortCause) == StartupAbortCause.TimedOut
                    ? new TimeoutException($"Host startup did not complete within the configured startup timeout of {startupTimeout}.", ex)
                    : ex;
                lifecycle.TryFail(failure);
                var exitCode = ExitCodes.FromException(failure);
                LogStartupFailed(logger, failure, exitCode);
                await StopAfterAbortedStartupAsync(host, logger).ConfigureAwait(false);
                return Complete(lifecycle, logger);
            }

            try
            {
                await host.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogShutdownFailed(logger, ex);
            }

            return Complete(lifecycle, logger);
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= onUnhandledException;
            TaskScheduler.UnobservedTaskException -= onUnobservedTaskException;
        }
    }

    /// <summary>
    /// Stops the host after startup failed or was cancelled. Failures are logged and otherwise ignored.
    /// </summary>
    /// <param name="host">The host whose startup was aborted.</param>
    /// <param name="logger">The runner logger.</param>
    /// <returns>A task that completes when the stop attempt has finished.</returns>
    private static async Task StopAfterAbortedStartupAsync(IHost host, ILogger logger)
    {
        try
        {
            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogShutdownFailed(logger, ex);
        }
    }

    /// <summary>
    /// Ensures the lifecycle is terminal, logs the outcome and computes the exit code.
    /// </summary>
    /// <param name="lifecycle">The application lifecycle.</param>
    /// <param name="logger">The runner logger.</param>
    /// <returns>The process exit code.</returns>
    private static int Complete(ApplicationLifecycle lifecycle, ILogger logger)
    {
        // Covers paths where the coordinator never saw StoppedAsync, such as cancellation before hosted services were
        // resolved. Each call is a no-op when the transition is not valid from the current state.
        lifecycle.TryTransitionTo(ApplicationState.Stopping);
        lifecycle.TryTransitionTo(ApplicationState.Stopped);

        var exitCode = ExitCodes.FromLifecycle(lifecycle);
        LogHostTerminated(logger, lifecycle.State, exitCode);
        return exitCode;
    }

    /// <summary>
    /// Disposes the host, which disposes all singletons and flushes the Serilog pipeline.
    /// </summary>
    /// <param name="host">The host to dispose.</param>
    /// <param name="bootstrapLogger">Logger used if disposal fails, because the host's logging is being disposed.</param>
    /// <returns>A task that completes when disposal has finished.</returns>
    private static async Task DisposeHostAsync(IHost host, Logger bootstrapLogger)
    {
        try
        {
            if (host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                host.Dispose();
            }
        }
        catch (Exception ex)
        {
            bootstrapLogger.Error(ex, "Service host disposal failed");
        }
    }

    /// <summary>Logs process and environment details before the host starts.</summary>
    /// <param name="logger">The runner logger.</param>
    /// <param name="hostingMode">The selected hosting mode.</param>
    /// <param name="environmentName">The host environment name.</param>
    /// <param name="contentRoot">The content root path.</param>
    /// <param name="framework">The .NET runtime description.</param>
    /// <param name="processId">The process identifier.</param>
    [LoggerMessage(EventId = 4000, Level = LogLevel.Information, Message = "Starting service host in {HostingMode} mode; environment {EnvironmentName}; content root {ContentRoot}; runtime {Framework}; process {ProcessId}")]
    private static partial void LogHostStarting(ILogger logger, HostingMode hostingMode, string environmentName, string contentRoot, string framework, int processId);

    /// <summary>Logs the effective startup and shutdown timeouts.</summary>
    /// <param name="logger">The runner logger.</param>
    /// <param name="startupTimeout">The effective startup timeout.</param>
    /// <param name="shutdownTimeout">The effective shutdown timeout.</param>
    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Host startup timeout {StartupTimeout}; shutdown timeout {ShutdownTimeout}")]
    private static partial void LogHostTimeouts(ILogger logger, TimeSpan startupTimeout, TimeSpan shutdownTimeout);

    /// <summary>Logs that a shutdown request cancelled startup.</summary>
    /// <param name="logger">The runner logger.</param>
    [LoggerMessage(EventId = 4002, Level = LogLevel.Warning, Message = "Host startup was cancelled by a shutdown request")]
    private static partial void LogStartupCancelled(ILogger logger);

    /// <summary>Logs a startup failure.</summary>
    /// <param name="logger">The runner logger.</param>
    /// <param name="exception">The startup failure.</param>
    /// <param name="exitCode">The exit code that will be returned.</param>
    [LoggerMessage(EventId = 4003, Level = LogLevel.Critical, Message = "Host startup failed; the service will exit with code {ExitCode}")]
    private static partial void LogStartupFailed(ILogger logger, Exception exception, int exitCode);

    /// <summary>Logs an exception thrown while stopping the host.</summary>
    /// <param name="logger">The runner logger.</param>
    /// <param name="exception">The shutdown failure.</param>
    [LoggerMessage(EventId = 4004, Level = LogLevel.Error, Message = "Host shutdown completed with errors")]
    private static partial void LogShutdownFailed(ILogger logger, Exception exception);

    /// <summary>Logs the final lifecycle state and exit code.</summary>
    /// <param name="logger">The runner logger.</param>
    /// <param name="state">The final lifecycle state.</param>
    /// <param name="exitCode">The process exit code.</param>
    [LoggerMessage(EventId = 4005, Level = LogLevel.Information, Message = "Service host terminated in state {State} with exit code {ExitCode}")]
    private static partial void LogHostTerminated(ILogger logger, ApplicationState state, int exitCode);

    /// <summary>Logs an exception that was not handled on any thread.</summary>
    /// <param name="logger">The runner logger.</param>
    /// <param name="exception">The unhandled exception, if it derives from <see cref="Exception"/>.</param>
    /// <param name="isTerminating">Whether the runtime is terminating the process.</param>
    [LoggerMessage(EventId = 4006, Level = LogLevel.Critical, Message = "Unhandled exception; process terminating: {IsTerminating}")]
    private static partial void LogUnhandledException(ILogger logger, Exception? exception, bool isTerminating);

    /// <summary>Logs a faulted task whose exception was never observed.</summary>
    /// <param name="logger">The runner logger.</param>
    /// <param name="exception">The unobserved exception.</param>
    [LoggerMessage(EventId = 4007, Level = LogLevel.Warning, Message = "Unobserved task exception")]
    private static partial void LogUnobservedTaskException(ILogger logger, Exception exception);
}
