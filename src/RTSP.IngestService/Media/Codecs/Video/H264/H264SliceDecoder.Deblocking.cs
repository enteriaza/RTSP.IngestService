// Deblocking filter for progressive frames (ITU-T H.264 8.7). Runs once per picture after every macroblock is
// decoded, in macroblock address order: luma vertical edges, luma horizontal edges, then chroma. Each macroblock
// uses the disable_deblocking_filter_idc and offsets of its own slice; boundary strength follows 8.7.2.1 with
// reference pictures compared by identity rather than by index.

namespace RTSP.IngestService.Media.Codecs.Video.H264;

/// <summary>Deblocking part of the slice decoder.</summary>
internal sealed partial class H264SliceDecoder
{
    /// <summary>Applies the deblocking filter to the whole picture.</summary>
    public void Deblock()
    {
        Span<int> bs = stackalloc int[4];
        for (var addr = 0; addr < MbCount; addr++)
        {
            var slice = MbSlice[addr];
            var filter = _slices[slice];
            if (filter.DisableIdc == 1)
            {
                continue;
            }

            var mbx = addr % W;
            var mby = addr / W;
            var filterLeft = mbx > 0 && (filter.DisableIdc != 2 || MbSlice[addr - 1] == slice);
            var filterTop = mby > 0 && (filter.DisableIdc != 2 || MbSlice[addr - W] == slice);
            var eight = MbTransform8x8[addr];
            for (var dir = 0; dir < 2; dir++)
            {
                for (var edge = 0; edge < 4; edge++)
                {
                    if ((edge == 0 && !(dir == 0 ? filterLeft : filterTop)) || (eight && (edge & 1) == 1))
                    {
                        continue;
                    }

                    var any = false;
                    for (var k = 0; k < 4; k++)
                    {
                        bs[k] = Strength(mbx, mby, dir, edge, k);
                        any |= bs[k] != 0;
                    }

                    if (!any)
                    {
                        continue;
                    }

                    FilterLumaEdge(mbx, mby, dir, edge, bs, addr, filter);

                    if ((edge & 1) == 0)
                    {
                        FilterChromaEdge(mbx, mby, dir, edge / 2, bs, addr, filter);
                    }
                }
            }
        }
    }

    /// <summary>Derives bS for one 4-sample segment of a luma edge (8.7.2.1).</summary>
    /// <param name="mbx">The macroblock column.</param>
    /// <param name="mby">The macroblock row.</param>
    /// <param name="dir">0 vertical edge, 1 horizontal edge.</param>
    /// <param name="edge">The edge, 0 to 3.</param>
    /// <param name="k">The segment along the edge, 0 to 3.</param>
    /// <returns>bS, 0 to 4.</returns>
    private int Strength(int mbx, int mby, int dir, int edge, int k)
    {
        int qx = (mbx * 4) + (dir == 0 ? edge : k), qy = (mby * 4) + (dir == 0 ? k : edge);
        int px = dir == 0 ? qx - 1 : qx, py = dir == 0 ? qy : qy - 1;
        var qAddr = (mby * W) + mbx;
        var pAddr = ((py >> 2) * W) + (px >> 2);
        if (IsIntra(MbKindOf[pAddr]) || IsIntra(MbKindOf[qAddr]))
        {
            return edge == 0 ? 4 : 3;
        }

        int qg = (qy * W4) + qx, pg = (py * W4) + px;
        if (Coded[qg] != 0 || Coded[pg] != 0)
        {
            return 2;
        }

        var blocks4 = MbCount * 16;
        var blocks8 = MbCount * 4;
        int pg8 = ((py >> 1) * W8) + (px >> 1), qg8 = ((qy >> 1) * W8) + (qx >> 1);
        var refs = _pic.RefPicId;
        var mv = _pic.Mv;
        long p0 = refs[pg8], p1 = refs[blocks8 + pg8], q0 = refs[qg8], q1 = refs[blocks8 + qg8];
        var countP = (p0 >= 0 ? 1 : 0) + (p1 >= 0 ? 1 : 0);
        var countQ = (q0 >= 0 ? 1 : 0) + (q1 >= 0 ? 1 : 0);
        if (countP != countQ)
        {
            return 1;
        }

        if (countP == 1)
        {
            var pl = p0 >= 0 ? 0 : 1;
            var ql = q0 >= 0 ? 0 : 1;
            if ((pl == 0 ? p0 : p1) != (ql == 0 ? q0 : q1))
            {
                return 1;
            }

            return Differs(mv, blocks4, pl, pg, ql, qg) ? 1 : 0;
        }

        if (!((p0 == q0 && p1 == q1) || (p0 == q1 && p1 == q0)))
        {
            return 1;
        }

        if (p0 != p1)
        {
            return p0 == q0
                ? (Differs(mv, blocks4, 0, pg, 0, qg) || Differs(mv, blocks4, 1, pg, 1, qg) ? 1 : 0)
                : (Differs(mv, blocks4, 0, pg, 1, qg) || Differs(mv, blocks4, 1, pg, 0, qg) ? 1 : 0);
        }

        var straight = Differs(mv, blocks4, 0, pg, 0, qg) || Differs(mv, blocks4, 1, pg, 1, qg);
        var crossed = Differs(mv, blocks4, 0, pg, 1, qg) || Differs(mv, blocks4, 1, pg, 0, qg);
        return straight && crossed ? 1 : 0;
    }

