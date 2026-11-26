// CABAC ctxIdxInc derivations that depend on neighbouring macroblocks and partitions (ITU-T H.264 9.3.3.1.1).
// Neighbours are the left (A) and top (B) macroblocks or blocks, available only within the current slice.

namespace RTSP.IngestService.Media.Codecs.Video.H264;

/// <summary>CABAC context derivation part of the slice decoder.</summary>
internal sealed partial class H264SliceDecoder
{
    /// <summary>ctxIdxInc of mb_skip_flag (9.3.3.1.1.1).</summary>
    /// <returns>condTermFlagA + condTermFlagB.</returns>
    internal int SkipInc() => SkipCondition(MbX - 1, MbY) + SkipCondition(MbX, MbY - 1);

    /// <summary>ctxIdxInc of the first mb_type bin in I slices (9.3.3.1.1.3).</summary>
    /// <returns>condTermFlagA + condTermFlagB.</returns>
    internal int IntraTypeInc() => IntraTypeCondition(MbX - 1, MbY) + IntraTypeCondition(MbX, MbY - 1);

    /// <summary>ctxIdxInc of the first mb_type bin in B slices (9.3.3.1.1.3).</summary>
    /// <returns>condTermFlagA + condTermFlagB.</returns>
    internal int BTypeInc() => BTypeCondition(MbX - 1, MbY) + BTypeCondition(MbX, MbY - 1);

    /// <summary>ctxIdxInc of transform_size_8x8_flag (9.3.3.1.1.10).</summary>
    /// <returns>condTermFlagA + condTermFlagB.</returns>
    internal int Transform8x8Inc() => Transform8x8Condition(MbX - 1, MbY) + Transform8x8Condition(MbX, MbY - 1);

    /// <summary>ctxIdxInc of the first intra_chroma_pred_mode bin (9.3.3.1.1.8).</summary>
    /// <returns>condTermFlagA + condTermFlagB.</returns>
    internal int ChromaPredInc() => ChromaPredCondition(MbX - 1, MbY) + ChromaPredCondition(MbX, MbY - 1);

    /// <summary>ctxIdxInc of the first ref_idx bin (9.3.3.1.1.6).</summary>
    /// <param name="list">The list.</param>
    /// <param name="bx">The partition's absolute 4x4 column.</param>
    /// <param name="by">The partition's absolute 4x4 row.</param>
    /// <returns>condTermFlagA + 2 * condTermFlagB.</returns>
    internal int RefIdxInc(int list, int bx, int by) => RefIdxCondition(list, bx - 1, by) + (2 * RefIdxCondition(list, bx, by - 1));

    /// <summary>The sum of neighbouring |mvd| for the first mvd bin (9.3.3.1.1.7).</summary>
    /// <param name="list">The list.</param>
    /// <param name="component">0 horizontal, 1 vertical.</param>
    /// <param name="bx">The partition's absolute 4x4 column.</param>
    /// <param name="by">The partition's absolute 4x4 row.</param>
    /// <returns>absMvdCompA + absMvdCompB.</returns>
    internal int MvdSum(int list, int component, int bx, int by) => MvdAbsAt(list, component, bx - 1, by) + MvdAbsAt(list, component, bx, by - 1);

    /// <summary>ctxIdxInc of a coded_block_pattern luma bin (9.3.3.1.1.4).</summary>
    /// <param name="b8">The 8x8 block.</param>
    /// <param name="decoded">The luma bits decoded so far for the current macroblock.</param>
    /// <returns>condTermFlagA + 2 * condTermFlagB.</returns>
    internal int CbpLumaInc(int b8, int decoded)
    {
        var a = (b8 & 1) == 1
            ? ((decoded >> (b8 - 1)) & 1) == 0 ? 1 : 0
            : CbpLumaCondition(MbX - 1, MbY, b8 + 1);
        var b = b8 >= 2
            ? ((decoded >> (b8 - 2)) & 1) == 0 ? 1 : 0
            : CbpLumaCondition(MbX, MbY - 1, b8 + 2);
        return a + (2 * b);
    }

    /// <summary>ctxIdxInc of a coded_block_pattern chroma bin (9.3.3.1.1.4).</summary>
    /// <param name="bin">0 or 1.</param>
    /// <returns>condTermFlagA + 2 * condTermFlagB, plus 4 for the second bin.</returns>
    internal int CbpChromaInc(int bin) =>
        CbpChromaCondition(MbX - 1, MbY, bin) + (2 * CbpChromaCondition(MbX, MbY - 1, bin)) + (bin * 4);

    /// <summary>condTermFlagN of mb_skip_flag.</summary>
    /// <param name="mbx">The neighbour column.</param>
    /// <param name="mby">The neighbour row.</param>
    /// <returns>0 or 1.</returns>
    private int SkipCondition(int mbx, int mby) => MbAvailable(mbx, mby) && !IsSkip(MbKindOf[(mby * W) + mbx]) ? 1 : 0;

