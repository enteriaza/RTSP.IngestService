// Transcoding settings bound from the "Transcoding" configuration section. Every accepted publication is decoded and
// re-encoded into the canonical profile (H.264 High, yuv420p, 8-bit, BT.709; AAC-LC, 48 kHz, stereo) by a per-stream
// FFmpeg child process before HLS packaging. The canonical format itself is fixed; these settings choose encoder
// quality, admission limits, buffering and the FFmpeg executable. No free-form FFmpeg arguments are accepted.

using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Transcoding;

namespace RTSP.IngestService.Configuration;

/// <summary>What happens when a publisher sends media faster than its transcoder consumes it.</summary>
public enum TranscodingOverloadPolicy
{
    /// <summary>
    /// The publisher is never slowed. Video that does not fit the session's buffer is dropped together with every
    /// following frame up to the next keyframe, so no frame is decoded without its references; audio that does not
    /// fit is dropped frame by frame.
    /// </summary>
    DropUntilKeyframe,

    /// <summary>
    /// The connection waits for buffer space, slowing the publisher through TCP flow control. Exhaustion of the
    /// service-wide buffer budget still drops (as <see cref="DropUntilKeyframe"/>), so one publisher cannot hold
    /// memory other publishers need.
    /// </summary>
    Backpressure,
}

/// <summary>The libx264 speed/quality preset of the canonical video encoder.</summary>
public enum TranscodingVideoPreset
{
    /// <summary>x264 <c>ultrafast</c>.</summary>
    Ultrafast,

    /// <summary>x264 <c>superfast</c>.</summary>
    Superfast,

    /// <summary>x264 <c>veryfast</c>.</summary>
    Veryfast,

    /// <summary>x264 <c>faster</c>.</summary>
    Faster,

    /// <summary>x264 <c>fast</c>.</summary>
    Fast,

    /// <summary>x264 <c>medium</c>.</summary>
    Medium,
}

/// <summary>The canonical video encoder implementation.</summary>
public enum TranscodingVideoEncoder
{
    /// <summary>The libx264 software encoder.</summary>
    Libx264,
}

/// <summary>Hardware acceleration used for decoding and encoding.</summary>
public enum TranscodingHardwareAcceleration
{
    /// <summary>Software decoders and encoders only.</summary>
    None,
}

/// <summary>
/// Settings for canonical transcoding of published streams (<see cref="TranscodingPublishSessionHandler"/>).
/// </summary>
/// <remarks>
/// <para>The options are read once at startup; changing them requires a restart.</para>
/// <para>
/// When <see cref="Enabled"/> is <see langword="false"/> the service still accepts RTMP connections and serves HLS,
/// but rejects every publish (<c>NetStream.Publish.Failed</c>): media is never passed through untranscoded.
/// </para>
/// </remarks>
public sealed class TranscodingOptions : IValidatableObject
{
    /// <summary>Configuration section bound to this type.</summary>
    public const string SectionName = "Transcoding";

    /// <summary>
    /// Gets or sets a value indicating whether publishes are transcoded. Defaults to <see langword="true"/>. When
    /// <see langword="false"/>, every publish is rejected and FFmpeg is neither probed nor started.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the FFmpeg executable: an absolute path, or a path relative to the service directory. Defaults to
    /// <see langword="null"/>, meaning <c>ffmpeg/ffmpeg.exe</c> (Windows) or <c>ffmpeg/ffmpeg</c> (Linux) beside the
    /// service executable, where release packages place it. The <c>PATH</c> is never searched. FFmpeg 9.0 or later
    /// with libx264, libdav1d and the decoders listed in the documentation is required and verified at startup.
    /// </summary>
    public string? FfmpegPath { get; set; }

    /// <summary>
    /// Gets or sets the most publishes transcoded at once. A publish beyond the limit is rejected. Defaults to 16;
    /// valid range is 1 to 1000. This is an admission bound, not a measured capacity: each session runs a software
    /// decoder and encoder whose CPU cost depends on resolution, frame rate and preset.
    /// </summary>
    [Range(1, 1000)]
    public int MaxConcurrentSessions { get; set; } = 16;

    /// <summary>
    /// Gets or sets the widest accepted input picture in pixels. Wider video ends the publish. Defaults to 3840;
    /// valid range is 16 to 8192.
    /// </summary>
    [Range(16, 8192)]
    public int MaxInputWidth { get; set; } = 3840;

    /// <summary>
    /// Gets or sets the tallest accepted input picture in pixels. Taller video ends the publish. Defaults to 3840, so
    /// both 3840x2160 and 2160x3840 are accepted; valid range is 16 to 8192.
    /// </summary>
    [Range(16, 8192)]
    public int MaxInputHeight { get; set; } = 3840;

