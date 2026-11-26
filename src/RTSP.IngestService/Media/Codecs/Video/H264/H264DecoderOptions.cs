// Resource limits and input framing for the managed H.264 decoder. Every limit is checked before the memory it
// bounds is allocated; exceeding one raises CodecErrorKind.ResourceLimit.

namespace RTSP.IngestService.Media.Codecs.Video.H264;

/// <summary>How access units are framed.</summary>
internal enum H264StreamFormat
{
    /// <summary>Annex B byte stream: start-code delimited NAL units, parameter sets in band.</summary>
    AnnexB,

    /// <summary>Length-prefixed NAL units (ISO/IEC 14496-15) described by an AVCDecoderConfigurationRecord.</summary>
    LengthPrefixed,
}

/// <summary>The stream description given to <see cref="H264Decoder.Configure"/>.</summary>
internal sealed record H264DecoderConfiguration
{
    /// <summary>Gets the Annex B configuration.</summary>
    public static H264DecoderConfiguration AnnexB { get; } = new();

    /// <summary>Gets the framing.</summary>
    public H264StreamFormat Format { get; init; } = H264StreamFormat.AnnexB;

    /// <summary>Gets the AVCDecoderConfigurationRecord (avcC) for <see cref="H264StreamFormat.LengthPrefixed"/>.</summary>
    public ReadOnlyMemory<byte> DecoderConfigurationRecord { get; init; }

    /// <summary>Creates a length-prefixed configuration from an avcC record.</summary>
    /// <param name="record">The record.</param>
    /// <returns>The configuration.</returns>
    public static H264DecoderConfiguration FromAvcC(ReadOnlyMemory<byte> record) =>
        new() { Format = H264StreamFormat.LengthPrefixed, DecoderConfigurationRecord = record };
}

/// <summary>Limits of one <see cref="H264Decoder"/>.</summary>
internal sealed record H264DecoderOptions
{
    /// <summary>The largest DPB a stream can require (A.3.1 h, A.3.2 f) plus the picture being decoded.</summary>
    public const int MaxPicturesPerCall = 16 + 1;

    /// <summary>Gets the default limits.</summary>
    public static H264DecoderOptions Default { get; } = new();

    /// <summary>Gets the largest coded width in luma samples.</summary>
    public int MaxWidth { get; init; } = 4096;

    /// <summary>Gets the largest coded height in luma samples.</summary>
    public int MaxHeight { get; init; } = 4096;

    /// <summary>Gets the largest number of macroblocks per picture (36864 is level 5.1's MaxFS).</summary>
    public int MaxMacroblocks { get; init; } = 36864;

    /// <summary>Gets the largest access unit in bytes.</summary>
    public int MaxAccessUnitBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Gets the most NAL units per access unit (which also bounds slices per picture).</summary>
    public int MaxNalUnitsPerAccessUnit { get; init; } = 1024;

    /// <summary>Gets the largest SPS or PPS NAL unit in bytes.</summary>
    public int MaxParameterSetBytes { get; init; } = 4096;

    /// <summary>Gets the most bytes the decoded picture buffer, including the picture being decoded, may occupy.</summary>
    public long MaxDpbBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Gets the most output frames the caller may hold undisposed.</summary>
    public int MaxOutstandingFrames { get; init; } = 32;

    /// <summary>Validates the limits.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A limit is out of range.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxWidth, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxWidth, VideoFrame.MaxDimension);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxHeight, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxHeight, VideoFrame.MaxDimension);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxMacroblocks, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxMacroblocks, H264Sps.MaxDimensionInMbs * H264Sps.MaxDimensionInMbs);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAccessUnitBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxAccessUnitBytes, 256 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxNalUnitsPerAccessUnit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxNalUnitsPerAccessUnit, 1 << 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxParameterSetBytes, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxParameterSetBytes, 1 << 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDpbBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxOutstandingFrames, MaxPicturesPerCall);
    }
}