    /// <summary>condTermFlagN of the I-slice mb_type prefix.</summary>
    /// <param name="mbx">The neighbour column.</param>
    /// <param name="mby">The neighbour row.</param>
    /// <returns>0 or 1.</returns>
    private int IntraTypeCondition(int mbx, int mby) =>
        MbAvailable(mbx, mby) && MbKindOf[(mby * W) + mbx] is not (H264MbKind.I4x4 or H264MbKind.I8x8) ? 1 : 0;

    /// <summary>condTermFlagN of the B-slice mb_type prefix.</summary>
    /// <param name="mbx">The neighbour column.</param>
    /// <param name="mby">The neighbour row.</param>
    /// <returns>0 or 1.</returns>
    private int BTypeCondition(int mbx, int mby) =>
        MbAvailable(mbx, mby) && MbKindOf[(mby * W) + mbx] is not (H264MbKind.BSkip or H264MbKind.BDirect) ? 1 : 0;

    /// <summary>condTermFlagN of transform_size_8x8_flag.</summary>
    /// <param name="mbx">The neighbour column.</param>
    /// <param name="mby">The neighbour row.</param>
    /// <returns>0 or 1.</returns>
    private int Transform8x8Condition(int mbx, int mby) => MbAvailable(mbx, mby) && MbTransform8x8[(mby * W) + mbx] ? 1 : 0;

    /// <summary>condTermFlagN of intra_chroma_pred_mode.</summary>
    /// <param name="mbx">The neighbour column.</param>
    /// <param name="mby">The neighbour row.</param>
    /// <returns>0 or 1.</returns>
    private int ChromaPredCondition(int mbx, int mby)
    {
        if (!MbAvailable(mbx, mby))
        {
            return 0;
        }

        var addr = (mby * W) + mbx;
        var kind = MbKindOf[addr];
        return IsIntra(kind) && kind != H264MbKind.IPcm && MbChromaMode[addr] != 0 ? 1 : 0;
    }

    /// <summary>condTermFlagN of ref_idx.</summary>
    /// <param name="list">The list.</param>
    /// <param name="bx">The neighbour 4x4 column.</param>
    /// <param name="by">The neighbour 4x4 row.</param>
    /// <returns>0 or 1.</returns>
    private int RefIdxCondition(int list, int bx, int by)
    {
        var addr = BlockMb(bx, by);
        if (addr < 0)
        {
            return 0;
        }

        var kind = MbKindOf[addr];
        if (IsSkip(kind) || IsIntra(kind))
        {
            return 0;
        }

        var g8 = ((by >> 1) * W8) + (bx >> 1);
        return Direct8x8[g8] == 0 && _pic.RefIdx[(list * MbCount * 4) + g8] > 0 ? 1 : 0;
    }

    /// <summary>Returns the stored |mvd| of a neighbouring block, 0 when unavailable.</summary>
    /// <param name="list">The list.</param>
    /// <param name="component">The component.</param>
    /// <param name="bx">The neighbour 4x4 column.</param>
    /// <param name="by">The neighbour 4x4 row.</param>
    /// <returns>The value.</returns>
    private int MvdAbsAt(int list, int component, int bx, int by) =>
        BlockMb(bx, by) < 0 ? 0 : MvdAbs[(((list * MbCount * 16) + (by * W4) + bx) * 2) + component];

    /// <summary>condTermFlagN of a coded_block_pattern luma bin in a neighbouring macroblock.</summary>
    /// <param name="mbx">The neighbour column.</param>
    /// <param name="mby">The neighbour row.</param>
    /// <param name="b8">The neighbour's 8x8 block.</param>
    /// <returns>0 or 1.</returns>
    private int CbpLumaCondition(int mbx, int mby, int b8)
    {
        if (!MbAvailable(mbx, mby))
        {
            return 0;
        }

        var addr = (mby * W) + mbx;
        return MbKindOf[addr] != H264MbKind.IPcm && ((MbCbp[addr] >> b8) & 1) == 0 ? 1 : 0;
    }

    /// <summary>condTermFlagN of a coded_block_pattern chroma bin.</summary>
    /// <param name="mbx">The neighbour column.</param>
    /// <param name="mby">The neighbour row.</param>
    /// <param name="bin">0 or 1.</param>
    /// <returns>0 or 1.</returns>
    private int CbpChromaCondition(int mbx, int mby, int bin)
    {
        if (!MbAvailable(mbx, mby))
        {
            return 0;
        }

        var addr = (mby * W) + mbx;
        if (MbKindOf[addr] == H264MbKind.IPcm)
        {
            return 1;
        }

        var chroma = MbCbp[addr] >> 4;
        return (bin == 0 ? chroma != 0 : chroma == 2) ? 1 : 0;
    }
}
