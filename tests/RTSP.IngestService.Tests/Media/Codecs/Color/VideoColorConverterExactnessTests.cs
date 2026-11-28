// Bit-exactness of the colour conversion's optimised paths. DeterministicMath against a loop-form copy of its series as
// first written (the definition of its results); the 8-bit Y'CbCr light tables against the per-sample computation, for
// every Y' and chroma code of every table. Equality is of the IEEE-754 bit patterns, with no tolerance.

using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Video.Color;

namespace RTSP.IngestService.Tests.Media.Codecs.Color;

/// <summary>Bit-exactness of the colour conversion's optimised paths.</summary>
public sealed class VideoColorConverterExactnessTests
{
    /// <summary>Gets the 8-bit Y'CbCr colour descriptions that are linearised: transfer, matrix, primaries, full range, HDR peak (0 for none).</summary>
    public static TheoryData<int, int, int, bool, int> LinearisedCases
    {
        get
        {
            var data = new TheoryData<int, int, int, bool, int>();
            int[] primaries = [1, 5, 6, 7, 9];
            var i = 0;
            foreach (var transfer in new[] { 1, 4, 5, 6, 8, 13, 14, 15, 16, 18 })
            {
                foreach (var matrix in new[] { 1, 6, 9 })
                {
                    foreach (var fullRange in new[] { false, true })
                    {
                        var p = primaries[i++ % primaries.Length];
                        data.Add(transfer, matrix, TransferFunctions.IsBt1886(transfer) && p == 1 ? 9 : p, fullRange, 0);
                    }
                }
            }

            data.Add(6, 5, 5, false, 0);
            data.Add(16, 5, 6, true, 0);
            data.Add(16, 9, 9, false, 150);
            data.Add(16, 9, 9, true, 4000);
            data.Add(18, 9, 9, false, 150);
            data.Add(18, 1, 1, true, 4000);
            return data;
        }
    }

    /// <summary>Log, Exp and Pow equal the loop-form series bit for bit.</summary>
    [Fact]
    public void DeterministicMathMatchesLoopForm()
    {
        var state = 0x2545F4914F6CDD1DUL;
        double Next()
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            return (state >> 11) / 9007199254740992.0;
        }

        for (var i = 0; i < 1_000_000; i++)
        {
            var u = Next();
            var x = BitConverter.Int64BitsToDouble((long)(state >> 1));
            AssertSame(OriginalLog(Math.Abs(x)), DeterministicMath.Log(Math.Abs(x)), "Log", x);
            AssertSame(OriginalLog(u), DeterministicMath.Log(u), "Log", u);
            var e = (u * 1460) - 750;
            AssertSame(OriginalExp(e), DeterministicMath.Exp(e), "Exp", e);
            var y = (Next() * 16) - 8;
            AssertSame(OriginalPow(u, y), DeterministicMath.Pow(u, y), "Pow", u);
            AssertSame(OriginalPow(u * 10000, y), DeterministicMath.Pow(u * 10000, y), "Pow", u * 10000);
        }

