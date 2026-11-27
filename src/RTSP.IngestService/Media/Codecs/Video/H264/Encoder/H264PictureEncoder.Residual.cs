// Intra mode decision, transform and quantisation, reconstruction and the macroblock_layer() syntax. Prediction
// uses the decoder's spec implementations (H264IntraPrediction, H264InterPrediction, H264Transform) so encoder and
// decoder reconstruct with the same arithmetic; the forward transform, quantisation and dequantisation are the
// encoder's own. Quantisation uses a dead zone of 1/3 (intra) or 1/6 (inter) of a step; an inter 8x8 luma block or
// the chroma AC of an inter macroblock whose only levels are at most two isolated +-1 values is zeroed, because
// coding such blocks costs more bits than the distortion they remove.

namespace RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

/// <summary>Residual coding and syntax writing.</summary>
internal sealed partial class H264PictureEncoder
{
    /// <summary>The chosen intra chroma prediction mode.</summary>
    private int _chromaMode;

    /// <summary>The chosen Intra16x16 prediction mode.</summary>
    private int _intra16Mode;

    /// <summary>coded_block_pattern of the macroblock being written.</summary>
    private int _cbp;

    /// <summary>Chooses the intra coding and reports its cost.</summary>
    /// <param name="interCost">The best inter cost.</param>
    /// <param name="cost">Receives the cost of the choice.</param>
    /// <returns>The choice; <see cref="MbChoice.Inter"/> when the inter cost is lower.</returns>
    private MbChoice ChooseIntra(int interCost, out int cost)
    {
        var stride = _pic.Width;
        var origin = (_mby * 16 * stride) + (_mbx * 16);
        bool left = MbAvailable(_mbx - 1, _mby), top = MbAvailable(_mbx, _mby - 1), topLeft = MbAvailable(_mbx - 1, _mby - 1);
        ChooseChromaMode(left, top, topLeft);
        Span<int> diff = stackalloc int[16];
        var best16 = int.MaxValue;
        for (var mode = 0; mode < 4; mode++)
        {
            if (!H264IntraPrediction.Predict16x16(_pic.Luma, stride, origin, mode, left, top, topLeft))
            {
                continue;
            }

            var satd = 0;
            for (var b = 0; b < 16; b++)
            {
                satd += BlockSatd(_pic.Luma, origin, stride, b & 3, b >> 2, diff);
            }

            var c = satd + BitCost(mode == 2 ? 3 : 5);
            if (c < best16)
            {
                best16 = c;
                _intra16Mode = mode;
            }
        }

        best16 += BitCost(4);
        var try4x4 = _header.IsIntra || Math.Min(best16, interCost) > 1024 + BitCost(24);
        if (try4x4)
        {
            var cost4 = TrialIntra4x4(left, top);
            if (cost4 < best16 && cost4 < interCost)
            {
                cost = cost4;
                return MbChoice.Intra4x4;
            }

        }

        cost = Math.Min(best16, interCost);
        return interCost <= best16 ? MbChoice.Inter : MbChoice.Intra16x16;
    }

    /// <summary>Chooses the intra chroma prediction mode by SATD.</summary>
    /// <param name="left">Left availability.</param>
    /// <param name="top">Top availability.</param>
    /// <param name="topLeft">Top-left availability.</param>
    private void ChooseChromaMode(bool left, bool top, bool topLeft)
    {
        var cs = _pic.Width / 2;
        var origin = (_mby * 8 * cs) + (_mbx * 8);
        var best = int.MaxValue;
        Span<int> diff = stackalloc int[16];
        for (var mode = 0; mode < 4; mode++)
        {
            if (!H264IntraPrediction.PredictChroma(_pic.Cb, cs, origin, mode, left, top, topLeft)
                || !H264IntraPrediction.PredictChroma(_pic.Cr, cs, origin, mode, left, top, topLeft))
            {
                continue;
            }

            var satd = 0;
            for (var c = 0; c < 2; c++)
            {
                for (var b = 0; b < 4; b++)
                {
                    satd += ChromaSatd(c == 0 ? _pic.Cb : _pic.Cr, origin, cs, c, b & 1, b >> 1, diff);
                }
            }

            var cost = satd + BitCost(H264BitWriter.UeBits(mode));
            if (cost < best)
            {
                best = cost;
                _chromaMode = mode;
            }
        }
    }

