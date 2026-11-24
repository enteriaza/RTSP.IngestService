// Enumerates the execution environments the service supports. The mode selects the host lifetime and log sinks.

namespace RTSP.IngestService.Hosting;

/// <summary>
/// The execution environment detected at process start.
/// </summary>
public enum HostingMode
{
    /// <summary>Interactive or redirected console execution using the default console lifetime (Ctrl+C, SIGTERM).</summary>
    Console = 0,

    /// <summary>Running under the Windows Service Control Manager.</summary>
    WindowsService = 1,

    /// <summary>Running as a systemd service unit.</summary>
    Systemd = 2,
}
