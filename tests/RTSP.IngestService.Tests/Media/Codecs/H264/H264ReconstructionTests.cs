// Inverse transforms, intra prediction and fractional-sample interpolation of the managed H.264 decoder, checked
// against values derived by hand from the equations of ITU-T H.264 clause 8.

using RTSP.IngestService.Media.Codecs.Video.H264;

namespace RTSP.IngestService.Tests.Media.Codecs.H264;

/// <summary>Reconstruction primitive tests.</summary>
public sealed class H264ReconstructionTests
{
    /// <summary>The 4x4 Hadamard transform is its own inverse up to a factor of 16.</summary>
    [Fact]
    public void HadamardIsSelfInverse()
    {
        var random = new Random(7);
        var input = new int[16];
        for (var i = 0; i < 16; i++)
        {
            input[i] = random.Next(-2048, 2048);
        }

        var once = new int[16];
        var twice = new int[16];
        H264Transform.InverseHadamard4x4(input, once);
        H264Transform.InverseHadamard4x4(once, twice);
        Assert.Equal(input.Select(static v => v * 16), twice);
    }

    /// <summary>A DC-only coefficient adds (d + 32) &gt;&gt; 6 to every sample of a 4x4 or 8x8 block.</summary>
    /// <param name="size">The block size.</param>
    /// <param name="dc">The scaled DC coefficient.</param>
    /// <param name="expected">The expected residual.</param>
    [Theory]
    [InlineData(4, 640, 10)]
    [InlineData(4, -640, -10)]
    [InlineData(4, 31, 0)]
    [InlineData(4, 32, 1)]
    [InlineData(8, 640, 10)]
    [InlineData(8, -33, -1)]
    public void DcResidualIsUniform(int size, int dc, int expected)
    {
        var plane = Enumerable.Repeat((byte)100, size * size).ToArray();
        var d = new int[size * size];
        d[0] = dc;
        if (size == 4)
        {
            H264Transform.Add4x4(d, plane, 0, 4);
        }
        else
        {
            H264Transform.Add8x8(d, plane, 0, 8);
        }

        Assert.All(plane, v => Assert.Equal(100 + expected, v));
    }

    /// <summary>Reconstructed samples are clipped to 0 and 255.</summary>
    [Fact]
    public void ResidualIsClipped()
    {
        var plane = new byte[16];
        Array.Fill(plane, (byte)250, 0, 8);
        Array.Fill(plane, (byte)5, 8, 8);
        var d = new int[16];
        d[0] = 640;
        H264Transform.Add4x4(d, plane, 0, 4);
        Assert.All(plane[..8], v => Assert.Equal(255, v));
        Assert.All(plane[8..], v => Assert.Equal(15, v));

        d[0] = -64 * 300;
        H264Transform.Add4x4(d, plane, 0, 4);
        Assert.All(plane, v => Assert.Equal(0, v));
    }

    /// <summary>Every Intra4x4 and Intra8x8 mode predicts a constant from constant neighbours.</summary>
    /// <param name="mode">The prediction mode.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void DirectionalModesPreserveFlatNeighbours(int mode)
    {
        foreach (var size in (int[])[4, 8])
        {
            const int stride = 32;
            var plane = Enumerable.Repeat((byte)77, stride * 32).ToArray();
            var offset = (8 * stride) + 8;
            for (var y = 0; y < size; y++)
            {
                plane.AsSpan(offset + (y * stride), size).Clear();
            }

            var ok = size == 4
                ? H264IntraPrediction.Predict4x4(plane, stride, offset, mode, true, true, true, true)
                : H264IntraPrediction.Predict8x8(plane, stride, offset, mode, true, true, true, true);
            Assert.True(ok);
            for (var y = 0; y < size; y++)
            {
                Assert.All(plane.AsSpan(offset + (y * stride), size).ToArray(), v => Assert.Equal(77, v));
            }
        }
    }

