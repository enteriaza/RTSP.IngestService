// Inter prediction syntax and motion derivation: mb_pred / sub_mb_pred for P and B macroblocks (7.3.5.1, 7.3.5.2),
// motion vector prediction (8.4.1.3), P_Skip (8.4.1.1), spatial and temporal direct prediction (8.4.1.2), and the
// dispatch of motion compensation with default, explicit or implicit weighting (8.4.2.3). Motion is stored per 4x4
// block, reference indices per 8x8 block, in the picture, where neighbours and later pictures read it.

namespace RTSP.IngestService.Media.Codecs.Video.H264;

/// <summary>Inter prediction part of the slice decoder.</summary>
internal sealed partial class H264SliceDecoder
{
    /// <summary>Partition count per P/B partition mode (16x16, 16x8, 8x16, 8x8).</summary>
    private static ReadOnlySpan<byte> PartCount => [1, 2, 2, 4];

    /// <summary>Prediction lists (1 L0, 2 L1, 3 Bi) of B_16x8 / B_8x16 partitions by (mb_type - 4) / 2, two per type.</summary>
    private static ReadOnlySpan<byte> BPartPred => [1, 1, 2, 2, 1, 2, 2, 1, 1, 3, 2, 3, 3, 1, 3, 2, 3, 3];

    /// <summary>Sub-partition shape (0 8x8, 1 8x4, 2 4x8, 3 4x4) by B sub_mb_type; entry 0 (direct) is unused.</summary>
    private static ReadOnlySpan<byte> BSubShape => [0, 0, 0, 0, 1, 2, 1, 2, 1, 2, 3, 3, 3];

    /// <summary>Prediction lists by B sub_mb_type; 0 for direct.</summary>
    private static ReadOnlySpan<byte> BSubPred => [0, 1, 2, 3, 1, 1, 2, 2, 3, 3, 1, 2, 3];

    /// <summary>Reads mb_pred or sub_mb_pred of an inter macroblock.</summary>
    /// <typeparam name="T">The entropy reader.</typeparam>
    /// <param name="s">The reader.</param>
    /// <param name="mbType">mb_type (inter range of the slice type).</param>
    private void ReadInterPrediction<T>(ref T s, int mbType)
        where T : IH264EntropyReader, allows ref struct
    {
        _noSubPartitionBelow8x8 = true;
        var ref0Only = false;
        if (SliceType == H264SliceType.P)
        {
            SetKind(H264MbKind.PInter);
            _partMode = mbType >= 3 ? 3 : mbType;
            ref0Only = mbType == 4;
            _partPred[0] = _partPred[1] = _partPred[2] = _partPred[3] = 1;
        }
        else if (mbType == 0)
        {
            SetKind(H264MbKind.BDirect);
            for (var i = 0; i < 4; i++)
            {
                SetDirectFlag(i);
            }

            return;
        }
        else
        {
            SetKind(H264MbKind.BInter);
            if (mbType <= 3)
            {
                _partMode = 0;
                _partPred[0] = mbType;
            }
            else if (mbType <= 21)
            {
                _partMode = (mbType & 1) == 0 ? 1 : 2;
                _partPred[0] = BPartPred[(((mbType - 4) >> 1) * 2)];
                _partPred[1] = BPartPred[(((mbType - 4) >> 1) * 2) + 1];
            }
            else
            {
                _partMode = 3;
            }
        }

        if (_partMode == 3)
        {
            for (var i = 0; i < 4; i++)
            {
                var sub = s.SubMbType(this);
                if (sub > (SliceType == H264SliceType.P ? 3 : 12))
                {
                    CodecException.InvalidData("sub_mb_type is out of range.");
                }

                _subType[i] = sub;
                if (SliceType == H264SliceType.B)
                {
                    _partPred[i] = BSubPred[sub];
                    if (sub == 0)
                    {
                        SetDirectFlag(i);
                        _noSubPartitionBelow8x8 &= _direct8x8Inference;
                        continue;
                    }

                    _subShape[i] = BSubShape[sub];
                }
                else
                {
                    _subShape[i] = sub;
                }

                if (_subShape[i] != 0)
                {
                    _noSubPartitionBelow8x8 = false;
                }
            }

            for (var list = 0; list < 2; list++)
            {
                for (var i = 0; i < 4; i++)
                {
                    if ((_partPred[i] & (1 << list)) == 0)
                    {
                        continue;
                    }

                    var x4 = (i & 1) * 2;
                    var y4 = (i >> 1) * 2;
                    _refSyntax[(list * 4) + i] = NumRefIdxActive[list] > 1 && !ref0Only ? ReadRefIdx(ref s, list, x4, y4, 2, 2) : 0;
                }
            }

            for (var list = 0; list < 2; list++)
            {
                for (var i = 0; i < 4; i++)
                {
                    if ((_partPred[i] & (1 << list)) == 0)
                    {
                        continue;
                    }

                    var x4 = (i & 1) * 2;
                    var y4 = (i >> 1) * 2;
                    switch (_subShape[i])
                    {
                        case 0:
                            ReadMvd(ref s, list, x4, y4, 2, 2);
                            break;
                        case 1:
                            ReadMvd(ref s, list, x4, y4, 2, 1);
                            ReadMvd(ref s, list, x4, y4 + 1, 2, 1);
                            break;
                        case 2:
                            ReadMvd(ref s, list, x4, y4, 1, 2);
                            ReadMvd(ref s, list, x4 + 1, y4, 1, 2);
                            break;
                        default:
                            ReadMvd(ref s, list, x4, y4, 1, 1);
                            ReadMvd(ref s, list, x4 + 1, y4, 1, 1);
                            ReadMvd(ref s, list, x4, y4 + 1, 1, 1);
                            ReadMvd(ref s, list, x4 + 1, y4 + 1, 1, 1);
                            break;
                    }
                }
            }

            return;
        }

        var parts = PartCount[_partMode];
        for (var list = 0; list < 2; list++)
        {
            for (var p = 0; p < parts; p++)
            {
                if ((_partPred[p] & (1 << list)) != 0)
                {
                    PartitionGeometry(p, out var x4, out var y4, out var w4, out var h4);
                    _refSyntax[(list * 4) + p] = NumRefIdxActive[list] > 1 ? ReadRefIdx(ref s, list, x4, y4, w4, h4) : 0;
                }
            }
        }

        for (var list = 0; list < 2; list++)
        {
            for (var p = 0; p < parts; p++)
            {
                if ((_partPred[p] & (1 << list)) != 0)
                {
                    PartitionGeometry(p, out var x4, out var y4, out var w4, out var h4);
                    ReadMvd(ref s, list, x4, y4, w4, h4);
                }
            }
        }
    }

