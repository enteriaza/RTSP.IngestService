// The syntax elements of macroblock_layer() that CAVLC (9.2) and CABAC (9.3) code differently. The macroblock layer
// itself (7.3.5) is written once, generic over this interface; the readers are ref structs over the slice data.

namespace RTSP.IngestService.Media.Codecs.Video.H264;

/// <summary>Reads entropy-coded macroblock syntax elements.</summary>
internal interface IH264EntropyReader
{
    /// <summary>Gets a value indicating whether this is the CABAC reader.</summary>
    bool IsCabac { get; }

    /// <summary>Reads mb_type, numbered as in the slice type's table (Tables 7-11, 7-13, 7-14).</summary>
    /// <param name="d">The slice decoder.</param>
    /// <returns>The value.</returns>
    int MbType(H264SliceDecoder d);

    /// <summary>Reads transform_size_8x8_flag.</summary>
    /// <param name="d">The slice decoder.</param>
    /// <returns>The flag.</returns>
    bool Transform8x8(H264SliceDecoder d);

    /// <summary>Reads prev_intra4x4/8x8_pred_mode_flag and rem_intra4x4/8x8_pred_mode.</summary>
    /// <returns>-1 when the predicted mode is used, otherwise rem_intra_pred_mode.</returns>
    int IntraPredMode();

    /// <summary>Reads intra_chroma_pred_mode.</summary>
    /// <param name="d">The slice decoder.</param>
    /// <returns>The value.</returns>
    int IntraChromaPredMode(H264SliceDecoder d);

    /// <summary>Reads sub_mb_type.</summary>
    /// <param name="d">The slice decoder.</param>
    /// <returns>The value.</returns>
    int SubMbType(H264SliceDecoder d);

    /// <summary>Reads ref_idx_lX for the partition whose top-left 4x4 block is at (bx, by).</summary>
    /// <param name="d">The slice decoder.</param>
    /// <param name="list">The list.</param>
    /// <param name="bx">The absolute 4x4 column.</param>
    /// <param name="by">The absolute 4x4 row.</param>
    /// <returns>The value.</returns>
    int RefIdx(H264SliceDecoder d, int list, int bx, int by);

    /// <summary>Reads one component of mvd_lX for the partition whose top-left 4x4 block is at (bx, by).</summary>
    /// <param name="d">The slice decoder.</param>
    /// <param name="list">The list.</param>
    /// <param name="component">0 horizontal, 1 vertical.</param>
    /// <param name="bx">The absolute 4x4 column.</param>
    /// <param name="by">The absolute 4x4 row.</param>
    /// <returns>The value.</returns>
    int Mvd(H264SliceDecoder d, int list, int component, int bx, int by);

    /// <summary>Reads coded_block_pattern.</summary>
    /// <param name="d">The slice decoder.</param>
    /// <returns>Luma bits 0 to 3 and chroma in bits 4 and 5.</returns>
    int CodedBlockPattern(H264SliceDecoder d);

    /// <summary>Reads mb_qp_delta.</summary>
    /// <param name="d">The slice decoder.</param>
    /// <returns>The value.</returns>
    int MbQpDelta(H264SliceDecoder d);

    /// <summary>Reads one residual block (7.3.5.3.2 or 7.3.5.3.3).</summary>
    /// <param name="d">The slice decoder.</param>
    /// <param name="block">Which block, for neighbour contexts.</param>
    /// <param name="startIdx">startIdx.</param>
    /// <param name="endIdx">endIdx.</param>
    /// <param name="maxNumCoeff">maxNumCoeff.</param>
    /// <param name="levels">Receives coefficient levels by list index; entries outside the coded run are left zero.</param>
    /// <returns>The number of non-zero coefficients.</returns>
    int ResidualBlock(H264SliceDecoder d, in H264ResidualBlock block, int startIdx, int endIdx, int maxNumCoeff, Span<int> levels);

    /// <summary>Reads I_PCM samples.</summary>
    /// <param name="samples">Receives 384 samples.</param>
    void Pcm(Span<byte> samples);
}

/// <summary>Residual block categories (ctxBlockCat, Table 9-42).</summary>
internal enum H264BlockCategory
{
    /// <summary>Intra16x16 luma DC.</summary>
    LumaDc = 0,

    /// <summary>Intra16x16 luma AC.</summary>
    LumaAc = 1,

    /// <summary>Luma 4x4.</summary>
    Luma4x4 = 2,

    /// <summary>Chroma DC.</summary>
    ChromaDc = 3,

    /// <summary>Chroma AC.</summary>
    ChromaAc = 4,

    /// <summary>Luma 8x8.</summary>
    Luma8x8 = 5,
}

/// <summary>Identifies a residual block for context derivation.</summary>
/// <param name="Category">The category.</param>
/// <param name="Index">luma4x4BlkIdx, luma8x8BlkIdx, or chroma4x4BlkIdx.</param>
/// <param name="Component">0 for Cb, 1 for Cr (chroma categories).</param>
internal readonly record struct H264ResidualBlock(H264BlockCategory Category, int Index, int Component);
