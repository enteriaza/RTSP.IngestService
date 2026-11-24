// HLS packaging and in-memory segment storage limits bound from the "Hls" configuration section. They set the live
// segmentation policy, the playlist window and the memory bounds of retained segments. Values are validated at
// startup and never clamped.

using System.ComponentModel.DataAnnotations;

namespace RTSP.IngestService.Configuration;

/// <summary>
/// Settings for fragmented-MP4 HLS packaging (<see cref="Hls.HlsPackagingHandler"/>) and the bounded in-memory
/// segment store (<see cref="Hls.InMemoryHlsSegmentStore"/>).
/// </summary>
/// <remarks>The options are read once at startup; changing them requires a restart.</remarks>
public sealed class HlsOptions : IValidatableObject
{
    /// <summary>Configuration section bound to this type.</summary>
    public const string SectionName = "Hls";

    /// <summary>
    /// Gets or sets the target media segment duration. A segment is closed at the first video sync sample (IDR) at
    /// or after this duration, so segments are usually somewhat longer. Defaults to 4 seconds; valid range is one
    /// second to one minute.
    /// </summary>
    [TimeSpanRange("00:00:01", "00:01:00")]
    public TimeSpan TargetSegmentDuration { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Gets or sets the longest a segment may grow while waiting for a video sync sample. When reached the segment
    /// is closed at the next sample even though the following segment then does not start with a sync sample.
    /// It also sets <c>#EXT-X-TARGETDURATION</c> (rounded up to whole seconds). Defaults to 10 seconds; valid range
    /// is one second to two minutes, and at least <see cref="TargetSegmentDuration"/>.
    /// </summary>
    [TimeSpanRange("00:00:01", "00:02:00")]
    public TimeSpan MaxSegmentDuration { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the minimum number of media segments listed in a live playlist. Older segments are removed only
    /// while the playlist stays at least this long and at least three target durations long. Defaults to 6; valid
    /// range is 3 to 100.
    /// </summary>
    [Range(3, 100)]
    public int PlaylistWindowSize { get; set; } = 6;

    /// <summary>
    /// Gets or sets the most segment bytes (initialization and media segments, including segments kept briefly after
    /// leaving the playlist) retained for one stream, in mebibytes. Defaults to 128; valid range is 1 to 4096, and at
    /// least <see cref="MaxSegmentSize"/>.
    /// </summary>
    [Range(1, 4096)]
    public int MaxSessionStorageMegabytes { get; set; } = 128;

    /// <summary>
    /// Gets or sets the most segment bytes retained across all streams, in mebibytes. Defaults to 2048; valid range
    /// is 1 to 65536, and at least <see cref="MaxSessionStorageMegabytes"/>.
    /// </summary>
    [Range(1, 65536)]
    public int MaxTotalStorageMegabytes { get; set; } = 2048;

    /// <summary>
    /// Gets or sets the most streams packaged at once. A publish beyond the limit is rejected. Defaults to 100;
    /// valid range is 1 to 10000.
    /// </summary>
    [Range(1, 10000)]
    public int MaxSessions { get; set; } = 100;

    /// <summary>
    /// Gets or sets the largest initialization segment, in bytes. Defaults to 131072; valid range is 4096 to
    /// 1048576.
    /// </summary>
    [Range(4096, 1048576)]
    public int MaxInitializationSegmentSize { get; set; } = 128 * 1024;

    /// <summary>
    /// Gets or sets the largest media segment, in bytes. A segment that would grow larger is closed early. Defaults to
    /// 33554432 (32 MiB); valid range is 65536 to 268435456.
    /// </summary>
    [Range(65536, 268435456)]
    public int MaxSegmentSize { get; set; } = 32 * 1024 * 1024;

    /// <summary>
    /// Gets or sets how long the playlist and segments of a stream that ended normally stay available, with
    /// <c>#EXT-X-ENDLIST</c>. Defaults to 30 seconds; valid range is zero to ten minutes.
    /// </summary>
    [TimeSpanRange("00:00:00", "00:10:00")]
    public TimeSpan EndedStreamRetention { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets <see cref="MaxSessionStorageMegabytes"/> in bytes.</summary>
    internal long MaxSessionStorageBytes => MaxSessionStorageMegabytes * 1024L * 1024L;

    /// <summary>Gets <see cref="MaxTotalStorageMegabytes"/> in bytes.</summary>
    internal long MaxTotalStorageBytes => MaxTotalStorageMegabytes * 1024L * 1024L;

    /// <summary>Validates the relationships between settings.</summary>
    /// <param name="validationContext">The validation context.</param>
    /// <returns>A failure for each violated relationship.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MaxSegmentDuration < TargetSegmentDuration)
        {
            yield return new ValidationResult(
                $"{nameof(MaxSegmentDuration)} ({MaxSegmentDuration}) must be at least {nameof(TargetSegmentDuration)} ({TargetSegmentDuration}).",
                [nameof(MaxSegmentDuration), nameof(TargetSegmentDuration)]);
        }

        if (MaxSessionStorageBytes < MaxSegmentSize)
        {
            yield return new ValidationResult(
                $"{nameof(MaxSessionStorageMegabytes)} ({MaxSessionStorageMegabytes} MiB) must hold at least one segment of {nameof(MaxSegmentSize)} ({MaxSegmentSize} bytes).",
                [nameof(MaxSessionStorageMegabytes), nameof(MaxSegmentSize)]);
        }

        if (MaxTotalStorageMegabytes < MaxSessionStorageMegabytes)
        {
            yield return new ValidationResult(
                $"{nameof(MaxTotalStorageMegabytes)} ({MaxTotalStorageMegabytes}) must be at least {nameof(MaxSessionStorageMegabytes)} ({MaxSessionStorageMegabytes}).",
                [nameof(MaxTotalStorageMegabytes), nameof(MaxSessionStorageMegabytes)]);
        }
    }
}