    /// <summary>Returns whether two motion vectors differ by four or more quarter samples in either component.</summary>
    /// <param name="mv">The motion field.</param>
    /// <param name="blocks4">4x4 blocks per list.</param>
    /// <param name="listP">The list of p's vector.</param>
    /// <param name="pg">p's 4x4 block.</param>
    /// <param name="listQ">The list of q's vector.</param>
    /// <param name="qg">q's 4x4 block.</param>
    /// <returns><see langword="true"/> when the vectors differ enough.</returns>
    private static bool Differs(short[] mv, int blocks4, int listP, int pg, int listQ, int qg)
    {
        var ip = ((listP * blocks4) + pg) * 2;
        var iq = ((listQ * blocks4) + qg) * 2;
        return Math.Abs(mv[ip] - mv[iq]) >= 4 || Math.Abs(mv[ip + 1] - mv[iq + 1]) >= 4;
    }

    /// <summary>Filters one luma edge of a macroblock.</summary>
    /// <param name="mbx">The macroblock column.</param>
    /// <param name="mby">The macroblock row.</param>
    /// <param name="dir">0 vertical, 1 horizontal.</param>
    /// <param name="edge">The edge, 0 to 3.</param>
    /// <param name="bs">bS per 4-sample segment.</param>
    /// <param name="addr">The macroblock address.</param>
    /// <param name="filter">The macroblock's slice parameters.</param>
    private void FilterLumaEdge(int mbx, int mby, int dir, int edge, ReadOnlySpan<int> bs, int addr, SliceFilter filter)
    {
        var stride = _pic.Width;
        var pAddr = edge != 0 ? addr : dir == 0 ? addr - 1 : addr - W;
        var qpav = (MbQp[pAddr] + MbQp[addr] + 1) >> 1;
        var indexA = Math.Clamp(qpav + filter.OffsetA, 0, 51);
        var indexB = Math.Clamp(qpav + filter.OffsetB, 0, 51);
        int alpha = H264Tables.Alpha[indexA], beta = H264Tables.Beta[indexB];
        var origin = (mby * 16 * stride) + (mbx * 16);
        for (var i = 0; i < 16; i++)
        {
            var strength = bs[i >> 2];
            if (strength == 0)
            {
                continue;
            }

            var index = dir == 0 ? origin + (i * stride) + (edge * 4) : origin + (edge * 4 * stride) + i;
            FilterSamples(_pic.Luma, index, dir == 0 ? 1 : stride, strength, alpha, beta, indexA, false);
        }
    }

    /// <summary>Filters one chroma edge of a macroblock in both components.</summary>
    /// <param name="mbx">The macroblock column.</param>
    /// <param name="mby">The macroblock row.</param>
    /// <param name="dir">0 vertical, 1 horizontal.</param>
    /// <param name="edge">The chroma edge, 0 or 1.</param>
    /// <param name="bs">bS of the corresponding luma edge.</param>
    /// <param name="addr">The macroblock address.</param>
    /// <param name="filter">The macroblock's slice parameters.</param>
    private void FilterChromaEdge(int mbx, int mby, int dir, int edge, ReadOnlySpan<int> bs, int addr, SliceFilter filter)
    {
        var stride = _pic.Width / 2;
        var pAddr = edge != 0 ? addr : dir == 0 ? addr - 1 : addr - W;
        var origin = (mby * 8 * stride) + (mbx * 8);
        for (var c = 0; c < 2; c++)
        {
            var offset = c == 0 ? filter.CbOffset : filter.CrOffset;
            var qpp = H264Tables.ChromaQp[Math.Clamp(MbQp[pAddr] + offset, 0, 51)];
            var qpq = H264Tables.ChromaQp[Math.Clamp(MbQp[addr] + offset, 0, 51)];
            var qpav = (qpp + qpq + 1) >> 1;
            var indexA = Math.Clamp(qpav + filter.OffsetA, 0, 51);
            var indexB = Math.Clamp(qpav + filter.OffsetB, 0, 51);
            int alpha = H264Tables.Alpha[indexA], beta = H264Tables.Beta[indexB];
            var plane = c == 0 ? _pic.Cb : _pic.Cr;
            for (var i = 0; i < 8; i++)
            {
                var strength = bs[i >> 1];
                if (strength == 0)
                {
                    continue;
                }

                var index = dir == 0 ? origin + (i * stride) + (edge * 4) : origin + (edge * 4 * stride) + i;
                FilterSamples(plane, index, dir == 0 ? 1 : stride, strength, alpha, beta, indexA, true);
            }
        }
    }