    /// <summary>Returns the 4x4-unit rectangle of a macroblock partition.</summary>
    /// <param name="p">mbPartIdx.</param>
    /// <param name="x4">Column.</param>
    /// <param name="y4">Row.</param>
    /// <param name="w4">Width.</param>
    /// <param name="h4">Height.</param>
    private void PartitionGeometry(int p, out int x4, out int y4, out int w4, out int h4)
    {
        (x4, y4, w4, h4) = _partMode switch
        {
            0 => (0, 0, 4, 4),
            1 => (0, p * 2, 4, 2),
            2 => (p * 2, 0, 2, 4),
            _ => ((p & 1) * 2, (p >> 1) * 2, 2, 2),
        };
    }

    /// <summary>Marks an 8x8 block as direct-predicted (CABAC context state).</summary>
    /// <param name="b8">The 8x8 block index.</param>
    private void SetDirectFlag(int b8) => Direct8x8[(((MbY * 2) + (b8 >> 1)) * W8) + (MbX * 2) + (b8 & 1)] = 1;

    /// <summary>Reads and range-checks ref_idx, recording it for the contexts of later partitions.</summary>
    /// <typeparam name="T">The entropy reader.</typeparam>
    /// <param name="s">The reader.</param>
    /// <param name="list">The list.</param>
    /// <param name="x4">Partition column.</param>
    /// <param name="y4">Partition row.</param>
    /// <param name="w4">Partition width.</param>
    /// <param name="h4">Partition height.</param>
    /// <returns>The reference index.</returns>
    private int ReadRefIdx<T>(ref T s, int list, int x4, int y4, int w4, int h4)
        where T : IH264EntropyReader, allows ref struct
    {
        var value = s.RefIdx(this, list, (MbX * 4) + x4, (MbY * 4) + y4);
        if (value >= NumRefIdxActive[list])
        {
            CodecException.InvalidData("ref_idx exceeds num_ref_idx_active.");
        }

        for (var y = y4; y < y4 + h4; y += 2)
        {
            for (var x = x4; x < x4 + w4; x += 2)
            {
                _pic.RefIdx[(list * MbCount * 4) + (((MbY * 2) + (y >> 1)) * W8) + (MbX * 2) + (x >> 1)] = (sbyte)value;
            }
        }

        return value;
    }

