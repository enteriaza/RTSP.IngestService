// Inverse transforms (ITU-T H.264 8.5.10, 8.5.12.2, 8.5.13.2) and the residual addition with clipping (8.5.14).

namespace RTSP.IngestService.Media.Codecs.Video.H264;

/// <summary>Inverse integer transforms.</summary>
internal static class H264Transform
{
    /// <summary>Applies the 4x4 Hadamard transform of the Intra16x16 DC levels (8-320).</summary>
    /// <param name="c">The levels, raster order.</param>
    /// <param name="f">Receives the transformed values, raster order.</param>
    public static void InverseHadamard4x4(ReadOnlySpan<int> c, Span<int> f)
    {
        Span<int> t = stackalloc int[16];
        for (var i = 0; i < 4; i++)
        {
            int c0 = c[i * 4], c1 = c[(i * 4) + 1], c2 = c[(i * 4) + 2], c3 = c[(i * 4) + 3];
            t[i * 4] = c0 + c1 + c2 + c3;
            t[(i * 4) + 1] = c0 + c1 - c2 - c3;
            t[(i * 4) + 2] = c0 - c1 - c2 + c3;
            t[(i * 4) + 3] = c0 - c1 + c2 - c3;
        }

        for (var j = 0; j < 4; j++)
        {
            int c0 = t[j], c1 = t[4 + j], c2 = t[8 + j], c3 = t[12 + j];
            f[j] = c0 + c1 + c2 + c3;
            f[4 + j] = c0 + c1 - c2 - c3;
            f[8 + j] = c0 - c1 - c2 + c3;
            f[12 + j] = c0 - c1 + c2 - c3;
        }
    }

    /// <summary>Inverse transforms a 4x4 block and adds it to the prediction in place.</summary>
    /// <param name="d">The scaled coefficients, raster order.</param>
    /// <param name="plane">The plane holding the prediction.</param>
    /// <param name="offset">The block's first sample.</param>
    /// <param name="stride">The plane stride.</param>
    public static void Add4x4(ReadOnlySpan<int> d, byte[] plane, int offset, int stride)
    {
        Span<int> h = stackalloc int[16];
        for (var i = 0; i < 4; i++)
        {
            int d0 = d[i * 4], d1 = d[(i * 4) + 1], d2 = d[(i * 4) + 2], d3 = d[(i * 4) + 3];
            int e0 = d0 + d2, e1 = d0 - d2, e2 = (d1 >> 1) - d3, e3 = d1 + (d3 >> 1);
            h[i * 4] = e0 + e3;
            h[(i * 4) + 1] = e1 + e2;
            h[(i * 4) + 2] = e1 - e2;
            h[(i * 4) + 3] = e0 - e3;
        }

        for (var j = 0; j < 4; j++)
        {
            int f0 = h[j], f1 = h[4 + j], f2 = h[8 + j], f3 = h[12 + j];
            int g0 = f0 + f2, g1 = f0 - f2, g2 = (f1 >> 1) - f3, g3 = f1 + (f3 >> 1);
            AddSample(plane, offset + j, g0 + g3);
            AddSample(plane, offset + stride + j, g1 + g2);
            AddSample(plane, offset + (2 * stride) + j, g1 - g2);
            AddSample(plane, offset + (3 * stride) + j, g0 - g3);
        }
    }

    /// <summary>Inverse transforms an 8x8 block and adds it to the prediction in place.</summary>
    /// <param name="d">The scaled coefficients, raster order.</param>
    /// <param name="plane">The plane holding the prediction.</param>
    /// <param name="offset">The block's first sample.</param>
    /// <param name="stride">The plane stride.</param>
    public static void Add8x8(ReadOnlySpan<int> d, byte[] plane, int offset, int stride)
    {
        Span<int> g = stackalloc int[64];
        Span<int> line = stackalloc int[8];
        for (var i = 0; i < 8; i++)
        {
            Transform8(d.Slice(i * 8, 8), line);
            line.CopyTo(g.Slice(i * 8, 8));
        }

        Span<int> column = stackalloc int[8];
        for (var j = 0; j < 8; j++)
        {
            for (var i = 0; i < 8; i++)
            {
                column[i] = g[(i * 8) + j];
            }

            Transform8(column, line);
            for (var i = 0; i < 8; i++)
            {
                AddSample(plane, offset + (i * stride) + j, line[i]);
            }
        }
    }

    /// <summary>The one-dimensional 8-point inverse transform (8-330 to 8-353).</summary>
    /// <param name="d">The input.</param>
    /// <param name="o">The output.</param>
    private static void Transform8(ReadOnlySpan<int> d, Span<int> o)
    {
        var a0 = d[0] + d[4];
        var a4 = d[0] - d[4];
        var a2 = (d[2] >> 1) - d[6];
        var a6 = d[2] + (d[6] >> 1);
        var b0 = a0 + a6;
        var b2 = a4 + a2;
        var b4 = a4 - a2;
        var b6 = a0 - a6;
        var a1 = -d[3] + d[5] - d[7] - (d[7] >> 1);
        var a3 = d[1] + d[7] - d[3] - (d[3] >> 1);
        var a5 = -d[1] + d[7] + d[5] + (d[5] >> 1);
        var a7 = d[3] + d[5] + d[1] + (d[1] >> 1);
        var b1 = a1 + (a7 >> 2);
        var b7 = a7 - (a1 >> 2);
        var b3 = a3 + (a5 >> 2);
        var b5 = (a3 >> 2) - a5;
        o[0] = b0 + b7;
        o[1] = b2 + b5;
        o[2] = b4 + b3;
        o[3] = b6 + b1;
        o[4] = b6 - b1;
        o[5] = b4 - b3;
        o[6] = b2 - b5;
        o[7] = b0 - b7;
    }

    /// <summary>Adds a residual sample, (value + 32) &gt;&gt; 6, with clipping.</summary>
    /// <param name="plane">The plane.</param>
    /// <param name="index">The sample index.</param>
    /// <param name="value">The unnormalised residual.</param>
    private static void AddSample(byte[] plane, int index, int value) =>
        plane[index] = (byte)Math.Clamp(plane[index] + ((value + 32) >> 6), 0, 255);
}