    /// <summary>Codes the luma as Intra_4x4, choosing each block's mode, and returns the cost.</summary>
    /// <param name="left">Left macroblock availability.</param>
    /// <param name="top">Top macroblock availability.</param>
    /// <returns>The summed SATD and mode bits.</returns>
    private int TrialIntra4x4(bool left, bool top)
    {
        var stride = _pic.Width;
        var origin = (_mby * 16 * stride) + (_mbx * 16);
        var topRightMb = MbAvailable(_mbx + 1, _mby - 1);
        var topLeftMb = MbAvailable(_mbx - 1, _mby - 1);
        Span<int> diff = stackalloc int[16];
        var cost = BitCost(8);
        for (var blk = 0; blk < 16; blk++)
        {
            var r = BlkRaster[blk];
            int bx = r & 3, by = r >> 2;
            var offset = origin + (by * 4 * stride) + (bx * 4);
            var hasLeft = bx > 0 || left;
            var hasTop = by > 0 || top;
            var hasTopLeft = (bx > 0 && by > 0) || (bx == 0 && by > 0 && left) || (by == 0 && bx > 0 && top) || (bx == 0 && by == 0 && topLeftMb);
            var hasTopRight = TopRightAvailable(bx, by, top, topRightMb);
            var predicted = PredictedIntraMode(bx, by);
            var bestMode = 2;
            var bestCost = int.MaxValue;
            for (var mode = 0; mode < 9; mode++)
            {
                if (!H264IntraPrediction.Predict4x4(_pic.Luma, stride, offset, mode, hasLeft, hasTop, hasTopRight, hasTopLeft))
                {
                    continue;
                }

                var c = BlockSatd(_pic.Luma, origin, stride, bx, by, diff) + BitCost(mode == predicted ? 1 : 4);
                if (c < bestCost)
                {
                    bestCost = c;
                    bestMode = mode;
                }
            }

            H264IntraPrediction.Predict4x4(_pic.Luma, stride, offset, bestMode, hasLeft, hasTop, hasTopRight, hasTopLeft);
            _modes[r] = (sbyte)bestMode;
            _pic.IntraModes[(_mbAddr * 16) + r] = (sbyte)bestMode;
            CodeLumaBlock(r, intra: true);
            cost += bestCost;
        }

        return cost;
    }

    /// <summary>Returns whether the top-right samples of a 4x4 block are available (6.4.11.4).</summary>
    /// <param name="bx">Block column.</param>
    /// <param name="by">Block row.</param>
    /// <param name="top">Top macroblock availability.</param>
    /// <param name="topRightMb">Top-right macroblock availability.</param>
    /// <returns>Whether they are available.</returns>
    private static bool TopRightAvailable(int bx, int by, bool top, bool topRightMb)
    {
        if (by == 0)
        {
            return bx < 3 ? top : topRightMb;
        }

        if (bx == 3)
        {
            return false;
        }

        // Inside the macroblock: available when the block above-right precedes this one in decoding order.
        var current = Array.IndexOf(BlkRaster, (byte)((by * 4) + bx));
        var upRight = Array.IndexOf(BlkRaster, (byte)(((by - 1) * 4) + bx + 1));
        return upRight < current;
    }

    /// <summary>Returns predIntra4x4PredMode for a block (8.3.1.1).</summary>
    /// <param name="bx">Block column.</param>
    /// <param name="by">Block row.</param>
    /// <returns>The predicted mode.</returns>
    private int PredictedIntraMode(int bx, int by)
    {
        var a = NeighbourIntraMode(bx - 1, by);
        var b = NeighbourIntraMode(bx, by - 1);
        return a < 0 || b < 0 ? 2 : Math.Min(a, b);
    }

    /// <summary>Returns the Intra4x4 mode of a neighbouring block, 2 for a non-Intra_4x4 macroblock, or -1 when unavailable.</summary>
    /// <param name="bx">Block column relative to the macroblock, -1 to 3.</param>
    /// <param name="by">Block row, -1 to 3.</param>
    /// <returns>The mode.</returns>
    private int NeighbourIntraMode(int bx, int by)
    {
        int mbx = _mbx, mby = _mby;
        if (bx < 0)
        {
            mbx--;
            bx += 4;
        }

        if (by < 0)
        {
            mby--;
            by += 4;
        }

        if (mbx == _mbx && mby == _mby)
        {
            return _modes[(by * 4) + bx];
        }

        if (!MbAvailable(mbx, mby))
        {
            return -1;
        }

        var mode = _pic.IntraModes[(((mby * _layout.WidthInMbs) + mbx) * 16) + (by * 4) + bx];
        return mode < 0 ? 2 : mode;
    }