    /// <summary>Reads mvd for a partition and records it per 4x4 block.</summary>
    /// <typeparam name="T">The entropy reader.</typeparam>
    /// <param name="s">The reader.</param>
    /// <param name="list">The list.</param>
    /// <param name="x4">Partition column.</param>
    /// <param name="y4">Partition row.</param>
    /// <param name="w4">Partition width.</param>
    /// <param name="h4">Partition height.</param>
    private void ReadMvd<T>(ref T s, int list, int x4, int y4, int w4, int h4)
        where T : IH264EntropyReader, allows ref struct
    {
        var bx = (MbX * 4) + x4;
        var by = (MbY * 4) + y4;
        var mx = s.Mvd(this, list, 0, bx, by);
        var my = s.Mvd(this, list, 1, bx, by);
        var ax = (byte)Math.Min(Math.Abs(mx), 127);
        var ay = (byte)Math.Min(Math.Abs(my), 127);
        for (var y = y4; y < y4 + h4; y++)
        {
            for (var x = x4; x < x4 + w4; x++)
            {
                var r = (y * 4) + x;
                _mvdSyntax[(((list * 16) + r) * 2)] = mx;
                _mvdSyntax[(((list * 16) + r) * 2) + 1] = my;
                var g = ((list * MbCount * 16) + (((MbY * 4) + y) * W4) + (MbX * 4) + x) * 2;
                MvdAbs[g] = ax;
                MvdAbs[g + 1] = ay;
            }
        }
    }

    /// <summary>Clears the motion of an intra macroblock.</summary>
    private void ClearMotion()
    {
        for (var list = 0; list < 2; list++)
        {
            for (var row = 0; row < 4; row++)
            {
                _pic.Mv.AsSpan(((list * MbCount * 16) + ((((MbY * 4) + row) * W4) + (MbX * 4))) * 2, 8).Clear();
            }
        }
    }

    /// <summary>Derives the motion of the current inter macroblock.</summary>
    private void DeriveMotion()
    {
        _derivedMask = 0;
        _spatialReady = false;
        switch (Kind)
        {
            case H264MbKind.PSkip:
                {
                    var mv = SkipMotion();
                    SetMotion(0, 0, 4, 4, 0, mv, -1, 0);
                    return;
                }

            case H264MbKind.BSkip:
            case H264MbKind.BDirect:
                for (var i = 0; i < 4; i++)
                {
                    DeriveDirect(i);
                }

                return;
        }

        if (_partMode != 3)
        {
            var shape = _partMode;
            for (var p = 0; p < PartCount[_partMode]; p++)
            {
                PartitionGeometry(p, out var x4, out var y4, out var w4, out var h4);
                DerivePartition(p, x4, y4, w4, h4, shape);
            }

            return;
        }

        for (var i = 0; i < 4; i++)
        {
            if (SliceType == H264SliceType.B && _subType[i] == 0)
            {
                DeriveDirect(i);
                continue;
            }

            var x4 = (i & 1) * 2;
            var y4 = (i >> 1) * 2;
            switch (_subShape[i])
            {
                case 0:
                    DerivePartition(i, x4, y4, 2, 2, 0);
                    break;
                case 1:
                    DerivePartition(i, x4, y4, 2, 1, 0);
                    DerivePartition(i, x4, y4 + 1, 2, 1, 0);
                    break;
                case 2:
                    DerivePartition(i, x4, y4, 1, 2, 0);
                    DerivePartition(i, x4 + 1, y4, 1, 2, 0);
                    break;
                default:
                    DerivePartition(i, x4, y4, 1, 1, 0);
                    DerivePartition(i, x4 + 1, y4, 1, 1, 0);
                    DerivePartition(i, x4, y4 + 1, 1, 1, 0);
                    DerivePartition(i, x4 + 1, y4 + 1, 1, 1, 0);
                    break;
            }
        }
    }

    /// <summary>Derives and stores the motion of one (sub-)partition from its syntax.</summary>
    /// <param name="p">mbPartIdx (the 8x8 index for sub-partitions).</param>
    /// <param name="x4">Column.</param>
    /// <param name="y4">Row.</param>
    /// <param name="w4">Width.</param>
    /// <param name="h4">Height.</param>
    /// <param name="shape">1 for 16x8 or 2 for 8x16 directional prediction, otherwise 0.</param>
    private void DerivePartition(int p, int x4, int y4, int w4, int h4, int shape)
    {
        Span<int> refs = [-1, -1];
        Span<int> mvs = [0, 0];
        for (var list = 0; list < 2; list++)
        {
            if ((_partPred[p] & (1 << list)) == 0)
            {
                continue;
            }

            var refIdx = _refSyntax[(list * 4) + p];
            var mvp = PredictMv(list, refIdx, x4, y4, w4, h4, shape);
            var r = (y4 * 4) + x4;
            refs[list] = refIdx;
            mvs[list] = PackChecked(MvX(mvp) + _mvdSyntax[((list * 16) + r) * 2], MvY(mvp) + _mvdSyntax[(((list * 16) + r) * 2) + 1]);
        }

        SetMotion(x4, y4, w4, h4, refs[0], mvs[0], refs[1], mvs[1]);
    }

