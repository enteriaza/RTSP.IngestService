// Deterministic colour arithmetic. Math.Pow, Exp and Log are implemented by the runtime's C library and may round
// differently across platforms, so colour conversion uses its own versions built only from IEEE-754 +, -, * and /
// (correctly rounded everywhere) plus exact bit manipulation, which makes converted pictures identical on every
// platform. Accuracy is within a few units in the last place, far below the half code value that matters.
//
// Formulas: ITU-T H.273 (code points); ITU-R BT.709 and BT.2020 (primaries, luma coefficients); ITU-R BT.1886
// (reference EOTF, Lb = 0); IEC 61966-2-1 (sRGB); SMPTE ST 2084 (PQ); ITU-R BT.2100 (HLG and its OOTF); ITU-R
// BT.2390 (EETF); SMPTE RP 177 (RGB-to-XYZ from chromaticities). No code is copied from any implementation.

namespace RTSP.IngestService.Media.Codecs.Video.Color;

/// <summary>Platform-independent elementary functions.</summary>
internal static class DeterministicMath
{
    /// <summary>ln 2, high part (exact in 32 bits of mantissa).</summary>
    private const double Ln2Hi = 6.93147180369123816490e-01;

    /// <summary>ln 2, low part.</summary>
    private const double Ln2Lo = 1.90821492927058770002e-10;

    /// <summary>1 / ln 2.</summary>
    private const double InvLn2 = 1.44269504088896338700e+00;

    /// <summary>Returns the natural logarithm; NaN below zero, -infinity at zero.</summary>
    /// <param name="x">The argument.</param>
    /// <returns>ln x.</returns>
    public static double Log(double x)
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
            // Subnormal: scale into the normal range exactly.
            return Log(x * 4503599627370496.0) - (52 * (Ln2Hi + Ln2Lo));
        }

        var e = exponent - 1023;
        var m = BitConverter.Int64BitsToDouble((bits & 0x000FFFFFFFFFFFFFL) | 0x3FF0000000000000L);
        if (m > 1.4142135623730951)
        {
            m *= 0.5;
            e++;
        }

        // ln m = 2 atanh s, s = (m - 1) / (m + 1), |s| <= 0.1716: Horner over 1/k for k = 23, 21, ..., 3. Written out
        // because the loop is several times slower; each step's operations and their order define the result bit for
        // bit, and each constant 1.0 / k is the correctly rounded quotient the loop computed.
        var s = (m - 1) / (m + 1);
        var s2 = s * s;
        var sum = 1.0 / 23;
        sum = (sum * s2) + (1.0 / 21);
        sum = (sum * s2) + (1.0 / 19);
        sum = (sum * s2) + (1.0 / 17);
        sum = (sum * s2) + (1.0 / 15);
        sum = (sum * s2) + (1.0 / 13);
        sum = (sum * s2) + (1.0 / 11);
        sum = (sum * s2) + (1.0 / 9);
        sum = (sum * s2) + (1.0 / 7);
        sum = (sum * s2) + (1.0 / 5);
        sum = (sum * s2) + (1.0 / 3);

        var lnM = 2 * (s + (s * s2 * sum));
        return (e * Ln2Hi) + ((e * Ln2Lo) + lnM);
    }

    /// <summary>Returns e raised to a power.</summary>
    /// <param name="x">The exponent.</param>
    /// <returns>exp x.</returns>
    public static double Exp(double x)
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

        var k = Math.Floor((x * InvLn2) + 0.5);
        var r = (x - (k * Ln2Hi)) - (k * Ln2Lo);

        // |r| <= 0.347: Taylor series to r^17 / 17!, sum = 1 + (sum * r / n) for n = 17 down to 1. Written out because
        // the loop is several times slower; each step's operations and their order define the result bit for bit.
        var sum = 1 + (r / 17);
        sum = 1 + (sum * r / 16);
        sum = 1 + (sum * r / 15);
        sum = 1 + (sum * r / 14);
        sum = 1 + (sum * r / 13);
        sum = 1 + (sum * r / 12);
        sum = 1 + (sum * r / 11);
        sum = 1 + (sum * r / 10);
        sum = 1 + (sum * r / 9);
        sum = 1 + (sum * r / 8);
        sum = 1 + (sum * r / 7);
        sum = 1 + (sum * r / 6);
        sum = 1 + (sum * r / 5);
        sum = 1 + (sum * r / 4);
        sum = 1 + (sum * r / 3);
        sum = 1 + (sum * r / 2);
        sum = 1 + (sum * r / 1);

        return Scale(sum, (int)k);
    }

    /// <summary>Returns x raised to y for x >= 0.</summary>
    /// <param name="x">The base, non-negative.</param>
    /// <param name="y">The exponent.</param>
    /// <returns>x^y; 0 for x = 0 and y > 0; 1 for y = 0.</returns>
    public static double Pow(double x, double y)
    {
        if (y == 0)
        {
            return 1;
        }

        if (x == 1)
        {
            return 1;
        }

        if (x <= 0)
        {
            return x == 0 && y > 0 ? 0 : double.NaN;
        }

        return Exp(y * Log(x));
    }

    /// <summary>Returns x * 2^k exactly (barring underflow).</summary>
    /// <param name="x">The value, in [0.5, 2].</param>
    /// <param name="k">The power.</param>
    /// <returns>The scaled value.</returns>
    private static double Scale(double x, int k)
    {
        // Apply in at most three steps so every factor is a normal power of two.
        while (k > 1000)
        {
            x *= BitConverter.Int64BitsToDouble((long)(1023 + 1000) << 52);
            k -= 1000;
        }

        while (k < -1000)
        {
            x *= BitConverter.Int64BitsToDouble((long)(1023 - 1000) << 52);
            k += 1000;
        }

        return x * BitConverter.Int64BitsToDouble((long)(1023 + k) << 52);
    }
}