    /// <summary>Returns the SATD between the source and the prediction held in the reconstruction of a 4x4 luma block.</summary>
    /// <param name="plane">The plane holding the prediction.</param>
    /// <param name="origin">The macroblock's first sample.</param>
    /// <param name="stride">The stride.</param>
    /// <param name="bx">Block column.</param>
    /// <param name="by">Block row.</param>
    /// <param name="diff">Scratch of 16.</param>
    /// <returns>SATD.</returns>
    private int BlockSatd(byte[] plane, int origin, int stride, int bx, int by, Span<int> diff)
    {
        for (var j = 0; j < 4; j++)
        {
            for (var i = 0; i < 4; i++)
            {
                diff[(j * 4) + i] = _srcY[(((by * 4) + j) * 16) + (bx * 4) + i] - plane[origin + (((by * 4) + j) * stride) + (bx * 4) + i];
            }
        }

        return H264EncoderTables.Satd4x4(diff);
    }

    /// <summary>Returns the SATD of a chroma 4x4 block against the prediction in the plane.</summary>
    /// <param name="plane">The plane.</param>
    /// <param name="origin">The macroblock's first chroma sample.</param>
    /// <param name="stride">The chroma stride.</param>
    /// <param name="component">0 for Cb, 1 for Cr.</param>
    /// <param name="bx">Block column, 0 or 1.</param>
    /// <param name="by">Block row, 0 or 1.</param>
    /// <param name="diff">Scratch of 16.</param>
    /// <returns>SATD.</returns>
    private int ChromaSatd(byte[] plane, int origin, int stride, int component, int bx, int by, Span<int> diff)
    {
        for (var j = 0; j < 4; j++)
        {
            for (var i = 0; i < 4; i++)
            {
                diff[(j * 4) + i] = _srcC[(component * 64) + (((by * 4) + j) * 8) + (bx * 4) + i] - plane[origin + (((by * 4) + j) * stride) + (bx * 4) + i];
            }
        }

        return H264EncoderTables.Satd4x4(diff);
    }

    /// <summary>Codes the residual of one 4x4 luma block whose prediction is in the reconstruction, and reconstructs it.</summary>
    /// <param name="raster">The raster block index.</param>
    /// <param name="intra">Whether intra rounding applies.</param>
    /// <returns>The number of non-zero levels.</returns>
    private int CodeLumaBlock(int raster, bool intra)
    {
        var stride = _pic.Width;
        var offset = (((_mby * 16) + ((raster >> 2) * 4)) * stride) + (_mbx * 16) + ((raster & 3) * 4);
        Span<int> coefficients = stackalloc int[16];
        ForwardLuma(raster, offset, stride, coefficients);
        var levels = _luma.AsSpan(raster * 16, 16);
        var count = Quantize(coefficients, levels, 0, _qp, intra);
        ReconstructBlock(levels, 0, _qp, _pic.Luma, offset, stride, 0);
        return count;
    }

    /// <summary>Computes the forward transform of a luma block's residual against the prediction in the reconstruction.</summary>
    /// <param name="raster">The raster block index.</param>
    /// <param name="offset">The block's first sample in the plane.</param>
    /// <param name="stride">The stride.</param>
    /// <param name="coefficients">Receives the coefficients, raster order.</param>
    private void ForwardLuma(int raster, int offset, int stride, Span<int> coefficients)
    {
        Span<int> diff = stackalloc int[16];
        int bx = raster & 3, by = raster >> 2;
        for (var j = 0; j < 4; j++)
        {
            for (var i = 0; i < 4; i++)
            {
                diff[(j * 4) + i] = _srcY[(((by * 4) + j) * 16) + (bx * 4) + i] - _pic.Luma[offset + (j * stride) + i];
            }
        }

        H264EncoderTables.Forward4x4(diff, coefficients);
    }

    /// <summary>Quantises coefficients into levels in scan order.</summary>
    /// <param name="coefficients">The coefficients, raster order.</param>
    /// <param name="levels">Receives 16 levels in scan order; positions before <paramref name="start"/> are zeroed.</param>
    /// <param name="start">The first scan position quantised (1 when the DC is coded separately).</param>
    /// <param name="qp">The QP.</param>
    /// <param name="intra">Whether intra rounding applies.</param>
    /// <returns>The number of non-zero levels.</returns>
    private static int Quantize(ReadOnlySpan<int> coefficients, Span<int> levels, int start, int qp, bool intra)
    {
        var qbits = 15 + (qp / 6);
        var f = (1 << qbits) / (intra ? 3 : 6);
        var count = 0;
        levels[..start].Clear();
        for (var k = start; k < 16; k++)
        {
            var p = H264Tables.Zigzag4x4[k];
            var c = coefficients[p];
            var level = (int)((((long)Math.Abs(c) * H264EncoderTables.Multiplier(qp % 6, p)) + f) >> qbits);
            levels[k] = c < 0 ? -level : level;
            count += level != 0 ? 1 : 0;
        }

        return count;
    }

