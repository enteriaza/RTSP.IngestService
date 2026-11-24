// Application identity settings bound from the "Application" configuration section. The name identifies the
// service in structured logs and is used as the Windows Service name reported to the Service Control Manager.

using System.ComponentModel.DataAnnotations;

namespace RTSP.IngestService.Configuration;

/// <summary>
/// Identity of this service instance. <see cref="Name"/> is mandatory and has no code default, so a deployment
/// that omits it fails startup validation.
/// </summary>
public sealed class ApplicationOptions
{
    /// <summary>Configuration section bound to this type.</summary>
    public const string SectionName = "Application";

    /// <summary>
    /// Gets or sets the service name. It is attached to every log event as the <c>Application</c> property and used
    /// as <c>WindowsServiceLifetimeOptions.ServiceName</c>. Must start with a letter and contain at most 64 letters,
    /// digits, '.', '_' or '-' characters.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[A-Za-z][A-Za-z0-9._-]{0,63}$")]
    public string Name { get; set; } = string.Empty;
}
