// P-slice decisions: motion vector prediction (8.4.1.3) and the P_Skip derivation (8.4.1.1) exactly as a decoder
// performs them, integer motion search by a shrinking diamond from the predictor, the zero vector and the 16x16
// result, quarter-sample refinement by SATD, and the choice between P_Skip, P_L0_16x16, P_8x8 and intra. Every
// candidate is bounded: vectors stay within the level's vertical range and at most 24 samples outside the picture.

using System.Buffers.Binary;
using System.Runtime.Intrinsics;

namespace RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

/// <summary>Inter mode decision and motion estimation.</summary>
internal sealed partial class H264PictureEncoder
{
    /// <summary>Whether the chosen inter macroblock uses four 8x8 partitions.</summary>
    private bool _split;

    /// <summary>The reference index of the chosen inter macroblock.</summary>
    private int _refIdx;

    /// <summary>Scratch for one predicted block.</summary>
    private readonly byte[] _block = new byte[256];

    /// <summary>A neighbouring partition's motion for prediction.</summary>
    /// <param name="Available">Whether the partition is available.</param>
    /// <param name="Ref">Its reference index, -1 for intra or unavailable.</param>
    /// <param name="X">Horizontal motion.</param>
    /// <param name="Y">Vertical motion.</param>
    private readonly record struct Motion(bool Available, int Ref, int X, int Y);

    /// <summary>Chooses the coding of a P-slice macroblock.</summary>
    /// <param name="cost">Receives the cost of the choice.</param>
    /// <returns>The choice.</returns>
    private MbChoice ChooseInter(out int cost)
    {
        var ref0 = _refs.Length > 0 ? _refs[0] : null;
        if (ref0 is not null)
        {
            var (smx, smy) = SkipPredictor();
            PredictMacroblock(ref0, smx, smy, _predY, _predC);
            if (_header.ForceSkip || ResidualVanishes())
            {
                SetMotion(0, 0, 4, 0, smx, smy, ref0.Id);
                _pic.Kind[_mbAddr] = H264MbKind.Skip;
                _pic.Qp[_mbAddr] = (byte)_qp;
                _pic.TotalCoeff.AsSpan(_mbAddr * 16, 16).Clear();
                _pic.ChromaCoeff.AsSpan(_mbAddr * 8, 8).Clear();
                _pic.IntraModes.AsSpan(_mbAddr * 16, 16).Fill(-1);
                StoreSamples(_predY, _predC);
                cost = LumaSatd(_predY);
                return MbChoice.Skip;
            }
        }

        var best = int.MaxValue;
        int bestRef = -1, bmx = 0, bmy = 0, bpx = 0, bpy = 0;
        for (var r = 0; r < _refs.Length; r++)
        {
            if (_refs[r] is not { } reference)
            {
                continue;
            }

            _doneBlocks = 0;
            var (pmx, pmy) = Predictor(0, 0, 4, r);
            var c = Search(reference, 0, 0, 16, pmx, pmy, pmx, pmy, out var mx, out var my) + BitCost(1 + RefBits(r));
            if (c < best)
            {
                (best, bestRef, bmx, bmy, bpx, bpy) = (c, r, mx, my, pmx, pmy);
            }
        }

        if (bestRef < 0)
        {
            _doneBlocks = 0;
            return ChooseIntra(int.MaxValue, out cost);
        }

        var reference0 = _refs[bestRef]!;
        var split = 0;
        Span<int> splitMotion = stackalloc int[8];
        Span<int> splitMvp = stackalloc int[8];
        _doneBlocks = 0;
        for (var p = 0; p < 4; p++)
        {
            int bx = (p & 1) * 2, by = (p >> 1) * 2;
            var (pmx, pmy) = Predictor(bx, by, 2, bestRef);
            split += Search(reference0, bx, by, 8, pmx, pmy, bmx, bmy, out var mx, out var my) + BitCost(1 + RefBits(bestRef));
            (splitMotion[2 * p], splitMotion[(2 * p) + 1], splitMvp[2 * p], splitMvp[(2 * p) + 1]) = (mx, my, pmx, pmy);
            SetMotion(bx, by, 2, bestRef, mx, my, reference0.Id);
        }

        split += BitCost(4);
        _split = split < best;
        _refIdx = bestRef;
        var interCost = Math.Min(split, best);
        if (_split)
        {
            splitMotion.CopyTo(_partition);
            splitMvp.CopyTo(_partitionMvp);
        }
        else
        {
            (_partition[0], _partition[1], _partitionMvp[0], _partitionMvp[1]) = (bmx, bmy, bpx, bpy);
        }

        var choice = ChooseIntra(interCost, out cost);
        if (choice != MbChoice.Inter)
        {
            return choice;
        }

        // Re-establish the chosen motion (the 8x8 trial overwrote it) and its prediction.
        for (var p = 0; p < (_split ? 4 : 1); p++)
        {
            int bx = (p & 1) * 2, by = (p >> 1) * 2;
            int size = _split ? 2 : 4, mx = _partition[2 * p], my = _partition[(2 * p) + 1];
            SetMotion(_split ? bx : 0, _split ? by : 0, size, bestRef, mx, my, reference0.Id);
            PredictPartition(reference0, _split ? bx : 0, _split ? by : 0, size * 4, mx, my, _bestPredY, _predC);
        }

        _pic.IntraModes.AsSpan(_mbAddr * 16, 16).Fill(-1);
        return MbChoice.Inter;
    }

