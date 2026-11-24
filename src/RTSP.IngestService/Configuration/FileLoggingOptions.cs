// Rolling file log settings bound from the "FileLogging" configuration section. File logging is the durable log
// destination for Windows Service execution, where no console is attached.

using System.ComponentModel.DataAnnotations;

namespace RTSP.IngestService.Configuration;

/// <summary>
/// Controls whether a rolling JSON log file is written and how it is retained.
/// </summary>
public sealed class FileLoggingOptions
{
    /// <summary>Configuration section bound to this type.</summary>
    public const string SectionName = "FileLogging";

    /// <summary>
    /// Gets or sets when file logging is active. Defaults to <see cref="FileLoggingMode.Auto"/>.
    /// </summary>
    public FileLoggingMode Mode { get; set; } = FileLoggingMode.Auto;

    /// <summary>
    /// Gets or sets the log file path. Relative paths are resolved against the host content root (the application
    /// directory). Serilog inserts the date, and a sequence number when size rolling occurs, before the extension.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Path { get; set; } = "logs/rtsp-ingest-service-.log";

    /// <summary>
    /// Gets or sets how many rolled files are kept before the oldest is deleted. Defaults to 31; valid range is 1 to 365.
    /// </summary>
    [Range(1, 365)]
    public int RetainedFileCountLimit { get; set; } = 31;

    /// <summary>
    /// Gets or sets the size, in mebibytes, at which the current file is rolled to a new one. Defaults to 100; valid
    /// range is 1 to 10240.
    /// </summary>
    [Range(1, 10240)]
    public int FileSizeLimitMegabytes { get; set; } = 100;
}
