// CAVLC syntax elements (ITU-T H.264 9.1, 9.2): Exp-Golomb mapped elements and residual_block_cavlc(). The
// coeff_token, total_zeros and run_before codes are built once from the tables in H264Tables; construction
// fails if a transcribed table is not a prefix code.

using RTSP.IngestService.Media.Codecs.Common.Bitstreams;
using RTSP.IngestService.Media.Codecs.Common.Entropy;

namespace RTSP.IngestService.Media.Codecs.Video.H264;

/// <summary>Reads CAVLC macroblock syntax.</summary>
/// <param name="reader">The slice data reader.</param>
internal ref struct H264CavlcSyntax(BitstreamReader reader) : IH264EntropyReader
{
    /// <summary>The slice data reader.</summary>
    public BitstreamReader Reader = reader;

    /// <inheritdoc/>
    public readonly bool IsCabac => false;

    /// <inheritdoc/>
    public int MbType(H264SliceDecoder d) => H264Syntax.ReadUe(ref Reader, 48, "mb_type");

    /// <inheritdoc/>
    public bool Transform8x8(H264SliceDecoder d) => Reader.ReadBit();

    /// <inheritdoc/>
    public int IntraPredMode() => Reader.ReadBit() ? -1 : (int)Reader.Read(3);

    /// <inheritdoc/>
    public int IntraChromaPredMode(H264SliceDecoder d) => H264Syntax.ReadUe(ref Reader, 3, "intra_chroma_pred_mode");

    /// <inheritdoc/>
    public int SubMbType(H264SliceDecoder d) => H264Syntax.ReadUe(ref Reader, 12, "sub_mb_type");

    /// <inheritdoc/>
    public int RefIdx(H264SliceDecoder d, int list, int bx, int by) =>
        d.NumRefIdxActive[list] == 2 ? (Reader.ReadBit() ? 0 : 1) : H264Syntax.ReadUe(ref Reader, 31, "ref_idx");

    /// <inheritdoc/>
    public int Mvd(H264SliceDecoder d, int list, int component, int bx, int by) =>
        H264Syntax.ReadSe(ref Reader, -(1 << 15), (1 << 15) - 1, "mvd");

    /// <inheritdoc/>
    public int CodedBlockPattern(H264SliceDecoder d)
    {
        var code = H264Syntax.ReadUe(ref Reader, 47, "coded_block_pattern");
        return d.CurrentIsIntraNxN ? H264Tables.CodedBlockPatternIntra[code] : H264Tables.CodedBlockPatternInter[code];
    }

    /// <inheritdoc/>
    public int MbQpDelta(H264SliceDecoder d) => H264Syntax.ReadSe(ref Reader, -26, 25, "mb_qp_delta");

    /// <inheritdoc/>
    public int ResidualBlock(H264SliceDecoder d, in H264ResidualBlock block, int startIdx, int endIdx, int maxNumCoeff, Span<int> levels)
    {
        var nc = block.Category switch
        {
            H264BlockCategory.ChromaDc => -1,
            H264BlockCategory.ChromaAc => d.ChromaNc(block.Component, block.Index),
            H264BlockCategory.LumaDc => d.LumaNc(0),
            _ => d.LumaNc(block.Index),
        };
        int totalCoeff, trailingOnes;
        if (nc >= 8)
        {
            var value = (int)Reader.Read(6);
            if (value == 3)
            {
                totalCoeff = 0;
                trailingOnes = 0;
            }
            else
            {
                totalCoeff = (value >> 2) + 1;
                trailingOnes = value & 3;
                if (trailingOnes > totalCoeff)
                {
                    CodecException.InvalidData("coeff_token is invalid.");
                }
            }
        }
        else
        {
            var table = nc < 0 ? H264CavlcCodes.ChromaDcCoeffToken : H264CavlcCodes.CoeffToken[nc < 2 ? 0 : nc < 4 ? 1 : 2];
            var symbol = table.Symbols[table.Decoder.Decode(ref Reader)];
            totalCoeff = symbol >> 2;
            trailingOnes = symbol & 3;
        }

        if (totalCoeff == 0)
        {
            return 0;
        }

        var span = endIdx - startIdx + 1;
        if (totalCoeff > span)
        {
            CodecException.InvalidData("coeff_token exceeds the block size.");
        }

        Span<int> levelVal = stackalloc int[16];
        var suffixLength = totalCoeff > 10 && trailingOnes < 3 ? 1 : 0;
        for (var i = 0; i < totalCoeff; i++)
        {
            if (i < trailingOnes)
            {
                levelVal[i] = Reader.ReadBit() ? -1 : 1;
                continue;
            }

            var prefix = 0;
            while (!Reader.ReadBit())
            {
                if (++prefix > 28)
                {
                    CodecException.InvalidData("level_prefix is too long.");
                }
            }

            var suffixSize = prefix == 14 && suffixLength == 0 ? 4 : prefix >= 15 ? prefix - 3 : suffixLength;
            var levelCode = (long)(Math.Min(15, prefix) << suffixLength) + (suffixSize > 0 ? Reader.Read(suffixSize) : 0);
            if (prefix >= 15 && suffixLength == 0)
            {
                levelCode += 15;
            }

            if (prefix >= 16)
            {
                levelCode += (1L << (prefix - 3)) - 4096;
            }

            if (i == trailingOnes && trailingOnes < 3)
            {
                levelCode += 2;
            }

            var level = (levelCode & 1) == 0 ? (levelCode + 2) >> 1 : (-levelCode - 1) >> 1;
            if (level > H264SliceDecoder.MaxCoefficient || level < -H264SliceDecoder.MaxCoefficient)
            {
                CodecException.InvalidData("A coefficient level is out of range.");
            }

            levelVal[i] = (int)level;
            if (suffixLength == 0)
            {
                suffixLength = 1;
            }

            if (Math.Abs(level) > (3 << (suffixLength - 1)) && suffixLength < 6)
            {
                suffixLength++;
            }
        }

        var zerosLeft = 0;
        if (totalCoeff < span)
        {
            var tables = maxNumCoeff == 4 ? H264CavlcCodes.ChromaDcTotalZeros : H264CavlcCodes.TotalZeros;
            var table = tables[totalCoeff - 1];
            zerosLeft = table.Symbols[table.Decoder.Decode(ref Reader)];
            if (zerosLeft > span - totalCoeff)
            {
                CodecException.InvalidData("total_zeros exceeds the block size.");
            }
        }

        var coeffNum = -1;
        Span<int> runVal = stackalloc int[16];
        for (var i = 0; i < totalCoeff - 1; i++)
        {
            var run = 0;
            if (zerosLeft > 0)
            {
                var table = H264CavlcCodes.RunBefore[Math.Min(zerosLeft, 7) - 1];
                run = table.Symbols[table.Decoder.Decode(ref Reader)];
                if (run > zerosLeft)
                {
                    CodecException.InvalidData("run_before exceeds zerosLeft.");
                }
            }

            runVal[i] = run;
            zerosLeft -= run;
        }

        runVal[totalCoeff - 1] = zerosLeft;
        for (var i = totalCoeff - 1; i >= 0; i--)
        {
            coeffNum += runVal[i] + 1;
            levels[startIdx + coeffNum] = levelVal[i];
        }

        return totalCoeff;
    }

    /// <inheritdoc/>
    public void Pcm(Span<byte> samples) => H264CabacReader.ReadPcmSamples(ref Reader, samples);
}

