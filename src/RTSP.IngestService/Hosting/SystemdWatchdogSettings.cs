// Parses the watchdog contract that systemd passes to a service whose unit sets WatchdogSec=. systemd exports
// WATCHDOG_USEC (the timeout in microseconds) and WATCHDOG_PID (the process expected to send keep-alives).

using System.Globalization;

namespace RTSP.IngestService.Hosting;

/// <summary>
/// The systemd watchdog timeout for this process, or <see langword="null"/> when the watchdog is not enabled.
/// </summary>
/// <param name="Timeout">The watchdog timeout, or <see langword="null"/> if the watchdog is disabled for this process.</param>
public sealed record SystemdWatchdogSettings(TimeSpan? Timeout)
{
    /// <summary>Environment variable carrying the watchdog timeout in microseconds.</summary>
    public const string WatchdogUsecVariable = "WATCHDOG_USEC";

    /// <summary>Environment variable carrying the PID that is expected to send keep-alive notifications.</summary>
    public const string WatchdogPidVariable = "WATCHDOG_PID";

    /// <summary>Settings representing a disabled watchdog.</summary>
    public static SystemdWatchdogSettings Disabled { get; } = new((TimeSpan?)null);

    /// <summary>
    /// Upper bound on <see cref="PingInterval"/>. Keeps the interval within the range accepted by
    /// <see cref="PeriodicTimer"/>; pinging more often than necessary is harmless.
    /// </summary>
    public static readonly TimeSpan MaxPingInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// Gets the keep-alive interval: half the timeout, as recommended by <c>sd_watchdog_enabled(3)</c>, capped at
    /// <see cref="MaxPingInterval"/>; or <see langword="null"/> when the watchdog is disabled.
    /// </summary>
    public TimeSpan? PingInterval => Timeout is { } timeout ? TimeSpan.FromTicks(Math.Min(timeout.Ticks / 2, MaxPingInterval.Ticks)) : null;

    /// <summary>
    /// Reads the watchdog settings for the current process from the environment.
    /// </summary>
    /// <returns>The parsed settings.</returns>
    public static SystemdWatchdogSettings FromEnvironment() => Parse(
        Environment.GetEnvironmentVariable(WatchdogUsecVariable),
        Environment.GetEnvironmentVariable(WatchdogPidVariable),
        Environment.ProcessId);

    /// <summary>
    /// Parses raw watchdog variables following <c>sd_watchdog_enabled(3)</c>: the watchdog is enabled when
    /// <paramref name="watchdogUsec"/> is a positive integer and <paramref name="watchdogPid"/> is either absent or
    /// equal to <paramref name="currentProcessId"/>.
    /// </summary>
    /// <param name="watchdogUsec">The value of <c>WATCHDOG_USEC</c>, if set.</param>
    /// <param name="watchdogPid">The value of <c>WATCHDOG_PID</c>, if set.</param>
    /// <param name="currentProcessId">The current process identifier.</param>
    /// <returns>The parsed settings; <see cref="Disabled"/> for absent, malformed or non-matching values.</returns>
    public static SystemdWatchdogSettings Parse(string? watchdogUsec, string? watchdogPid, int currentProcessId)
    {
        if (!ulong.TryParse(watchdogUsec, NumberStyles.None, CultureInfo.InvariantCulture, out var microseconds) || microseconds == 0)
        {
            return Disabled;
        }

        if (!string.IsNullOrEmpty(watchdogPid)
            && (!int.TryParse(watchdogPid, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid != currentProcessId))
        {
            return Disabled;
        }

        // TimeSpan.FromMicroseconds is limited to long; larger values are far beyond any meaningful watchdog.
        var timeout = microseconds > long.MaxValue / 10 ? TimeSpan.MaxValue : TimeSpan.FromMicroseconds((long)microseconds);
        return new SystemdWatchdogSettings(timeout);
    }
}
