// Colour conversion to BT.709 limited-range 4:2:0. Thresholds fixed in the phase plan before evaluation: SDR
// conversions within 1 code value of an independent double-precision reference (runtime Math.Pow and Math.Exp,
// the published BT.2087 BT.2020-to-BT.709 matrix, primaries matrices derived here separately); tone mapping
// monotonic in luminance with output bounded to the nominal range; unspecified and HDR colour never converted
// without an explicit policy; unsupported code points rejected.

using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Video.Color;

namespace RTSP.IngestService.Tests.Media.Codecs.Color;

/// <summary>Managed colour conversion.</summary>
public sealed class VideoColorConverterTests
{
    /// <summary>Policies that accept everything.</summary>
    private static readonly VideoColorConversionOptions Permissive = new() { Unspecified = UnspecifiedColorPolicy.AssumeBt709, Hdr = HdrPolicy.ToneMap };

    /// <summary>Gets the SDR conversion cases: layout, bit depth, primaries, transfer, matrix, full range.</summary>
    public static TheoryData<string, int, int, int, int, bool> SdrCases => new()
    {
        { "Yuv420", 8, 1, 1, 1, true },
        { "Yuv420", 8, 6, 6, 6, false },
        { "Yuv420", 8, 5, 6, 5, true },
        { "Yuv422", 10, 9, 14, 9, false },
        { "Yuv444", 10, 1, 1, 1, false },
        { "Yuv444", 12, 9, 15, 9, true },
        { "Rgb", 8, 1, 13, 0, true },
        { "Rgb", 10, 9, 14, 0, false },
        { "Yuv420", 8, 1, 4, 1, false },
    };

    /// <summary>A BT.709 8-bit 4:2:0 limited-range image is copied unchanged.</summary>
    [Fact]
    public void CanonicalSourceIsCopied()
    {
        var image = Synthetic(VideoImageLayout.Yuv420, 8, VideoColorInfo.Bt709, 64, 32, out var planes);
        using var frame = new VideoFrame(64, 32, 0, new MediaTimeBase(1, 90000), VideoColorInfo.Bt709);
        VideoColorConverter.Convert(image, frame, Permissive, TestContext.Current.CancellationToken);
        Assert.Equal(planes[0].Select(static v => (byte)v), frame.GetPlane(0).ToArray());
        Assert.Equal(planes[1].Select(static v => (byte)v), frame.GetPlane(1).ToArray());
        Assert.Equal(planes[2].Select(static v => (byte)v), frame.GetPlane(2).ToArray());

        using var copy = new VideoFrame(64, 32, 0, new MediaTimeBase(1, 90000), VideoColorInfo.Bt709);
        VideoColorConverter.Convert(VideoImage.FromFrame(frame), copy, Permissive, TestContext.Current.CancellationToken);
        Assert.Equal(frame.GetPlane(0).ToArray(), copy.GetPlane(0).ToArray());
    }

    /// <summary>SDR conversions are within one code value of the double-precision reference.</summary>
    /// <param name="layout">The layout name.</param>
    /// <param name="depth">The bit depth.</param>
    /// <param name="primaries">The primaries code.</param>
    /// <param name="transfer">The transfer code.</param>
    /// <param name="matrix">The matrix code.</param>
    /// <param name="fullRange">Whether the source is full range.</param>
    [Theory]
    [MemberData(nameof(SdrCases))]
    public void SdrWithinOneCodeOfReference(string layout, int depth, int primaries, int transfer, int matrix, bool fullRange)
    {
        var kind = Enum.Parse<VideoImageLayout>(layout);
        var color = new VideoColorInfo((byte)primaries, (byte)transfer, (byte)matrix, fullRange);
        const int width = 96, height = 64;
        var image = Synthetic(kind, depth, color, width, height, out var planes);
        using var frame = new VideoFrame(width, height, 0, new MediaTimeBase(1, 90000), VideoColorInfo.Bt709);
        VideoColorConverter.Convert(image, frame, Permissive, TestContext.Current.CancellationToken);
        var (ry, rcb, rcr) = Reference(kind, depth, color, width, height, planes);
        AssertWithinOne(ry, frame.GetPlane(0), "Y");
        AssertWithinOne(rcb, frame.GetPlane(1), "Cb");
        AssertWithinOne(rcr, frame.GetPlane(2), "Cr");
    }