    /// <summary>Filters one line of samples across an edge (8.7.2.3, 8.7.2.4).</summary>
    /// <param name="plane">The plane.</param>
    /// <param name="index">The index of q0.</param>
    /// <param name="step">The distance between samples across the edge.</param>
    /// <param name="bs">bS, 1 to 4.</param>
    /// <param name="alpha">α.</param>
    /// <param name="beta">β.</param>
    /// <param name="indexA">indexA.</param>
    /// <param name="chroma">Whether chroma-style filtering applies.</param>
    private static void FilterSamples(byte[] plane, int index, int step, int bs, int alpha, int beta, int indexA, bool chroma)
    {
        int p0 = plane[index - step], q0 = plane[index], p1 = plane[index - (2 * step)], q1 = plane[index + step];
        if (Math.Abs(p0 - q0) >= alpha || Math.Abs(p1 - p0) >= beta || Math.Abs(q1 - q0) >= beta)
        {
            return;
        }

        int p2 = chroma ? 0 : plane[index - (3 * step)], q2 = chroma ? 0 : plane[index + (2 * step)];
        var ap = Math.Abs(p2 - p0);
        var aq = Math.Abs(q2 - q0);
        if (bs < 4)
        {
            int tc0 = H264Tables.Tc0[(indexA * 3) + bs - 1];
            var tc = chroma ? tc0 + 1 : tc0 + (ap < beta ? 1 : 0) + (aq < beta ? 1 : 0);
            var delta = Math.Clamp((((q0 - p0) << 2) + (p1 - q1) + 4) >> 3, -tc, tc);
            plane[index - step] = Clip(p0 + delta);
            plane[index] = Clip(q0 - delta);
            if (!chroma)
            {
                if (ap < beta)
                {
                    plane[index - (2 * step)] = (byte)(p1 + Math.Clamp((p2 + ((p0 + q0 + 1) >> 1) - (p1 << 1)) >> 1, -tc0, tc0));
                }

                if (aq < beta)
                {
                    plane[index + step] = (byte)(q1 + Math.Clamp((q2 + ((p0 + q0 + 1) >> 1) - (q1 << 1)) >> 1, -tc0, tc0));
                }
            }

            return;
        }

        var strong = Math.Abs(p0 - q0) < ((alpha >> 2) + 2);
        if (!chroma && ap < beta && strong)
        {
            var p3 = plane[index - (4 * step)];
            plane[index - step] = (byte)((p2 + (2 * p1) + (2 * p0) + (2 * q0) + q1 + 4) >> 3);
            plane[index - (2 * step)] = (byte)((p2 + p1 + p0 + q0 + 2) >> 2);
            plane[index - (3 * step)] = (byte)(((2 * p3) + (3 * p2) + p1 + p0 + q0 + 4) >> 3);
        }
        else
        {
            plane[index - step] = (byte)(((2 * p1) + p0 + q1 + 2) >> 2);
        }

        if (!chroma && aq < beta && strong)
        {
            var q3 = plane[index + (3 * step)];
            plane[index] = (byte)((p1 + (2 * p0) + (2 * q0) + (2 * q1) + q2 + 4) >> 3);
            plane[index + step] = (byte)((p0 + q0 + q1 + q2 + 2) >> 2);
            plane[index + (2 * step)] = (byte)(((2 * q3) + (3 * q2) + q1 + q0 + p0 + 4) >> 3);
        }
        else
        {
            plane[index] = (byte)(((2 * q1) + q0 + p1 + 2) >> 2);
        }
    }

    /// <summary>Clips to 8 bits.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The sample.</returns>
    private static byte Clip(int value) => (byte)Math.Clamp(value, 0, 255);
}
