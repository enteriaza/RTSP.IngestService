// The encoder's in-loop deblocking filter (ITU-T H.264 8.7) for progressive 4:2:0 frames with
// disable_deblocking_filter_idc 0 and zero filter offsets, applied to the reconstructed picture after all of its
// macroblocks are coded, so reference pictures match what a decoder reconstructs. It is written independently of
// the decoder's filter; the round-trip tests compare the two bit for bit. Boundary strength follows 8.7.2.1 for
// frame macroblocks: 4 on macroblock edges with an intra side, 3 on internal intra edges, 2 where either 4x4 block
// has coefficients, 1 for different reference pictures or a motion vector difference of four quarter samples.

namespace RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

/// <summary>Deblocks a reconstructed encoder picture.</summary>
internal static class H264EncoderDeblocking
{
    /// <summary>Filters every macroblock edge of the picture in macroblock raster order.</summary>
    /// <param name="picture">The reconstructed picture.</param>
    public static void Filter(H264EncoderPicture picture)
    {
        Span<int> bs = stackalloc int[16];
        for (var mby = 0; mby < picture.HeightInMbs; mby++)
        {
            for (var mbx = 0; mbx < picture.WidthInMbs; mbx++)
            {
                for (var dir = 0; dir < 2; dir++)
                {
                    for (var edge = 0; edge < 4; edge++)
                    {
                        if (edge == 0 && (dir == 0 ? mbx == 0 : mby == 0))
                        {
                            continue;
                        }

                        var any = false;
                        for (var k = 0; k < 4; k++)
                        {
                            bs[k] = Strength(picture, mbx, mby, dir, edge, k);
                            any |= bs[k] != 0;
                        }

                        if (!any)
                        {
                            continue;
                        }

                        FilterLuma(picture, mbx, mby, dir, edge, bs);
                        if ((edge & 1) == 0)
                        {
                            FilterChroma(picture, mbx, mby, dir, edge / 2, bs);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Derives bS for the k-th 4x4 block along an edge.</summary>
    /// <param name="picture">The picture.</param>
    /// <param name="mbx">Macroblock column.</param>
    /// <param name="mby">Macroblock row.</param>
    /// <param name="dir">0 for vertical edges, 1 for horizontal.</param>
    /// <param name="edge">The edge, 0 to 3 in 4-sample units.</param>
    /// <param name="k">The block along the edge.</param>
    /// <returns>bS.</returns>
    private static int Strength(H264EncoderPicture picture, int mbx, int mby, int dir, int edge, int k)
    {
        int qbx = dir == 0 ? edge : k, qby = dir == 0 ? k : edge;
        int pmbx = mbx, pmby = mby, pbx = qbx, pby = qby;
        if (dir == 0)
        {
            pbx--;
        }
        else
        {
            pby--;
        }

        if (pbx < 0)
        {
            pmbx--;
            pbx += 4;
        }

        if (pby < 0)
        {
            pmby--;
            pby += 4;
        }

        var qmb = (mby * picture.WidthInMbs) + mbx;
        var pmb = (pmby * picture.WidthInMbs) + pmbx;
        var intraQ = picture.Kind[qmb] >= H264MbKind.Intra16x16;
        var intraP = picture.Kind[pmb] >= H264MbKind.Intra16x16;
        if (intraP || intraQ)
        {
            return edge == 0 ? 4 : 3;
        }

        var q = picture.Block(mbx, mby, qbx, qby);
        var p = picture.Block(pmbx, pmby, pbx, pby);
        if (picture.TotalCoeff[(qmb * 16) + Raster(qbx, qby)] != 0 || picture.TotalCoeff[(pmb * 16) + Raster(pbx, pby)] != 0)
        {
            return 2;
        }

        if (picture.RefId[q] != picture.RefId[p])
        {
            return 1;
        }

        return Math.Abs(picture.Mv[2 * q] - picture.Mv[2 * p]) >= 4 || Math.Abs(picture.Mv[(2 * q) + 1] - picture.Mv[(2 * p) + 1]) >= 4 ? 1 : 0;
    }

    /// <summary>Returns the per-macroblock index of a 4x4 block stored in raster order.</summary>
    /// <param name="bx">Block column.</param>
    /// <param name="by">Block row.</param>
    /// <returns>The index.</returns>
    private static int Raster(int bx, int by) => (by * 4) + bx;

    /// <summary>Filters one luma edge of 16 samples.</summary>
    /// <param name="picture">The picture.</param>
    /// <param name="mbx">Macroblock column.</param>
    /// <param name="mby">Macroblock row.</param>
    /// <param name="dir">The direction.</param>
    /// <param name="edge">The edge.</param>
    /// <param name="bs">bS per 4x4 block along the edge.</param>
    private static void FilterLuma(H264EncoderPicture picture, int mbx, int mby, int dir, int edge, ReadOnlySpan<int> bs)
    {
        var stride = picture.Width;
        var qmb = (mby * picture.WidthInMbs) + mbx;
        var pmb = edge != 0 ? qmb : dir == 0 ? qmb - 1 : qmb - picture.WidthInMbs;
        var qpav = (picture.Qp[qmb] + picture.Qp[pmb] + 1) >> 1;
        var origin = (mby * 16 * stride) + (mbx * 16);
        for (var i = 0; i < 16; i++)
        {
            var index = dir == 0 ? origin + (i * stride) + (edge * 4) : origin + (edge * 4 * stride) + i;
            FilterSamples(picture.Luma, index, dir == 0 ? 1 : stride, bs[i >> 2], qpav, false);
        }
    }

    /// <summary>Filters one chroma edge of 8 samples in both chroma planes.</summary>
    /// <param name="picture">The picture.</param>
    /// <param name="mbx">Macroblock column.</param>
    /// <param name="mby">Macroblock row.</param>
    /// <param name="dir">The direction.</param>
    /// <param name="edge">The chroma edge, 0 or 1 in 4-sample units.</param>
    /// <param name="bs">bS of the corresponding luma edge.</param>
    private static void FilterChroma(H264EncoderPicture picture, int mbx, int mby, int dir, int edge, ReadOnlySpan<int> bs)
    {
        var stride = picture.Width / 2;
        var qmb = (mby * picture.WidthInMbs) + mbx;
        var pmb = edge != 0 ? qmb : dir == 0 ? qmb - 1 : qmb - picture.WidthInMbs;
        var qpav = (ChromaQp(picture.Qp[qmb]) + ChromaQp(picture.Qp[pmb]) + 1) >> 1;
        var origin = (mby * 8 * stride) + (mbx * 8);
        for (var i = 0; i < 8; i++)
        {
            var index = dir == 0 ? origin + (i * stride) + (edge * 4) : origin + (edge * 4 * stride) + i;
            var strength = bs[(i * 2) >> 2];
            FilterSamples(picture.Cb, index, dir == 0 ? 1 : stride, strength, qpav, true);
            FilterSamples(picture.Cr, index, dir == 0 ? 1 : stride, strength, qpav, true);
        }
    }

    /// <summary>Returns QPc for a QPY with chroma_qp_index_offset 0.</summary>
    /// <param name="qp">QPY.</param>
    /// <returns>QPc.</returns>
    private static int ChromaQp(int qp) => H264Tables.ChromaQp[Math.Clamp(qp, 0, 51)];

    /// <summary>Filters the samples across one edge position (8.7.2.3, 8.7.2.4).</summary>
    /// <param name="plane">The plane.</param>
    /// <param name="q0">The index of q0.</param>
    /// <param name="step">The distance between samples across the edge.</param>
    /// <param name="bs">bS.</param>
    /// <param name="qpav">qPav.</param>
    /// <param name="chroma">Whether chroma filtering applies.</param>
    private static void FilterSamples(byte[] plane, int q0, int step, int bs, int qpav, bool chroma)
    {
        if (bs == 0)
        {
            return;
        }

        var indexA = Math.Clamp(qpav, 0, 51);
        int alpha = H264Tables.Alpha[indexA], beta = H264Tables.Beta[indexA];
        int p0 = plane[q0 - step], p1 = plane[q0 - (2 * step)], q0v = plane[q0], q1 = plane[q0 + step];
        if (Math.Abs(p0 - q0v) >= alpha || Math.Abs(p1 - p0) >= beta || Math.Abs(q1 - q0v) >= beta)
        {
            return;
        }

        int p2 = chroma ? 0 : plane[q0 - (3 * step)], q2 = chroma ? 0 : plane[q0 + (2 * step)];
        var ap = Math.Abs(p2 - p0);
        var aq = Math.Abs(q2 - q0v);
        if (bs < 4)
        {
            var tc0 = H264Tables.Tc0[(indexA * 3) + bs - 1];
            var tc = chroma ? tc0 + 1 : tc0 + (ap < beta ? 1 : 0) + (aq < beta ? 1 : 0);
            var delta = Math.Clamp((((q0v - p0) << 2) + (p1 - q1) + 4) >> 3, -tc, tc);
            plane[q0 - step] = Clip(p0 + delta);
            plane[q0] = Clip(q0v - delta);
            if (!chroma)
            {
                if (ap < beta)
                {
                    plane[q0 - (2 * step)] = (byte)(p1 + Math.Clamp((p2 + ((p0 + q0v + 1) >> 1) - (p1 << 1)) >> 1, -tc0, tc0));
                }

                if (aq < beta)
                {
                    plane[q0 + step] = (byte)(q1 + Math.Clamp((q2 + ((p0 + q0v + 1) >> 1) - (q1 << 1)) >> 1, -tc0, tc0));
                }
            }

            return;
        }

        if (chroma)
        {
            plane[q0 - step] = (byte)(((2 * p1) + p0 + q1 + 2) >> 2);
            plane[q0] = (byte)(((2 * q1) + q0v + p1 + 2) >> 2);
            return;
        }

        var p3 = plane[q0 - (4 * step)];
        var q3 = plane[q0 + (3 * step)];
        var strong = Math.Abs(p0 - q0v) < ((alpha >> 2) + 2);
        if (ap < beta && strong)
        {
            plane[q0 - step] = (byte)((p2 + (2 * p1) + (2 * p0) + (2 * q0v) + q1 + 4) >> 3);
            plane[q0 - (2 * step)] = (byte)((p2 + p1 + p0 + q0v + 2) >> 2);
            plane[q0 - (3 * step)] = (byte)(((2 * p3) + (3 * p2) + p1 + p0 + q0v + 4) >> 3);
        }
        else
        {
            plane[q0 - step] = (byte)(((2 * p1) + p0 + q1 + 2) >> 2);
        }

        if (aq < beta && strong)
        {
            plane[q0] = (byte)((p1 + (2 * p0) + (2 * q0v) + (2 * q1) + q2 + 4) >> 3);
            plane[q0 + step] = (byte)((p0 + q0v + q1 + q2 + 2) >> 2);
            plane[q0 + (2 * step)] = (byte)(((2 * q3) + (3 * q2) + q1 + q0v + p0 + 4) >> 3);
        }
        else
        {
            plane[q0] = (byte)(((2 * q1) + q0v + p1 + 2) >> 2);
        }
    }

    /// <summary>Clips to 8 bits.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The sample.</returns>
    private static byte Clip(int value) => (byte)Math.Clamp(value, 0, 255);
}
