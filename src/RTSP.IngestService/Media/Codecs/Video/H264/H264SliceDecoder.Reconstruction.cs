// Slice set-up and sample reconstruction: per-slice scaling factors (8.5.9), weights (8.4.2.3), direct-mode scale
// factors (8.4.1.2.3), intra prediction with its neighbour availability (8.3), and residual decoding: scaling,
// the Intra16x16 and chroma DC transforms, and the 4x4 and 8x8 inverse transforms (8.5.10 to 8.5.14).

namespace RTSP.IngestService.Media.Codecs.Video.H264;

/// <summary>Reconstruction part of the slice decoder.</summary>
internal sealed partial class H264SliceDecoder
{
    /// <summary>RefPicList0 and RefPicList1 of the slice.</summary>
    private readonly H264Picture?[][] _lists = [new H264Picture?[32], new H264Picture?[32]];

    /// <summary>LevelScale4x4 at <c>((list * 6) + qP % 6) * 16 + raster</c>.</summary>
    private readonly int[] _levelScale4x4 = new int[6 * 6 * 16];

    /// <summary>LevelScale8x8 at <c>((list * 6) + qP % 6) * 64 + raster</c>.</summary>
    private readonly int[] _levelScale8x8 = new int[2 * 6 * 64];

    /// <summary>The resolved 4x4 scaling lists.</summary>
    private readonly byte[] _scaling4x4 = new byte[6 * 16];

    /// <summary>The resolved 8x8 scaling lists.</summary>
    private readonly byte[] _scaling8x8 = new byte[2 * 64];

    /// <summary>Explicit weights, laid out as <see cref="H264SliceHeader.Weights"/>.</summary>
    private readonly int[] _weights = new int[2 * 32 * 3];

    /// <summary>Explicit offsets.</summary>
    private readonly int[] _offsets = new int[2 * 32 * 3];

    /// <summary>Implicit w0 at <c>refIdxL0 * 32 + refIdxL1</c>.</summary>
    private readonly int[] _implicitW0 = new int[32 * 32];

    /// <summary>Temporal direct DistScaleFactor per refIdxL0, or int.MinValue to copy mvCol.</summary>
    private readonly int[] _temporalScale = new int[32];

    /// <summary>Partition prediction lists (1 L0, 2 L1, 3 both).</summary>
    private readonly int[] _partPred = new int[4];

    /// <summary>sub_mb_type per 8x8.</summary>
    private readonly int[] _subType = new int[4];

    /// <summary>Sub-partition shape per 8x8.</summary>
    private readonly int[] _subShape = new int[4];

    /// <summary>ref_idx syntax at <c>list * 4 + partition</c>.</summary>
    private readonly int[] _refSyntax = new int[8];

    /// <summary>mvd syntax at <c>((list * 16) + raster4x4) * 2 + component</c>.</summary>
    private readonly int[] _mvdSyntax = new int[64];

    /// <summary>Intra prediction modes: by luma4x4BlkIdx, or by luma8x8BlkIdx for 8x8.</summary>
    private readonly int[] _intraModes = new int[16];

    /// <summary>Spatial direct reference indices.</summary>
    private readonly int[] _spatialRef = new int[2];

    /// <summary>Spatial direct predictors.</summary>
    private readonly int[] _spatialMv = new int[2];

    /// <summary>Intra16x16 DC levels, raster.</summary>
    private readonly int[] _lumaDc = new int[16];

    /// <summary>Luma 4x4 levels per luma4x4BlkIdx, raster.</summary>
    private readonly int[] _luma4x4 = new int[256];

    /// <summary>Luma 8x8 levels per luma8x8BlkIdx, raster.</summary>
    private readonly int[] _luma8x8 = new int[256];

    /// <summary>Chroma DC levels, Cb then Cr.</summary>
    private readonly int[] _chromaDc = new int[8];

    /// <summary>Chroma AC levels per (component * 4 + chroma4x4BlkIdx), raster.</summary>
    private readonly int[] _chromaAc = new int[128];

    /// <summary>Scaled coefficients of one block.</summary>
    private readonly int[] _scaled = new int[64];