    /// <summary>Unspecified colour is rejected or assumed exactly as the policy says.</summary>
    [Fact]
    public void UnspecifiedColorFollowsPolicy()
    {
        var unspecified = Synthetic(VideoImageLayout.Yuv420, 8, VideoColorInfo.Unspecified, 32, 16, out var planes);
        using var frame = new VideoFrame(32, 16, 0, new MediaTimeBase(1, 90000), VideoColorInfo.Bt709);
        var reject = new VideoColorConversionOptions { Unspecified = UnspecifiedColorPolicy.Reject, Hdr = HdrPolicy.Reject };
        Assert.Equal(CodecErrorKind.Unsupported, Assert.Throws<CodecException>(() => VideoColorConverter.Convert(unspecified, frame, reject, TestContext.Current.CancellationToken)).Kind);

        VideoColorConverter.Convert(unspecified, frame, reject with { Unspecified = UnspecifiedColorPolicy.AssumeBt709 }, TestContext.Current.CancellationToken);
        Assert.Equal(planes[0].Select(static v => (byte)v), frame.GetPlane(0).ToArray());

        VideoColorConverter.Convert(unspecified, frame, reject with { Unspecified = UnspecifiedColorPolicy.AssumeBt601 }, TestContext.Current.CancellationToken);
        var explicit601 = VideoImage.FromBytes(32, 16, VideoImageLayout.Yuv420, new VideoColorInfo(6, 6, 6, false), [.. planes.Select(static p => (ReadOnlyMemory<byte>)p.Select(static v => (byte)v).ToArray())], [32, 16, 16]);
        using var expected = new VideoFrame(32, 16, 0, new MediaTimeBase(1, 90000), VideoColorInfo.Bt709);
        VideoColorConverter.Convert(explicit601, expected, reject, TestContext.Current.CancellationToken);
        Assert.Equal(expected.GetPlane(0).ToArray(), frame.GetPlane(0).ToArray());
        Assert.Equal(expected.GetPlane(1).ToArray(), frame.GetPlane(1).ToArray());
    }

