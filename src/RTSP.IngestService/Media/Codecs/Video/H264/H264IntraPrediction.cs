// Intra sample prediction (ITU-T H.264 8.3.1.2, 8.3.2.2, 8.3.3, 8.3.4). Each method writes the prediction into the
// picture and returns false when the mode needs neighbouring samples that are not available, which the caller
// reports as invalid data. Intra4x4 and Intra8x8 share one implementation over the edge samples; Intra8x8 filters
// them first (8.3.2.2.1).

namespace RTSP.IngestService.Media.Codecs.Video.H264;

/// <summary>Intra prediction.</summary>
internal static class H264IntraPrediction
{
    /// <summary>Predicts an Intra4x4 block.</summary>
    /// <param name="plane">The luma plane.</param>
    /// <param name="stride">The stride.</param>
    /// <param name="offset">The block's first sample.</param>
    /// <param name="mode">Intra4x4PredMode.</param>
    /// <param name="left">Whether the left samples are available.</param>
    /// <param name="top">Whether the top samples are available.</param>
    /// <param name="topRight">Whether the top-right samples are available.</param>
    /// <param name="topLeft">Whether the top-left sample is available.</param>
    /// <returns><see langword="false"/> when the mode is not usable.</returns>
    public static bool Predict4x4(byte[] plane, int stride, int offset, int mode, bool left, bool top, bool topRight, bool topLeft)
    {
        Span<int> edge = stackalloc int[4 + 1 + 8];
        LoadEdge(plane, stride, offset, 4, left, top, topRight, topLeft, edge);
        return PredictNxN(plane, stride, offset, 4, mode, left, top, topLeft, edge);
    }

    /// <summary>Predicts an Intra8x8 block, filtering the reference samples first.</summary>
    /// <param name="plane">The luma plane.</param>
    /// <param name="stride">The stride.</param>
    /// <param name="offset">The block's first sample.</param>
    /// <param name="mode">Intra8x8PredMode.</param>
    /// <param name="left">Whether the left samples are available.</param>
    /// <param name="top">Whether the top samples are available.</param>
    /// <param name="topRight">Whether the top-right samples are available.</param>
    /// <param name="topLeft">Whether the top-left sample is available.</param>
    /// <returns><see langword="false"/> when the mode is not usable.</returns>
    public static bool Predict8x8(byte[] plane, int stride, int offset, int mode, bool left, bool top, bool topRight, bool topLeft)
    {
        Span<int> raw = stackalloc int[8 + 1 + 16];
        LoadEdge(plane, stride, offset, 8, left, top, topRight, topLeft, raw);
        Span<int> edge = stackalloc int[8 + 1 + 16];
        raw.CopyTo(edge);
        const int c = 8;
        if (top)
        {
            edge[c + 1] = topLeft ? (raw[c] + (2 * raw[c + 1]) + raw[c + 2] + 2) >> 2 : ((3 * raw[c + 1]) + raw[c + 2] + 2) >> 2;
            for (var x = 1; x < 15; x++)
            {
                edge[c + 1 + x] = (raw[c + x] + (2 * raw[c + 1 + x]) + raw[c + 2 + x] + 2) >> 2;
            }

            edge[c + 16] = (raw[c + 15] + (3 * raw[c + 16]) + 2) >> 2;
        }

        if (topLeft)
        {
            edge[c] = (top, left) switch
            {
                (true, true) => (raw[c + 1] + (2 * raw[c]) + raw[c - 1] + 2) >> 2,
                (true, false) => ((3 * raw[c]) + raw[c + 1] + 2) >> 2,
                (false, true) => ((3 * raw[c]) + raw[c - 1] + 2) >> 2,
                _ => raw[c],
            };
        }

        if (left)
        {
            edge[c - 1] = topLeft ? (raw[c] + (2 * raw[c - 1]) + raw[c - 2] + 2) >> 2 : ((3 * raw[c - 1]) + raw[c - 2] + 2) >> 2;
            for (var y = 1; y < 7; y++)
            {
                edge[c - 1 - y] = (raw[c - y] + (2 * raw[c - 1 - y]) + raw[c - 2 - y] + 2) >> 2;
            }

            edge[0] = (raw[1] + (3 * raw[0]) + 2) >> 2;
        }

        return PredictNxN(plane, stride, offset, 8, mode, left, top, topLeft, edge);
    }