    /// <summary>The picture being decoded.</summary>
    private H264Picture _pic = null!;

    /// <summary>The current slice number.</summary>
    private int _sliceNum;

    /// <summary>first_mb_in_slice.</summary>
    private int _firstMb;

    /// <summary>QPY of the current (or last) macroblock.</summary>
    private int _qp;

    /// <summary>transform_8x8_mode_flag.</summary>
    private bool _transform8x8Mode;

    /// <summary>constrained_intra_pred_flag.</summary>
    private bool _constrainedIntra;

    /// <summary>direct_8x8_inference_flag.</summary>
    private bool _direct8x8Inference;

    /// <summary>direct_spatial_mv_pred_flag.</summary>
    private bool _directSpatial;

    /// <summary>noSubMbPartSizeLessThan8x8Flag.</summary>
    private bool _noSubPartitionBelow8x8;

    /// <summary>Whether the spatial direct predictors of the macroblock are computed.</summary>
    private bool _spatialReady;

    /// <summary>directZeroPredictionFlag.</summary>
    private bool _spatialZero;

    /// <summary>Partition mode: 0 16x16, 1 16x8, 2 8x16, 3 8x8.</summary>
    private int _partMode;

    /// <summary>Intra16x16PredMode.</summary>
    private int _intra16x16Mode;

    /// <summary>4x4 blocks of the current macroblock whose motion is derived.</summary>
    private int _derivedMask;

    /// <summary>Weighting: 0 default, 1 explicit, 2 implicit.</summary>
    private int _weightMode;

    /// <summary>luma_log2_weight_denom.</summary>
    private int _lumaLog2Denom;

    /// <summary>chroma_log2_weight_denom.</summary>
    private int _chromaLog2Denom;

    /// <summary>chroma_qp_index_offset.</summary>
    private int _cbOffset;

    /// <summary>second_chroma_qp_index_offset.</summary>
    private int _crOffset;

    /// <summary>Whether the Intra16x16 DC block has coefficients.</summary>
    private bool _lumaDcCoded;

    /// <summary>luma4x4BlkIdx bits of blocks with coefficients.</summary>
    private int _lumaCodedMask;

    /// <summary>Chroma blocks with coefficients: bit component * 4 + chroma4x4BlkIdx.</summary>
    private int _chromaCodedMask;

    /// <summary>Clears the coefficient scratch.</summary>
    private void InitializeScratch()
    {
        Array.Clear(_lumaDc);
        Array.Clear(_luma4x4);
        Array.Clear(_luma8x8);
        Array.Clear(_chromaDc);
        Array.Clear(_chromaAc);
    }

