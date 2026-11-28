// The half samples of one reference picture at every integer position, for motion-search cost evaluation: b (8-241)
// right of each sample, h (8-242) below it and j (8-243 to 8-245) diagonally, with reference samples outside the
// picture clamped to the edge (8-228, 8-229). A quarter sample averages two of these or a full sample (Table 8-12).
// A block whose samples, and the samples one to the right of and below them, lie inside the picture reads only
// values that equal H264InterPrediction.Luma's; other blocks are left to it.

namespace RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

/// <summary>Precomputed luma half-sample planes of a reference picture.</summary>
internal sealed class H264HalfSamplePlanes
{
    /// <summary>The plane width, the stride.</summary>
    private readonly int _width;

    /// <summary>The plane height.</summary>
    private readonly int _height;

    /// <summary>b at each integer position.</summary>
    private readonly byte[] _b;

    /// <summary>h at each integer position.</summary>
    private readonly byte[] _h;

    /// <summary>j at each integer position.</summary>
    private readonly byte[] _j;

    /// <summary>Unclipped horizontal 6-tap sums of six rows, for j.</summary>
    private readonly int[] _sums;

    /// <summary>The full-sample plane of the reference the planes were computed from.</summary>
    private byte[] _luma = [];

    /// <summary>The identity of that reference, or -1.</summary>
    private long _id = -1;

    /// <summary>Initializes a new instance of the <see cref="H264HalfSamplePlanes"/> class.</summary>
    /// <param name="width">The padded luma width.</param>
    /// <param name="height">The padded luma height.</param>
    public H264HalfSamplePlanes(int width, int height)
    {
        _width = width;
        _height = height;
        _b = new byte[width * height];
        _h = new byte[width * height];
        _j = new byte[width * height];
        _sums = new int[6 * width];
    }

    /// <summary>Gets the bytes the planes hold.</summary>
    /// <param name="width">The padded luma width.</param>
    /// <param name="height">The padded luma height.</param>
    /// <returns>The byte count.</returns>
    public static long ByteCount(int width, int height) => (3L * width * height) + (6L * 4 * width);

    /// <summary>Computes the planes of a reference unless they are already of it.</summary>
    /// <param name="reference">The reference picture, at the planes' size.</param>
    public void Prepare(H264EncoderPicture reference)
    {
        if (reference.Id == _id || reference.Width != _width || reference.Height != _height)
        {
            return;
        }

        var g = reference.Luma;
        int w = _width, height = _height;

        // Slot (v + 6) % 6 holds the horizontal sums of row Clamp(v): rows -2 to 2 first, then row y + 3 for output row y.
        for (var v = -2; v < 3; v++)
        {
            HorizontalSums(g, Math.Clamp(v, 0, height - 1), _sums.AsSpan(((v + 6) % 6) * w, w));
        }

        for (var y = 0; y < height; y++)
        {
            HorizontalSums(g, Math.Min(y + 3, height - 1), _sums.AsSpan(((y + 3) % 6) * w, w));
            ReadOnlySpan<int> s0 = _sums.AsSpan(((y + 4) % 6) * w, w), s1 = _sums.AsSpan(((y + 5) % 6) * w, w), s2 = _sums.AsSpan((y % 6) * w, w);
            ReadOnlySpan<int> s3 = _sums.AsSpan(((y + 1) % 6) * w, w), s4 = _sums.AsSpan(((y + 2) % 6) * w, w), s5 = _sums.AsSpan(((y + 3) % 6) * w, w);
            ReadOnlySpan<byte> g0 = Row(g, y - 2), g1 = Row(g, y - 1), g2 = Row(g, y), g3 = Row(g, y + 1), g4 = Row(g, y + 2), g5 = Row(g, y + 3);
            Span<byte> b = _b.AsSpan(y * w, w), h = _h.AsSpan(y * w, w), j = _j.AsSpan(y * w, w);
            for (var x = 0; x < w; x++)
            {
                b[x] = Clip((s2[x] + 16) >> 5);
                h[x] = Clip((g0[x] - (5 * g1[x]) + (20 * g2[x]) + (20 * g3[x]) - (5 * g4[x]) + g5[x] + 16) >> 5);
                j[x] = Clip((s0[x] - (5 * s1[x]) + (20 * s2[x]) + (20 * s3[x]) - (5 * s4[x]) + s5[x] + 512) >> 10);
            }
        }

        _luma = g;
        _id = reference.Id;
    }