    /// <summary>Policies have no default: unset policies are rejected.</summary>
    [Fact]
    public void PoliciesMustBeExplicit()
    {
        var image = Synthetic(VideoImageLayout.Yuv420, 8, VideoColorInfo.Bt709, 16, 16, out _);
        using var frame = new VideoFrame(16, 16, 0, new MediaTimeBase(1, 90000), VideoColorInfo.Bt709);
        Assert.Throws<ArgumentException>(() => VideoColorConverter.Convert(image, frame, new VideoColorConversionOptions { Unspecified = default, Hdr = HdrPolicy.ToneMap }, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => VideoColorConverter.Convert(image, frame, new VideoColorConversionOptions { Unspecified = UnspecifiedColorPolicy.Reject, Hdr = default }, TestContext.Current.CancellationToken));
    }

    /// <summary>HDR is never converted as SDR: it is rejected unless tone mapping is requested.</summary>
    /// <param name="transfer">16 (PQ) or 18 (HLG).</param>
    [Theory]
    [InlineData(16)]
    [InlineData(18)]
    public void HdrRequiresToneMapping(int transfer)
    {
        var image = Synthetic(VideoImageLayout.Yuv420, 10, new VideoColorInfo(9, (byte)transfer, 9, false), 16, 16, out _);
        using var frame = new VideoFrame(16, 16, 0, new MediaTimeBase(1, 90000), VideoColorInfo.Bt709);
        var options = new VideoColorConversionOptions { Unspecified = UnspecifiedColorPolicy.AssumeBt709, Hdr = HdrPolicy.Reject };
        Assert.Equal(CodecErrorKind.Unsupported, Assert.Throws<CodecException>(() => VideoColorConverter.Convert(image, frame, options, TestContext.Current.CancellationToken)).Kind);
        VideoColorConverter.Convert(image, frame, options with { Hdr = HdrPolicy.ToneMap }, TestContext.Current.CancellationToken);
    }

    /// <summary>Tone mapping of a grey ramp is monotonic, maps black to black and the content peak to the SDR peak, and stays in range.</summary>
    /// <param name="transfer">16 (PQ) or 18 (HLG).</param>
    /// <param name="peak">The stated content peak, or 0 for none.</param>
    [Theory]
    [InlineData(16, 0)]
    [InlineData(16, 4000)]
    [InlineData(16, 150)]
    [InlineData(18, 0)]
    public void ToneMappingIsMonotonicAndBounded(int transfer, int peak)
    {
        const int width = 1024, height = 2;
        var luma = new ushort[width * height];
        var chroma = new ushort[width / 2];
        for (var i = 0; i < width; i++)
        {
            luma[i] = luma[width + i] = (ushort)(64 + (i * 876 / (width - 1)));
        }

        Array.Fill(chroma, (ushort)512);
        var color = new VideoColorInfo(9, (byte)transfer, 9, false);
        var image = VideoImage.FromWords(width, height, VideoImageLayout.Yuv420, 10, color, [luma, chroma, chroma], [width, width / 2, width / 2], peak == 0 ? null : peak);
        using var frame = new VideoFrame(width, height, 0, new MediaTimeBase(1, 90000), VideoColorInfo.Bt709);
        VideoColorConverter.Convert(image, frame, Permissive, TestContext.Current.CancellationToken);
        var y = frame.GetPlane(0)[..width].ToArray();
        for (var i = 1; i < width; i++)
        {
            Assert.True(y[i] >= y[i - 1], $"Luma decreased at {i}: {y[i - 1]} then {y[i]}.");
        }

        Assert.Equal(16, y[0]);
        Assert.InRange(y[^1], 16, 235);
        if (transfer == 18 || peak is 0 or >= 1000)
        {
            Assert.Equal(235, y[^1]);
        }

        Assert.All(frame.GetPlane(1).ToArray(), static c => Assert.InRange(c, 127, 129));

        // Saturated BT.2020 colours stay within the nominal range and keep their dominant component.
        var red = VideoImage.FromWords(2, 2, VideoImageLayout.Yuv444, 10, color, [new ushort[] { 300, 300, 300, 300 }, new ushort[] { 380, 380, 380, 380 }, new ushort[] { 960, 960, 960, 960 }], [2, 2, 2]);
        using var redFrame = new VideoFrame(2, 2, 0, new MediaTimeBase(1, 90000), VideoColorInfo.Bt709);
        VideoColorConverter.Convert(red, redFrame, Permissive, TestContext.Current.CancellationToken);
        Assert.True(redFrame.GetPlane(2)[0] > 200, "Red did not stay red.");
        Assert.InRange(redFrame.GetPlane(0)[0], 16, 235);
    }

    /// <summary>Unsupported code points and mismatched destinations are rejected.</summary>
    [Fact]
    public void RejectsUnsupported()
    {
        using var frame = new VideoFrame(16, 16, 0, new MediaTimeBase(1, 90000), VideoColorInfo.Bt709);
        foreach (var color in new[] { new VideoColorInfo(10, 1, 1, false), new VideoColorInfo(1, 11, 1, false), new VideoColorInfo(1, 1, 4, false), new VideoColorInfo(1, 1, 10, false), new VideoColorInfo(0, 1, 1, false) })
        {
            var image = Synthetic(VideoImageLayout.Yuv420, 8, color, 16, 16, out _);
            Assert.Equal(CodecErrorKind.Unsupported, Assert.Throws<CodecException>(() => VideoColorConverter.Convert(image, frame, Permissive, TestContext.Current.CancellationToken)).Kind);
        }

        var rgb = Synthetic(VideoImageLayout.Rgb, 8, new VideoColorInfo(1, 1, 1, true), 16, 16, out _);
        Assert.Throws<CodecException>(() => VideoColorConverter.Convert(rgb, frame, Permissive, TestContext.Current.CancellationToken));
        var other = Synthetic(VideoImageLayout.Yuv420, 8, VideoColorInfo.Bt709, 32, 16, out _);
        Assert.Throws<ArgumentException>(() => VideoColorConverter.Convert(other, frame, Permissive, TestContext.Current.CancellationToken));
        using var untagged = new VideoFrame(16, 16, 0, new MediaTimeBase(1, 90000), VideoColorInfo.Unspecified);
        Assert.Throws<ArgumentException>(() => VideoColorConverter.Convert(Synthetic(VideoImageLayout.Yuv420, 8, VideoColorInfo.Bt709, 16, 16, out _), untagged, Permissive, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => VideoImage.FromBytes(16, 16, VideoImageLayout.Yuv420, VideoColorInfo.Bt709, [new byte[255], new byte[64], new byte[64]], [16, 8, 8]));
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoImage.FromWords(16, 16, VideoImageLayout.Yuv420, 17, VideoColorInfo.Bt709, [new ushort[256], new ushort[64], new ushort[64]], [16, 8, 8]));
    }

    /// <summary>The deterministic elementary functions agree with the runtime's to within a few ulps.</summary>
    [Fact]
    public void DeterministicMathIsAccurate()
    {
        var state = 12345u;
        for (var i = 0; i < 20000; i++)
        {
            state = (state * 1664525) + 1013904223;
            var u = state / 4294967296.0;
            var x = Math.Pow(10, (u * 12) - 8);
            AssertClose(Math.Log(x), DeterministicMath.Log(x), 4e-16, 1e-300);
            var e = (u * 80) - 40;
            AssertClose(Math.Exp(e), DeterministicMath.Exp(e), 4e-16, 0);
            var p = (u * 6) - 3;
            AssertClose(Math.Pow(u, p), DeterministicMath.Pow(u, p), 3e-15, 0);
        }

        Assert.Equal(0, DeterministicMath.Pow(0, 2.4));
        Assert.Equal(1, DeterministicMath.Pow(0.3, 0));
        Assert.Equal(double.NegativeInfinity, DeterministicMath.Log(0));
        Assert.True(double.IsNaN(DeterministicMath.Log(-1)));
        Assert.Equal(0, DeterministicMath.Exp(-800));
        Assert.Equal(double.PositiveInfinity, DeterministicMath.Exp(800));
    }

    /// <summary>Asserts relative closeness.</summary>
    /// <param name="expected">The expected value.</param>
    /// <param name="actual">The actual value.</param>
    /// <param name="relative">The relative tolerance.</param>
    /// <param name="absolute">The absolute tolerance.</param>
    private static void AssertClose(double expected, double actual, double relative, double absolute)
    {
        var tolerance = Math.Max(absolute, Math.Abs(expected) * relative);
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected:R}, got {actual:R}.");
    }

