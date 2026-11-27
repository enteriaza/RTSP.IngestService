// residual_block_cavlc() writing (ITU-T H.264 7.3.5.3.2, 9.2): coeff_token, trailing-one signs, level_prefix and
// level_suffix with the adaptive suffix length (including the High-profile level_prefix escapes above 15),
// total_zeros and run_before. It is the exact inverse of the decoder's parse and uses the same code tables
// (H264Tables); the CAVLC context nC is derived by the caller from the neighbouring blocks (9.2.1).

namespace RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

/// <summary>CAVLC residual block and macroblock-layer code writing.</summary>
internal static class H264CavlcWriter
{
    /// <summary>me(v) codeNum by coded_block_pattern for Intra_4x4 macroblocks.</summary>
    private static readonly byte[] IntraCbpCode = Invert(H264Tables.CodedBlockPatternIntra);

    /// <summary>me(v) codeNum by coded_block_pattern for inter macroblocks.</summary>
    private static readonly byte[] InterCbpCode = Invert(H264Tables.CodedBlockPatternInter);

    /// <summary>Writes coded_block_pattern as me(v).</summary>
    /// <param name="w">The writer.</param>
    /// <param name="cbp">The pattern: luma bits 0 to 3, chroma in bits 4 and 5.</param>
    /// <param name="intra">Whether the macroblock is Intra_4x4.</param>
    public static void WriteCbp(H264BitWriter w, int cbp, bool intra) => w.WriteUe(intra ? IntraCbpCode[cbp] : InterCbpCode[cbp]);

    /// <summary>Returns the length of coded_block_pattern as me(v).</summary>
    /// <param name="cbp">The pattern.</param>
    /// <param name="intra">Whether the macroblock is Intra_4x4.</param>
    /// <returns>The bits.</returns>
    public static int CbpBits(int cbp, bool intra) => H264BitWriter.UeBits(intra ? IntraCbpCode[cbp] : InterCbpCode[cbp]);

    /// <summary>Writes one residual block.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="levels">The levels in scan order; its length is maxNumCoeff (4, 15 or 16).</param>
    /// <param name="nC">The context: -1 for chroma DC, otherwise 0 or more.</param>
    /// <returns>TotalCoeff.</returns>
    public static int WriteBlock(H264BitWriter w, ReadOnlySpan<int> levels, int nC)
    {
        var maxNumCoeff = levels.Length;
        Span<int> values = stackalloc int[16];
        Span<int> runs = stackalloc int[16];
        var total = 0;
        var lastIndex = -1;
        for (var i = maxNumCoeff - 1; i >= 0; i--)
        {
            if (levels[i] == 0)
            {
                continue;
            }

            if (lastIndex < 0)
            {
                lastIndex = i;
            }

            values[total] = levels[i];
            var run = 0;
            for (var j = i - 1; j >= 0 && levels[j] == 0; j--)
            {
                run++;
            }

            runs[total] = run;
            total++;
        }

        var trailingOnes = 0;
        while (trailingOnes < total && trailingOnes < 3 && Math.Abs(values[trailingOnes]) == 1)
        {
            trailingOnes++;
        }

        WriteCoeffToken(w, total, trailingOnes, nC);
        if (total == 0)
        {
            return 0;
        }

        for (var i = 0; i < trailingOnes; i++)
        {
            w.WriteBit(values[i] < 0);
        }

        var suffixLength = total > 10 && trailingOnes < 3 ? 1 : 0;
        for (var i = trailingOnes; i < total; i++)
        {
            var level = values[i];
            var levelCode = level > 0 ? (2 * level) - 2 : (-2 * level) - 1;
            if (i == trailingOnes && trailingOnes < 3)
            {
                levelCode -= 2;
            }

            WriteLevel(w, levelCode, suffixLength);
            if (suffixLength == 0)
            {
                suffixLength = 1;
            }

            if (Math.Abs(level) > (3 << (suffixLength - 1)) && suffixLength < 6)
            {
                suffixLength++;
            }
        }

        if (total < maxNumCoeff)
        {
            var totalZeros = lastIndex + 1 - total;
            if (maxNumCoeff == 4)
            {
                var index = ((total - 1) * 4) + totalZeros;
                w.Write(H264Tables.ChromaDcTotalZerosCodes[index], H264Tables.ChromaDcTotalZerosLengths[index]);
            }
            else
            {
                var index = ((total - 1) * 16) + totalZeros;
                w.Write(H264Tables.TotalZerosCodes[index], H264Tables.TotalZerosLengths[index]);
            }

            var zerosLeft = totalZeros;
            for (var i = 0; i < total - 1 && zerosLeft > 0; i++)
            {
                var index = ((Math.Min(zerosLeft, 7) - 1) * 15) + runs[i];
                w.Write(H264Tables.RunBeforeCodes[index], H264Tables.RunBeforeLengths[index]);
                zerosLeft -= runs[i];
            }
        }

        return total;
    }