    /// <summary>Intra4x4 vertical, horizontal and DC follow 8-46, 8-47 and 8-48 to 8-51.</summary>
    [Fact]
    public void Intra4x4BasicModes()
    {
        const int stride = 16;
        var plane = new byte[stride * 8];
        var offset = stride + 4;
        byte[] top = [10, 20, 30, 40];
        byte[] left = [50, 60, 70, 80];
        top.CopyTo(plane, offset - stride);
        for (var y = 0; y < 4; y++)
        {
            plane[offset + (y * stride) - 1] = left[y];
        }

        Assert.True(H264IntraPrediction.Predict4x4(plane, stride, offset, 0, true, true, false, true));
        AssertBlock(plane, stride, offset, 4, (x, _) => top[x]);

        Assert.True(H264IntraPrediction.Predict4x4(plane, stride, offset, 1, true, true, false, true));
        AssertBlock(plane, stride, offset, 4, (_, y) => left[y]);

        Assert.True(H264IntraPrediction.Predict4x4(plane, stride, offset, 2, true, true, false, true));
        AssertBlock(plane, stride, offset, 4, (_, _) => (100 + 260 + 4) >> 3);

        Assert.True(H264IntraPrediction.Predict4x4(plane, stride, offset, 2, false, true, false, false));
        AssertBlock(plane, stride, offset, 4, (_, _) => (100 + 2) >> 2);

        Assert.True(H264IntraPrediction.Predict4x4(plane, stride, offset, 2, false, false, false, false));
        AssertBlock(plane, stride, offset, 4, (_, _) => 128);

        Assert.False(H264IntraPrediction.Predict4x4(plane, stride, offset, 0, true, false, false, false));
        Assert.False(H264IntraPrediction.Predict4x4(plane, stride, offset, 1, false, true, false, false));
        Assert.False(H264IntraPrediction.Predict4x4(plane, stride, offset, 4, true, true, false, false));
    }

    /// <summary>Intra16x16 plane prediction reproduces a flat area, and the modes check availability.</summary>
    [Fact]
    public void Intra16x16Modes()
    {
        const int stride = 48;
        var plane = Enumerable.Repeat((byte)90, stride * 48).ToArray();
        var offset = (16 * stride) + 16;
        Assert.True(H264IntraPrediction.Predict16x16(plane, stride, offset, 3, true, true, true));
        AssertBlock(plane, stride, offset, 16, (_, _) => 90);

        Assert.True(H264IntraPrediction.Predict16x16(plane, stride, offset, 2, false, false, false));
        AssertBlock(plane, stride, offset, 16, (_, _) => 128);
        Assert.False(H264IntraPrediction.Predict16x16(plane, stride, offset, 3, true, true, false));
        Assert.False(H264IntraPrediction.Predict16x16(plane, stride, offset, 0, true, false, false));
    }

    /// <summary>Chroma DC prediction uses the per-quadrant neighbour rules of 8.3.4.1 to 8.3.4.3.</summary>
    [Fact]
    public void ChromaDcQuadrants()
    {
        const int stride = 16;
        var plane = new byte[stride * 16];
        var offset = stride + 4;
        for (var x = 0; x < 8; x++)
        {
            plane[offset - stride + x] = (byte)(x < 4 ? 10 : 50);
        }

        for (var y = 0; y < 8; y++)
        {
            plane[offset + (y * stride) - 1] = (byte)(y < 4 ? 20 : 60);
        }

        Assert.True(H264IntraPrediction.PredictChroma(plane, stride, offset, 0, false, true, false));
        AssertBlock(plane, stride, offset, 8, (x, _) => x < 4 ? 10 : 50);

        Assert.True(H264IntraPrediction.PredictChroma(plane, stride, offset, 0, true, false, false));
        AssertBlock(plane, stride, offset, 8, (_, y) => y < 4 ? 20 : 60);

        Assert.True(H264IntraPrediction.PredictChroma(plane, stride, offset, 0, true, true, true));
        AssertBlock(plane, stride, offset, 8, static (x, y) => (x < 4, y < 4) switch
        {
            (true, true) => (40 + 80 + 4) >> 3,
            (false, true) => 50,
            (true, false) => 60,
            _ => (200 + 240 + 4) >> 3,
        });
    }

