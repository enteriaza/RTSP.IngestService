// Host startup and shutdown timing bound from the "Lifetime" configuration section. The startup timeout is enforced
// by ServiceRunner and the shutdown timeout is applied to HostOptions; together they govern how long hosted services
// may take to start and to stop gracefully.

namespace RTSP.IngestService.Configuration;

/// <summary>
/// Startup and graceful-shutdown limits for the service host.
/// </summary>
/// <remarks>
/// Service managers enforce their own limits: the systemd unit's <c>TimeoutStartSec</c>/<c>TimeoutStopSec</c> must
/// exceed these values, and Windows enforces its own deadline when the operating system itself shuts down.
/// </remarks>
public sealed class LifetimeOptions
{
    /// <summary>Configuration section bound to this type.</summary>
    public const string SectionName = "Lifetime";

    /// <summary>
    /// Gets or sets the maximum time hosted services may spend starting before startup is cancelled and treated as a
    /// failure. Defaults to one minute; valid range is one second to one hour.
    /// </summary>
    [TimeSpanRange("00:00:01", "01:00:00")]
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets the maximum time hosted services may spend stopping. When it expires the cancellation token
    /// passed to <c>StopAsync</c> is cancelled and the host proceeds without waiting further. Defaults to 30
    /// seconds; valid range is one second to ten minutes.
    /// </summary>
    [TimeSpanRange("00:00:01", "00:10:00")]
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