    /// <summary>Derives the P_Skip motion vector (8.4.1.1).</summary>
    /// <returns>The packed motion vector.</returns>
    private int SkipMotion()
    {
        var a = Neighbor(0, -1, 0);
        var b = Neighbor(0, 0, -1);
        if (!a.Available || !b.Available || (a.Ref == 0 && a.Mv == 0) || (b.Ref == 0 && b.Mv == 0))
        {
            return 0;
        }

        return PredictMv(0, 0, 0, 0, 4, 4, 0);
    }

    /// <summary>Derives mvpLX (8.4.1.3).</summary>
    /// <param name="list">The list.</param>
    /// <param name="refIdx">The partition's reference index.</param>
    /// <param name="x4">Partition column.</param>
    /// <param name="y4">Partition row.</param>
    /// <param name="w4">Partition width.</param>
    /// <param name="h4">Partition height.</param>
    /// <param name="shape">1 for 16x8, 2 for 8x16, otherwise 0.</param>
    /// <returns>The packed predictor.</returns>
    private int PredictMv(int list, int refIdx, int x4, int y4, int w4, int h4, int shape)
    {
        _ = h4;
        var a = Neighbor(list, x4 - 1, y4);
        var b = Neighbor(list, x4, y4 - 1);
        var c = Neighbor(list, x4 + w4, y4 - 1);
        if (!c.Available)
        {
            c = Neighbor(list, x4 - 1, y4 - 1);
        }

        if (shape == 1)
        {
            if (y4 == 0 && b.Ref == refIdx)
            {
                return b.Mv;
            }

            if (y4 != 0 && a.Ref == refIdx)
            {
                return a.Mv;
            }
        }
        else if (shape == 2)
        {
            if (x4 == 0 && a.Ref == refIdx)
            {
                return a.Mv;
            }

            if (x4 != 0 && c.Ref == refIdx)
            {
                return c.Mv;
            }
        }

        if (!b.Available && !c.Available && a.Available)
        {
            b = a;
            c = a;
        }

        var matches = (a.Ref == refIdx ? 1 : 0) + (b.Ref == refIdx ? 1 : 0) + (c.Ref == refIdx ? 1 : 0);
        if (matches == 1)
        {
            return a.Ref == refIdx ? a.Mv : b.Ref == refIdx ? b.Mv : c.Mv;
        }

        return Pack(Median(MvX(a.Mv), MvX(b.Mv), MvX(c.Mv)), Median(MvY(a.Mv), MvY(b.Mv), MvY(c.Mv)));
    }

    /// <summary>Returns the motion of the 4x4 block at a macroblock-relative position (6.4.11.7).</summary>
    /// <param name="list">The list.</param>
    /// <param name="x4">Column relative to the current macroblock.</param>
    /// <param name="y4">Row relative to the current macroblock.</param>
    /// <returns>The neighbour; unavailable and non-predicting neighbours have reference -1 and a zero vector.</returns>
    private NeighborMotion Neighbor(int list, int x4, int y4)
    {
        var bx = (MbX * 4) + x4;
        var by = (MbY * 4) + y4;
        if (bx < 0 || by < 0 || bx >= W4)
        {
            return NeighborMotion.None;
        }

        var mbx = bx >> 2;
        var mby = by >> 2;
        if (mbx == MbX && mby == MbY)
        {
            if (((_derivedMask >> (((by & 3) * 4) + (bx & 3))) & 1) == 0)
            {
                return NeighborMotion.None;
            }
        }
        else if (!MbAvailable(mbx, mby))
        {
            return NeighborMotion.None;
        }

        var addr = (mby * W) + mbx;
        if (_pic.IntraMb[addr] != 0)
        {
            return new NeighborMotion(true, -1, 0);
        }

        var refIdx = _pic.RefIdx[(list * MbCount * 4) + ((by >> 1) * W8) + (bx >> 1)];
        if (refIdx < 0)
        {
            return new NeighborMotion(true, -1, 0);
        }

        var g = ((list * MbCount * 16) + (by * W4) + bx) * 2;
        return new NeighborMotion(true, refIdx, Pack(_pic.Mv[g], _pic.Mv[g + 1]));
    }