    /// <summary>Integer-position luma prediction copies samples and replicates the picture edge.</summary>
    [Fact]
    public void LumaIntegerPositionClampsToEdge()
    {
        const int width = 16, height = 16;
        var plane = new byte[width * height];
        for (var i = 0; i < plane.Length; i++)
        {
            plane[i] = (byte)i;
        }

        var output = new byte[16];
        H264InterPrediction.Luma(plane, width, height, -3, 14, 0, 0, 4, output);
        for (var j = 0; j < 4; j++)
        {
            for (var i = 0; i < 4; i++)
            {
                Assert.Equal(plane[(Math.Min(14 + j, 15) * width) + Math.Max(i - 3, 0)], output[(j * 4) + i]);
            }
        }
    }

    /// <summary>On a horizontal ramp the 6-tap half sample is the exact midpoint and the quarter sample averages (8-241, 8-250).</summary>
    [Fact]
    public void LumaHalfAndQuarterSamplesOnRamp()
    {
        const int width = 32, height = 16;
        var plane = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                plane[(y * width) + x] = (byte)(8 * x);
            }
        }

        var output = new byte[16];
        H264InterPrediction.Luma(plane, width, height, 8, 4, 2, 0, 4, output);
        Assert.Equal(Expected(static x => (8 * x) + 4), output);
        H264InterPrediction.Luma(plane, width, height, 8, 4, 1, 0, 4, output);
        Assert.Equal(Expected(static x => (8 * x) + 2), output);
        H264InterPrediction.Luma(plane, width, height, 8, 4, 0, 2, 4, output);
        Assert.Equal(Expected(static x => 8 * x), output);
        H264InterPrediction.Luma(plane, width, height, 8, 4, 2, 2, 4, output);
        Assert.Equal(Expected(static x => (8 * x) + 4), output);

        static byte[] Expected(Func<int, int> value)
        {
            var result = new byte[16];
            for (var j = 0; j < 4; j++)
            {
                for (var i = 0; i < 4; i++)
                {
                    result[(j * 4) + i] = (byte)value(8 + i);
                }
            }

            return result;
        }
    }

    /// <summary>Every fractional luma position of a flat reference is flat.</summary>
    [Fact]
    public void LumaFractionsPreserveFlatReference()
    {
        var plane = Enumerable.Repeat((byte)200, 16 * 16).ToArray();
        var output = new byte[64];
        for (var fy = 0; fy < 4; fy++)
        {
            for (var fx = 0; fx < 4; fx++)
            {
                H264InterPrediction.Luma(plane, 16, 16, -6, 11, fx, fy, 8, output);
                Assert.All(output, v => Assert.Equal(200, v));
            }
        }
    }

    /// <summary>
    /// Every fractional luma position, at every block size, inside the picture, across its edges and far outside it,
    /// equals a per-sample evaluation of 8-241 to 8-261 and Table 8-12 on edge-clamped reference samples.
    /// </summary>
    [Fact]
    public void LumaMatchesPerSampleEquations()
    {
        const int width = 48, height = 40;
        var random = new Random(11);
        var plane = new byte[width * height];
        random.NextBytes(plane);
        var output = new byte[256];
        for (var trial = 0; trial < 60; trial++)
        {
            int x = random.Next(-24, width + 8), y = random.Next(-24, height + 8);
            foreach (var size in new[] { 4, 8, 16 })
            {
                for (var fy = 0; fy < 4; fy++)
                {
                    for (var fx = 0; fx < 4; fx++)
                    {
                        H264InterPrediction.Luma(plane, width, height, x, y, fx, fy, size, output);
                        for (var j = 0; j < size; j++)
                        {
                            for (var i = 0; i < size; i++)
                            {
                                var expected = LumaSample(plane, width, height, x + i, y + j, fx, fy);
                                Assert.True(expected == output[(j * size) + i], $"({x}, {y}) size {size} frac ({fx}, {fy}) sample ({i}, {j}) is {output[(j * size) + i]}, expected {expected}.");
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>Chroma prediction is the eighth-sample bilinear blend of 8-266.</summary>
    [Fact]
    public void ChromaBilinear()
    {
        byte[] plane = [0, 64, 128, 192];
        var output = new byte[4];
        H264InterPrediction.Chroma(plane, 2, 2, 0, 0, 4, 4, 2, output);
        Assert.Equal((byte)(((16 * 0) + (16 * 64) + (16 * 128) + (16 * 192) + 32) >> 6), output[0]);
        Assert.Equal((byte)(((16 * 64) + (16 * 64) + (16 * 192) + (16 * 192) + 32) >> 6), output[1]);

        H264InterPrediction.Chroma(plane, 2, 2, 0, 0, 1, 7, 1, output);
        Assert.Equal((byte)(((7 * 1 * 0) + (1 * 1 * 64) + (7 * 7 * 128) + (1 * 7 * 192) + 32) >> 6), output[0]);
    }

    /// <summary>Evaluates one luma prediction sample directly from the equations of 8.4.2.2.1.</summary>
    /// <param name="plane">The reference plane.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="x">The integer sample x of G.</param>
    /// <param name="y">The integer sample y of G.</param>
    /// <param name="fx">xFracL.</param>
    /// <param name="fy">yFracL.</param>
    /// <returns>The predicted sample.</returns>
    private static int LumaSample(byte[] plane, int width, int height, int x, int y, int fx, int fy)
    {
        int G(int dx, int dy) => plane[(Math.Clamp(y + dy, 0, height - 1) * width) + Math.Clamp(x + dx, 0, width - 1)];
        static int Tap(Func<int, int> at) => at(-2) - (5 * at(-1)) + (20 * at(0)) + (20 * at(1)) - (5 * at(2)) + at(3);
        static int Clip(int v) => Math.Clamp(v, 0, 255);
        int B1(int dx, int dy) => Tap(t => G(dx + t, dy));
        int H1(int dx, int dy) => Tap(t => G(dx, dy + t));
        var b = Clip((B1(0, 0) + 16) >> 5);
        var h = Clip((H1(0, 0) + 16) >> 5);
        var s = Clip((B1(0, 1) + 16) >> 5);
        var m = Clip((H1(1, 0) + 16) >> 5);
        var j = Clip((Tap(t => B1(0, t)) + 512) >> 10);
        return (fx, fy) switch
        {
            (0, 0) => G(0, 0),
            (1, 0) => (G(0, 0) + b + 1) >> 1,
            (2, 0) => b,
            (3, 0) => (b + G(1, 0) + 1) >> 1,
            (0, 1) => (G(0, 0) + h + 1) >> 1,
            (0, 2) => h,
            (0, 3) => (h + G(0, 1) + 1) >> 1,
            (1, 1) => (b + h + 1) >> 1,
            (3, 1) => (b + m + 1) >> 1,
            (1, 3) => (h + s + 1) >> 1,
            (3, 3) => (s + m + 1) >> 1,
            (2, 2) => j,
            (2, 1) => (b + j + 1) >> 1,
            (2, 3) => (j + s + 1) >> 1,
            (1, 2) => (h + j + 1) >> 1,
            _ => (j + m + 1) >> 1,
        };
    }

    /// <summary>Asserts a block's samples.</summary>
    /// <param name="plane">The plane.</param>
    /// <param name="stride">The stride.</param>
    /// <param name="offset">The block's first sample.</param>
    /// <param name="size">The block side.</param>
    /// <param name="expected">The expected sample at (x, y).</param>
    private static void AssertBlock(byte[] plane, int stride, int offset, int size, Func<int, int, int> expected)
    {
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                Assert.True(expected(x, y) == plane[offset + (y * stride) + x], $"Sample ({x}, {y}) is {plane[offset + (y * stride) + x]}, expected {expected(x, y)}.");
            }
        }
    }
}
