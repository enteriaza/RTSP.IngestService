// Configuration of the managed H.264 encoder. Every value is validated before an encoder is created; the production
// video stage derives the IDR interval, quantiser and peak bitrate from Transcoding:Video. Rate-control values have
// no defaults on purpose: the caller states the bitrate policy explicitly. Structural defaults (one reference frame,
// a 16-sample motion search range, a VBV buffer of twice the peak rate) match the canonical profile the service
// previously produced with FFmpeg (libx264 veryfast, -bufsize = 2 x -maxrate).

namespace RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

/// <summary>How the encoder chooses the quantiser.</summary>
internal enum H264RateControlMode
{
    /// <summary>
    /// A fixed base QP (I pictures <see cref="H264EncoderOptions.IntraQpOffset"/> lower), raised per picture only as
    /// far as the VBV cap requires: constrained-quality VBR, the semantics of the production CRF plus -maxrate.
    /// </summary>
    ConstantQuality,

    /// <summary>A long-term average bitrate target under the same VBV cap: constrained VBR, not CBR.</summary>
    AverageBitrate,
}

/// <summary>How access units are framed.</summary>
internal enum H264OutputFormat
{
    /// <summary>NAL units with 4-byte big-endian length prefixes (ISO/IEC 14496-15), the FLV and fMP4 form; parameter sets are carried in the avcC record.</summary>
    LengthPrefixed,

    /// <summary>NAL units with 4-byte start codes (Annex B); every IDR access unit repeats the SPS and PPS.</summary>
    AnnexB,
}

/// <summary>Validated settings of an <see cref="H264Encoder"/>.</summary>
internal sealed record H264EncoderOptions
{
    /// <summary>The largest width or height accepted, matching the managed decoder.</summary>
    public const int MaxDimension = 4096;

    /// <summary>The largest picture in macroblocks: level 5.2 MaxFS, matching the managed decoder.</summary>
    public const int MaxMacroblocks = 36864;

    /// <summary>The most reference frames.</summary>
    public const int MaxReferenceFramesLimit = 4;

    /// <summary>The smallest and largest peak bitrate, in kbit/s; the range of <c>Transcoding:Video:MaxBitrateKilobits</c>.</summary>
    public const int MinBitrateKilobits = 100;

    /// <summary>The largest peak or target bitrate, in kbit/s.</summary>
    public const int MaxBitrateKilobitsLimit = 100000;

    /// <summary>Gets the coded width in luma samples: even, 16 to <see cref="MaxDimension"/>.</summary>
    public required int Width { get; init; }

    /// <summary>Gets the coded height in luma samples: even, 16 to <see cref="MaxDimension"/>.</summary>
    public required int Height { get; init; }

    /// <summary>Gets the IDR interval in media time (<c>Transcoding:Video:KeyframeInterval</c>), one second to one minute.</summary>
    public required TimeSpan KeyframeInterval { get; init; }

    /// <summary>Gets the rate-control mode.</summary>
    public required H264RateControlMode RateControl { get; init; }

    /// <summary>Gets the P-picture QP of <see cref="H264RateControlMode.ConstantQuality"/>, and the starting QP of <see cref="H264RateControlMode.AverageBitrate"/>; <see cref="MinQp"/> to <see cref="MaxQp"/>.</summary>
    public required int BaseQp { get; init; }

    /// <summary>Gets the VBV peak bitrate in kbit/s (<c>Transcoding:Video:MaxBitrateKilobits</c>).</summary>
    public required int MaxBitrateKilobits { get; init; }

    /// <summary>Gets the average target in kbit/s for <see cref="H264RateControlMode.AverageBitrate"/>, at most <see cref="MaxBitrateKilobits"/>; 0 otherwise.</summary>
    public int TargetBitrateKilobits { get; init; }

    /// <summary>Gets the VBV buffer in kbit; 0 means twice <see cref="MaxBitrateKilobits"/>, as the former FFmpeg arguments did.</summary>
    public int BufferSizeKilobits { get; init; }

    /// <summary>Gets the lowest QP rate control may choose; libx264 defaults to 0.</summary>
    public int MinQp { get; init; }

    /// <summary>Gets the highest QP rate control may choose; 51 is the 8-bit maximum.</summary>
    public int MaxQp { get; init; } = 51;

    /// <summary>Gets how much lower the QP of an IDR picture is than the P-picture QP: 3, the 1.4 quantiser-step ratio libx264 uses by default.</summary>
    public int IntraQpOffset { get; init; } = 3;

    /// <summary>Gets the number of reference frames, 1 to <see cref="MaxReferenceFramesLimit"/>; libx264 veryfast uses 1.</summary>
    public int MaxReferenceFrames { get; init; } = 1;