    /// <summary>Predicts an Intra16x16 macroblock.</summary>
    /// <param name="plane">The luma plane.</param>
    /// <param name="stride">The stride.</param>
    /// <param name="offset">The macroblock's first sample.</param>
    /// <param name="mode">Intra16x16PredMode.</param>
    /// <param name="left">Whether the left macroblock is available.</param>
    /// <param name="top">Whether the top macroblock is available.</param>
    /// <param name="topLeft">Whether the top-left macroblock is available.</param>
    /// <returns><see langword="false"/> when the mode is not usable.</returns>
    public static bool Predict16x16(byte[] plane, int stride, int offset, int mode, bool left, bool top, bool topLeft) =>
        PredictBlock(plane, stride, offset, 16, mode, left, top, topLeft, false);

    /// <summary>Predicts an 8x8 chroma block of a 4:2:0 macroblock.</summary>
    /// <param name="plane">The chroma plane.</param>
    /// <param name="stride">The stride.</param>
    /// <param name="offset">The block's first sample.</param>
    /// <param name="mode">intra_chroma_pred_mode (0 DC, 1 horizontal, 2 vertical, 3 plane).</param>
    /// <param name="left">Whether the left macroblock is available.</param>
    /// <param name="top">Whether the top macroblock is available.</param>
    /// <param name="topLeft">Whether the top-left macroblock is available.</param>
    /// <returns><see langword="false"/> when the mode is not usable.</returns>
    public static bool PredictChroma(byte[] plane, int stride, int offset, int mode, bool left, bool top, bool topLeft)
    {
        if (mode != 0)
        {
            return PredictBlock(plane, stride, offset, 8, mode switch { 1 => 1, 2 => 0, _ => 3 }, left, top, topLeft, true);
        }

        for (var b = 0; b < 4; b++)
        {
            int xo = (b & 1) * 4, yo = (b >> 1) * 4;
            int sumTop = 0, sumLeft = 0;
            for (var i = 0; i < 4; i++)
            {
                if (top)
                {
                    sumTop += plane[offset - stride + xo + i];
                }

                if (left)
                {
                    sumLeft += plane[offset + ((yo + i) * stride) - 1];
                }
            }

            int value;
            if (b is 0 or 3)
            {
                value = top && left ? (sumTop + sumLeft + 4) >> 3 : left ? (sumLeft + 2) >> 2 : top ? (sumTop + 2) >> 2 : 128;
            }
            else if (b == 1)
            {
                value = top ? (sumTop + 2) >> 2 : left ? (sumLeft + 2) >> 2 : 128;
            }
            else
            {
                value = left ? (sumLeft + 2) >> 2 : top ? (sumTop + 2) >> 2 : 128;
            }

            for (var y = 0; y < 4; y++)
            {
                plane.AsSpan(offset + ((yo + y) * stride) + xo, 4).Fill((byte)value);
            }
        }

        return true;
    }

    /// <summary>
    /// Loads the edge samples: <c>edge[n - 1 - y]</c> = p[-1, y], <c>edge[n]</c> = p[-1, -1] and
    /// <c>edge[n + 1 + x]</c> = p[x, -1] for x below 2n, substituting p[n - 1, -1] when the top right is missing.
    /// </summary>
    /// <param name="plane">The plane.</param>
    /// <param name="stride">The stride.</param>
    /// <param name="offset">The block's first sample.</param>
    /// <param name="n">The block side.</param>
    /// <param name="left">Whether the left samples are available.</param>
    /// <param name="top">Whether the top samples are available.</param>
    /// <param name="topRight">Whether the top-right samples are available.</param>
    /// <param name="topLeft">Whether the top-left sample is available.</param>
    /// <param name="edge">Receives 3n + 1 samples.</param>
    private static void LoadEdge(byte[] plane, int stride, int offset, int n, bool left, bool top, bool topRight, bool topLeft, Span<int> edge)
    {
        if (left)
        {
            for (var y = 0; y < n; y++)
            {
                edge[n - 1 - y] = plane[offset + (y * stride) - 1];
            }
        }

        if (topLeft)
        {
            edge[n] = plane[offset - stride - 1];
        }

        if (top)
        {
            for (var x = 0; x < 2 * n; x++)
            {
                edge[n + 1 + x] = x < n || topRight ? plane[offset - stride + x] : plane[offset - stride + n - 1];
            }
        }
    }