    /// <summary>Derives direct-mode motion for one 8x8 block (8.4.1.2).</summary>
    /// <param name="b8">The 8x8 block index.</param>
    private void DeriveDirect(int b8)
    {
        SetDirectFlag(b8);
        var list1 = _lists[1][0];
        if (list1 is null || list1.NonExisting || !list1.IsAllocated)
        {
            CodecException.InvalidData("Direct prediction requires RefPicList1[0].");
        }

        var x8 = (b8 & 1) * 2;
        var y8 = (b8 >> 1) * 2;
        var step = _direct8x8Inference ? 2 : 1;
        if (_directSpatial)
        {
            PrepareSpatial();
            for (var y = y8; y < y8 + 2; y += step)
            {
                for (var x = x8; x < x8 + 2; x += step)
                {
                    var col = Colocated(list1, x, y);
                    var colZero = list1.Mark == H264ReferenceMark.ShortTerm && col.Ref == 0
                        && Math.Abs(MvX(col.Mv)) <= 1 && Math.Abs(MvY(col.Mv)) <= 1;
                    var mv0 = _spatialZero || _spatialRef[0] < 0 || (_spatialRef[0] == 0 && colZero) ? 0 : _spatialMv[0];
                    var mv1 = _spatialZero || _spatialRef[1] < 0 || (_spatialRef[1] == 0 && colZero) ? 0 : _spatialMv[1];
                    SetMotion(x, y, step, step, _spatialRef[0], mv0, _spatialRef[1], mv1);
                }
            }

            return;
        }

        for (var y = y8; y < y8 + 2; y += step)
        {
            for (var x = x8; x < x8 + 2; x += step)
            {
                var col = Colocated(list1, x, y);
                var refIdx0 = 0;
                if (col.Ref >= 0)
                {
                    refIdx0 = -1;
                    for (var i = 0; i < NumRefIdxActive[0]; i++)
                    {
                        if (_lists[0][i]?.Id == col.PicId)
                        {
                            refIdx0 = i;
                            break;
                        }
                    }

                    if (refIdx0 < 0)
                    {
                        CodecException.InvalidData("The temporal direct co-located reference is not in RefPicList0.");
                    }
                }

                int mv0, mv1;
                var scale = _temporalScale[refIdx0];
                if (scale == int.MinValue)
                {
                    mv0 = col.Mv;
                    mv1 = 0;
                }
                else
                {
                    var mx0 = ((scale * MvX(col.Mv)) + 128) >> 8;
                    var my0 = ((scale * MvY(col.Mv)) + 128) >> 8;
                    mv0 = PackChecked(mx0, my0);
                    mv1 = PackChecked(mx0 - MvX(col.Mv), my0 - MvY(col.Mv));
                }

                SetMotion(x, y, step, step, refIdx0, mv0, 0, mv1);
            }
        }
    }

    /// <summary>Computes the spatial direct references and predictors of the current macroblock once.</summary>
    private void PrepareSpatial()
    {
        if (_spatialReady)
        {
            return;
        }

        _spatialReady = true;
        for (var list = 0; list < 2; list++)
        {
            var a = Neighbor(list, -1, 0);
            var b = Neighbor(list, 0, -1);
            var c = Neighbor(list, 4, -1);
            if (!c.Available)
            {
                c = Neighbor(list, -1, -1);
            }

            _spatialRef[list] = MinPositive(a.Ref, MinPositive(b.Ref, c.Ref));
        }

        _spatialZero = _spatialRef[0] < 0 && _spatialRef[1] < 0;
        if (_spatialZero)
        {
            _spatialRef[0] = 0;
            _spatialRef[1] = 0;
            _spatialMv[0] = 0;
            _spatialMv[1] = 0;
            return;
        }

        for (var list = 0; list < 2; list++)
        {
            _spatialMv[list] = _spatialRef[list] >= 0 ? PredictMv(list, _spatialRef[list], 0, 0, 4, 4, 0) : 0;
        }
    }