    /// <summary>Predicts a square luma block of the prepared reference when every sample it reads is inside the picture.</summary>
    /// <param name="reference">The reference picture.</param>
    /// <param name="x">xIntL of the block's top-left sample.</param>
    /// <param name="y">yIntL.</param>
    /// <param name="fx">xFracL, 0 to 3.</param>
    /// <param name="fy">yFracL, 0 to 3.</param>
    /// <param name="size">The block side: 4, 8 or 16.</param>
    /// <param name="output">Receives size * size samples.</param>
    /// <returns>Whether the block was predicted; otherwise the caller predicts it with <see cref="H264InterPrediction.Luma"/>.</returns>
    public bool TryLuma(H264EncoderPicture reference, int x, int y, int fx, int fy, int size, Span<byte> output)
    {
        if (reference.Id != _id || x < 0 || y < 0 || x + size >= _width || y + size >= _height)
        {
            return false;
        }

        var w = _width;
        (byte[] First, int FirstOffset, byte[]? Second, int SecondOffset) source = (fx, fy) switch
        {
            (0, 0) => (_luma, 0, null, 0),
            (2, 0) => (_b, 0, null, 0),
            (0, 2) => (_h, 0, null, 0),
            (2, 2) => (_j, 0, null, 0),
            (0, 1) => (_luma, 0, _h, 0),
            (0, 3) => (_h, 0, _luma, w),
            (1, 0) => (_luma, 0, _b, 0),
            (3, 0) => (_b, 0, _luma, 1),
            (1, 1) => (_b, 0, _h, 0),
            (3, 1) => (_b, 0, _h, 1),
            (1, 3) => (_b, w, _h, 0),
            (3, 3) => (_b, w, _h, 1),
            (1, 2) => (_h, 0, _j, 0),
            (3, 2) => (_j, 0, _h, 1),
            (2, 1) => (_b, 0, _j, 0),
            _ => (_j, 0, _b, w),
        };

        for (var j = 0; j < size; j++)
        {
            var row = output.Slice(j * size, size);
            var a = source.First.AsSpan(((y + j) * w) + x + source.FirstOffset, size);
            if (source.Second is null)
            {
                a.CopyTo(row);
                continue;
            }

            var b = source.Second.AsSpan(((y + j) * w) + x + source.SecondOffset, size);
            for (var i = 0; i < size; i++)
            {
                row[i] = (byte)((a[i] + b[i] + 1) >> 1);
            }
        }

        return true;
    }

    /// <summary>Computes the unclipped horizontal 6-tap sums of one row, edge samples clamped.</summary>
    /// <param name="plane">The full-sample plane.</param>
    /// <param name="y">The row.</param>
    /// <param name="sums">Receives one sum per column.</param>
    private void HorizontalSums(byte[] plane, int y, Span<int> sums)
    {
        int w = _width, last = w - 1;
        ReadOnlySpan<byte> row = plane.AsSpan(y * w, w);
        for (var x = 0; x < w; x++)
        {
            if (x >= 2 && x + 3 <= last)
            {
                sums[x] = row[x - 2] - (5 * row[x - 1]) + (20 * row[x]) + (20 * row[x + 1]) - (5 * row[x + 2]) + row[x + 3];
            }
            else
            {
                sums[x] = row[Math.Max(x - 2, 0)] - (5 * row[Math.Max(x - 1, 0)]) + (20 * row[x]) + (20 * row[Math.Min(x + 1, last)]) - (5 * row[Math.Min(x + 2, last)]) + row[Math.Min(x + 3, last)];
            }
        }
    }

    /// <summary>Returns a full-sample row, clamped to the picture.</summary>
    /// <param name="plane">The plane.</param>
    /// <param name="y">The row, possibly outside the picture.</param>
    /// <returns>The row.</returns>
    private ReadOnlySpan<byte> Row(byte[] plane, int y) => plane.AsSpan(Math.Clamp(y, 0, _height - 1) * _width, _width);

    /// <summary>Clips to 8 bits.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The clipped value.</returns>
    private static byte Clip(int value) => (byte)Math.Clamp(value, 0, 255);
}