    /// <summary>Gets the integer motion search range in luma samples around each predictor, 4 to 64; libx264 uses 16.</summary>
    public int SearchRange { get; init; } = 16;

    /// <summary>Gets the highest frame rate the stream may reach, used only to choose the level (<c>Transcoding:MaxInputFrameRate</c>).</summary>
    public int MaxFrameRate { get; init; } = 60;

    /// <summary>Gets the number of slices per picture, 1 to 32 and at most the macroblock rows.</summary>
    public int SlicesPerPicture { get; init; } = 1;

    /// <summary>Gets the access-unit framing.</summary>
    public H264OutputFormat Format { get; init; } = H264OutputFormat.LengthPrefixed;

    /// <summary>Gets how many encoded access units may be alive (not disposed) at once.</summary>
    public int MaxOutstandingUnits { get; init; } = 64;

    /// <summary>Gets the VBV buffer in bits.</summary>
    internal long BufferBits => (BufferSizeKilobits == 0 ? 2L * MaxBitrateKilobits : BufferSizeKilobits) * 1000L;

    /// <summary>Validates every value.</summary>
    /// <exception cref="ArgumentException">A value is out of range or inconsistent.</exception>
    public void Validate()
    {
        Check(Width is >= 16 and <= MaxDimension && (Width & 1) == 0, nameof(Width), "must be even and 16 to 4096");
        Check(Height is >= 16 and <= MaxDimension && (Height & 1) == 0, nameof(Height), "must be even and 16 to 4096");
        Check((long)((Width + 15) / 16) * ((Height + 15) / 16) <= MaxMacroblocks, nameof(Width), "exceeds 36864 macroblocks");
        Check(KeyframeInterval >= TimeSpan.FromSeconds(1) && KeyframeInterval <= TimeSpan.FromMinutes(1), nameof(KeyframeInterval), "must be one second to one minute");
        Check(Enum.IsDefined(RateControl), nameof(RateControl), "is not defined");
        Check(Enum.IsDefined(Format), nameof(Format), "is not defined");
        Check(MinQp is >= 0 and <= 51 && MaxQp is >= 0 and <= 51 && MinQp <= MaxQp, nameof(MinQp), "must satisfy 0 <= MinQp <= MaxQp <= 51");
        Check(BaseQp >= MinQp && BaseQp <= MaxQp, nameof(BaseQp), "must lie within MinQp and MaxQp");
        Check(IntraQpOffset is >= 0 and <= 12, nameof(IntraQpOffset), "must be 0 to 12");
        Check(MaxBitrateKilobits is >= MinBitrateKilobits and <= MaxBitrateKilobitsLimit, nameof(MaxBitrateKilobits), "must be 100 to 100000");
        Check(BufferSizeKilobits == 0 || BufferSizeKilobits >= MaxBitrateKilobits / 10 && BufferSizeKilobits <= 4 * MaxBitrateKilobits, nameof(BufferSizeKilobits), "must be 0, or a tenth to four times MaxBitrateKilobits");
        if (RateControl == H264RateControlMode.AverageBitrate)
        {
            Check(TargetBitrateKilobits >= MinBitrateKilobits && TargetBitrateKilobits <= MaxBitrateKilobits, nameof(TargetBitrateKilobits), "must be 100 to MaxBitrateKilobits");
        }
        else
        {
            Check(TargetBitrateKilobits == 0, nameof(TargetBitrateKilobits), "applies only to AverageBitrate");
        }

        Check(MaxReferenceFrames is >= 1 and <= MaxReferenceFramesLimit, nameof(MaxReferenceFrames), "must be 1 to 4");
        Check(SearchRange is >= 4 and <= 64, nameof(SearchRange), "must be 4 to 64");
        Check(MaxFrameRate is >= 1 and <= 240, nameof(MaxFrameRate), "must be 1 to 240");
        Check(SlicesPerPicture >= 1 && SlicesPerPicture <= Math.Min(32, (Height + 15) / 16), nameof(SlicesPerPicture), "must be 1 to 32 and at most the macroblock rows");
        Check(MaxOutstandingUnits is >= 1 and <= 4096, nameof(MaxOutstandingUnits), "must be 1 to 4096");
    }

    /// <summary>Throws when a condition fails.</summary>
    /// <param name="condition">The condition.</param>
    /// <param name="name">The option name.</param>
    /// <param name="message">What the option must satisfy.</param>
    private static void Check(bool condition, string name, string message)
    {
        if (!condition)
        {
            throw new ArgumentException($"H264EncoderOptions.{name} {message}.", name);
        }
    }
}
