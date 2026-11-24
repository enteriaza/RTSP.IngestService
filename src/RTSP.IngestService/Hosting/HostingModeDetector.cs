// Selects the hosting mode from the framework's service-manager detection helpers. The decision logic is separated
// from the detection probes so it can be tested without a real service manager.

using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace RTSP.IngestService.Hosting;

/// <summary>
/// Determines the <see cref="HostingMode"/> for the current process.
/// </summary>
public static class HostingModeDetector
{
    /// <summary>
    /// Detects the hosting mode using <see cref="WindowsServiceHelpers.IsWindowsService"/> (parent process is
    /// <c>services.exe</c>) and <see cref="SystemdHelpers.IsSystemdService"/> (parent process is systemd, or PID 1 with
    /// systemd notification variables). Both helpers cache their result for the life of the process.
    /// </summary>
    /// <returns>The detected hosting mode.</returns>
    public static HostingMode Detect() => Select(WindowsServiceHelpers.IsWindowsService(), SystemdHelpers.IsSystemdService());

    /// <summary>
    /// Selects the hosting mode from detection results. Windows Service takes precedence, then systemd; otherwise the
    /// process is treated as a console application.
    /// </summary>
    /// <param name="isWindowsService">Whether the process was started by the Windows Service Control Manager.</param>
    /// <param name="isSystemdService">Whether the process was started by systemd.</param>
    /// <returns>The selected hosting mode.</returns>
    public static HostingMode Select(bool isWindowsService, bool isSystemdService) => (isWindowsService, isSystemdService) switch
    {
        (true, _) => HostingMode.WindowsService,
        (false, true) => HostingMode.Systemd,
        _ => HostingMode.Console,
    };
}