    /// <summary>Configures the decoder for a slice.</summary>
    /// <param name="h">The header.</param>
    /// <param name="list0">RefPicList0.</param>
    /// <param name="list1">RefPicList1.</param>
    private void BeginSlice(H264SliceHeader h, H264Picture?[] list0, H264Picture?[] list1)
    {
        var pps = h.Pps;
        _sliceNum = _slices.Count;
        _slices.Add(new SliceFilter(h.DisableDeblocking, h.FilterOffsetA, h.FilterOffsetB, pps.ChromaQpIndexOffset, pps.SecondChromaQpIndexOffset));
        SliceType = h.Type;
        _firstMb = h.FirstMb;
        _qp = h.SliceQp;
        PrevQpDeltaNonZero = false;
        _transform8x8Mode = pps.Transform8x8Mode;
        _constrainedIntra = pps.ConstrainedIntraPred;
        _direct8x8Inference = h.Sps.Direct8x8Inference;
        _directSpatial = h.DirectSpatial;
        _cbOffset = pps.ChromaQpIndexOffset;
        _crOffset = pps.SecondChromaQpIndexOffset;
        for (var list = 0; list < 2; list++)
        {
            NumRefIdxActive[list] = h.NumRefIdxActive[list];
            Array.Clear(_lists[list]);
            Array.Copy(list == 0 ? list0 : list1, _lists[list], h.NumRefIdxActive[list]);
        }

        H264ScalingLists.Resolve(h.Sps, pps, _scaling4x4, _scaling8x8);
        ComputeLevelScales();
        _weightMode = h.HasWeightTable ? 1 : h.Type == H264SliceType.B && pps.WeightedBipredIdc == 2 ? 2 : 0;
        if (_weightMode == 1)
        {
            _lumaLog2Denom = h.LumaLog2Denom;
            _chromaLog2Denom = h.ChromaLog2Denom;
            h.Weights.CopyTo(_weights, 0);
            h.Offsets.CopyTo(_offsets, 0);
        }

        if (h.Type != H264SliceType.B)
        {
            return;
        }

        var current = (long)_pic.Poc;
        for (var i = 0; i < NumRefIdxActive[0]; i++)
        {
            var pic0 = _lists[0][i];
            if (pic0 is null)
            {
                continue;
            }

            if (_weightMode == 2)
            {
                for (var j = 0; j < NumRefIdxActive[1]; j++)
                {
                    var pic1 = _lists[1][j];
                    var w0 = 32;
                    if (pic1 is not null && pic0.Mark != H264ReferenceMark.LongTerm && pic1.Mark != H264ReferenceMark.LongTerm)
                    {
                        var scale = DistScaleFactor(current, pic0.Poc, pic1.Poc);
                        if (scale != int.MinValue && (scale >> 2) >= -64 && (scale >> 2) <= 128)
                        {
                            w0 = 64 - (scale >> 2);
                        }
                    }

                    _implicitW0[(i * 32) + j] = w0;
                }
            }

            var first1 = _lists[1][0];
            _temporalScale[i] = first1 is null || pic0.Mark == H264ReferenceMark.LongTerm ? int.MinValue : DistScaleFactor(current, pic0.Poc, first1.Poc);
        }
    }

    /// <summary>Computes DistScaleFactor (8-195 to 8-198).</summary>
    /// <param name="current">PicOrderCnt of the current picture.</param>
    /// <param name="poc0">PicOrderCnt of pic0.</param>
    /// <param name="poc1">PicOrderCnt of pic1.</param>
    /// <returns>The factor, or int.MinValue when pic1 and pic0 have equal order counts.</returns>
    private static int DistScaleFactor(long current, long poc0, long poc1)
    {
        var td = (int)Math.Clamp(poc1 - poc0, -128, 127);
        if (td == 0)
        {
            return int.MinValue;
        }

        var tb = (int)Math.Clamp(current - poc0, -128, 127);
        var tx = (16384 + Math.Abs(td / 2)) / td;
        return Math.Clamp(((tb * tx) + 32) >> 6, -1024, 1023);
    }

    /// <summary>Computes LevelScale4x4 and LevelScale8x8 from the resolved lists (8-315 to 8-320).</summary>
    private void ComputeLevelScales()
    {
        var v4 = H264Tables.NormAdjust4x4;
        var v8 = H264Tables.NormAdjust8x8;
        for (var m = 0; m < 6; m++)
        {
            for (var pos = 0; pos < 16; pos++)
            {
                int x = pos & 3, y = pos >> 2;
                var column = (x & 1) == 0 && (y & 1) == 0 ? 0 : (x & 1) == 1 && (y & 1) == 1 ? 1 : 2;
                for (var list = 0; list < 6; list++)
                {
                    _levelScale4x4[(((list * 6) + m) * 16) + pos] = _scaling4x4[(list * 16) + pos] * v4[(m * 3) + column];
                }
            }

            for (var pos = 0; pos < 64; pos++)
            {
                int x = pos & 7, y = pos >> 3;
                int column;
                if (x % 4 == 0 && y % 4 == 0)
                {
                    column = 0;
                }
                else if (x % 2 == 1 && y % 2 == 1)
                {
                    column = 1;
                }
                else if (x % 4 == 2 && y % 4 == 2)
                {
                    column = 2;
                }
                else if ((x % 4 == 0 && y % 2 == 1) || (x % 2 == 1 && y % 4 == 0))
                {
                    column = 3;
                }
                else if ((x % 4 == 0 && y % 4 == 2) || (x % 4 == 2 && y % 4 == 0))
                {
                    column = 4;
                }
                else
                {
                    column = 5;
                }

                for (var list = 0; list < 2; list++)
                {
                    _levelScale8x8[(((list * 6) + m) * 64) + pos] = _scaling8x8[(list * 64) + pos] * v8[(m * 6) + column];
                }
            }
        }
    }

