// Fractional sample interpolation (ITU-T H.264 8.4.2.2). Luma uses the 6-tap filter (1, -5, 20, 20, -5, 1) at half
// positions and averages at quarter positions; chroma uses eighth-sample bilinear weights. Reference samples outside
// the picture take the value of the nearest edge sample (8-228, 8-229).

namespace RTSP.IngestService.Media.Codecs.Video.H264;

/// <summary>Luma and chroma sample interpolation.</summary>
internal static class H264InterPrediction
{
    /// <summary>Predicts a square luma block.</summary>
    /// <param name="plane">The reference luma plane, stride <paramref name="width"/>.</param>
    /// <param name="width">The plane width.</param>
    /// <param name="height">The plane height.</param>
    /// <param name="x">xIntL of the block's top-left sample.</param>
    /// <param name="y">yIntL.</param>
    /// <param name="fx">xFracL, 0 to 3.</param>
    /// <param name="fy">yFracL, 0 to 3.</param>
    /// <param name="size">The block side: 4, 8 or 16.</param>
    /// <param name="output">Receives size * size samples.</param>
    /// <remarks>
    /// Sample (i, j) of the block is G at window (i + 2, j + 2). b (8-241) is the horizontal half sample right of a
    /// window sample, h (8-242) the vertical half sample below it, s and m the b below and the h to the right, j
    /// (8-243 to 8-245) the centre half sample filtered from unclipped b sums; quarter samples average two of these
    /// (8-250 to 8-261, Table 8-12). Each fractional class computes only the values it uses.
    /// </remarks>
    public static void Luma(byte[] plane, int width, int height, int x, int y, int fx, int fy, int size, Span<byte> output)
    {
        var ws = size + 5;
        Span<byte> window = stackalloc byte[21 * 21];
        if (x >= 2 && y >= 2 && x + size + 3 <= width && y + size + 3 <= height)
        {
            for (var j = 0; j < ws; j++)
            {
                plane.AsSpan(((y + j - 2) * width) + x - 2, ws).CopyTo(window.Slice(j * ws, ws));
            }
        }
        else
        {
            for (var j = 0; j < ws; j++)
            {
                var row = Math.Clamp(y + j - 2, 0, height - 1) * width;
                for (var i = 0; i < ws; i++)
                {
                    window[(j * ws) + i] = plane[row + Math.Clamp(x + i - 2, 0, width - 1)];
                }
            }
        }

        if (fx == 0 && fy == 0)
        {
            for (var j = 0; j < size; j++)
            {
                window.Slice(((j + 2) * ws) + 2, size).CopyTo(output.Slice(j * size, size));
            }
        }
        else if (fy == 0)
        {
            // b, averaged with G (xFrac 1) or the G to its right (xFrac 3).
            for (var j = 0; j < size; j++)
            {
                for (var i = 0; i < size; i++)
                {
                    var k = ((j + 2) * ws) + i;
                    var b = Clip((Tap(window, k, 1) + 16) >> 5);
                    output[(j * size) + i] = (byte)(fx == 2 ? b : (window[k + 2 + (fx >> 1)] + b + 1) >> 1);
                }
            }
        }
        else if (fx == 0)
        {
            // h, averaged with G (yFrac 1) or the G below it (yFrac 3).
            for (var j = 0; j < size; j++)
            {
                for (var i = 0; i < size; i++)
                {
                    var k = (j * ws) + i + 2;
                    var h = Clip((Tap(window, k, ws) + 16) >> 5);
                    output[(j * size) + i] = (byte)(fy == 2 ? h : (window[k + ((2 + (fy >> 1)) * ws)] + h + 1) >> 1);
                }
            }
        }
        else if (fx != 2 && fy != 2)
        {
            // Diagonal quarter samples: b (yFrac 1) or s (yFrac 3), averaged with h (xFrac 1) or m (xFrac 3).
            int rowOffset = fy == 1 ? 2 : 3, columnOffset = fx == 1 ? 2 : 3;
            for (var j = 0; j < size; j++)
            {
                for (var i = 0; i < size; i++)
                {
                    var horizontal = Clip((Tap(window, ((j + rowOffset) * ws) + i, 1) + 16) >> 5);
                    var vertical = Clip((Tap(window, (j * ws) + i + columnOffset, ws) + 16) >> 5);
                    output[(j * size) + i] = (byte)((horizontal + vertical + 1) >> 1);
                }
            }
        }
        else
        {
            LumaCentre(window, ws, fx, fy, size, output);
        }
    }

