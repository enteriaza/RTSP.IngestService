// Stream catalog settings bound from the "StreamCatalog" configuration section. The catalog reconciles from the RTMP
// registry and the HLS store at startup, on events it cannot apply incrementally, and periodically at this interval.

namespace RTSP.IngestService.Configuration;

/// <summary>
/// Settings for the stream catalog (<see cref="Streams.StreamCatalog"/>).
/// </summary>
/// <remarks>
/// The options are read once when the catalog service starts; changing them requires a restart.
/// </remarks>
public sealed class StreamCatalogOptions
{
    /// <summary>Configuration section bound to this type.</summary>
    public const string SectionName = "StreamCatalog";

    /// <summary>
    /// Gets or sets how often the catalog is reconciled from the authoritative state even without any trigger,
    /// bounding how long a missed event can leave it stale. Defaults to 30 seconds; valid range is one second to one
    /// hour.
    /// </summary>
    [TimeSpanRange("00:00:01", "01:00:00")]
    public TimeSpan ReconciliationInterval { get; set; } = TimeSpan.FromSeconds(30);
}