    /// <summary>Writes the macroblock type and prediction syntax of an inter macroblock.</summary>
    /// <param name="w">The writer.</param>
    private void WriteInterHeader(H264BitWriter w)
    {
        var count = _split ? 4 : 1;
        w.WriteUe(_split ? 3 : 0);
        if (_split)
        {
            for (var p = 0; p < 4; p++)
            {
                w.WriteUe(0);
            }
        }

        for (var p = 0; p < count; p++)
        {
            WriteRef(w, _refIdx);
        }

        for (var p = 0; p < count; p++)
        {
            w.WriteSe(_partition[2 * p] - _partitionMvp[2 * p]);
            w.WriteSe(_partition[(2 * p) + 1] - _partitionMvp[(2 * p) + 1]);
        }
    }

    /// <summary>Writes ref_idx_l0 as te(v).</summary>
    /// <param name="w">The writer.</param>
    /// <param name="refIdx">The index.</param>
    private void WriteRef(H264BitWriter w, int refIdx)
    {
        var range = _header.ActiveReferences - 1;
        if (range == 1)
        {
            w.WriteBit(refIdx == 0);
        }
        else if (range > 1)
        {
            w.WriteUe(refIdx);
        }
    }

    /// <summary>Returns the bits of ref_idx_l0.</summary>
    /// <param name="refIdx">The index.</param>
    /// <returns>The bits.</returns>
    private int RefBits(int refIdx)
    {
        var range = _header.ActiveReferences - 1;
        return range == 0 ? 0 : range == 1 ? 1 : H264BitWriter.UeBits(refIdx);
    }

    /// <summary>Records the motion of a square group of 4x4 blocks and marks them decided.</summary>
    /// <param name="bx">First block column.</param>
    /// <param name="by">First block row.</param>
    /// <param name="size">The side in blocks.</param>
    /// <param name="refIdx">The reference index.</param>
    /// <param name="mx">Horizontal motion.</param>
    /// <param name="my">Vertical motion.</param>
    /// <param name="refId">The reference picture's identity.</param>
    private void SetMotion(int bx, int by, int size, int refIdx, int mx, int my, long refId)
    {
        for (var j = by; j < by + size; j++)
        {
            for (var i = bx; i < bx + size; i++)
            {
                var index = _pic.Block(_mbx, _mby, i, j);
                _pic.Mv[2 * index] = (short)mx;
                _pic.Mv[(2 * index) + 1] = (short)my;
                _pic.RefIdx[index] = (sbyte)refIdx;
                _pic.RefId[index] = refId;
                _doneBlocks |= 1 << ((j * 4) + i);
            }
        }
    }