/// <summary>Transfer characteristics (H.273 code points) as display-referred EOTFs and their inverses.</summary>
internal static class TransferFunctions
{
    /// <summary>The ST 2084 constants.</summary>
    private const double M1 = 2610.0 / 16384, M2 = 2523.0 / 4096 * 128, C1 = 3424.0 / 4096, C2 = 2413.0 / 4096 * 32, C3 = 2392.0 / 4096 * 32;

    /// <summary>The HLG constants.</summary>
    private const double HlgA = 0.17883277, HlgB = 0.28466892, HlgC = 0.55991073;

    /// <summary>Gets whether a transfer code is SDR with the BT.1886 reference EOTF (BT.709, BT.601, BT.2020 10/12-bit).</summary>
    /// <param name="transfer">The code.</param>
    /// <returns>Whether it is.</returns>
    public static bool IsBt1886(int transfer) => transfer is 1 or 6 or 14 or 15;

    /// <summary>Gets whether a transfer code is HDR (PQ or HLG).</summary>
    /// <param name="transfer">The code.</param>
    /// <returns>Whether it is.</returns>
    public static bool IsHdr(int transfer) => transfer is 16 or 18;

    /// <summary>Gets whether a transfer code is supported.</summary>
    /// <param name="transfer">The code.</param>
    /// <returns>Whether it is.</returns>
    public static bool IsSupported(int transfer) => IsBt1886(transfer) || IsHdr(transfer) || transfer is 4 or 5 or 8 or 13;

    /// <summary>The SDR EOTF: a non-linear value in [0, 1] to relative display light in [0, 1].</summary>
    /// <param name="transfer">The code (not PQ or HLG).</param>
    /// <param name="v">The non-linear value; clamped to [0, 1].</param>
    /// <returns>The relative light.</returns>
    public static double SdrEotf(int transfer, double v)
    {
        v = Math.Clamp(v, 0, 1);
        return transfer switch
        {
            4 => DeterministicMath.Pow(v, 2.2),
            5 => DeterministicMath.Pow(v, 2.8),
            8 => v,
            13 => v <= 0.04045 ? v / 12.92 : DeterministicMath.Pow((v + 0.055) / 1.055, 2.4),
            _ => DeterministicMath.Pow(v, 2.4),
        };
    }

    /// <summary>The inverse BT.1886 EOTF: relative display light in [0, 1] to a BT.709 non-linear value.</summary>
    /// <param name="l">The light; clamped to [0, 1].</param>
    /// <returns>The non-linear value.</returns>
    public static double Bt1886Inverse(double l) => DeterministicMath.Pow(Math.Clamp(l, 0, 1), 1 / 2.4);

    /// <summary>The PQ EOTF: a non-linear value to absolute luminance in cd/m2.</summary>
    /// <param name="e">The value; clamped to [0, 1].</param>
    /// <returns>The luminance, 0 to 10000.</returns>
    public static double PqEotf(double e)
    {
        var p = DeterministicMath.Pow(Math.Clamp(e, 0, 1), 1 / M2);
        return 10000 * DeterministicMath.Pow(Math.Max(p - C1, 0) / (C2 - (C3 * p)), 1 / M1);
    }

    /// <summary>The inverse PQ EOTF: absolute luminance to a non-linear value.</summary>
    /// <param name="nits">The luminance; clamped to [0, 10000].</param>
    /// <returns>The value in [0, 1].</returns>
    public static double PqInverse(double nits)
    {
        var y = DeterministicMath.Pow(Math.Clamp(nits / 10000, 0, 1), M1);
        return DeterministicMath.Pow((C1 + (C2 * y)) / (1 + (C3 * y)), M2);
    }

    /// <summary>The HLG inverse OETF: a non-linear value to relative scene light.</summary>
    /// <param name="e">The value; clamped to [0, 1].</param>
    /// <returns>Scene light in [0, 1].</returns>
    public static double HlgInverseOetf(double e)
    {
        e = Math.Clamp(e, 0, 1);
        return e <= 0.5 ? e * e / 3 : (DeterministicMath.Exp((e - HlgC) / HlgA) + HlgB) / 12;
    }