    /// <summary>Reconstructs an intra macroblock: prediction then residual, block by block.</summary>
    private void ReconstructIntra()
    {
        ClearMotion();
        var luma = _pic.Luma;
        var stride = _pic.Width;
        var x0 = MbX * 16;
        var y0 = MbY * 16;
        var availA = IntraAvailable(MbX - 1, MbY);
        var availB = IntraAvailable(MbX, MbY - 1);
        var availC = IntraAvailable(MbX + 1, MbY - 1);
        var availD = IntraAvailable(MbX - 1, MbY - 1);
        if (Kind == H264MbKind.I4x4)
        {
            for (var blk = 0; blk < 16; blk++)
            {
                int bx = BlkX[blk], by = BlkY[blk];
                var left = bx > 0 || availA;
                var top = by > 0 || availB;
                var topLeft = bx > 0 && by > 0 || (bx > 0 ? availB : by > 0 ? availA : availD);
                var topRight = by == 0 ? (bx < 3 ? availB : availC) : bx < 3 && blk != 3 && blk != 11;
                var offset = ((y0 + (by * 4)) * stride) + x0 + (bx * 4);
                if (!H264IntraPrediction.Predict4x4(luma, stride, offset, _intraModes[blk], left, top, topRight, topLeft))
                {
                    CodecException.InvalidData("An Intra4x4 prediction mode uses unavailable samples.");
                }

                if ((_lumaCodedMask & (1 << blk)) != 0)
                {
                    Dequantize4x4(_luma4x4.AsSpan(blk * 16, 16), _qp, 0, false);
                    H264Transform.Add4x4(_scaled, luma, offset, stride);
                }
            }
        }
        else if (Kind == H264MbKind.I8x8)
        {
            for (var b8 = 0; b8 < 4; b8++)
            {
                int bx = b8 & 1, by = b8 >> 1;
                var left = bx > 0 || availA;
                var top = by > 0 || availB;
                var topLeft = bx > 0 && by > 0 || (bx > 0 ? availB : by > 0 ? availA : availD);
                var topRight = by == 0 ? (bx == 0 ? availB : availC) : bx == 0;
                var offset = ((y0 + (by * 8)) * stride) + x0 + (bx * 8);
                if (!H264IntraPrediction.Predict8x8(luma, stride, offset, _intraModes[b8], left, top, topRight, topLeft))
                {
                    CodecException.InvalidData("An Intra8x8 prediction mode uses unavailable samples.");
                }

                if ((_lumaCodedMask & (0xF << (b8 * 4))) != 0)
                {
                    Dequantize8x8(_luma8x8.AsSpan(b8 * 64, 64), _qp, 0);
                    H264Transform.Add8x8(_scaled, luma, offset, stride);
                }
            }
        }
        else
        {
            if (!H264IntraPrediction.Predict16x16(luma, stride, (y0 * stride) + x0, _intra16x16Mode, availA, availB, availD))
            {
                CodecException.InvalidData("An Intra16x16 prediction mode uses unavailable samples.");
            }

            ReconstructIntra16x16Residual(luma, stride, x0, y0);
        }

        var cstride = stride / 2;
        var coffset = (MbY * 8 * cstride) + (MbX * 8);
        int mode = MbChromaMode[MbAddr];
        if (!H264IntraPrediction.PredictChroma(_pic.Cb, cstride, coffset, mode, availA, availB, availD)
            || !H264IntraPrediction.PredictChroma(_pic.Cr, cstride, coffset, mode, availA, availB, availD))
        {
            CodecException.InvalidData("An intra chroma prediction mode uses unavailable samples.");
        }

        ReconstructChromaResidual(true);
    }