        double[] special = [0, -0.0, -1, double.Epsilon, double.MaxValue, double.PositiveInfinity, double.NegativeInfinity, double.NaN, 1, 1.4142135623730951, 1.4142135623730954, 0.5, 2, 709.78, 709.79, -745.1, -745.2];
        foreach (var v in special)
        {
            AssertSame(OriginalLog(v), DeterministicMath.Log(v), "Log", v);
            AssertSame(OriginalExp(v), DeterministicMath.Exp(v), "Exp", v);
            foreach (var p in new[] { 0, 1 / 2.4, 2.4, -2.4, 1 / 0.1593017578125 })
            {
                AssertSame(OriginalPow(v, p), DeterministicMath.Pow(v, p), "Pow", v);
            }
        }
    }

    /// <summary>
    /// With the light tables, every 8-bit Y'CbCr triplet that reads an entry converts bit for bit as the per-sample
    /// computation: all 65,536 (Y', Cr) pairs of the R' table and all 65,536 (Y', Cb) pairs of the B' table.
    /// </summary>
    /// <param name="transfer">The transfer code.</param>
    /// <param name="matrix">The matrix code.</param>
    /// <param name="primaries">The primaries code.</param>
    /// <param name="fullRange">Whether the codes are full range.</param>
    /// <param name="peak">The stated content peak, or 0 for none.</param>
    [Theory]
    [MemberData(nameof(LinearisedCases))]
    public void LightTablesMatchPerSampleComputation(int transfer, int matrix, int primaries, bool fullRange, int peak)
    {
        var color = new VideoColorInfo((byte)primaries, (byte)transfer, (byte)matrix, fullRange);
        var image = VideoImage.FromBytes(2, 2, VideoImageLayout.Yuv444, color, [new byte[4], new byte[4], new byte[4]], [2, 2, 2], peak == 0 ? null : peak);
        var tabled = new VideoColorConverter.Pipeline(image, color);
        var computed = new VideoColorConverter.Pipeline(image, color, lightTables: false);
        var differing = 0L;
        Parallel.For(0, 256, luma =>
        {
            var local = 0L;
            for (var chroma = 0; chroma < 256; chroma++)
            {
                var other = ((luma * 151) + (chroma * 7) + 13) & 255;
                local += Differs(tabled.Pixel(luma, other, chroma), computed.Pixel(luma, other, chroma));
                local += Differs(tabled.Pixel(luma, chroma, other), computed.Pixel(luma, chroma, other));
            }

            Interlocked.Add(ref differing, local);
        });

        Assert.Equal(0, differing);
    }

    /// <summary>Whole 8-bit 4:2:0 pictures convert identically with and without the light tables.</summary>
    /// <param name="primaries">The primaries code.</param>
    /// <param name="transfer">The transfer code.</param>
    /// <param name="matrix">The matrix code.</param>
    /// <param name="fullRange">Whether the codes are full range.</param>
    [Theory]
    [InlineData(6, 6, 6, false)]
    [InlineData(9, 16, 9, false)]
    [InlineData(9, 18, 9, true)]
    [InlineData(1, 13, 1, true)]
    public void PicturesMatchWithoutLightTables(int primaries, int transfer, int matrix, bool fullRange)
    {
        const int width = 66, height = 34;
        var color = new VideoColorInfo((byte)primaries, (byte)transfer, (byte)matrix, fullRange);
        var random = new Random(7);
        byte[][] planes = [new byte[width * height], new byte[width * height / 4], new byte[width * height / 4]];
        foreach (var plane in planes)
        {
            random.NextBytes(plane);
        }

        var image = VideoImage.FromBytes(width, height, VideoImageLayout.Yuv420, color, [.. planes.Select(static p => (ReadOnlyMemory<byte>)p)], [width, width / 2, width / 2]);
        using var tabled = new VideoFrame(width, height, 0, new MediaTimeBase(1, 90000), VideoColorInfo.Bt709);
        using var computed = new VideoFrame(width, height, 0, new MediaTimeBase(1, 90000), VideoColorInfo.Bt709);
        new VideoColorConverter.Pipeline(image, color).Run(tabled, TestContext.Current.CancellationToken);
        new VideoColorConverter.Pipeline(image, color, lightTables: false).Run(computed, TestContext.Current.CancellationToken);
        for (var plane = 0; plane < 3; plane++)
        {
            Assert.Equal(computed.GetPlane(plane).ToArray(), tabled.GetPlane(plane).ToArray());
        }
    }

    /// <summary>Returns 1 when two pixels differ in any bit, else 0.</summary>
    /// <param name="x">One pixel.</param>
    /// <param name="y">The other.</param>
    /// <returns>1 or 0.</returns>
    private static int Differs((double Y, double Cb, double Cr) x, (double Y, double Cb, double Cr) y) =>
        BitConverter.DoubleToInt64Bits(x.Y) == BitConverter.DoubleToInt64Bits(y.Y) && BitConverter.DoubleToInt64Bits(x.Cb) == BitConverter.DoubleToInt64Bits(y.Cb) &&
        BitConverter.DoubleToInt64Bits(x.Cr) == BitConverter.DoubleToInt64Bits(y.Cr) ? 0 : 1;

    /// <summary>Asserts identical bit patterns.</summary>
    /// <param name="expected">The loop-form result.</param>
    /// <param name="actual">The production result.</param>
    /// <param name="function">The function name.</param>
    /// <param name="argument">The argument.</param>
    private static void AssertSame(double expected, double actual, string function, double argument)
    {
        if (BitConverter.DoubleToInt64Bits(expected) != BitConverter.DoubleToInt64Bits(actual))
        {
            Assert.Fail($"{function}({argument:R}): expected {expected:R}, got {actual:R}.");
        }
    }

    /// <summary>The loop-form natural logarithm.</summary>
    /// <param name="x">The argument.</param>
    /// <returns>ln x.</returns>
    private static double OriginalLog(double x)
    {
        if (!(x > 0))
        {
            return x == 0 ? double.NegativeInfinity : double.NaN;
        }

        if (double.IsPositiveInfinity(x))
        {
            return x;
        }

        var bits = BitConverter.DoubleToInt64Bits(x);
        var exponent = (int)((bits >> 52) & 0x7FF);
        if (exponent == 0)
        {
            return OriginalLog(x * 4503599627370496.0) - (52 * (6.93147180369123816490e-01 + 1.90821492927058770002e-10));
        }

        var e = exponent - 1023;
        var m = BitConverter.Int64BitsToDouble((bits & 0x000FFFFFFFFFFFFFL) | 0x3FF0000000000000L);
        if (m > 1.4142135623730951)
        {
            m *= 0.5;
            e++;
        }

        var s = (m - 1) / (m + 1);
        var s2 = s * s;
        var sum = 0.0;
        for (var k = 23; k >= 3; k -= 2)
        {
            sum = (sum * s2) + (1.0 / k);
        }

        var lnM = 2 * (s + (s * s2 * sum));
        return (e * 6.93147180369123816490e-01) + ((e * 1.90821492927058770002e-10) + lnM);
    }

    /// <summary>The loop-form exponential.</summary>
    /// <param name="x">The exponent.</param>
    /// <returns>exp x.</returns>
    private static double OriginalExp(double x)
    {
        if (double.IsNaN(x))
        {
            return x;
        }

        if (x > 709.78)
        {
            return double.PositiveInfinity;
        }

        if (x < -745.1)
        {
            return 0;
        }

        var k = Math.Floor((x * 1.44269504088896338700e+00) + 0.5);
        var r = (x - (k * 6.93147180369123816490e-01)) - (k * 1.90821492927058770002e-10);
        var sum = 1.0;
        for (var n = 17; n >= 1; n--)
        {
            sum = 1 + (sum * r / n);
        }

        var scale = (int)k;
        while (scale > 1000)
        {
            sum *= BitConverter.Int64BitsToDouble((long)(1023 + 1000) << 52);
            scale -= 1000;
        }

        while (scale < -1000)
        {
            sum *= BitConverter.Int64BitsToDouble((long)(1023 - 1000) << 52);
            scale += 1000;
        }

        return sum * BitConverter.Int64BitsToDouble((long)(1023 + scale) << 52);
    }

    /// <summary>The loop-form power, for x >= 0.</summary>
    /// <param name="x">The base.</param>
    /// <param name="y">The exponent.</param>
    /// <returns>x^y.</returns>
    private static double OriginalPow(double x, double y)
    {
        if (y == 0 || x == 1)
        {
            return 1;
        }

        if (x <= 0)
        {
            return x == 0 && y > 0 ? 0 : double.NaN;
        }

        return OriginalExp(y * OriginalLog(x));
    }
}
