// Extends the framework WindowsServiceLifetime with two behaviours the base class does not provide: it asks the
// Service Control Manager for enough time to complete the configured graceful shutdown, and it reports the
// application's exit code to SCM so that failures are visible and can trigger service recovery actions.

using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Lifecycle;

namespace RTSP.IngestService.Hosting;

/// <summary>
/// Windows Service host lifetime. Start, stop and shutdown handling is inherited from
/// <see cref="WindowsServiceLifetime"/>: SCM is told the service is running as soon as SCM calls <c>OnStart</c>, and
/// a stop or system-shutdown request triggers <see cref="IHostApplicationLifetime.StopApplication"/> and blocks the SCM
/// handler until the host raises <see cref="IHostApplicationLifetime.ApplicationStopped"/> or
/// <see cref="HostOptions.ShutdownTimeout"/> elapses.
/// </summary>
/// <remarks>
/// <para>
/// On stop, this class first requests a wait hint covering <see cref="HostOptions.ShutdownTimeout"/> plus
/// <see cref="StopWaitHintMargin"/>, so SCM and tools such as <c>Stop-Service</c> do not consider the service hung
/// during a long graceful shutdown.
/// </para>
/// <para>
/// After the host has stopped, <see cref="System.ServiceProcess.ServiceBase.ExitCode"/> is set from the application
/// lifecycle (see <see cref="ExitCodes.FromLifecycle(IApplicationLifecycle)"/>). A non-zero value is reported to SCM
/// as the service's Win32 exit code; recovery actions run for such stops only when the service is configured with
/// <c>sc.exe failureflag &lt;name&gt; 1</c>.
/// </para>
/// <para>
/// During operating-system shutdown Windows applies its own deadline and ignores additional-time requests, so no
/// wait hint is requested there.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceControlLifetime : WindowsServiceLifetime
{
    /// <summary>Allowance added to the shutdown timeout for host disposal after hosted services have stopped.</summary>
    public static readonly TimeSpan StopWaitHintMargin = TimeSpan.FromSeconds(5);

    /// <summary>Lifecycle used to derive the exit code reported to SCM.</summary>
    private readonly IApplicationLifecycle _lifecycle;

    /// <summary>The configured graceful shutdown timeout.</summary>
    private readonly TimeSpan _shutdownTimeout;

    /// <summary>
    /// Initializes a new instance of the <see cref="WindowsServiceControlLifetime"/> class.
    /// </summary>
    /// <param name="environment">The host environment.</param>
    /// <param name="applicationLifetime">The host application lifetime.</param>
    /// <param name="loggerFactory">Logger factory used by the base lifetime.</param>
    /// <param name="hostOptions">Host options supplying the shutdown timeout.</param>
    /// <param name="windowsServiceOptions">Windows Service options supplying the service name.</param>
    /// <param name="lifecycle">The application lifecycle used to compute the exit code.</param>
    public WindowsServiceControlLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> hostOptions,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptions,
        IApplicationLifecycle lifecycle)
        : base(environment, applicationLifetime, loggerFactory, hostOptions, windowsServiceOptions)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);

        _lifecycle = lifecycle;
        _shutdownTimeout = hostOptions.Value.ShutdownTimeout;
    }

    /// <summary>
    /// Converts a shutdown timeout into the SCM wait hint, in milliseconds, requested when a stop begins.
    /// </summary>
    /// <param name="shutdownTimeout">The configured graceful shutdown timeout.</param>
    /// <returns>The timeout plus <see cref="StopWaitHintMargin"/>, clamped to <see cref="int.MaxValue"/> milliseconds.</returns>
    public static int GetStopWaitHintMilliseconds(TimeSpan shutdownTimeout) =>
        (int)Math.Min(int.MaxValue, (shutdownTimeout + StopWaitHintMargin).TotalMilliseconds);

    /// <summary>
    /// Handles an SCM stop request, or a stop initiated by the host itself: requests a wait hint for the configured
    /// shutdown, waits for the host to stop (bounded by the shutdown timeout), then records the exit code.
    /// </summary>
    protected override void OnStop()
    {
        RequestAdditionalTime(GetStopWaitHintMilliseconds(_shutdownTimeout));
        base.OnStop();
        ExitCode = ExitCodes.FromLifecycle(_lifecycle);
    }

    /// <summary>
    /// Handles operating-system shutdown: waits for the host to stop (bounded by the shutdown timeout and by the
    /// operating system's own deadline), then records the exit code.
    /// </summary>
    protected override void OnShutdown()
    {
        base.OnShutdown();
        ExitCode = ExitCodes.FromLifecycle(_lifecycle);
    }
}