    /// <summary>Adds the residual of an inter macroblock to its prediction.</summary>
    /// <param name="cbp">coded_block_pattern.</param>
    /// <param name="transform8x8">transform_size_8x8_flag.</param>
    private void ReconstructInterResidual(int cbp, bool transform8x8)
    {
        if (cbp == 0)
        {
            return;
        }

        var luma = _pic.Luma;
        var stride = _pic.Width;
        var x0 = MbX * 16;
        var y0 = MbY * 16;
        if (transform8x8)
        {
            for (var b8 = 0; b8 < 4; b8++)
            {
                if ((_lumaCodedMask & (0xF << (b8 * 4))) != 0)
                {
                    Dequantize8x8(_luma8x8.AsSpan(b8 * 64, 64), _qp, 1);
                    H264Transform.Add8x8(_scaled, luma, ((y0 + ((b8 >> 1) * 8)) * stride) + x0 + ((b8 & 1) * 8), stride);
                }
            }
        }
        else
        {
            for (var blk = 0; blk < 16; blk++)
            {
                if ((_lumaCodedMask & (1 << blk)) != 0)
                {
                    Dequantize4x4(_luma4x4.AsSpan(blk * 16, 16), _qp, 3, false);
                    H264Transform.Add4x4(_scaled, luma, ((y0 + (BlkY[blk] * 4)) * stride) + x0 + (BlkX[blk] * 4), stride);
                }
            }
        }

        ReconstructChromaResidual(false);
    }

    /// <summary>Returns whether a neighbouring macroblock's samples may be used for intra prediction.</summary>
    /// <param name="mbx">The column.</param>
    /// <param name="mby">The row.</param>
    /// <returns><see langword="true"/> when available and, under constrained intra prediction, intra coded.</returns>
    private bool IntraAvailable(int mbx, int mby) =>
        MbAvailable(mbx, mby) && (!_constrainedIntra || IsIntra(MbKindOf[(mby * W) + mbx]));

    /// <summary>Reconstructs the Intra16x16 residual: DC transform (8.5.10) then each 4x4 block.</summary>
    /// <param name="luma">The luma plane.</param>
    /// <param name="stride">The stride.</param>
    /// <param name="x0">The macroblock x.</param>
    /// <param name="y0">The macroblock y.</param>
    private void ReconstructIntra16x16Residual(byte[] luma, int stride, int x0, int y0)
    {
        Span<int> dc = stackalloc int[16];
        if (_lumaDcCoded)
        {
            H264Transform.InverseHadamard4x4(_lumaDc, dc);
            var qp = _qp;
            var scale = _levelScale4x4[(qp % 6) * 16];
            for (var i = 0; i < 16; i++)
            {
                long value = qp >= 36 ? ((long)dc[i] * scale) << ((qp / 6) - 6) : (((long)dc[i] * scale) + (1 << (5 - (qp / 6)))) >> (6 - (qp / 6));
                dc[i] = CheckScaled(value);
            }
        }

        for (var blk = 0; blk < 16; blk++)
        {
            var dcValue = dc[(BlkY[blk] * 4) + BlkX[blk]];
            if ((_lumaCodedMask & (1 << blk)) == 0 && dcValue == 0)
            {
                continue;
            }

            Dequantize4x4(_luma4x4.AsSpan(blk * 16, 16), _qp, 0, true);
            _scaled[0] = dcValue;
            H264Transform.Add4x4(_scaled, luma, ((y0 + (BlkY[blk] * 4)) * stride) + x0 + (BlkX[blk] * 4), stride);
        }

        Array.Clear(_lumaDc);
    }

