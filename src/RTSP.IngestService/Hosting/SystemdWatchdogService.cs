// Sends systemd watchdog keep-alives (WATCHDOG=1) through the framework's ISystemdNotifier while the application is
// healthy. Keep-alives stop when the application enters the Failed state, letting systemd's watchdog restart a
// service that can no longer make progress. Registered only in systemd hosting mode.

using System.Net.Sockets;
using Microsoft.Extensions.Hosting.Systemd;
using RTSP.IngestService.Lifecycle;

namespace RTSP.IngestService.Hosting;

/// <summary>
/// Background service that notifies the systemd watchdog at <see cref="SystemdWatchdogSettings.PingInterval"/>.
/// </summary>
/// <remarks>
/// <para>
/// The service does nothing and completes immediately when systemd notifications are unavailable
/// (<see cref="ISystemdNotifier.IsEnabled"/> is <see langword="false"/>) or the unit does not set <c>WatchdogSec=</c>.
/// </para>
/// <para>
/// A keep-alive is sent immediately on start and then once per interval while the lifecycle state is
/// <see cref="ApplicationState.Starting"/>, <see cref="ApplicationState.Running"/> or
/// <see cref="ApplicationState.Stopping"/>. Keep-alives are driven by a timer, so they demonstrate that the process
/// and its thread pool are responsive and that the application has not failed; they do not prove that other
/// background work is making progress.
/// </para>
/// </remarks>
public sealed partial class SystemdWatchdogService : BackgroundService
{
    /// <summary>The <c>sd_notify</c> keep-alive message.</summary>
    public static readonly ServiceState WatchdogKeepAlive = new("WATCHDOG=1");

    /// <summary>Framework notifier that writes to the systemd notification socket.</summary>
    private readonly ISystemdNotifier _notifier;

    /// <summary>Watchdog timeout parsed from the environment.</summary>
    private readonly SystemdWatchdogSettings _settings;

    /// <summary>Lifecycle consulted before each keep-alive.</summary>
    private readonly IApplicationLifecycle _lifecycle;

    /// <summary>Clock driving the keep-alive timer.</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>Logger for watchdog diagnostics.</summary>
    private readonly ILogger<SystemdWatchdogService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemdWatchdogService"/> class.
    /// </summary>
    /// <param name="notifier">The systemd notifier.</param>
    /// <param name="settings">The watchdog settings for this process.</param>
    /// <param name="lifecycle">The application lifecycle.</param>
    /// <param name="timeProvider">Clock driving the keep-alive timer.</param>
    /// <param name="logger">Logger for watchdog diagnostics.</param>
    public SystemdWatchdogService(
        ISystemdNotifier notifier,
        SystemdWatchdogSettings settings,
        IApplicationLifecycle lifecycle,
        TimeProvider timeProvider,
        ILogger<SystemdWatchdogService> logger)
    {
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _notifier = notifier;
        _settings = settings;
        _lifecycle = lifecycle;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Sends keep-alives until <paramref name="stoppingToken"/> is cancelled. A failed send is logged as a warning and
    /// retried at the next interval.
    /// </summary>
    /// <param name="stoppingToken">Cancelled when the host stops this service.</param>
    /// <returns>A task that completes when the watchdog is disabled, or is cancelled when the service stops.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_notifier.IsEnabled || _settings is not { Timeout: { } timeout, PingInterval: { } interval })
        {
            LogWatchdogDisabled();
            return;
        }

        LogWatchdogEnabled(timeout, interval);

        using var timer = new PeriodicTimer(interval, _timeProvider);
        var suppressionLogged = false;

        do
        {
            var state = _lifecycle.State;
            if (state is ApplicationState.Starting or ApplicationState.Running or ApplicationState.Stopping)
            {
                SendKeepAlive();
            }
            else if (!suppressionLogged)
            {
                suppressionLogged = true;
                LogKeepAliveSuppressed(state);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Sends one keep-alive, logging socket failures instead of faulting the service.</summary>
    private void SendKeepAlive()
    {
        try
        {
            _notifier.Notify(WatchdogKeepAlive);
        }
        catch (SocketException ex)
        {
            LogKeepAliveFailed(ex);
        }
    }

    /// <summary>Logs that the watchdog is not enabled for this process.</summary>
    [LoggerMessage(EventId = 2000, Level = LogLevel.Debug, Message = "systemd watchdog is not enabled for this process; no keep-alives will be sent")]
    private partial void LogWatchdogDisabled();

    /// <summary>Logs the active watchdog timeout and keep-alive interval.</summary>
    /// <param name="watchdogTimeout">The watchdog timeout configured by the unit.</param>
    /// <param name="keepAliveInterval">The interval between keep-alives.</param>
    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "systemd watchdog enabled with timeout {WatchdogTimeout}; sending keep-alives every {KeepAliveInterval}")]
    private partial void LogWatchdogEnabled(TimeSpan watchdogTimeout, TimeSpan keepAliveInterval);

    /// <summary>Logs, once, that keep-alives have stopped because of the lifecycle state.</summary>
    /// <param name="state">The lifecycle state that suppressed keep-alives.</param>
    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "systemd watchdog keep-alives suspended because the application is {State}")]
    private partial void LogKeepAliveSuppressed(ApplicationState state);

    /// <summary>Logs a failure to write a keep-alive to the notification socket.</summary>
    /// <param name="exception">The socket failure.</param>
    [LoggerMessage(EventId = 2003, Level = LogLevel.Warning, Message = "Failed to send systemd watchdog keep-alive")]
    private partial void LogKeepAliveFailed(Exception exception);
}