/// <summary>Prefix-code decoders of the CAVLC tables.</summary>
internal static class H264CavlcCodes
{
    /// <summary>Gets coeff_token for 0 &lt;= nC &lt; 2, 2 &lt;= nC &lt; 4 and 4 &lt;= nC &lt; 8; symbols are TotalCoeff * 4 + TrailingOnes.</summary>
    public static readonly Code[] CoeffToken =
    [
        Build(H264Tables.CoeffTokenCodes[..68], H264Tables.CoeffTokenLengths[..68]),
        Build(H264Tables.CoeffTokenCodes.Slice(68, 68), H264Tables.CoeffTokenLengths.Slice(68, 68)),
        Build(H264Tables.CoeffTokenCodes.Slice(136, 68), H264Tables.CoeffTokenLengths.Slice(136, 68)),
    ];

    /// <summary>Gets coeff_token for nC = -1.</summary>
    public static readonly Code ChromaDcCoeffToken = Build(H264Tables.ChromaDcCoeffTokenCodes, H264Tables.ChromaDcCoeffTokenLengths);

    /// <summary>Gets total_zeros per TotalCoeff - 1 for 4x4 blocks.</summary>
    public static readonly Code[] TotalZeros = BuildRows(H264Tables.TotalZerosCodes, H264Tables.TotalZerosLengths, 15, 16);

    /// <summary>Gets total_zeros per TotalCoeff - 1 for chroma DC.</summary>
    public static readonly Code[] ChromaDcTotalZeros = BuildRows(H264Tables.ChromaDcTotalZerosCodes, H264Tables.ChromaDcTotalZerosLengths, 3, 4);

    /// <summary>Gets run_before per Min(zerosLeft, 7) - 1.</summary>
    public static readonly Code[] RunBefore = BuildRows(H264Tables.RunBeforeCodes, H264Tables.RunBeforeLengths, 7, 15);

    /// <summary>Builds one code from entries whose index is the symbol; length 0 marks an absent symbol.</summary>
    /// <param name="codes">The codewords.</param>
    /// <param name="lengths">The lengths.</param>
    /// <returns>The code.</returns>
    private static Code Build(ReadOnlySpan<byte> codes, ReadOnlySpan<byte> lengths)
    {
        var count = 0;
        foreach (var length in lengths)
        {
            count += length != 0 ? 1 : 0;
        }

        var c = new uint[count];
        var l = new byte[count];
        var symbols = new int[count];
        var n = 0;
        for (var i = 0; i < lengths.Length; i++)
        {
            if (lengths[i] != 0)
            {
                c[n] = codes[i];
                l[n] = lengths[i];
                symbols[n++] = i;
            }
        }

        return new Code(new PrefixCodeDecoder(c, l, 8), symbols);
    }

    /// <summary>Builds one code per table row.</summary>
    /// <param name="codes">The codewords.</param>
    /// <param name="lengths">The lengths.</param>
    /// <param name="rows">The number of rows.</param>
    /// <param name="width">Entries per row.</param>
    /// <returns>The codes.</returns>
    private static Code[] BuildRows(ReadOnlySpan<byte> codes, ReadOnlySpan<byte> lengths, int rows, int width)
    {
        var result = new Code[rows];
        for (var r = 0; r < rows; r++)
        {
            result[r] = Build(codes.Slice(r * width, width), lengths.Slice(r * width, width));
        }

        return result;
    }

    /// <summary>A decoder and the syntax value of each of its symbols.</summary>
    /// <param name="Decoder">The decoder.</param>
    /// <param name="Symbols">The value per decoded symbol.</param>
    internal sealed record Code(PrefixCodeDecoder Decoder, int[] Symbols);
}