    /// <summary>Dequantises scan-order levels (8.5.12.1, flat scaling) and adds the inverse transform to the plane.</summary>
    /// <param name="levels">16 levels in scan order.</param>
    /// <param name="start">The first scan position to dequantise.</param>
    /// <param name="qp">The QP.</param>
    /// <param name="plane">The plane holding the prediction.</param>
    /// <param name="offset">The block's first sample.</param>
    /// <param name="stride">The stride.</param>
    /// <param name="dc">The already scaled DC value when <paramref name="start"/> is 1.</param>
    private static void ReconstructBlock(ReadOnlySpan<int> levels, int start, int qp, byte[] plane, int offset, int stride, int dc)
    {
        Span<int> d = stackalloc int[16];
        var any = dc != 0;
        d[0] = dc;
        for (var k = start; k < 16; k++)
        {
            var p = H264Tables.Zigzag4x4[k];
            d[p] = levels[k] * H264EncoderTables.Scale(qp % 6, p) << (qp / 6);
            any |= levels[k] != 0;
        }

        if (any)
        {
            H264Transform.Add4x4(d, plane, offset, stride);
        }
    }

    /// <summary>Codes the Intra_16x16 residual of the prediction in the reconstruction.</summary>
    private void CodeIntra16x16()
    {
        var stride = _pic.Width;
        var origin = (_mby * 16 * stride) + (_mbx * 16);
        Span<int> coefficients = stackalloc int[256];
        Span<int> dcMatrix = stackalloc int[16];
        Span<int> transformed = stackalloc int[16];
        for (var r = 0; r < 16; r++)
        {
            var offset = origin + ((r >> 2) * 4 * stride) + ((r & 3) * 4);
            ForwardLuma(r, offset, stride, coefficients.Slice(r * 16, 16));
            dcMatrix[r] = coefficients[r * 16];
        }

        H264EncoderTables.Hadamard4x4(dcMatrix, transformed);
        var qbits = 16 + (_qp / 6);
        var f = (1 << qbits) / 3;
        var mf = H264EncoderTables.Multiplier(_qp % 6, 0);
        var anyAc = false;
        for (var k = 0; k < 16; k++)
        {
            var c = transformed[H264Tables.Zigzag4x4[k]] / 2;
            var level = (int)((((long)Math.Abs(c) * mf) + f) >> qbits);
            _lumaDc[k] = c < 0 ? -level : level;
        }

        for (var r = 0; r < 16; r++)
        {
            anyAc |= Quantize(coefficients.Slice(r * 16, 16), _luma.AsSpan(r * 16, 16), 1, _qp, true) != 0;
        }

        if (!anyAc)
        {
            _luma.AsSpan().Clear();
        }

        // Reconstruction (8.5.10): inverse Hadamard of the DC levels, scaled, then each block's AC.
        Span<int> levelsRaster = stackalloc int[16];
        for (var k = 0; k < 16; k++)
        {
            levelsRaster[H264Tables.Zigzag4x4[k]] = _lumaDc[k];
        }

        H264Transform.InverseHadamard4x4(levelsRaster, transformed);
        var scale = 16 * H264EncoderTables.Scale(_qp % 6, 0);
        for (var r = 0; r < 16; r++)
        {
            long value = _qp >= 36 ? ((long)transformed[r] * scale) << ((_qp / 6) - 6) : (((long)transformed[r] * scale) + (1 << (5 - (_qp / 6)))) >> (6 - (_qp / 6));
            ReconstructBlock(_luma.AsSpan(r * 16, 16), 1, _qp, _pic.Luma, origin + ((r >> 2) * 4 * stride) + ((r & 3) * 4), stride, (int)value);
        }

        _cbp = anyAc ? 15 : 0;
    }