    /// <summary>Writes coeff_token.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="total">TotalCoeff.</param>
    /// <param name="trailingOnes">TrailingOnes.</param>
    /// <param name="nC">The context.</param>
    private static void WriteCoeffToken(H264BitWriter w, int total, int trailingOnes, int nC)
    {
        var index = (total * 4) + trailingOnes;
        if (nC < 0)
        {
            w.Write(H264Tables.ChromaDcCoeffTokenCodes[index], H264Tables.ChromaDcCoeffTokenLengths[index]);
        }
        else if (nC >= 8)
        {
            w.Write(total == 0 ? 3u : (uint)(((total - 1) << 2) | trailingOnes), 6);
        }
        else
        {
            var table = nC < 2 ? 0 : nC < 4 ? 1 : 2;
            w.Write(H264Tables.CoeffTokenCodes[(table * 68) + index], H264Tables.CoeffTokenLengths[(table * 68) + index]);
        }
    }

    /// <summary>Writes level_prefix and level_suffix for a levelCode (9.2.2.1).</summary>
    /// <param name="w">The writer.</param>
    /// <param name="levelCode">The level code.</param>
    /// <param name="suffixLength">suffixLength.</param>
    private static void WriteLevel(H264BitWriter w, int levelCode, int suffixLength)
    {
        int prefix;
        int suffix;
        int suffixSize;
        var escapeBase = suffixLength == 0 ? 30 : 15 << suffixLength;
        if (suffixLength == 0 && levelCode < 14)
        {
            prefix = levelCode;
            suffix = 0;
            suffixSize = 0;
        }
        else if (suffixLength == 0 && levelCode < 30)
        {
            prefix = 14;
            suffix = levelCode - 14;
            suffixSize = 4;
        }
        else if (suffixLength > 0 && levelCode < escapeBase)
        {
            prefix = levelCode >> suffixLength;
            suffix = levelCode & ((1 << suffixLength) - 1);
            suffixSize = suffixLength;
        }
        else
        {
            var rest = levelCode - escapeBase;
            prefix = 15;
            while (rest >= (1 << (prefix - 2)) - 4096)
            {
                prefix++;
            }

            suffix = prefix == 15 ? rest : rest - ((1 << (prefix - 3)) - 4096);
            suffixSize = prefix - 3;
        }

        w.Write(0, prefix);
        w.Write(1, 1);
        if (suffixSize > 0)
        {
            w.Write((uint)suffix, suffixSize);
        }
    }

    /// <summary>Inverts a codeNum-to-pattern table.</summary>
    /// <param name="table">codeNum to coded_block_pattern.</param>
    /// <returns>coded_block_pattern to codeNum.</returns>
    private static byte[] Invert(ReadOnlySpan<byte> table)
    {
        var inverse = new byte[48];
        for (var code = 0; code < table.Length; code++)
        {
            inverse[table[code]] = (byte)code;
        }

        return inverse;
    }
}
