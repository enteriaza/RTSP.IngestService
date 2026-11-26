// Deployment identity bound from the "Application" configuration section: which runtime instance, logical node,
// region and availability zone this process is. Every value is optional, so single-node and non-cloud deployments
// need no configuration; an absent value is simply not reported, never invented. The application name is not here:
// it is the entry assembly's name (ApplicationIdentity), and the retired Application:Name setting is rejected so that
// a deployment still setting it learns that it no longer has an effect.

using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace RTSP.IngestService.Configuration;

/// <summary>Deployment identity of this service instance (<c>Application</c> section).</summary>
/// <remarks>
/// Values are opaque identifiers chosen by the deployment, with no cloud-specific format: 1 to 128 (instance and node)
/// or 1 to 64 (region and zone) letters, digits, '.', '_', ':' or '-', starting and ending with a letter or digit.
/// They appear in every log event and as OpenTelemetry resource attributes, so they must not contain secrets.
/// </remarks>
public sealed class ApplicationOptions : IValidatableObject
{
    /// <summary>Configuration section bound to this type.</summary>
    public const string SectionName = "Application";

    /// <summary>The longest instance or node identifier.</summary>
    public const int MaxIdentifierLength = 128;

    /// <summary>The longest region or availability zone.</summary>
    public const int MaxLocationLength = 64;

    /// <summary>The retired key that once configured the application name.</summary>
    internal const string RetiredNameKey = "Name";

    /// <summary>
    /// Gets or sets the runtime instance identifier, for example a VM, container or EC2 instance ID
    /// (<c>i-0123456789abcdef0</c>). Unset by default. Reported as <c>service.instance.id</c>.
    /// </summary>
    [StringLength(MaxIdentifierLength)]
    public string? InstanceId { get; set; }

    /// <summary>
    /// Gets or sets the logical infrastructure node, for example <c>node01.af-south-1</c>. Unset by default. Reported as
    /// <c>application.node.id</c>.
    /// </summary>
    [StringLength(MaxIdentifierLength)]
    public string? NodeId { get; set; }

    /// <summary>Gets or sets the deployment region, for example <c>af-south-1</c>. Unset by default. Reported as <c>cloud.region</c>.</summary>
    [StringLength(MaxLocationLength)]
    public string? Region { get; set; }

    /// <summary>
    /// Gets or sets the availability zone, for example <c>af-south-1a</c>. Unset by default. Reported as
    /// <c>cloud.availability_zone</c>.
    /// </summary>
    [StringLength(MaxLocationLength)]
    public string? AvailabilityZone { get; set; }

    /// <summary>
    /// Throws when the section still sets the retired <c>Application:Name</c>, with the reason and the fix, so that it
    /// fails startup as a configuration error (exit code 78) instead of being silently ignored.
    /// </summary>
    /// <param name="section">The <c>Application</c> section.</param>
    /// <exception cref="InvalidOperationException"><c>Application:Name</c> is set.</exception>
    internal static void ThrowIfNameConfigured(IConfiguration section)
    {
        if (section[RetiredNameKey] is not null)
        {
            throw new InvalidOperationException(
                $"Application:Name is no longer supported. The application name is the entry assembly's name ({ApplicationIdentity.ServiceAssemblyName}) and cannot be configured; " +
                "remove Application:Name from the configuration files and the Application__Name environment variable. " +
                "To tell deployments apart, set Application:InstanceId, Application:NodeId, Application:Region or Application:AvailabilityZone.");
        }
    }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var (name, value) in new[] { (nameof(InstanceId), InstanceId), (nameof(NodeId), NodeId), (nameof(Region), Region), (nameof(AvailabilityZone), AvailabilityZone) })
        {
            if (!TryValidateValue(value, out var error))
            {
                yield return new ValidationResult($"Application:{name} '{value}' {error}.", [name]);
            }
        }
    }

    /// <summary>Checks one configured identity value.</summary>
    /// <param name="value">The value, or <see langword="null"/> when not configured.</param>
    /// <param name="error">Why the value is invalid.</param>
    /// <returns><see langword="true"/> if the value is unset or valid.</returns>
    private static bool TryValidateValue(string? value, [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (value is null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "is empty; remove the setting to leave it unset, or give it a value";
            return false;
        }

        if (!char.IsAsciiLetterOrDigit(value[0]) || !char.IsAsciiLetterOrDigit(value[^1]))
        {
            error = "must start and end with a letter or digit";
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or ':' or '-'))
            {
                error = $"contains '{c}'; only letters, digits, '.', '_', ':' and '-' are allowed";
                return false;
            }
        }

        return true;
    }
}