    /// <summary>Returns the co-located motion for a 4x4 block of the current macroblock (8.4.1.2.1).</summary>
    /// <param name="colPic">RefPicList1[0].</param>
    /// <param name="x4">Column within the macroblock.</param>
    /// <param name="y4">Row within the macroblock.</param>
    /// <returns>refIdxCol, mvCol and the referenced picture.</returns>
    private (int Ref, int Mv, long PicId) Colocated(H264Picture colPic, int x4, int y4)
    {
        if (colPic.IntraMb[MbAddr] != 0)
        {
            return (-1, 0, -1);
        }

        if (_direct8x8Inference)
        {
            x4 = (x4 >> 1) * 3;
            y4 = (y4 >> 1) * 3;
        }

        var bx = (MbX * 4) + x4;
        var by = (MbY * 4) + y4;
        var g8 = ((by >> 1) * W8) + (bx >> 1);
        var list = colPic.RefIdx[g8] >= 0 ? 0 : 1;
        var refIdx = colPic.RefIdx[(list * MbCount * 4) + g8];
        var g = ((list * MbCount * 16) + (by * W4) + bx) * 2;
        return (refIdx, Pack(colPic.Mv[g], colPic.Mv[g + 1]), colPic.RefPicId[(list * MbCount * 4) + g8]);
    }

    /// <summary>Stores motion for a rectangle of 4x4 blocks and marks them derived.</summary>
    /// <param name="x4">Column.</param>
    /// <param name="y4">Row.</param>
    /// <param name="w4">Width.</param>
    /// <param name="h4">Height.</param>
    /// <param name="ref0">refIdxL0 or -1.</param>
    /// <param name="mv0">Packed mvL0.</param>
    /// <param name="ref1">refIdxL1 or -1.</param>
    /// <param name="mv1">Packed mvL1.</param>
    private void SetMotion(int x4, int y4, int w4, int h4, int ref0, int mv0, int ref1, int mv1)
    {
        Span<int> refs = [ref0, ref1];
        Span<int> mvs = [ref0 >= 0 ? mv0 : 0, ref1 >= 0 ? mv1 : 0];
        for (var list = 0; list < 2; list++)
        {
            long picId = -1;
            if (refs[list] >= 0)
            {
                var picture = _lists[list][refs[list]];
                if (picture is null || picture.NonExisting || !picture.IsAllocated)
                {
                    CodecException.InvalidData("A partition references a missing reference picture.");
                }

                picId = picture.Id;
            }

            for (var y = y4; y < y4 + h4; y++)
            {
                for (var x = x4; x < x4 + w4; x++)
                {
                    var bx = (MbX * 4) + x;
                    var by = (MbY * 4) + y;
                    var g = ((list * MbCount * 16) + (by * W4) + bx) * 2;
                    _pic.Mv[g] = (short)MvX(mvs[list]);
                    _pic.Mv[g + 1] = (short)MvY(mvs[list]);
                    var g8 = (list * MbCount * 4) + ((by >> 1) * W8) + (bx >> 1);
                    _pic.RefIdx[g8] = (sbyte)refs[list];
                    _pic.RefPicId[g8] = picId;
                    _derivedMask |= 1 << ((y * 4) + x);
                }
            }
        }
    }

    /// <summary>Runs motion compensation for the current macroblock, merging blocks with identical motion.</summary>
    private void PredictInter()
    {
        if (UniformMotion(0, 0, 4))
        {
            PredictBlock(0, 0, 16);
            return;
        }

        for (var b8 = 0; b8 < 4; b8++)
        {
            var x4 = (b8 & 1) * 2;
            var y4 = (b8 >> 1) * 2;
            if (UniformMotion(x4, y4, 2))
            {
                PredictBlock(x4, y4, 8);
                continue;
            }

            for (var k = 0; k < 4; k++)
            {
                PredictBlock(x4 + (k & 1), y4 + (k >> 1), 4);
            }
        }
    }

