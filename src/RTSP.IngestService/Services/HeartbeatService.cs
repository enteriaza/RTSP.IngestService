// Operational heartbeat: periodically logs the application lifecycle state and uptime so operators can confirm from
// the logs alone that the service is alive. The interval is reloadable through IOptionsMonitor.

using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Lifecycle;

namespace RTSP.IngestService.Services;

/// <summary>
/// Background service that logs a heartbeat every <see cref="HeartbeatOptions.Interval"/>.
/// </summary>
/// <remarks>
/// The interval is re-read after every heartbeat, so a configuration change takes effect from the following
/// heartbeat. If the reloaded configuration fails validation, the last valid interval is kept and a warning is logged
/// on each heartbeat until the configuration is corrected.
/// </remarks>
public sealed partial class HeartbeatService : BackgroundService
{
    /// <summary>Source of the current, validated heartbeat settings.</summary>
    private readonly IOptionsMonitor<HeartbeatOptions> _options;

    /// <summary>Lifecycle whose state is reported in each heartbeat.</summary>
    private readonly IApplicationLifecycle _lifecycle;

    /// <summary>Clock driving the heartbeat timer and uptime measurement.</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>Logger for heartbeat entries.</summary>
    private readonly ILogger<HeartbeatService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeartbeatService"/> class.
    /// </summary>
    /// <param name="options">Monitor for the heartbeat settings.</param>
    /// <param name="lifecycle">The application lifecycle.</param>
    /// <param name="timeProvider">Clock driving the heartbeat timer.</param>
    /// <param name="logger">Logger for heartbeat entries.</param>
    public HeartbeatService(
        IOptionsMonitor<HeartbeatOptions> options,
        IApplicationLifecycle lifecycle,
        TimeProvider timeProvider,
        ILogger<HeartbeatService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _lifecycle = lifecycle;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Logs a heartbeat at each interval until <paramref name="stoppingToken"/> is cancelled.
    /// </summary>
    /// <param name="stoppingToken">Cancelled when the host stops this service.</param>
    /// <returns>A task that is cancelled when the service stops.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startedAt = _timeProvider.GetTimestamp();
        var interval = _options.CurrentValue.Interval;

        using var timer = new PeriodicTimer(interval, _timeProvider);
        LogHeartbeatStarted(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var uptime = _timeProvider.GetElapsedTime(startedAt);
            LogHeartbeat(_lifecycle.State, uptime);

            var next = ReadInterval(interval);
            if (next != interval)
            {
                interval = next;
                timer.Period = interval;
                LogIntervalChanged(interval);
            }
        }
    }

    /// <summary>
    /// Reads the current interval, falling back to <paramref name="current"/> when the reloaded configuration is invalid.
    /// </summary>
    /// <param name="current">The interval in use.</param>
    /// <returns>The interval to use for the next heartbeat.</returns>
    private TimeSpan ReadInterval(TimeSpan current)
    {
        try
        {
            return _options.CurrentValue.Interval;
        }
        catch (OptionsValidationException ex)
        {
            LogInvalidReload(current, ex.Message);
            return current;
        }
    }

    /// <summary>Logs that the heartbeat timer has been created.</summary>
    /// <param name="interval">The initial interval.</param>
    [LoggerMessage(EventId = 3000, Level = LogLevel.Information, Message = "Heartbeat started with interval {Interval}")]
    private partial void LogHeartbeatStarted(TimeSpan interval);

    /// <summary>Logs a heartbeat.</summary>
    /// <param name="state">The current lifecycle state.</param>
    /// <param name="uptime">Time since the heartbeat service started.</param>
    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Heartbeat: application is {State}, uptime {Uptime}")]
    private partial void LogHeartbeat(ApplicationState state, TimeSpan uptime);

    /// <summary>Logs that a reloaded interval has been applied.</summary>
    /// <param name="interval">The new interval.</param>
    [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "Heartbeat interval changed to {Interval}")]
    private partial void LogIntervalChanged(TimeSpan interval);

    /// <summary>Logs that the reloaded heartbeat configuration is invalid and the previous interval is retained.</summary>
    /// <param name="interval">The interval being retained.</param>
    /// <param name="validationMessage">The validation failure message.</param>
    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning, Message = "Reloaded heartbeat configuration is invalid; keeping interval {Interval}: {ValidationMessage}")]
    private partial void LogInvalidReload(TimeSpan interval, string validationMessage);
}