    /// <summary>The HLG system gamma for a display peak (BT.2100 note 5e), 1.2 at 1000 cd/m2.</summary>
    /// <param name="peakNits">The nominal display peak.</param>
    /// <returns>The gamma.</returns>
    public static double HlgSystemGamma(double peakNits) => 1.2 + (0.42 * DeterministicMath.Log(peakNits / 1000) / DeterministicMath.Log(10));
}

/// <summary>Colour primaries and luma coefficients (H.273 code points).</summary>
internal static class ColorPrimaries
{
    /// <summary>Gets whether a primaries code is supported.</summary>
    /// <param name="primaries">The code.</param>
    /// <returns>Whether it is.</returns>
    public static bool IsSupported(int primaries) => primaries is 1 or 5 or 6 or 7 or 9;

    /// <summary>Gets whether a matrix code is supported for Y'CbCr input.</summary>
    /// <param name="matrix">The code.</param>
    /// <returns>Whether it is.</returns>
    public static bool IsSupportedMatrix(int matrix) => matrix is 1 or 5 or 6 or 9;

    /// <summary>Returns the luma coefficients Kr and Kb of a matrix code.</summary>
    /// <param name="matrix">The code: 1 (BT.709), 5 or 6 (BT.601), 9 (BT.2020 non-constant luminance).</param>
    /// <returns>Kr and Kb.</returns>
    public static (double Kr, double Kb) LumaCoefficients(int matrix) => matrix switch
    {
        1 => (0.2126, 0.0722),
        5 or 6 => (0.299, 0.114),
        9 => (0.2627, 0.0593),
        _ => throw new ArgumentOutOfRangeException(nameof(matrix)),
    };

    /// <summary>Returns the matrix converting linear RGB of one set of primaries to linear RGB of another, both with a D65 white.</summary>
    /// <param name="from">The source primaries code.</param>
    /// <param name="to">The destination primaries code.</param>
    /// <returns>A row-major 3x3 matrix.</returns>
    public static double[] Conversion(int from, int to)
    {
        var source = ToXyz(from);
        var destination = Invert(ToXyz(to));
        var result = new double[9];
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                result[(r * 3) + c] = (destination[r * 3] * source[c]) + (destination[(r * 3) + 1] * source[3 + c]) + (destination[(r * 3) + 2] * source[6 + c]);
            }
        }

        return result;
    }

    /// <summary>Returns the RGB-to-XYZ matrix of a set of primaries (SMPTE RP 177).</summary>
    /// <param name="primaries">The code.</param>
    /// <returns>A row-major 3x3 matrix.</returns>
    private static double[] ToXyz(int primaries)
    {
        var (rx, ry, gx, gy, bx, by) = primaries switch
        {
            1 => (0.640, 0.330, 0.300, 0.600, 0.150, 0.060),
            5 => (0.640, 0.330, 0.290, 0.600, 0.150, 0.060),
            6 or 7 => (0.630, 0.340, 0.310, 0.595, 0.155, 0.070),
            9 => (0.708, 0.292, 0.170, 0.797, 0.131, 0.046),
            _ => throw new ArgumentOutOfRangeException(nameof(primaries)),
        };
        const double wx = 0.3127, wy = 0.3290;
        double[] p =
        [
            rx / ry, gx / gy, bx / by,
            1, 1, 1,
            (1 - rx - ry) / ry, (1 - gx - gy) / gy, (1 - bx - by) / by,
        ];
        var inverse = Invert(p);
        double wX = wx / wy, wY = 1, wZ = (1 - wx - wy) / wy;
        var s = new double[3];
        for (var i = 0; i < 3; i++)
        {
            s[i] = (inverse[i * 3] * wX) + (inverse[(i * 3) + 1] * wY) + (inverse[(i * 3) + 2] * wZ);
        }

        var m = new double[9];
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                m[(r * 3) + c] = p[(r * 3) + c] * s[c];
            }
        }

        return m;
    }

    /// <summary>Inverts a 3x3 matrix by cofactors.</summary>
    /// <param name="m">Row-major.</param>
    /// <returns>The inverse.</returns>
    private static double[] Invert(double[] m)
    {
        var a = (m[4] * m[8]) - (m[5] * m[7]);
        var b = (m[5] * m[6]) - (m[3] * m[8]);
        var c = (m[3] * m[7]) - (m[4] * m[6]);
        var det = (m[0] * a) + (m[1] * b) + (m[2] * c);
        return
        [
            a / det, ((m[2] * m[7]) - (m[1] * m[8])) / det, ((m[1] * m[5]) - (m[2] * m[4])) / det,
            b / det, ((m[0] * m[8]) - (m[2] * m[6])) / det, ((m[2] * m[3]) - (m[0] * m[5])) / det,
            c / det, ((m[1] * m[6]) - (m[0] * m[7])) / det, ((m[0] * m[4]) - (m[1] * m[3])) / det,
        ];
    }
}