    /// <summary>Predicts a square chroma block (8-266).</summary>
    /// <param name="plane">The reference chroma plane, stride <paramref name="width"/>.</param>
    /// <param name="width">The plane width.</param>
    /// <param name="height">The plane height.</param>
    /// <param name="x">xIntC of the block's top-left sample.</param>
    /// <param name="y">yIntC.</param>
    /// <param name="fx">xFracC, 0 to 7.</param>
    /// <param name="fy">yFracC, 0 to 7.</param>
    /// <param name="size">The block side: 2, 4 or 8.</param>
    /// <param name="output">Receives size * size samples.</param>
    public static void Chroma(byte[] plane, int width, int height, int x, int y, int fx, int fy, int size, Span<byte> output)
    {
        var wa = (8 - fx) * (8 - fy);
        var wb = fx * (8 - fy);
        var wc = (8 - fx) * fy;
        var wd = fx * fy;
        for (var j = 0; j < size; j++)
        {
            var r0 = Math.Clamp(y + j, 0, height - 1) * width;
            var r1 = Math.Clamp(y + j + 1, 0, height - 1) * width;
            for (var i = 0; i < size; i++)
            {
                var c0 = Math.Clamp(x + i, 0, width - 1);
                var c1 = Math.Clamp(x + i + 1, 0, width - 1);
                output[(j * size) + i] = (byte)(((wa * plane[r0 + c0]) + (wb * plane[r0 + c1]) + (wc * plane[r1 + c0]) + (wd * plane[r1 + c1]) + 32) >> 6);
            }
        }
    }

    /// <summary>Predicts the fractional classes that use the centre half sample j: (2, 2), and j averaged with b, s, h or m.</summary>
    /// <param name="window">Reference samples from (-2, -2), stride <paramref name="ws"/>.</param>
    /// <param name="ws">The window stride.</param>
    /// <param name="fx">xFracL.</param>
    /// <param name="fy">yFracL; one of the two is 2.</param>
    /// <param name="size">The block side.</param>
    /// <param name="output">Receives size * size samples.</param>
    private static void LumaCentre(ReadOnlySpan<byte> window, int ws, int fx, int fy, int size, Span<byte> output)
    {
        // Unclipped horizontal 6-tap sums for window rows -2 .. size + 2, stride size.
        Span<int> b1 = stackalloc int[21 * 16];
        for (var j = 0; j < ws; j++)
        {
            for (var i = 0; i < size; i++)
            {
                b1[(j * size) + i] = Tap(window, (j * ws) + i, 1);
            }
        }

        for (var j = 0; j < size; j++)
        {
            for (var i = 0; i < size; i++)
            {
                var centre = Clip((Tap(b1, (j * size) + i, size) + 512) >> 10);
                var other = (fx, fy) switch
                {
                    (2, 2) => centre,
                    (2, 1) => Clip((b1[((j + 2) * size) + i] + 16) >> 5),
                    (2, 3) => Clip((b1[((j + 3) * size) + i] + 16) >> 5),
                    (1, 2) => Clip((Tap(window, (j * ws) + i + 2, ws) + 16) >> 5),
                    _ => Clip((Tap(window, (j * ws) + i + 3, ws) + 16) >> 5),
                };
                output[(j * size) + i] = (byte)((centre + other + 1) >> 1);
            }
        }
    }

    /// <summary>Applies the 6-tap filter (1, -5, 20, 20, -5, 1) to six samples.</summary>
    /// <param name="samples">The samples.</param>
    /// <param name="k">The first sample.</param>
    /// <param name="stride">The distance between taps: 1 horizontally, the row stride vertically.</param>
    /// <returns>The unclipped, unrounded sum.</returns>
    private static int Tap(ReadOnlySpan<byte> samples, int k, int stride) =>
        samples[k] - (5 * samples[k + stride]) + (20 * samples[k + (2 * stride)]) + (20 * samples[k + (3 * stride)]) - (5 * samples[k + (4 * stride)]) + samples[k + (5 * stride)];

    /// <summary>Applies the 6-tap filter to six intermediate sums.</summary>
    /// <param name="sums">The sums.</param>
    /// <param name="k">The first sum.</param>
    /// <param name="stride">The distance between taps.</param>
    /// <returns>The unclipped, unrounded sum.</returns>
    private static int Tap(ReadOnlySpan<int> sums, int k, int stride) =>
        sums[k] - (5 * sums[k + stride]) + (20 * sums[k + (2 * stride)]) + (20 * sums[k + (3 * stride)]) - (5 * sums[k + (4 * stride)]) + sums[k + (5 * stride)];

    /// <summary>Clips to 8 bits.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The clipped value.</returns>
    private static int Clip(int value) => Math.Clamp(value, 0, 255);
}
