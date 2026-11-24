// Stream lifecycle event settings bound from the "StreamEvents" configuration section. Events are best-effort
// notifications held in one bounded in-process channel; this section only sizes that channel.

using System.ComponentModel.DataAnnotations;

namespace RTSP.IngestService.Configuration;

/// <summary>
/// Settings for the stream lifecycle event channel (<see cref="Streams.StreamEventChannel"/>).
/// </summary>
/// <remarks>
/// The options are read once when the channel is created; changing them requires a restart.
/// </remarks>
public sealed class StreamEventOptions
{
    /// <summary>Configuration section bound to this type.</summary>
    public const string SectionName = "StreamEvents";

    /// <summary>
    /// Gets or sets how many undelivered events the channel holds. When it is full, publishing a new event discards
    /// the oldest undelivered one. Defaults to 1024; valid range is 16 to 65536.
    /// </summary>
    [Range(16, 65536)]
    public int QueueCapacity { get; set; } = 1024;
}