    /// <summary>Asserts that every code is within one of the reference.</summary>
    /// <param name="reference">The reference codes.</param>
    /// <param name="actual">The converted codes.</param>
    /// <param name="plane">The plane name.</param>
    private static void AssertWithinOne(int[] reference, ReadOnlySpan<byte> actual, string plane)
    {
        Assert.Equal(reference.Length, actual.Length);
        var worst = 0;
        for (var i = 0; i < reference.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(reference[i] - actual[i]));
        }

        Assert.True(worst <= 1, $"{plane}: a code differs from the reference by {worst}.");
    }

    /// <summary>Creates a deterministic smooth-plus-detail image in a layout.</summary>
    /// <param name="layout">The layout.</param>
    /// <param name="depth">The bit depth.</param>
    /// <param name="color">The colour description.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="planes">Receives the planes as integers.</param>
    /// <returns>The image.</returns>
    private static VideoImage Synthetic(VideoImageLayout layout, int depth, VideoColorInfo color, int width, int height, out int[][] planes)
    {
        int sx = layout is VideoImageLayout.Yuv420 or VideoImageLayout.Yuv422 ? 1 : 0, sy = layout == VideoImageLayout.Yuv420 ? 1 : 0;
        var max = (1 << depth) - 1;
        var limited = !color.FullRange;
        planes = new int[3][];
        var widths = new int[3];
        for (var p = 0; p < 3; p++)
        {
            int pw = p == 0 ? width : width >> sx, ph = p == 0 ? height : height >> sy;
            widths[p] = pw;
            planes[p] = new int[pw * ph];
            for (var j = 0; j < ph; j++)
            {
                for (var i = 0; i < pw; i++)
                {
                    var t = (((i * 37) + (j * 53) + (p * 101)) % 997) / 996.0;
                    var luma = p == 0 || layout == VideoImageLayout.Rgb;
                    double lo = limited ? (luma ? 16 : 16) << (depth - 8) : 0, hi = limited ? (luma ? 235 : 240) << (depth - 8) : max;
                    planes[p][(j * pw) + i] = (int)Math.Round(lo + (t * (hi - lo)));
                }
            }
        }

        if (depth == 8)
        {
            var copy = planes;
            return VideoImage.FromBytes(width, height, layout, color, [.. copy.Select(static p => (ReadOnlyMemory<byte>)p.Select(static v => (byte)v).ToArray())], widths);
        }

        var words = planes;
        return VideoImage.FromWords(width, height, layout, depth, color, [.. words.Select(static p => (ReadOnlyMemory<ushort>)p.Select(static v => (ushort)v).ToArray())], widths);
    }

    /// <summary>The double-precision reference conversion.</summary>
    /// <param name="layout">The layout.</param>
    /// <param name="depth">The bit depth.</param>
    /// <param name="color">The colour.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="planes">The source planes.</param>
    /// <returns>Y, Cb and Cr codes.</returns>
    private static (int[] Y, int[] Cb, int[] Cr) Reference(VideoImageLayout layout, int depth, VideoColorInfo color, int width, int height, int[][] planes)
    {
        int sx = layout is VideoImageLayout.Yuv420 or VideoImageLayout.Yuv422 ? 1 : 0, sy = layout == VideoImageLayout.Yuv420 ? 1 : 0;
        var cwSource = width >> sx;
        var y = new int[width * height];
        var cb = new double[(width / 2) * (height / 2)];
        var cr = new double[(width / 2) * (height / 2)];
        var max = (double)((1 << depth) - 1);
        var s = (double)(1 << (depth - 8));
        var (kr, kb) = color.Matrix switch { 1 => (0.2126, 0.0722), 5 or 6 => (0.299, 0.114), 9 => (0.2627, 0.0593), _ => (0.0, 0.0) };
        var gamut = color.Primaries switch
        {
            1 => null,
            9 => new[] { 1.6605, -0.5876, -0.0728, -0.1246, 1.1329, -0.0083, -0.0182, -0.1006, 1.1187 },
            _ => PrimariesTo709(color.Primaries),
        };
        var linear = gamut is not null || color.Transfer is not (1 or 6 or 14 or 15);
        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                int a = planes[0][(j * width) + i], b = planes[1][((j >> sy) * cwSource) + (i >> sx)], c = planes[2][((j >> sy) * cwSource) + (i >> sx)];
                double r, g, bl;
                if (layout == VideoImageLayout.Rgb)
                {
                    (r, g, bl) = color.FullRange ? (a / max, b / max, c / max) : ((a - (16 * s)) / (219 * s), (b - (16 * s)) / (219 * s), (c - (16 * s)) / (219 * s));
                }
                else
                {
                    var (yn, pb, pr) = color.FullRange
                        ? (a / max, (b - (max + 1) / 2) / max, (c - (max + 1) / 2) / max)
                        : ((a - (16 * s)) / (219 * s), (b - (128 * s)) / (224 * s), (c - (128 * s)) / (224 * s));
                    r = yn + ((2 - (2 * kr)) * pr);
                    bl = yn + ((2 - (2 * kb)) * pb);
                    g = (yn - (kr * r) - (kb * bl)) / (1 - kr - kb);
                }

                if (linear)
                {
                    double Eotf(double v)
                    {
                        v = Math.Clamp(v, 0, 1);
                        return color.Transfer switch
                        {
                            4 => Math.Pow(v, 2.2),
                            13 => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4),
                            _ => Math.Pow(v, 2.4),
                        };
                    }

                    (r, g, bl) = (Eotf(r), Eotf(g), Eotf(bl));
                    if (gamut is { } m)
                    {
                        (r, g, bl) = ((m[0] * r) + (m[1] * g) + (m[2] * bl), (m[3] * r) + (m[4] * g) + (m[5] * bl), (m[6] * r) + (m[7] * g) + (m[8] * bl));
                    }

                    (r, g, bl) = (Math.Pow(Math.Clamp(r, 0, 1), 1 / 2.4), Math.Pow(Math.Clamp(g, 0, 1), 1 / 2.4), Math.Pow(Math.Clamp(bl, 0, 1), 1 / 2.4));
                }

                var luma = (0.2126 * r) + (0.7152 * g) + (0.0722 * bl);
                y[(j * width) + i] = Quantize(16 + (219 * luma));
                cb[((j / 2) * (width / 2)) + (i / 2)] += (bl - luma) / 1.8556 / 4;
                cr[((j / 2) * (width / 2)) + (i / 2)] += (r - luma) / 1.5748 / 4;
            }
        }

        return (y, [.. cb.Select(static v => Quantize(128 + (224 * v)))], [.. cr.Select(static v => Quantize(128 + (224 * v)))]);
    }

    /// <summary>Rounds and clips to [1, 254].</summary>
    /// <param name="v">The value.</param>
    /// <returns>The code.</returns>
    private static int Quantize(double v) => (int)Math.Clamp(Math.Round(v, MidpointRounding.AwayFromZero), 1, 254);

    /// <summary>Derives a primaries-to-BT.709 matrix by Gaussian elimination, independently of the converter.</summary>
    /// <param name="primaries">5 or 6.</param>
    /// <returns>The matrix.</returns>
    private static double[] PrimariesTo709(int primaries)
    {
        static double[,] Npm(double[] xy)
        {
            var m = new double[3, 3];
            for (var k = 0; k < 3; k++)
            {
                double x = xy[2 * k], y = xy[(2 * k) + 1];
                m[0, k] = x / y;
                m[1, k] = 1;
                m[2, k] = (1 - x - y) / y;
            }

            var w = Solve(m, [0.3127 / 0.3290, 1, (1 - 0.3127 - 0.3290) / 0.3290]);
            for (var r = 0; r < 3; r++)
            {
                for (var k = 0; k < 3; k++)
                {
                    m[r, k] *= w[k];
                }
            }

            return m;
        }

        var source = Npm(primaries == 5 ? [0.64, 0.33, 0.29, 0.60, 0.15, 0.06] : [0.630, 0.340, 0.310, 0.595, 0.155, 0.070]);
        var target = Npm([0.64, 0.33, 0.30, 0.60, 0.15, 0.06]);
        var result = new double[9];
        for (var k = 0; k < 3; k++)
        {
            var column = Solve(target, [source[0, k], source[1, k], source[2, k]]);
            for (var r = 0; r < 3; r++)
            {
                result[(r * 3) + k] = column[r];
            }
        }

        return result;
    }

    /// <summary>Solves a 3x3 system by Gaussian elimination with partial pivoting.</summary>
    /// <param name="a">The matrix (copied).</param>
    /// <param name="b">The right-hand side.</param>
    /// <returns>The solution.</returns>
    private static double[] Solve(double[,] a, double[] b)
    {
        var m = (double[,])a.Clone();
        var v = (double[])b.Clone();
        for (var col = 0; col < 3; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < 3; r++)
            {
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col]))
                {
                    pivot = r;
                }
            }

            for (var k = 0; k < 3; k++)
            {
                (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
            }

            (v[col], v[pivot]) = (v[pivot], v[col]);
            for (var r = col + 1; r < 3; r++)
            {
                var f = m[r, col] / m[col, col];
                for (var k = col; k < 3; k++)
                {
                    m[r, k] -= f * m[col, k];
                }

                v[r] -= f * v[col];
            }
        }

        var x = new double[3];
        for (var r = 2; r >= 0; r--)
        {
            var sum = v[r];
            for (var k = r + 1; k < 3; k++)
            {
                sum -= m[r, k] * x[k];
            }

            x[r] = sum / m[r, r];
        }

        return x;
    }
}
