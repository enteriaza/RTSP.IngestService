// Encoder-side constants and the forward transforms. The quantisation multipliers are derived here from the
// decoder's normalisation values (ITU-T H.264 8.5.9, Table 8-13 via H264Tables.NormAdjust4x4) so that dequantising
// a quantised forward-transform coefficient reproduces it: MF = round(2^15 * k / v), with k = 4, 64/25 and 16/5 for
// the three coefficient classes of the 4x4 core transform. The Lagrange multiplier follows the published
// rate-constrained mode decision relation lambda_mode = 0.85 * 2^((QP - 12) / 3) (Wiegand et al., IEEE TCSVT 13(7),
// 2003), used as its square root for SATD costs; it and the quantiser step 0.85 * 2^((QP - 12) / 6) are tabulated as
// integers so the encoder never depends on platform floating-point library rounding.

using System.Buffers.Binary;
using System.Runtime.Intrinsics;

namespace RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

/// <summary>Quantisation tables, cost multipliers and forward transforms.</summary>
internal static class H264EncoderTables
{
    /// <summary>Gets sqrt(lambda_mode) * 256 by QP.</summary>
    public static ReadOnlySpan<int> Lambda256 =>
    [
        59, 66, 74, 83, 94, 105, 118, 132, 149, 167, 187, 210, 236, 265, 297, 334, 375, 421, 472, 530, 595, 668, 749, 841,
        944, 1060, 1189, 1335, 1499, 1682, 1888, 2119, 2379, 2670, 2997, 3364, 3776, 4239, 4758, 5341, 5995, 6729, 7553,
        8478, 9516, 10681, 11989, 13457, 15105, 16955, 19031, 21362,
    ];

    /// <summary>Gets the quantiser step 0.85 * 2^((QP - 12) / 6) * 65536 by QP.</summary>
    public static ReadOnlySpan<int> QScale65536 =>
    [
        13926, 15632, 17546, 19695, 22107, 24814, 27853, 31264, 35092, 39390, 44214, 49628, 55706, 62527, 70185, 78780,
        88427, 99256, 111411, 125055, 140369, 157559, 176854, 198512, 222822, 250110, 280739, 315118, 353709, 397024,
        445645, 500219, 561477, 630237, 707417, 794049, 891290, 1000439, 1122955, 1260474, 1414834, 1588098, 1782579,
        2000878, 2245909, 2520948, 2829668, 3176195, 3565158, 4001755, 4491818, 5041895,
    ];

    /// <summary>The forward quantisation multipliers at <c>qp % 6 * 16 + raster position</c>.</summary>
    private static readonly int[] Multipliers = BuildMultipliers();

    /// <summary>Gets the multiplier of a coefficient.</summary>
    /// <param name="qpRem">QP % 6.</param>
    /// <param name="position">The raster position, 0 to 15.</param>
    /// <returns>MF.</returns>
    public static int Multiplier(int qpRem, int position) => Multipliers[(qpRem * 16) + position];

    /// <summary>Gets the dequantisation scale v of a coefficient (flat scaling lists): the decoder multiplies by v * 2^(QP / 6).</summary>
    /// <param name="qpRem">QP % 6.</param>
    /// <param name="position">The raster position.</param>
    /// <returns>v.</returns>
    public static int Scale(int qpRem, int position) => H264Tables.NormAdjust4x4[(qpRem * 3) + Class(position)];

    /// <summary>Applies the forward 4x4 core transform (the inverse of 8.5.12.2 up to scaling) to a residual block.</summary>
    /// <param name="residual">The residual, raster order.</param>
    /// <param name="coefficients">Receives the unscaled coefficients, raster order.</param>
    public static void Forward4x4(ReadOnlySpan<int> residual, Span<int> coefficients)
    {
        Span<int> t = stackalloc int[16];
        for (var i = 0; i < 4; i++)
        {
            int x0 = residual[i * 4], x1 = residual[(i * 4) + 1], x2 = residual[(i * 4) + 2], x3 = residual[(i * 4) + 3];
            int s03 = x0 + x3, d03 = x0 - x3, s12 = x1 + x2, d12 = x1 - x2;
            t[i * 4] = s03 + s12;
            t[(i * 4) + 1] = (2 * d03) + d12;
            t[(i * 4) + 2] = s03 - s12;
            t[(i * 4) + 3] = d03 - (2 * d12);
        }

        for (var j = 0; j < 4; j++)
        {
            int x0 = t[j], x1 = t[4 + j], x2 = t[8 + j], x3 = t[12 + j];
            int s03 = x0 + x3, d03 = x0 - x3, s12 = x1 + x2, d12 = x1 - x2;
            coefficients[j] = s03 + s12;
            coefficients[4 + j] = (2 * d03) + d12;
            coefficients[8 + j] = s03 - s12;
            coefficients[12 + j] = d03 - (2 * d12);
        }
    }

