// Enumerates the file logging activation policies accepted by the "FileLogging:Mode" configuration key.

namespace RTSP.IngestService.Configuration;

/// <summary>
/// Determines when the rolling file log sink is enabled.
/// </summary>
public enum FileLoggingMode
{
    /// <summary>
    /// File logging is enabled only for Windows Service execution. Console and systemd execution rely on standard
    /// output, which systemd forwards to the journal.
    /// </summary>
    Auto = 0,

    /// <summary>File logging is enabled in every hosting mode.</summary>
    Enabled = 1,

    /// <summary>File logging is disabled in every hosting mode.</summary>
    Disabled = 2,
}
