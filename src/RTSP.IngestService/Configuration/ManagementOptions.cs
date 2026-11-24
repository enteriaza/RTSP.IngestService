// Management endpoint settings bound from the "Management" configuration section. The endpoints share the HTTP
// origin's listener (the "Http" section), so there is no separate address, port or connection limit here.

namespace RTSP.IngestService.Configuration;

/// <summary>
/// Settings for the management endpoints (<see cref="Management.ManagementEndpoints"/>) served by the HTTP origin.
/// </summary>
/// <remarks>
/// The options are read once when the HTTP request pipeline is created; changing them requires a restart.
/// </remarks>
public sealed class ManagementOptions
{
    /// <summary>Configuration section bound to this type.</summary>
    public const string SectionName = "Management";

    /// <summary>
    /// Gets or sets a value indicating whether the management endpoints (<c>/health/*</c>) are served. Defaults to
    /// <see langword="true"/>. When <see langword="false"/> those paths are handled as unknown HLS paths (404);
    /// the HLS endpoints, the application lifecycle and the service-manager integration are unaffected.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
