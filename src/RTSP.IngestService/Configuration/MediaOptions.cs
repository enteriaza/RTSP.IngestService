// Media processing limits bound from the "Media" configuration section. They bound the work and memory spent on each
// encoded sample and set the threshold for reporting timestamp jumps. Values are validated at startup and never
// clamped.

using System.ComponentModel.DataAnnotations;

namespace RTSP.IngestService.Configuration;

/// <summary>
/// Settings for media processing of published streams (<see cref="Media.MediaPublishSessionHandler"/>).
/// </summary>
/// <remarks>
/// The options are read once at startup; changing them requires a restart.
/// </remarks>
public sealed class MediaOptions
{
    /// <summary>Configuration section bound to this type.</summary>
    public const string SectionName = "Media";

    /// <summary>
    /// Gets or sets the largest encoded sample, in bytes after the FLV packet header, that is delivered downstream.
    /// Larger samples are dropped and reported as <see cref="Media.MediaErrorKind.SampleTooLarge"/>. Defaults to
    /// 8 MiB; valid range is 1024 to 16777215 (the largest RTMP message).
    /// </summary>
    [Range(1024, 16777215)]
    public int MaxSampleSize { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the most NAL units one video access unit may contain. Access units with more are dropped and
    /// reported as <see cref="Media.MediaErrorKind.TooManyNalUnits"/>. Defaults to 256; valid range is 1 to 4096.
    /// </summary>
    [Range(1, 4096)]
    public int MaxNalUnitsPerSample { get; set; } = 256;

    /// <summary>
    /// Gets or sets the forward decode-timestamp step, between consecutive samples of one track, above which the
    /// later sample is flagged as a <see cref="Media.TimestampDiscontinuity.Jump"/>. Timestamps are never altered.
    /// Defaults to 10 seconds; valid range is one second to one hour.
    /// </summary>
    [TimeSpanRange("00:00:01", "01:00:00")]
    public TimeSpan DiscontinuityThreshold { get; set; } = TimeSpan.FromSeconds(10);
}
