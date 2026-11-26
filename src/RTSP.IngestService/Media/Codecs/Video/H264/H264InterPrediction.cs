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
    public static void Luma(byte[] plane, int width, int height, int x, int y, int fx, int fy, int size, Span<byte> output)
    {
        var ws = size + 5;
        Span<byte> window = stackalloc byte[21 * 21];
        for (var j = 0; j < ws; j++)
        {
            var row = Math.Clamp(y + j - 2, 0, height - 1) * width;
            for (var i = 0; i < ws; i++)
            {
                window[(j * ws) + i] = plane[row + Math.Clamp(x + i - 2, 0, width - 1)];
            }
        }

        if (fx == 0 && fy == 0)
        {
            for (var j = 0; j < size; j++)
            {
                window.Slice(((j + 2) * ws) + 2, size).CopyTo(output.Slice(j * size, size));
            }

            return;
        }

        // b1 for rows -2 .. size + 2 (horizontal half-sample sums), h1 for columns 0 .. size (vertical sums).
        Span<int> b1 = stackalloc int[21 * 16];
        Span<int> h1 = stackalloc int[16 * 17];
        for (var j = 0; j < ws; j++)
        {
            for (var i = 0; i < size; i++)
            {
                var k = (j * ws) + i;
                b1[(j * size) + i] = window[k] - (5 * window[k + 1]) + (20 * window[k + 2]) + (20 * window[k + 3]) - (5 * window[k + 4]) + window[k + 5];
            }
        }

        var hw = size + 1;
        for (var j = 0; j < size; j++)
        {
            for (var i = 0; i <= size; i++)
            {
                var k = (j * ws) + i + 2;
                h1[(j * hw) + i] = window[k] - (5 * window[k + ws]) + (20 * window[k + (2 * ws)]) + (20 * window[k + (3 * ws)]) - (5 * window[k + (4 * ws)]) + window[k + (5 * ws)];
            }
        }

        for (var j = 0; j < size; j++)
        {
            for (var i = 0; i < size; i++)
            {
                output[(j * size) + i] = (byte)Sample(window, ws, b1, h1, size, i, j, fx, fy);
            }
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

    /// <summary>Computes one luma prediction sample (8-241 to 8-261, Table 8-12).</summary>
    /// <param name="window">Reference samples from (-2, -2), stride <paramref name="ws"/>.</param>
    /// <param name="ws">The window stride.</param>
    /// <param name="b1">Horizontal intermediate sums for rows -2 onwards, stride <paramref name="size"/>.</param>
    /// <param name="h1">Vertical intermediate sums, stride <paramref name="size"/> + 1.</param>
    /// <param name="size">The block side.</param>
    /// <param name="i">Column.</param>
    /// <param name="j">Row.</param>
    /// <param name="fx">xFracL.</param>
    /// <param name="fy">yFracL.</param>
    /// <returns>The sample.</returns>
    private static int Sample(ReadOnlySpan<byte> window, int ws, ReadOnlySpan<int> b1, ReadOnlySpan<int> h1, int size, int i, int j, int fx, int fy)
    {
        var hw = size + 1;
        var g = window[((j + 2) * ws) + i + 2];
        var b = Clip((b1[((j + 2) * size) + i] + 16) >> 5);
        var h = Clip((h1[(j * hw) + i] + 16) >> 5);
        switch (fx, fy)
        {
            case (1, 0):
                return (g + b + 1) >> 1;
            case (2, 0):
                return b;
            case (3, 0):
                return (window[((j + 2) * ws) + i + 3] + b + 1) >> 1;
            case (0, 1):
                return (g + h + 1) >> 1;
            case (0, 2):
                return h;
            case (0, 3):
                return (window[((j + 3) * ws) + i + 2] + h + 1) >> 1;
        }

        var m = Clip((h1[(j * hw) + i + 1] + 16) >> 5);
        var s = Clip((b1[((j + 3) * size) + i] + 16) >> 5);
        switch (fx, fy)
        {
            case (1, 1):
                return (b + h + 1) >> 1;
            case (3, 1):
                return (b + m + 1) >> 1;
            case (1, 3):
                return (h + s + 1) >> 1;
            case (3, 3):
                return (m + s + 1) >> 1;
        }

        var k = (j * size) + i;
        var sum = b1[k] - (5 * b1[k + size]) + (20 * b1[k + (2 * size)]) + (20 * b1[k + (3 * size)]) - (5 * b1[k + (4 * size)]) + b1[k + (5 * size)];
        var center = Clip((sum + 512) >> 10);
        return (fx, fy) switch
        {
            (2, 2) => center,
            (2, 1) => (b + center + 1) >> 1,
            (2, 3) => (s + center + 1) >> 1,
            (1, 2) => (h + center + 1) >> 1,
            _ => (m + center + 1) >> 1,
        };
    }
    /// <summary>Clips to 8 bits.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The clipped value.</returns>
    private static int Clip(int value) => Math.Clamp(value, 0, 255);
}