    /// <summary>
    /// Gets or sets the highest accepted input frame rate, measured from video timestamps over a two-second window
    /// with a 10% tolerance. A faster stream ends the publish. Defaults to 60; valid range is 1 to 240.
    /// </summary>
    [Range(1, 240)]
    public int MaxInputFrameRate { get; set; } = 60;

    /// <summary>
    /// Gets or sets the most video frames buffered for one session between the connection and its transcoder.
    /// Defaults to 120; valid range is 8 to 10000.
    /// </summary>
    [Range(8, 10000)]
    public int MaxBufferedVideoFrames { get; set; } = 120;

    /// <summary>
    /// Gets or sets the most audio frames buffered for one session between the connection and its transcoder.
    /// Defaults to 500; valid range is 16 to 20000.
    /// </summary>
    [Range(16, 20000)]
    public int MaxBufferedAudioFrames { get; set; } = 500;

    /// <summary>
    /// Gets or sets the most encoded bytes buffered for one session, in mebibytes. Defaults to 64; valid range is 1 to
    /// 1024, and at most <see cref="MaxTotalBufferMegabytes"/>.
    /// </summary>
    [Range(1, 1024)]
    public int MaxSessionBufferMegabytes { get; set; } = 64;

    /// <summary>
    /// Gets or sets the most encoded bytes buffered across all sessions, in mebibytes. Defaults to 512; valid range is
    /// 1 to 65536.
    /// </summary>
    [Range(1, 65536)]
    public int MaxTotalBufferMegabytes { get; set; } = 512;

    /// <summary>Gets or sets the overload behaviour. Defaults to <see cref="TranscodingOverloadPolicy.DropUntilKeyframe"/>.</summary>
    [EnumDataType(typeof(TranscodingOverloadPolicy))]
    public TranscodingOverloadPolicy OverloadPolicy { get; set; } = TranscodingOverloadPolicy.DropUntilKeyframe;

    /// <summary>
    /// Gets or sets how long a normally ended publish waits for its transcoder to flush buffered media into HLS before
    /// the process is killed. Defaults to 5 seconds; valid range is one second to one minute.
    /// </summary>
    [TimeSpanRange("00:00:01", "00:01:00")]
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the decoder and encoder threads per session; 0 lets FFmpeg choose. Defaults to 0; valid range is 0
    /// to 64.
    /// </summary>
    [Range(0, 64)]
    public int ThreadsPerSession { get; set; }

    /// <summary>
    /// Gets or sets the accepted input video codecs, or <see langword="null"/> (the default) for every supported
    /// codec. A publish using another video codec is ended.
    /// </summary>
    public SourceVideoCodec[]? AcceptedVideoCodecs { get; set; }

    /// <summary>
    /// Gets or sets the accepted input audio codecs, or <see langword="null"/> (the default) for every supported
    /// codec. A publish using another audio codec is ended.
    /// </summary>
    public SourceAudioCodec[]? AcceptedAudioCodecs { get; set; }

    /// <summary>Gets or sets the hardware acceleration policy. Only <see cref="TranscodingHardwareAcceleration.None"/> exists.</summary>
    [EnumDataType(typeof(TranscodingHardwareAcceleration))]
    public TranscodingHardwareAcceleration HardwareAcceleration { get; set; }

    /// <summary>Gets or sets the canonical video encoder settings.</summary>
    [ValidateObjectMembers]
    public TranscodingVideoOptions Video { get; set; } = new();

    /// <summary>Gets or sets the canonical audio encoder settings.</summary>
    [ValidateObjectMembers]
    public TranscodingAudioOptions Audio { get; set; } = new();

    /// <summary>Gets <see cref="MaxSessionBufferMegabytes"/> in bytes.</summary>
    internal long MaxSessionBufferBytes => MaxSessionBufferMegabytes * 1024L * 1024L;

    /// <summary>Gets <see cref="MaxTotalBufferMegabytes"/> in bytes.</summary>
    internal long MaxTotalBufferBytes => MaxTotalBufferMegabytes * 1024L * 1024L;