    /// <summary>Codes the luma residual of an inter macroblock whose prediction is in the reconstruction.</summary>
    /// <returns>The luma coded_block_pattern bits.</returns>
    private int CodeInterLuma()
    {
        var stride = _pic.Width;
        var origin = (_mby * 16 * stride) + (_mbx * 16);
        Span<int> coefficients = stackalloc int[16];
        var cbp = 0;
        for (var b8 = 0; b8 < 4; b8++)
        {
            int ones = 0, large = 0;
            for (var i = 0; i < 4; i++)
            {
                var r = BlkRaster[(b8 * 4) + i];
                var offset = origin + ((r >> 2) * 4 * stride) + ((r & 3) * 4);
                ForwardLuma(r, offset, stride, coefficients);
                var levels = _luma.AsSpan(r * 16, 16);
                Quantize(coefficients, levels, 0, _qp, false);
                foreach (var level in levels)
                {
                    ones += Math.Abs(level) == 1 ? 1 : 0;
                    large += Math.Abs(level) > 1 ? 1 : 0;
                }
            }

            if (large == 0 && ones <= 2)
            {
                for (var i = 0; i < 4; i++)
                {
                    _luma.AsSpan(BlkRaster[(b8 * 4) + i] * 16, 16).Clear();
                }

                continue;
            }

            cbp |= 1 << b8;
            for (var i = 0; i < 4; i++)
            {
                var r = BlkRaster[(b8 * 4) + i];
                ReconstructBlock(_luma.AsSpan(r * 16, 16), 0, _qp, _pic.Luma, origin + ((r >> 2) * 4 * stride) + ((r & 3) * 4), stride, 0);
            }
        }

        return cbp;
    }

    /// <summary>Returns whether the inter prediction in <see cref="_predY"/> and <see cref="_predC"/> leaves no coded residual.</summary>
    /// <returns>Whether every luma 8x8 block and both chroma components quantise to nothing.</returns>
    private bool ResidualVanishes()
    {
        Span<int> diff = stackalloc int[16];
        Span<int> coefficients = stackalloc int[16];
        Span<int> levels = stackalloc int[16];
        for (var b8 = 0; b8 < 4; b8++)
        {
            int ones = 0, large = 0;
            for (var i = 0; i < 4; i++)
            {
                var r = BlkRaster[(b8 * 4) + i];
                int bx = r & 3, by = r >> 2;
                for (var j = 0; j < 4; j++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        var k = (((by * 4) + j) * 16) + (bx * 4) + x;
                        diff[(j * 4) + x] = _srcY[k] - _predY[k];
                    }
                }

                H264EncoderTables.Forward4x4(diff, coefficients);
                Quantize(coefficients, levels, 0, _qp, false);
                foreach (var level in levels)
                {
                    ones += Math.Abs(level) == 1 ? 1 : 0;
                    large += Math.Abs(level) > 1 ? 1 : 0;
                }
            }

            if (large != 0 || ones > 2)
            {
                return false;
            }
        }

