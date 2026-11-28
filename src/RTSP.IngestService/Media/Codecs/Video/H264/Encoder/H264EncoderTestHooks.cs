// Test-only controls that make the encoder emit legal but unusual syntax, so the managed decoder's reference
// management and slice handling can be checked against FFmpeg on streams no production encoder writes: POC types 0
// and 1, frame_num gaps, non-reference pictures, long-term references and memory management operations, list
// modification, forced I_PCM, arbitrary slice order and redundant slices; and lend the encoder wavefront helper
// threads for parallelism tests and benchmarks. They are internal, reachable from no configuration, and never set by
// production code.

namespace RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

/// <summary>Per-picture overrides.</summary>
internal sealed class H264PictureControl
{
    /// <summary>Gets a value indicating whether the picture is coded with nal_ref_idc 0.</summary>
    public bool NonReference { get; init; }

    /// <summary>Gets how many frame_num values to skip before this picture (requires gaps to be allowed).</summary>
    public int FrameNumGap { get; init; }

    /// <summary>Gets a value indicating whether an IDR picture is marked long-term (long_term_reference_flag).</summary>
    public bool LongTermReference { get; init; }

    /// <summary>Gets the memory management operations of the picture.</summary>
    public IReadOnlyList<H264Mmco> Mmcos { get; init; } = [];

    /// <summary>Gets the list 0 modification commands of the picture.</summary>
    public IReadOnlyList<H264ListModification> ListModifications { get; init; } = [];

    /// <summary>Gets a predicate of macroblock addresses to code as I_PCM.</summary>
    public Func<int, bool>? ForcePcm { get; init; }
}

/// <summary>Sequence-level overrides and the per-picture control callback.</summary>
internal sealed class H264EncoderTestHooks
{
    /// <summary>Gets profile_idc; 66 (Baseline) permits the arbitrary slice order and redundant slices.</summary>
    public int ProfileIdc { get; init; } = 100;

    /// <summary>Gets pic_order_cnt_type, 0 to 2.</summary>
    public int PocType { get; init; } = 2;

    /// <summary>Gets a value indicating whether gaps_in_frame_num_value_allowed_flag is set.</summary>
    public bool GapsAllowed { get; init; }

    /// <summary>Gets a value indicating whether every slice is followed by a redundant copy (redundant_pic_cnt 1).</summary>
    public bool RedundantSlices { get; init; }

    /// <summary>Gets a value indicating whether the slices of each picture are emitted in reverse order (ASO).</summary>
    public bool ReverseSliceOrder { get; init; }

    /// <summary>Gets weighted_bipred_idc of the PPS (for streams that add B slices).</summary>
    public int WeightedBipredIdc { get; init; }

    /// <summary>Gets max_num_reorder_frames of the VUI.</summary>
    public int MaxNumReorderFrames { get; init; }

    /// <summary>Gets log2_max_frame_num.</summary>
    public int Log2MaxFrameNum { get; init; } = 8;

    /// <summary>Gets the per-picture control by picture index since the encoder was created.</summary>
    public Func<long, H264PictureControl?>? Control { get; init; }

    /// <summary>Gets helper threads that code each picture as a row wavefront with the encoding thread; the caller owns and disposes them.</summary>
    public H264WavefrontHelpers? Wavefront { get; init; }
}