    /// <summary>Resolves <see cref="FfmpegPath"/> to an absolute path.</summary>
    /// <param name="baseDirectory">The service directory.</param>
    /// <returns>The executable path.</returns>
    internal string ResolveFfmpegPath(string baseDirectory)
    {
        var configured = string.IsNullOrWhiteSpace(FfmpegPath)
            ? Path.Combine("ffmpeg", OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")
            : FfmpegPath;
        return Path.GetFullPath(configured, baseDirectory);
    }

    /// <summary>Whether a video codec is accepted.</summary>
    /// <param name="codec">The codec.</param>
    /// <returns><see langword="true"/> if accepted.</returns>
    internal bool Accepts(SourceVideoCodec codec) => AcceptedVideoCodecs is null || AcceptedVideoCodecs.Contains(codec);

    /// <summary>Whether an audio codec is accepted.</summary>
    /// <param name="codec">The codec.</param>
    /// <returns><see langword="true"/> if accepted.</returns>
    internal bool Accepts(SourceAudioCodec codec) => AcceptedAudioCodecs is null || AcceptedAudioCodecs.Contains(codec);

    /// <summary>Validates the relationships between settings.</summary>
    /// <param name="validationContext">The validation context.</param>
    /// <returns>A failure for each violated relationship.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MaxSessionBufferMegabytes > MaxTotalBufferMegabytes)
        {
            yield return new ValidationResult(
                $"{nameof(MaxSessionBufferMegabytes)} ({MaxSessionBufferMegabytes}) must not exceed {nameof(MaxTotalBufferMegabytes)} ({MaxTotalBufferMegabytes}).",
                [nameof(MaxSessionBufferMegabytes), nameof(MaxTotalBufferMegabytes)]);
        }

        if (FfmpegPath is { } path && (path.Length == 0 || path.Trim().Length != path.Length || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0))
        {
            yield return new ValidationResult($"{nameof(FfmpegPath)} must be a non-empty path without surrounding whitespace or invalid characters.", [nameof(FfmpegPath)]);
        }

        foreach (var result in ValidateList(AcceptedVideoCodecs, nameof(AcceptedVideoCodecs)))
        {
            yield return result;
        }

        foreach (var result in ValidateList(AcceptedAudioCodecs, nameof(AcceptedAudioCodecs)))
        {
            yield return result;
        }
    }

    /// <summary>Validates an accepted-codec list.</summary>
    /// <typeparam name="T">The codec enumeration.</typeparam>
    /// <param name="values">The list.</param>
    /// <param name="name">The property name.</param>
    /// <returns>A failure for an empty list, an undefined value or a duplicate.</returns>
    private static IEnumerable<ValidationResult> ValidateList<T>(T[]? values, string name)
        where T : struct, Enum
    {
        if (values is null)
        {
            yield break;
        }

        if (values.Length == 0)
        {
            yield return new ValidationResult($"{name} must list at least one codec, or be omitted to accept every supported codec.", [name]);
        }
        else if (values.Any(static value => !Enum.IsDefined(value)))
        {
            yield return new ValidationResult($"{name} contains an undefined codec.", [name]);
        }
        else if (values.Distinct().Count() != values.Length)
        {
            yield return new ValidationResult($"{name} lists a codec more than once.", [name]);
        }
    }
}

/// <summary>Canonical H.264 encoder settings. Codec, profile (High), pixel format and colour description are fixed.</summary>
public sealed class TranscodingVideoOptions
{
    /// <summary>Gets or sets the encoder. Only <see cref="TranscodingVideoEncoder.Libx264"/> exists.</summary>
    [EnumDataType(typeof(TranscodingVideoEncoder))]
    public TranscodingVideoEncoder Encoder { get; set; } = TranscodingVideoEncoder.Libx264;

    /// <summary>Gets or sets the x264 preset. Defaults to <see cref="TranscodingVideoPreset.Veryfast"/>.</summary>
    [EnumDataType(typeof(TranscodingVideoPreset))]
    public TranscodingVideoPreset Preset { get; set; } = TranscodingVideoPreset.Veryfast;

    /// <summary>Gets or sets the constant rate factor (lower is better quality). Defaults to 21; valid range is 0 to 51.</summary>
    [Range(0, 51)]
    public int ConstantRateFactor { get; set; } = 21;

    /// <summary>
    /// Gets or sets the peak video bitrate in kilobits per second (VBV cap with a two-second buffer). Defaults to 8000;
    /// valid range is 100 to 100000.
    /// </summary>
    [Range(100, 100000)]
    public int MaxBitrateKilobits { get; set; } = 8000;

    /// <summary>
    /// Gets or sets the interval between IDR frames, in media time. HLS segments are cut at the first IDR frame after
    /// <c>Hls:TargetSegmentDuration</c>, so a longer interval lengthens segments. Defaults to <see langword="null"/>,
    /// which uses <c>Hls:TargetSegmentDuration</c> (one IDR frame per segment); valid range is one second to one
    /// minute.
    /// </summary>
    [TimeSpanRange("00:00:01", "00:01:00")]
    public TimeSpan? KeyframeInterval { get; set; }
}

/// <summary>Canonical AAC encoder settings. Codec (AAC-LC), sample rate (48 kHz) and layout (stereo) are fixed.</summary>
public sealed class TranscodingAudioOptions
{
    /// <summary>Gets or sets the AAC bitrate in kilobits per second. Defaults to 160; valid range is 32 to 512.</summary>
    [Range(32, 512)]
    public int BitrateKilobits { get; set; } = 160;
}