    /// <summary>Returns a neighbouring block's motion (6.4.11.7) for a position relative to the macroblock.</summary>
    /// <param name="bx">Block column, -1 to 4.</param>
    /// <param name="by">Block row, -1 to 3.</param>
    /// <returns>The motion.</returns>
    private Motion Neighbour(int bx, int by)
    {
        if (bx is >= 0 and < 4 && by is >= 0 and < 4)
        {
            if ((_doneBlocks & (1 << ((by * 4) + bx))) == 0)
            {
                return default;
            }

            var inside = _pic.Block(_mbx, _mby, bx, by);
            return new Motion(true, _pic.RefIdx[inside], _pic.Mv[2 * inside], _pic.Mv[(2 * inside) + 1]);
        }

        int mbx = _mbx + (bx < 0 ? -1 : bx >= 4 ? 1 : 0), mby = _mby + (by < 0 ? -1 : 0);
        if (!MbAvailable(mbx, mby))
        {
            return default;
        }

        var index = _pic.Block(mbx, mby, (bx + 4) & 3, (by + 4) & 3);
        var refIdx = _pic.RefIdx[index];
        return refIdx < 0 ? new Motion(true, -1, 0, 0) : new Motion(true, refIdx, _pic.Mv[2 * index], _pic.Mv[(2 * index) + 1]);
    }

    /// <summary>Derives the luma motion vector predictor of a square partition (8.4.1.3).</summary>
    /// <param name="bx">First block column.</param>
    /// <param name="by">First block row.</param>
    /// <param name="width">The width in blocks.</param>
    /// <param name="refIdx">The partition's reference index.</param>
    /// <returns>The predictor.</returns>
    private (int X, int Y) Predictor(int bx, int by, int width, int refIdx)
    {
        var a = Neighbour(bx - 1, by);
        var b = Neighbour(bx, by - 1);
        var c = Neighbour(bx + width, by - 1);
        if (!c.Available)
        {
            c = Neighbour(bx - 1, by - 1);
        }

        if (!b.Available && !c.Available && a.Available)
        {
            b = a;
            c = a;
        }

        a = a.Available ? a : new Motion(false, -1, 0, 0);
        b = b.Available ? b : new Motion(false, -1, 0, 0);
        c = c.Available ? c : new Motion(false, -1, 0, 0);
        var matches = (a.Ref == refIdx ? 1 : 0) + (b.Ref == refIdx ? 1 : 0) + (c.Ref == refIdx ? 1 : 0);
        if (matches == 1)
        {
            var m = a.Ref == refIdx ? a : b.Ref == refIdx ? b : c;
            return (m.X, m.Y);
        }

        return (Median(a.X, b.X, c.X), Median(a.Y, b.Y, c.Y));
    }

    /// <summary>Derives the P_Skip motion vector (8.4.1.1).</summary>
    /// <returns>The vector.</returns>
    private (int X, int Y) SkipPredictor()
    {
        _doneBlocks = 0;
        var a = Neighbour(-1, 0);
        var b = Neighbour(0, -1);
        if (!a.Available || !b.Available || (a.Ref == 0 && a.X == 0 && a.Y == 0) || (b.Ref == 0 && b.X == 0 && b.Y == 0))
        {
            return (0, 0);
        }

        return Predictor(0, 0, 4, 0);
    }