    /// <summary>Returns whether all 4x4 blocks of a square region share references and vectors.</summary>
    /// <param name="x4">Column.</param>
    /// <param name="y4">Row.</param>
    /// <param name="size">Side in 4x4 blocks.</param>
    /// <returns><see langword="true"/> when uniform.</returns>
    private bool UniformMotion(int x4, int y4, int size)
    {
        var bx0 = (MbX * 4) + x4;
        var by0 = (MbY * 4) + y4;
        for (var list = 0; list < 2; list++)
        {
            var refBase = list * MbCount * 4;
            var first = _pic.RefIdx[refBase + ((by0 >> 1) * W8) + (bx0 >> 1)];
            var mvBase = list * MbCount * 16;
            var gFirst = (mvBase + (by0 * W4) + bx0) * 2;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var bx = bx0 + x;
                    var by = by0 + y;
                    if (_pic.RefIdx[refBase + ((by >> 1) * W8) + (bx >> 1)] != first)
                    {
                        return false;
                    }

                    var g = (mvBase + (by * W4) + bx) * 2;
                    if (_pic.Mv[g] != _pic.Mv[gFirst] || _pic.Mv[g + 1] != _pic.Mv[gFirst + 1])
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>Predicts one square block from its stored motion into the picture (8.4.2).</summary>
    /// <param name="x4">Column within the macroblock.</param>
    /// <param name="y4">Row within the macroblock.</param>
    /// <param name="size">Side in luma samples: 4, 8 or 16.</param>
    private void PredictBlock(int x4, int y4, int size)
    {
        var bx = (MbX * 4) + x4;
        var by = (MbY * 4) + y4;
        var g8 = ((by >> 1) * W8) + (bx >> 1);
        var ref0 = _pic.RefIdx[g8];
        var ref1 = _pic.RefIdx[(MbCount * 4) + g8];
        var px = bx * 4;
        var py = by * 4;
        var stride = _pic.Width;
        var cstride = stride / 2;
        var csize = size / 2;
        Span<byte> l0 = stackalloc byte[256];
        Span<byte> l1 = stackalloc byte[256];
        Span<byte> c0 = stackalloc byte[128];
        Span<byte> c1 = stackalloc byte[128];
        var lumaCount = size * size;
        var chromaCount = csize * csize;
        if (ref0 >= 0)
        {
            Fetch(_lists[0][ref0]!, 0, bx, by, px, py, size, l0, c0);
        }

        if (ref1 >= 0)
        {
            Fetch(_lists[1][ref1]!, 1, bx, by, px, py, size, l1, c1);
        }

        if (ref0 < 0 && ref1 < 0)
        {
            CodecException.InvalidData("An inter partition predicts from no list.");
        }

        for (var plane = 0; plane < 3; plane++)
        {
            var count = plane == 0 ? lumaCount : chromaCount;
            var width = plane == 0 ? size : csize;
            var p0 = plane == 0 ? l0 : c0.Slice((plane - 1) * 64);
            var p1 = plane == 0 ? l1 : c1.Slice((plane - 1) * 64);
            var target = plane == 0 ? _pic.Luma : plane == 1 ? _pic.Cb : _pic.Cr;
            var targetStride = plane == 0 ? stride : cstride;
            var origin = plane == 0 ? (py * stride) + px : ((py / 2) * cstride) + (px / 2);
            for (var i = 0; i < count; i++)
            {
                var y = i / width;
                var x = i - (y * width);
                int value;
                if (ref0 >= 0 && ref1 >= 0)
                {
                    value = _weightMode switch
                    {
                        0 => (p0[i] + p1[i] + 1) >> 1,
                        1 => Bi(p0[i], p1[i], ref0, ref1, plane),
                        _ => ((p0[i] * _implicitW0[(ref0 * 32) + ref1]) + (p1[i] * (64 - _implicitW0[(ref0 * 32) + ref1])) + 32) >> 6,
                    };
                }
                else
                {
                    var list = ref0 >= 0 ? 0 : 1;
                    var sample = list == 0 ? p0[i] : p1[i];
                    value = _weightMode == 1 ? Uni(sample, list, list == 0 ? ref0 : ref1, plane) : sample;
                }

                target[origin + (y * targetStride) + x] = (byte)Math.Clamp(value, 0, 255);
            }
        }
    }

    /// <summary>Interpolates one list's luma and chroma prediction for a block.</summary>
    /// <param name="reference">The reference picture.</param>
    /// <param name="list">The list.</param>
    /// <param name="bx">The block's absolute 4x4 column.</param>
    /// <param name="by">The block's absolute 4x4 row.</param>
    /// <param name="px">Luma x.</param>
    /// <param name="py">Luma y.</param>
    /// <param name="size">Luma block side.</param>
    /// <param name="luma">Receives luma samples.</param>
    /// <param name="chroma">Receives Cb then Cr samples, 64 apart.</param>
    private void Fetch(H264Picture reference, int list, int bx, int by, int px, int py, int size, Span<byte> luma, Span<byte> chroma)
    {
        var g = ((list * MbCount * 16) + (by * W4) + bx) * 2;
        int mx = _pic.Mv[g];
        int my = _pic.Mv[g + 1];
        var width = reference.Width;
        var height = reference.Height;
        H264InterPrediction.Luma(reference.Luma, width, height, px + (mx >> 2), py + (my >> 2), mx & 3, my & 3, size, luma);
        var csize = size / 2;
        var cx = (px / 2) + (mx >> 3);
        var cy = (py / 2) + (my >> 3);
        H264InterPrediction.Chroma(reference.Cb, width / 2, height / 2, cx, cy, mx & 7, my & 7, csize, chroma);
        H264InterPrediction.Chroma(reference.Cr, width / 2, height / 2, cx, cy, mx & 7, my & 7, csize, chroma[64..]);
    }

    /// <summary>Explicit weighted prediction from one list (8-449).</summary>
    /// <param name="sample">The prediction sample.</param>
    /// <param name="list">The list.</param>
    /// <param name="refIdx">The reference index.</param>
    /// <param name="plane">0 Y, 1 Cb, 2 Cr.</param>
    /// <returns>The weighted sample, unclipped.</returns>
    private int Uni(int sample, int list, int refIdx, int plane)
    {
        var index = (((list * 32) + refIdx) * 3) + plane;
        var logWd = plane == 0 ? _lumaLog2Denom : _chromaLog2Denom;
        var w = _weights[index];
        var o = _offsets[index];
        return logWd >= 1 ? (((sample * w) + (1 << (logWd - 1))) >> logWd) + o : (sample * w) + o;
    }

    /// <summary>Explicit weighted bi-prediction (8-451).</summary>
    /// <param name="s0">The list 0 sample.</param>
    /// <param name="s1">The list 1 sample.</param>
    /// <param name="ref0">refIdxL0.</param>
    /// <param name="ref1">refIdxL1.</param>
    /// <param name="plane">0 Y, 1 Cb, 2 Cr.</param>
    /// <returns>The weighted sample, unclipped.</returns>
    private int Bi(int s0, int s1, int ref0, int ref1, int plane)
    {
        var i0 = (ref0 * 3) + plane;
        var i1 = ((32 + ref1) * 3) + plane;
        var logWd = plane == 0 ? _lumaLog2Denom : _chromaLog2Denom;
        return (((s0 * _weights[i0]) + (s1 * _weights[i1]) + (1 << logWd)) >> (logWd + 1)) + ((_offsets[i0] + _offsets[i1] + 1) >> 1);
    }

    /// <summary>MinPositive (8-184).</summary>
    /// <param name="x">The first value.</param>
    /// <param name="y">The second value.</param>
    /// <returns>The smaller non-negative value, or the larger value when one is negative.</returns>
    private static int MinPositive(int x, int y) => x >= 0 && y >= 0 ? Math.Min(x, y) : Math.Max(x, y);

    /// <summary>The median of three values.</summary>
    /// <param name="a">The first.</param>
    /// <param name="b">The second.</param>
    /// <param name="c">The third.</param>
    /// <returns>The median.</returns>
    private static int Median(int a, int b, int c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));

    /// <summary>Packs a motion vector into an int.</summary>
    /// <param name="x">The horizontal component (16-bit range).</param>
    /// <param name="y">The vertical component.</param>
    /// <returns>The packed vector.</returns>
    internal static int Pack(int x, int y) => (int)((ushort)(short)x | ((uint)(ushort)(short)y << 16));

    /// <summary>Packs a derived motion vector, rejecting components outside the 16-bit range a conforming stream keeps them in (8.4.1).</summary>
    /// <param name="x">The horizontal component.</param>
    /// <param name="y">The vertical component.</param>
    /// <returns>The packed vector.</returns>
    /// <exception cref="CodecException">A component is out of range.</exception>
    private static int PackChecked(int x, int y)
    {
        if (x is < short.MinValue or > short.MaxValue || y is < short.MinValue or > short.MaxValue)
        {
            CodecException.InvalidData("A motion vector is outside the range of a conforming stream.");
        }

        return Pack(x, y);
    }

    /// <summary>Returns the horizontal component of a packed vector.</summary>
    /// <param name="mv">The packed vector.</param>
    /// <returns>The component.</returns>
    internal static int MvX(int mv) => (short)(mv & 0xFFFF);

    /// <summary>Returns the vertical component of a packed vector.</summary>
    /// <param name="mv">The packed vector.</param>
    /// <returns>The component.</returns>
    internal static int MvY(int mv) => (short)(mv >> 16);

    /// <summary>A neighbouring partition's motion for one list.</summary>
    /// <param name="Available">Whether the partition is available.</param>
    /// <param name="Ref">The reference index, -1 when unavailable, intra, or not predicting from the list.</param>
    /// <param name="Mv">The packed vector.</param>
    private readonly record struct NeighborMotion(bool Available, int Ref, int Mv)
    {
        /// <summary>Gets the default (unavailable) neighbour.</summary>
        public static NeighborMotion None => new(false, -1, 0);
    }
}