    /// <summary>Applies the 4x4 Hadamard transform (unnormalised).</summary>
    /// <param name="input">The values, raster order.</param>
    /// <param name="output">Receives the transform, raster order.</param>
    public static void Hadamard4x4(ReadOnlySpan<int> input, Span<int> output) => H264Transform.InverseHadamard4x4(input, output);

    /// <summary>Returns the sum of absolute Hadamard-transformed differences of a 4x4 block, halved.</summary>
    /// <param name="difference">The differences, raster order.</param>
    /// <returns>SATD.</returns>
    /// <remarks>
    /// The sum of magnitudes does not depend on the order or sign of the transformed values, so the columns are
    /// transformed across four row vectors and each row within its vector by pairwise butterflies.
    /// </remarks>
    public static int Satd4x4(ReadOnlySpan<int> difference) =>
        Satd4x4(Vector128.Create(difference), Vector128.Create(difference[4..]), Vector128.Create(difference[8..]), Vector128.Create(difference[12..]));

    /// <summary>Returns the SATD of the difference of two 4x4 sample blocks.</summary>
    /// <param name="source">The first sample of the source block.</param>
    /// <param name="sourceStride">The source stride.</param>
    /// <param name="prediction">The first sample of the predicted block.</param>
    /// <param name="predictionStride">The prediction stride.</param>
    /// <returns>SATD of source minus prediction.</returns>
    public static int Satd4x4(ReadOnlySpan<byte> source, int sourceStride, ReadOnlySpan<byte> prediction, int predictionStride)
    {
        return Satd4x4(Row(source, prediction, 0), Row(source, prediction, 1), Row(source, prediction, 2), Row(source, prediction, 3));

        Vector128<int> Row(ReadOnlySpan<byte> s, ReadOnlySpan<byte> p, int j) => Widen(s[(j * sourceStride)..]) - Widen(p[(j * predictionStride)..]);

        static Vector128<int> Widen(ReadOnlySpan<byte> samples) =>
            Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalar(BinaryPrimitives.ReadUInt32LittleEndian(samples)).AsByte())).AsInt32();
    }

    /// <summary>Returns the SATD of four rows of differences.</summary>
    /// <param name="r0">Row 0.</param>
    /// <param name="r1">Row 1.</param>
    /// <param name="r2">Row 2.</param>
    /// <param name="r3">Row 3.</param>
    /// <returns>SATD.</returns>
    private static int Satd4x4(Vector128<int> r0, Vector128<int> r1, Vector128<int> r2, Vector128<int> r3)
    {
        Vector128<int> a0 = r0 + r1, a1 = r0 - r1, a2 = r2 + r3, a3 = r2 - r3;
        var sum = Vector128.Abs(Rows(a0 + a2)) + Vector128.Abs(Rows(a0 - a2)) + Vector128.Abs(Rows(a1 + a3)) + Vector128.Abs(Rows(a1 - a3));
        return (Vector128.Sum(sum) + 1) >> 1;

        static Vector128<int> Rows(Vector128<int> v)
        {
            var even = Vector128.Create(-1, 0, -1, 0);
            var pairs = Vector128.Shuffle(v, Vector128.Create(1, 0, 3, 2));
            var t = Vector128.ConditionalSelect(even, v + pairs, v - pairs);
            var halves = Vector128.Shuffle(t, Vector128.Create(2, 3, 0, 1));
            return Vector128.ConditionalSelect(Vector128.Create(-1, -1, 0, 0), t + halves, t - halves);
        }
    }

    /// <summary>Returns the coefficient class of a raster position: 0 for (even, even), 1 for (odd, odd), 2 otherwise.</summary>
    /// <param name="position">The position.</param>
    /// <returns>The class.</returns>
    private static int Class(int position)
    {
        var row = position >> 2;
        var column = position & 3;
        return (row & 1) == 0 && (column & 1) == 0 ? 0 : (row & 1) == 1 && (column & 1) == 1 ? 1 : 2;
    }

    /// <summary>Derives MF from the normalisation values.</summary>
    /// <returns>The table.</returns>
    private static int[] BuildMultipliers()
    {
        var table = new int[6 * 16];
        for (var m = 0; m < 6; m++)
        {
            for (var p = 0; p < 16; p++)
            {
                var v = H264Tables.NormAdjust4x4[(m * 3) + Class(p)];
                (long numerator, long denominator) = Class(p) switch
                {
                    0 => (32768L * 4, (long)v),
                    1 => (32768L * 64, 25L * v),
                    _ => (32768L * 16, 5L * v),
                };
                table[(m * 16) + p] = (int)(((2 * numerator) + denominator) / (2 * denominator));
            }
        }

        return table;
    }
}