    /// <summary>Returns the median of three values.</summary>
    /// <param name="a">A.</param>
    /// <param name="b">B.</param>
    /// <param name="c">C.</param>
    /// <returns>The median.</returns>
    private static int Median(int a, int b, int c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));

    /// <summary>Searches the motion of a square block and returns its SATD plus vector cost.</summary>
    /// <param name="reference">The reference picture.</param>
    /// <param name="bx">First block column in the macroblock.</param>
    /// <param name="by">First block row.</param>
    /// <param name="size">The side in samples, 8 or 16.</param>
    /// <param name="pmx">The predictor x.</param>
    /// <param name="pmy">The predictor y.</param>
    /// <param name="hintX">An extra candidate x.</param>
    /// <param name="hintY">An extra candidate y.</param>
    /// <param name="mx">Receives the vector x in quarter samples.</param>
    /// <param name="my">Receives the vector y.</param>
    /// <returns>The cost.</returns>
    private int Search(H264EncoderPicture reference, int bx, int by, int size, int pmx, int pmy, int hintX, int hintY, out int mx, out int my)
    {
        int x0 = (_mbx * 16) + (bx * 4), y0 = (_mby * 16) + (by * 4);
        int minX = -x0 - 24, maxX = reference.Width - x0 + 8;
        var vertical = (_layout.MaxVerticalMv / 4) - 2;
        int minY = Math.Max(-y0 - 24, -vertical), maxY = Math.Min(reference.Height - y0 + 8, vertical);
        int bestX = Math.Clamp((pmx + 2) >> 2, minX, maxX), bestY = Math.Clamp((pmy + 2) >> 2, minY, maxY);
        var bestCost = SadCost(reference, x0, y0, bx, by, size, bestX, bestY, pmx, pmy);
        Span<int> candidates = [0, 0, (hintX + 2) >> 2, (hintY + 2) >> 2];
        for (var i = 0; i < candidates.Length; i += 2)
        {
            int cx = Math.Clamp(candidates[i], minX, maxX), cy = Math.Clamp(candidates[i + 1], minY, maxY);
            var c = SadCost(reference, x0, y0, bx, by, size, cx, cy, pmx, pmy);
            if (c < bestCost)
            {
                (bestCost, bestX, bestY) = (c, cx, cy);
            }
        }

        int originX = bestX, originY = bestY;
        for (var step = Math.Max(1, _searchRange / 2); step >= 1; step /= 2)
        {
            for (var iteration = 0; iteration < 4; iteration++)
            {
                var moved = false;
                for (var d = 0; d < (step == 1 ? 8 : 4); d++)
                {
                    int dx = d switch { 0 => step, 1 => -step, 4 or 6 => 1, 5 or 7 => -1, _ => 0 };
                    int dy = d switch { 2 => step, 3 => -step, 4 or 5 => 1, 6 or 7 => -1, _ => 0 };
                    int cx = bestX + dx, cy = bestY + dy;
                    if (cx < minX || cx > maxX || cy < minY || cy > maxY || Math.Abs(cx - originX) > _searchRange || Math.Abs(cy - originY) > _searchRange)
                    {
                        continue;
                    }

                    var c = SadCost(reference, x0, y0, bx, by, size, cx, cy, pmx, pmy);
                    if (c < bestCost)
                    {
                        (bestCost, bestX, bestY) = (c, cx, cy);
                        moved = true;
                    }
                }

                if (!moved)
                {
                    break;
                }
            }
        }

        // Quarter-sample refinement: half-sample neighbours, then quarter-sample neighbours, by SATD.
        mx = bestX * 4;
        my = bestY * 4;
        var best = SatdCost(reference, x0, y0, bx, by, size, mx, my, pmx, pmy);
        for (var delta = 2; delta >= 1; delta--)
        {
            int cx0 = mx, cy0 = my;
            for (var d = 0; d < 8; d++)
            {
                int dx = d is 0 or 4 or 6 ? delta : d is 1 or 5 or 7 ? -delta : 0;
                int dy = d is 2 or 4 or 5 ? delta : d is 3 or 6 or 7 ? -delta : 0;
                int cx = cx0 + dx, cy = cy0 + dy;
                if (cx < minX * 4 || cx > maxX * 4 || cy < minY * 4 || cy > maxY * 4)
                {
                    continue;
                }

                var c = SatdCost(reference, x0, y0, bx, by, size, cx, cy, pmx, pmy);
                if (c < best)
                {
                    (best, mx, my) = (c, cx, cy);
                }
            }
        }

        return best;
    }

    /// <summary>Returns the SAD of an integer-position candidate plus its vector cost.</summary>
    /// <param name="reference">The reference.</param>
    /// <param name="x0">The block's picture x.</param>
    /// <param name="y0">The block's picture y.</param>
    /// <param name="bx">The block column in the macroblock.</param>
    /// <param name="by">The block row.</param>
    /// <param name="size">The side.</param>
    /// <param name="ix">The integer vector x.</param>
    /// <param name="iy">The integer vector y.</param>
    /// <param name="pmx">The predictor x.</param>
    /// <param name="pmy">The predictor y.</param>
    /// <returns>The cost.</returns>
    private int SadCost(H264EncoderPicture reference, int x0, int y0, int bx, int by, int size, int ix, int iy, int pmx, int pmy)
    {
        int width = reference.Width, height = reference.Height;
        var plane = reference.Luma;
        int px = x0 + ix, py = y0 + iy;
        int sad;
        if (px >= 0 && py >= 0 && px + size <= width && py + size <= height)
        {
            sad = Sad(_srcY.AsSpan((by * 64) + (bx * 4)), plane.AsSpan((py * width) + px), width, size);
        }
        else
        {
            sad = 0;
            for (var j = 0; j < size; j++)
            {
                var row = Math.Clamp(py + j, 0, height - 1) * width;
                var src = (((by * 4) + j) * 16) + (bx * 4);
                for (var i = 0; i < size; i++)
                {
                    sad += Math.Abs(_srcY[src + i] - plane[row + Math.Clamp(px + i, 0, width - 1)]);
                }
            }
        }

        return sad + BitCost(H264BitWriter.SeBits((ix * 4) - pmx) + H264BitWriter.SeBits((iy * 4) - pmy));
    }

    /// <summary>Returns the sum of absolute differences of a block that lies inside the reference.</summary>
    /// <param name="source">The source block's first sample, stride 16.</param>
    /// <param name="reference">The reference block's first sample.</param>
    /// <param name="stride">The reference stride.</param>
    /// <param name="size">The side, 8 or 16.</param>
    /// <returns>The SAD.</returns>
    private static int Sad(ReadOnlySpan<byte> source, ReadOnlySpan<byte> reference, int stride, int size)
    {
        // Each 16-bit lane gains at most 2 * 255 per row, so 16 rows cannot overflow.
        var sum = Vector128<ushort>.Zero;
        for (var j = 0; j < size; j++)
        {
            Vector128<byte> a = Row(source[(j * 16)..], size), b = Row(reference[(j * stride)..], size);
            var (low, high) = Vector128.Widen(Vector128.Max(a, b) - Vector128.Min(a, b));
            sum += low + high;
        }

        var (sumLow, sumHigh) = Vector128.Widen(sum);
        return (int)Vector128.Sum(sumLow + sumHigh);

        static Vector128<byte> Row(ReadOnlySpan<byte> row, int size) =>
            size == 16 ? Vector128.Create(row) : Vector128.CreateScalar(BinaryPrimitives.ReadUInt64LittleEndian(row)).AsByte();
    }

    /// <summary>Returns the SATD of a quarter-sample candidate plus its vector cost.</summary>
    /// <param name="reference">The reference.</param>
    /// <param name="x0">The block's picture x.</param>
    /// <param name="y0">The block's picture y.</param>
    /// <param name="bx">The block column in the macroblock.</param>
    /// <param name="by">The block row.</param>
    /// <param name="size">The side.</param>
    /// <param name="mx">The vector x.</param>
    /// <param name="my">The vector y.</param>
    /// <param name="pmx">The predictor x.</param>
    /// <param name="pmy">The predictor y.</param>
    /// <returns>The cost.</returns>
    private int SatdCost(H264EncoderPicture reference, int x0, int y0, int bx, int by, int size, int mx, int my, int pmx, int pmy)
    {
        var block = _block.AsSpan(0, size * size);
        H264InterPrediction.Luma(reference.Luma, reference.Width, reference.Height, x0 + (mx >> 2), y0 + (my >> 2), mx & 3, my & 3, size, block);
        Span<int> diff = stackalloc int[16];
        var satd = 0;
        for (var j0 = 0; j0 < size; j0 += 4)
        {
            for (var i0 = 0; i0 < size; i0 += 4)
            {
                for (var j = 0; j < 4; j++)
                {
                    for (var i = 0; i < 4; i++)
                    {
                        diff[(j * 4) + i] = _srcY[(((by * 4) + j0 + j) * 16) + (bx * 4) + i0 + i] - block[((j0 + j) * size) + i0 + i];
                    }
                }

                satd += H264EncoderTables.Satd4x4(diff);
            }
        }

        return satd + BitCost(H264BitWriter.SeBits(mx - pmx) + H264BitWriter.SeBits(my - pmy));
    }

    /// <summary>Predicts the whole macroblock from one vector.</summary>
    /// <param name="reference">The reference.</param>
    /// <param name="mx">The vector x.</param>
    /// <param name="my">The vector y.</param>
    /// <param name="luma">Receives 256 luma samples.</param>
    /// <param name="chroma">Receives 64 Cb then 64 Cr samples.</param>
    private void PredictMacroblock(H264EncoderPicture reference, int mx, int my, byte[] luma, byte[] chroma) =>
        PredictPartition(reference, 0, 0, 16, mx, my, luma, chroma);

    /// <summary>Predicts a square partition into macroblock-sized buffers.</summary>
    /// <param name="reference">The reference.</param>
    /// <param name="bx">First block column.</param>
    /// <param name="by">First block row.</param>
    /// <param name="size">The luma side, 8 or 16.</param>
    /// <param name="mx">The vector x.</param>
    /// <param name="my">The vector y.</param>
    /// <param name="luma">The macroblock luma buffer, stride 16.</param>
    /// <param name="chroma">The macroblock chroma buffer: Cb then Cr, stride 8.</param>
    private void PredictPartition(H264EncoderPicture reference, int bx, int by, int size, int mx, int my, byte[] luma, byte[] chroma)
    {
        int x0 = (_mbx * 16) + (bx * 4), y0 = (_mby * 16) + (by * 4);
        var block = _block.AsSpan(0, size * size);
        H264InterPrediction.Luma(reference.Luma, reference.Width, reference.Height, x0 + (mx >> 2), y0 + (my >> 2), mx & 3, my & 3, size, block);
        for (var j = 0; j < size; j++)
        {
            block.Slice(j * size, size).CopyTo(luma.AsSpan((((by * 4) + j) * 16) + (bx * 4), size));
        }

        var cs = size / 2;
        int cx = x0 / 2, cy = y0 / 2;
        for (var c = 0; c < 2; c++)
        {
            var chromaBlock = _block.AsSpan(0, cs * cs);
            H264InterPrediction.Chroma(c == 0 ? reference.Cb : reference.Cr, reference.Width / 2, reference.Height / 2, cx + (mx >> 3), cy + (my >> 3), mx & 7, my & 7, cs, chromaBlock);
            for (var j = 0; j < cs; j++)
            {
                chromaBlock.Slice(j * cs, cs).CopyTo(chroma.AsSpan((c * 64) + (((by * 2) + j) * 8) + (bx * 2), cs));
            }
        }
    }

    /// <summary>Returns the luma SATD of the source against a macroblock prediction.</summary>
    /// <param name="prediction">256 samples.</param>
    /// <returns>SATD.</returns>
    private int LumaSatd(byte[] prediction)
    {
        Span<int> diff = stackalloc int[16];
        var satd = 0;
        for (var b = 0; b < 16; b++)
        {
            int bx = b & 3, by = b >> 2;
            for (var j = 0; j < 4; j++)
            {
                for (var i = 0; i < 4; i++)
                {
                    var k = (((by * 4) + j) * 16) + (bx * 4) + i;
                    diff[(j * 4) + i] = _srcY[k] - prediction[k];
                }
            }

            satd += H264EncoderTables.Satd4x4(diff);
        }

        return satd;
    }
}
