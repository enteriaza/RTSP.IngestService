// Operational heartbeat settings bound from the "Heartbeat" configuration section. The interval is read through
// IOptionsMonitor, so changes to the configuration file take effect without restarting the service.

namespace RTSP.IngestService.Configuration;

/// <summary>
/// Settings for <see cref="Services.HeartbeatService"/>, which periodically logs the lifecycle state and uptime.
/// </summary>
public sealed class HeartbeatOptions
{
    /// <summary>Configuration section bound to this type.</summary>
    public const string SectionName = "Heartbeat";

    /// <summary>
    /// Gets or sets the time between heartbeat log entries. Defaults to one minute; valid range is one second to one
    /// hour. A reloaded value is applied from the next heartbeat onwards.
    /// </summary>
    [TimeSpanRange("00:00:01", "01:00:00")]
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);
}