        return ChromaVanishes();
    }

    /// <summary>Returns whether the inter chroma prediction leaves no coded residual.</summary>
    /// <returns>Whether DC and AC quantise to nothing (AC after the isolated-ones rule).</returns>
    private bool ChromaVanishes()
    {
        var qpc = H264Tables.ChromaQp[_qp];
        Span<int> diff = stackalloc int[16];
        Span<int> coefficients = stackalloc int[64];
        Span<int> levels = stackalloc int[16];
        Span<int> dc = stackalloc int[4];
        int ones = 0, large = 0;
        for (var c = 0; c < 2; c++)
        {
            for (var b = 0; b < 4; b++)
            {
                for (var j = 0; j < 4; j++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        var k = (c * 64) + ((((b >> 1) * 4) + j) * 8) + ((b & 1) * 4) + x;
                        diff[(j * 4) + x] = _srcC[k] - _predC[k];
                    }
                }

                H264EncoderTables.Forward4x4(diff, coefficients.Slice(b * 16, 16));
                Quantize(coefficients.Slice(b * 16, 16), levels, 1, qpc, false);
                foreach (var level in levels)
                {
                    ones += Math.Abs(level) == 1 ? 1 : 0;
                    large += Math.Abs(level) > 1 ? 1 : 0;
                }
            }

            QuantizeChromaDc(coefficients, dc, qpc, false);
            if (dc[0] != 0 || dc[1] != 0 || dc[2] != 0 || dc[3] != 0)
            {
                return false;
            }
        }

        return large == 0 && ones <= 2;
    }

    /// <summary>Quantises the 2x2 chroma DC of four transformed blocks (8.5.11.1 inverse).</summary>
    /// <param name="coefficients">Four blocks of 16 coefficients, raster block order.</param>
    /// <param name="dc">Receives the DC levels, raster.</param>
    /// <param name="qpc">QPc.</param>
    /// <param name="intra">Whether intra rounding applies.</param>
    private static void QuantizeChromaDc(ReadOnlySpan<int> coefficients, Span<int> dc, int qpc, bool intra)
    {
        int c0 = coefficients[0], c1 = coefficients[16], c2 = coefficients[32], c3 = coefficients[48];
        Span<int> f = [c0 + c1 + c2 + c3, c0 - c1 + c2 - c3, c0 + c1 - c2 - c3, c0 - c1 - c2 + c3];
        var qbits = 16 + (qpc / 6);
        var rounding = (1 << qbits) / (intra ? 3 : 6);
        var mf = H264EncoderTables.Multiplier(qpc % 6, 0);
        for (var i = 0; i < 4; i++)
        {
            var level = (int)((((long)Math.Abs(f[i]) * mf) + rounding) >> qbits);
            dc[i] = f[i] < 0 ? -level : level;
        }
    }

    /// <summary>Codes both chroma components' residual against the prediction in the reconstruction.</summary>
    /// <param name="intra">Whether intra rounding applies.</param>
    /// <returns>The chroma coded_block_pattern, 0 to 2.</returns>
    private int CodeChroma(bool intra)
    {
        var cs = _pic.Width / 2;
        var origin = (_mby * 8 * cs) + (_mbx * 8);
        var qpc = H264Tables.ChromaQp[_qp];
        Span<int> diff = stackalloc int[16];
        Span<int> coefficients = stackalloc int[128];
        Span<int> levels = stackalloc int[16];
        int ones = 0, large = 0;
        var anyDc = false;
        for (var c = 0; c < 2; c++)
        {
            var plane = c == 0 ? _pic.Cb : _pic.Cr;
            for (var b = 0; b < 4; b++)
            {
                var offset = origin + ((b >> 1) * 4 * cs) + ((b & 1) * 4);
                for (var j = 0; j < 4; j++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        diff[(j * 4) + x] = _srcC[(c * 64) + ((((b >> 1) * 4) + j) * 8) + ((b & 1) * 4) + x] - plane[offset + (j * cs) + x];
                    }
                }

                var block = coefficients.Slice(((c * 4) + b) * 16, 16);
                H264EncoderTables.Forward4x4(diff, block);
                Quantize(block, levels, 1, qpc, intra);
                levels[1..].CopyTo(_chromaAc.AsSpan(((c * 4) + b) * 15, 15));
                foreach (var level in levels)
                {
                    ones += Math.Abs(level) == 1 ? 1 : 0;
                    large += Math.Abs(level) > 1 ? 1 : 0;
                }
            }

            QuantizeChromaDc(coefficients.Slice(c * 64, 64), _chromaDc.AsSpan(c * 4, 4), qpc, intra);
            for (var i = 0; i < 4; i++)
            {
                anyDc |= _chromaDc[(c * 4) + i] != 0;
            }
        }

        var anyAc = large != 0 || ones > (intra ? 0 : 2);
        if (!anyAc)
        {
            _chromaAc.AsSpan().Clear();
        }

        var cbp = anyAc ? 2 : anyDc ? 1 : 0;
        if (cbp == 0)
        {
            return 0;
        }

        Span<int> scan = stackalloc int[16];
        Span<int> dcScaled = stackalloc int[4];
        var scale = 16 * H264EncoderTables.Scale(qpc % 6, 0);
        for (var c = 0; c < 2; c++)
        {
            var plane = c == 0 ? _pic.Cb : _pic.Cr;
            int c0 = _chromaDc[c * 4], c1 = _chromaDc[(c * 4) + 1], c2 = _chromaDc[(c * 4) + 2], c3 = _chromaDc[(c * 4) + 3];
            dcScaled[0] = c0 + c1 + c2 + c3;
            dcScaled[1] = c0 - c1 + c2 - c3;
            dcScaled[2] = c0 + c1 - c2 - c3;
            dcScaled[3] = c0 - c1 - c2 + c3;
            for (var b = 0; b < 4; b++)
            {
                var dc = (int)(((long)dcScaled[b] * scale << (qpc / 6)) >> 5);
                scan[0] = 0;
                _chromaAc.AsSpan(((c * 4) + b) * 15, 15).CopyTo(scan[1..]);
                ReconstructBlock(scan, 1, qpc, plane, origin + ((b >> 1) * 4 * cs) + ((b & 1) * 4), cs, dc);
            }
        }

        return cbp;
    }

    /// <summary>Codes the chosen mode's residual and writes macroblock_layer().</summary>
    /// <param name="w">The writer.</param>
    /// <param name="mode">The choice.</param>
    private void WriteMacroblock(H264BitWriter w, MbChoice mode)
    {
        var stride = _pic.Width;
        var origin = (_mby * 16 * stride) + (_mbx * 16);
        var cs = stride / 2;
        var corigin = (_mby * 8 * cs) + (_mbx * 8);
        bool left = MbAvailable(_mbx - 1, _mby), top = MbAvailable(_mbx, _mby - 1), topLeft = MbAvailable(_mbx - 1, _mby - 1);
        _pic.Qp[_mbAddr] = (byte)_qp;
        _pic.TotalCoeff.AsSpan(_mbAddr * 16, 16).Clear();
        _pic.ChromaCoeff.AsSpan(_mbAddr * 8, 8).Clear();
        int chromaCbp;
        var typeOffset = _header.IsIntra ? 0 : 5;
        switch (mode)
        {
            case MbChoice.Intra16x16:
                H264IntraPrediction.Predict16x16(_pic.Luma, stride, origin, _intra16Mode, left, top, topLeft);
                CodeIntra16x16();
                PredictChroma(left, top, topLeft);
                chromaCbp = CodeChroma(true);
                SetIntraState(H264MbKind.Intra16x16);
                w.WriteUe(typeOffset + 1 + _intra16Mode + (4 * chromaCbp) + (_cbp != 0 ? 12 : 0));
                w.WriteUe(_chromaMode);
                w.WriteSe(0);
                WriteBlock(w, _lumaDc, LumaNc(0, 0), null);
                if (_cbp != 0)
                {
                    for (var blk = 0; blk < 16; blk++)
                    {
                        var r = BlkRaster[blk];
                        WriteBlock(w, _luma.AsSpan((r * 16) + 1, 15), LumaNc(r & 3, r >> 2), r);
                    }
                }

                WriteChroma(w, chromaCbp);
                return;

            case MbChoice.Intra4x4:
                PredictChroma(left, top, topLeft);
                chromaCbp = CodeChroma(true);
                SetIntraState(H264MbKind.Intra4x4);
                _cbp = LumaCbpFromLevels() | (chromaCbp << 4);
                w.WriteUe(typeOffset);
                for (var blk = 0; blk < 16; blk++)
                {
                    var r = BlkRaster[blk];
                    var predicted = PredictedIntraMode(r & 3, r >> 2);
                    var m = _modes[r];
                    w.WriteBit(m == predicted);
                    if (m != predicted)
                    {
                        w.Write((uint)(m < predicted ? m : m - 1), 3);
                    }
                }

                w.WriteUe(_chromaMode);
                H264CavlcWriter.WriteCbp(w, _cbp, true);
                WriteResidual4x4(w, chromaCbp);
                return;

            default:
                StoreSamples(_bestPredY, _predC);
                var lumaCbp = CodeInterLuma();
                chromaCbp = CodeChroma(false);
                _cbp = lumaCbp | (chromaCbp << 4);
                _pic.Kind[_mbAddr] = H264MbKind.Inter;
                WriteInterHeader(w);
                H264CavlcWriter.WriteCbp(w, _cbp, false);
                WriteResidual4x4(w, chromaCbp);
                return;
        }
    }

    /// <summary>Derives the luma coded_block_pattern bits from the quantised levels.</summary>
    /// <returns>The bits.</returns>
    private int LumaCbpFromLevels()
    {
        var cbp = 0;
        for (var b8 = 0; b8 < 4; b8++)
        {
            for (var i = 0; i < 4 && (cbp & (1 << b8)) == 0; i++)
            {
                foreach (var level in _luma.AsSpan(BlkRaster[(b8 * 4) + i] * 16, 16))
                {
                    if (level != 0)
                    {
                        cbp |= 1 << b8;
                        break;
                    }
                }
            }
        }

        return cbp;
    }

    /// <summary>Writes mb_qp_delta and the residual of a non-Intra16x16 macroblock.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="chromaCbp">The chroma pattern.</param>
    private void WriteResidual4x4(H264BitWriter w, int chromaCbp)
    {
        if (_cbp == 0)
        {
            return;
        }

        w.WriteSe(0);
        for (var blk = 0; blk < 16; blk++)
        {
            if ((_cbp & (1 << (blk >> 2))) == 0)
            {
                continue;
            }

            var r = BlkRaster[blk];
            WriteBlock(w, _luma.AsSpan(r * 16, 16), LumaNc(r & 3, r >> 2), r);
        }

        WriteChroma(w, chromaCbp);
    }

    /// <summary>Writes the chroma residual blocks.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="chromaCbp">The chroma pattern.</param>
    private void WriteChroma(H264BitWriter w, int chromaCbp)
    {
        if (chromaCbp == 0)
        {
            return;
        }

        H264CavlcWriter.WriteBlock(w, _chromaDc.AsSpan(0, 4), -1);
        H264CavlcWriter.WriteBlock(w, _chromaDc.AsSpan(4, 4), -1);
        if (chromaCbp != 2)
        {
            return;
        }

        for (var c = 0; c < 2; c++)
        {
            for (var b = 0; b < 4; b++)
            {
                var total = H264CavlcWriter.WriteBlock(w, _chromaAc.AsSpan(((c * 4) + b) * 15, 15), ChromaNc(c, b & 1, b >> 1));
                _pic.ChromaCoeff[(_mbAddr * 8) + (c * 4) + b] = (byte)total;
            }
        }
    }

    /// <summary>Writes one luma block and records its TotalCoeff.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="levels">The levels.</param>
    /// <param name="nC">The context.</param>
    /// <param name="raster">The raster block to record, or null for the Intra16x16 DC block.</param>
    private void WriteBlock(H264BitWriter w, ReadOnlySpan<int> levels, int nC, int? raster)
    {
        var total = H264CavlcWriter.WriteBlock(w, levels, nC);
        if (raster is { } r)
        {
            _pic.TotalCoeff[(_mbAddr * 16) + r] = (byte)total;
        }
    }

    /// <summary>Returns nC for a luma block (9.2.1).</summary>
    /// <param name="bx">Block column.</param>
    /// <param name="by">Block row.</param>
    /// <returns>nC.</returns>
    private int LumaNc(int bx, int by)
    {
        var a = LumaTotal(bx - 1, by);
        var b = LumaTotal(bx, by - 1);
        return a >= 0 && b >= 0 ? (a + b + 1) >> 1 : a >= 0 ? a : b >= 0 ? b : 0;
    }

    /// <summary>Returns the TotalCoeff of a neighbouring luma block, or -1 when unavailable.</summary>
    /// <param name="bx">Block column relative to the macroblock.</param>
    /// <param name="by">Block row.</param>
    /// <returns>The count.</returns>
    private int LumaTotal(int bx, int by)
    {
        int mbx = _mbx, mby = _mby;
        if (bx < 0)
        {
            mbx--;
            bx += 4;
        }

        if (by < 0)
        {
            mby--;
            by += 4;
        }

        if ((mbx != _mbx || mby != _mby) && !MbAvailable(mbx, mby))
        {
            return -1;
        }

        return _pic.TotalCoeff[(((mby * _layout.WidthInMbs) + mbx) * 16) + (by * 4) + bx];
    }

    /// <summary>Returns nC for a chroma AC block.</summary>
    /// <param name="component">0 for Cb, 1 for Cr.</param>
    /// <param name="bx">Block column, 0 or 1.</param>
    /// <param name="by">Block row, 0 or 1.</param>
    /// <returns>nC.</returns>
    private int ChromaNc(int component, int bx, int by)
    {
        var a = ChromaTotal(component, bx - 1, by);
        var b = ChromaTotal(component, bx, by - 1);
        return a >= 0 && b >= 0 ? (a + b + 1) >> 1 : a >= 0 ? a : b >= 0 ? b : 0;
    }

    /// <summary>Returns the TotalCoeff of a neighbouring chroma AC block, or -1 when unavailable.</summary>
    /// <param name="component">The component.</param>
    /// <param name="bx">Block column relative to the macroblock.</param>
    /// <param name="by">Block row.</param>
    /// <returns>The count.</returns>
    private int ChromaTotal(int component, int bx, int by)
    {
        int mbx = _mbx, mby = _mby;
        if (bx < 0)
        {
            mbx--;
            bx += 2;
        }

        if (by < 0)
        {
            mby--;
            by += 2;
        }

        if ((mbx != _mbx || mby != _mby) && !MbAvailable(mbx, mby))
        {
            return -1;
        }

        return _pic.ChromaCoeff[(((mby * _layout.WidthInMbs) + mbx) * 8) + (component * 4) + (by * 2) + bx];
    }

    /// <summary>Writes the intra chroma prediction into the reconstruction.</summary>
    /// <param name="left">Left availability.</param>
    /// <param name="top">Top availability.</param>
    /// <param name="topLeft">Top-left availability.</param>
    private void PredictChroma(bool left, bool top, bool topLeft)
    {
        var cs = _pic.Width / 2;
        var origin = (_mby * 8 * cs) + (_mbx * 8);
        H264IntraPrediction.PredictChroma(_pic.Cb, cs, origin, _chromaMode, left, top, topLeft);
        H264IntraPrediction.PredictChroma(_pic.Cr, cs, origin, _chromaMode, left, top, topLeft);
    }
}