    /// <summary>Reconstructs both chroma components' residual (8.5.11).</summary>
    /// <param name="intra">Whether the macroblock is intra (selects the scaling lists).</param>
    private void ReconstructChromaResidual(bool intra)
    {
        if (_chromaCodedMask == 0)
        {
            return;
        }

        var cstride = _pic.Width / 2;
        var origin = (MbY * 8 * cstride) + (MbX * 8);
        Span<int> dc = stackalloc int[4];
        for (var c = 0; c < 2; c++)
        {
            var plane = c == 0 ? _pic.Cb : _pic.Cr;
            var qpc = H264Tables.ChromaQp[Math.Clamp(_qp + (c == 0 ? _cbOffset : _crOffset), 0, 51)];
            var list = (intra ? 1 : 4) + c;
            var levels = _chromaDc.AsSpan(c * 4, 4);
            int c0 = levels[0], c1 = levels[1], c2 = levels[2], c3 = levels[3];
            dc[0] = c0 + c1 + c2 + c3;
            dc[1] = c0 - c1 + c2 - c3;
            dc[2] = c0 + c1 - c2 - c3;
            dc[3] = c0 - c1 - c2 + c3;
            var scale = _levelScale4x4[((list * 6) + (qpc % 6)) * 16];
            for (var i = 0; i < 4; i++)
            {
                dc[i] = CheckScaled(((long)dc[i] * scale << (qpc / 6)) >> 5);
            }

            levels.Clear();
            for (var blk = 0; blk < 4; blk++)
            {
                if (((_chromaCodedMask >> ((c * 4) + blk)) & 1) == 0 && dc[blk] == 0)
                {
                    continue;
                }

                Dequantize4x4(_chromaAc.AsSpan(((c * 4) + blk) * 16, 16), qpc, list, true);
                _scaled[0] = dc[blk];
                H264Transform.Add4x4(_scaled, plane, origin + ((blk >> 1) * 4 * cstride) + ((blk & 1) * 4), cstride);
            }
        }
    }

    /// <summary>Scales a 4x4 block into <see cref="_scaled"/> and clears the levels (8.5.12.1).</summary>
    /// <param name="levels">The levels, raster order; cleared.</param>
    /// <param name="qp">qP.</param>
    /// <param name="list">The scaling list index (0 to 5).</param>
    /// <param name="skipDc">Whether position 0 is a separately transformed DC value.</param>
    private void Dequantize4x4(Span<int> levels, int qp, int list, bool skipDc)
    {
        var scales = _levelScale4x4.AsSpan(((list * 6) + (qp % 6)) * 16, 16);
        var shift = qp / 6;
        for (var i = skipDc ? 1 : 0; i < 16; i++)
        {
            var c = levels[i];
            if (c == 0)
            {
                _scaled[i] = 0;
                continue;
            }

            long value = qp >= 24 ? ((long)c * scales[i]) << (shift - 4) : (((long)c * scales[i]) + (1 << (3 - shift))) >> (4 - shift);
            _scaled[i] = CheckScaled(value);
        }

        levels.Clear();
    }

    /// <summary>Scales an 8x8 block into <see cref="_scaled"/> and clears the levels (8.5.13.1).</summary>
    /// <param name="levels">The levels, raster order; cleared.</param>
    /// <param name="qp">qP.</param>
    /// <param name="list">0 intra, 1 inter.</param>
    private void Dequantize8x8(Span<int> levels, int qp, int list)
    {
        var scales = _levelScale8x8.AsSpan(((list * 6) + (qp % 6)) * 64, 64);
        var shift = qp / 6;
        for (var i = 0; i < 64; i++)
        {
            var c = levels[i];
            if (c == 0)
            {
                _scaled[i] = 0;
                continue;
            }

            long value = qp >= 36 ? ((long)c * scales[i]) << (shift - 6) : (((long)c * scales[i]) + (1 << (5 - shift))) >> (6 - shift);
            _scaled[i] = CheckScaled(value);
        }

        levels.Clear();
    }

    /// <summary>Enforces the range of scaled coefficients (8.5.12.1, 8.5.13.1).</summary>
    /// <param name="value">The scaled value.</param>
    /// <returns>The value.</returns>
    /// <exception cref="CodecException">The value is outside the 16-bit range.</exception>
    private static int CheckScaled(long value)
    {
        if (value < -MaxCoefficient || value >= MaxCoefficient)
        {
            CodecException.InvalidData("A scaled transform coefficient is out of range.");
        }

        return (int)value;
    }
}