    /// <summary>Applies an Intra4x4 or Intra8x8 mode from the edge samples (8-46 to 8-77, 8-78 to 8-106).</summary>
    /// <param name="plane">The plane.</param>
    /// <param name="stride">The stride.</param>
    /// <param name="offset">The block's first sample.</param>
    /// <param name="n">The block side (4 or 8).</param>
    /// <param name="mode">The prediction mode.</param>
    /// <param name="left">Whether the left samples are available.</param>
    /// <param name="top">Whether the top samples are available.</param>
    /// <param name="topLeft">Whether the top-left sample is available.</param>
    /// <param name="edge">The edge samples, laid out as by <see cref="LoadEdge"/>.</param>
    /// <returns><see langword="false"/> when the mode is not usable.</returns>
    private static bool PredictNxN(byte[] plane, int stride, int offset, int n, int mode, bool left, bool top, bool topLeft, ReadOnlySpan<int> edge)
    {
        var needed = mode switch
        {
            0 or 3 or 7 => top,
            1 or 8 => left,
            2 => true,
            4 or 5 or 6 => top && left && topLeft,
            _ => false,
        };
        if (!needed)
        {
            return false;
        }

        var log2 = n == 4 ? 2 : 3;
        var dc = 128;
        if (mode == 2)
        {
            int sumTop = 0, sumLeft = 0;
            for (var i = 0; i < n; i++)
            {
                sumTop += edge[n + 1 + i];
                sumLeft += edge[n - 1 - i];
            }

            dc = top && left ? (sumTop + sumLeft + n) >> (log2 + 1) : top ? (sumTop + (n >> 1)) >> log2 : left ? (sumLeft + (n >> 1)) >> log2 : 128;
        }

        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                int value;
                switch (mode)
                {
                    case 0:
                        value = Top(edge, n, x);
                        break;
                    case 1:
                        value = Left(edge, n, y);
                        break;
                    case 2:
                        value = dc;
                        break;
                    case 3:
                        value = x == n - 1 && y == n - 1 ? (Top(edge, n, (2 * n) - 2) + (3 * Top(edge, n, (2 * n) - 1)) + 2) >> 2 : Filter3(Top(edge, n, x + y), Top(edge, n, x + y + 1), Top(edge, n, x + y + 2));
                        break;
                    case 4:
                        {
                            var k = n + x - y;
                            value = (edge[k - 1] + (2 * edge[k]) + edge[k + 1] + 2) >> 2;
                            break;
                        }

                    case 5:
                        {
                            var z = (2 * x) - y;
                            value = z < -1
                                ? Filter3(Left(edge, n, y - (2 * x) - 1), Left(edge, n, y - (2 * x) - 2), Left(edge, n, y - (2 * x) - 3))
                                : z == -1
                                    ? Filter3(Left(edge, n, 0), Top(edge, n, -1), Top(edge, n, 0))
                                    : (z & 1) == 0
                                        ? (Top(edge, n, x - (y >> 1) - 1) + Top(edge, n, x - (y >> 1)) + 1) >> 1
                                        : Filter3(Top(edge, n, x - (y >> 1) - 2), Top(edge, n, x - (y >> 1) - 1), Top(edge, n, x - (y >> 1)));
                            break;
                        }

                    case 6:
                        {
                            var z = (2 * y) - x;
                            value = z < -1
                                ? Filter3(Top(edge, n, x - (2 * y) - 1), Top(edge, n, x - (2 * y) - 2), Top(edge, n, x - (2 * y) - 3))
                                : z == -1
                                    ? Filter3(Left(edge, n, 0), Top(edge, n, -1), Top(edge, n, 0))
                                    : (z & 1) == 0
                                        ? (Left(edge, n, y - (x >> 1) - 1) + Left(edge, n, y - (x >> 1)) + 1) >> 1
                                        : Filter3(Left(edge, n, y - (x >> 1) - 2), Left(edge, n, y - (x >> 1) - 1), Left(edge, n, y - (x >> 1)));
                            break;
                        }

                    case 7:
                        value = (y & 1) == 0
                            ? (Top(edge, n, x + (y >> 1)) + Top(edge, n, x + (y >> 1) + 1) + 1) >> 1
                            : Filter3(Top(edge, n, x + (y >> 1)), Top(edge, n, x + (y >> 1) + 1), Top(edge, n, x + (y >> 1) + 2));
                        break;
                    default:
                        {
                            var z = x + (2 * y);
                            var limit = (2 * n) - 3;
                            value = z > limit
                                ? Left(edge, n, n - 1)
                                : z == limit
                                    ? (Left(edge, n, n - 2) + (3 * Left(edge, n, n - 1)) + 2) >> 2
                                    : (z & 1) == 0
                                        ? (Left(edge, n, y + (x >> 1)) + Left(edge, n, y + (x >> 1) + 1) + 1) >> 1
                                        : Filter3(Left(edge, n, y + (x >> 1)), Left(edge, n, y + (x >> 1) + 1), Left(edge, n, y + (x >> 1) + 2));
                            break;
                        }
                }

                plane[offset + (y * stride) + x] = (byte)value;
            }
        }

        return true;
    }

    /// <summary>Returns p[x, -1] from the edge samples.</summary>
    /// <param name="edge">The edge samples.</param>
    /// <param name="n">The block side.</param>
    /// <param name="x">x, from -1.</param>
    /// <returns>The sample.</returns>
    private static int Top(ReadOnlySpan<int> edge, int n, int x) => edge[n + 1 + x];

    /// <summary>Returns p[-1, y] from the edge samples.</summary>
    /// <param name="edge">The edge samples.</param>
    /// <param name="n">The block side.</param>
    /// <param name="y">y, from -1.</param>
    /// <returns>The sample.</returns>
    private static int Left(ReadOnlySpan<int> edge, int n, int y) => edge[n - 1 - y];

    /// <summary>The three-tap filter (a + 2b + c + 2) &gt;&gt; 2.</summary>
    /// <param name="a">a.</param>
    /// <param name="b">b.</param>
    /// <param name="c">c.</param>
    /// <returns>The filtered value.</returns>
    private static int Filter3(int a, int b, int c) => (a + (2 * b) + c + 2) >> 2;

    /// <summary>Applies vertical, horizontal, DC or plane prediction to a 16x16 luma or 8x8 chroma block.</summary>
    /// <param name="plane">The plane.</param>
    /// <param name="stride">The stride.</param>
    /// <param name="offset">The block's first sample.</param>
    /// <param name="n">16 or 8.</param>
    /// <param name="mode">0 vertical, 1 horizontal, 2 DC, 3 plane.</param>
    /// <param name="left">Whether the left samples are available.</param>
    /// <param name="top">Whether the top samples are available.</param>
    /// <param name="topLeft">Whether the top-left sample is available.</param>
    /// <param name="chroma">Whether this is a chroma block (plane coefficients 34 instead of 5).</param>
    /// <returns><see langword="false"/> when the mode is not usable.</returns>
    private static bool PredictBlock(byte[] plane, int stride, int offset, int n, int mode, bool left, bool top, bool topLeft, bool chroma)
    {
        switch (mode)
        {
            case 0:
                if (!top)
                {
                    return false;
                }

                for (var y = 0; y < n; y++)
                {
                    Array.Copy(plane, offset - stride, plane, offset + (y * stride), n);
                }

                return true;
            case 1:
                if (!left)
                {
                    return false;
                }

                for (var y = 0; y < n; y++)
                {
                    var row = offset + (y * stride);
                    plane.AsSpan(row, n).Fill(plane[row - 1]);
                }

                return true;
            case 2:
                {
                    int sumTop = 0, sumLeft = 0;
                    for (var i = 0; i < n; i++)
                    {
                        sumTop += top ? plane[offset - stride + i] : 0;
                        sumLeft += left ? plane[offset + (i * stride) - 1] : 0;
                    }

                    var value = top && left ? (sumTop + sumLeft + 16) >> 5 : top ? (sumTop + 8) >> 4 : left ? (sumLeft + 8) >> 4 : 128;
                    for (var y = 0; y < n; y++)
                    {
                        plane.AsSpan(offset + (y * stride), n).Fill((byte)value);
                    }

                    return true;
                }

            case 3:
                {
                    if (!top || !left || !topLeft)
                    {
                        return false;
                    }

                    var half = n / 2;
                    int h = 0, v = 0;
                    for (var i = 0; i < half; i++)
                    {
                        h += (i + 1) * (plane[offset - stride + half + i] - plane[offset - stride + half - 2 - i]);
                        v += (i + 1) * (plane[offset + ((half + i) * stride) - 1] - plane[offset + ((half - 2 - i) * stride) - 1]);
                    }

                    var a = 16 * (plane[offset + ((n - 1) * stride) - 1] + plane[offset - stride + n - 1]);
                    var factor = chroma ? 34 : 5;
                    var b = ((factor * h) + 32) >> 6;
                    var c = ((factor * v) + 32) >> 6;
                    for (var y = 0; y < n; y++)
                    {
                        for (var x = 0; x < n; x++)
                        {
                            plane[offset + (y * stride) + x] = (byte)Math.Clamp((a + (b * (x - half + 1)) + (c * (y - half + 1)) + 16) >> 5, 0, 255);
                        }
                    }

                    return true;
                }

            default:
                return false;
        }
    }
}
