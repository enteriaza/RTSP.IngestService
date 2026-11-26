// Huffman coding of quantised spectral values with spectral codebooks 1 to 11 (ISO/IEC 14496-3, 4.6.3 and Tables
// 4.A.2 to 4.A.12), the exact inverse of the decoder's parsing: codebooks 1 to 4 code quadruples and 5 to 11 pairs;
// 1, 2, 5 and 6 are signed (offset indices), the others code magnitudes followed by one sign bit per non-zero value
// (1 = negative). Codebook 11 codes magnitudes of 16 and above as the escape symbol 16 followed, after the sign bits,
// by an escape_sequence: N one bits, a zero and N + 4 bits of m - 2^(N+4), with N = floor(log2 m) - 4.

using RTSP.IngestService.Media.Codecs.Common.Bitstreams;

namespace RTSP.IngestService.Media.Codecs.Audio.Aac.Encoder;

/// <summary>Spectral Huffman coding. Stateless and thread-safe.</summary>
internal static class AacSpectralCoder
{
    /// <summary>The largest magnitude a codebook can represent, indexed by codebook (11 escapes up to 8191).</summary>
    private static readonly int[] LargestMagnitude = [0, 1, 1, 2, 2, 4, 4, 7, 7, 12, 12, 8191];

    /// <summary>Gets the largest magnitude a codebook can represent.</summary>
    /// <param name="book">The codebook, 0 to 11.</param>
    /// <returns>The magnitude.</returns>
    public static int GetLargestMagnitude(int book) => LargestMagnitude[book];

    /// <summary>Counts the bits needed to code values with a codebook.</summary>
    /// <param name="values">The values, a multiple of four long; every magnitude must fit the codebook.</param>
    /// <param name="book">The codebook, 1 to 11.</param>
    /// <returns>The bit count.</returns>
    public static int Count(ReadOnlySpan<int> values, int book)
    {
        GetCodebook(book, out var lengths, out _);
        var bits = 0;
        if (book <= 4)
        {
            for (var i = 0; i < values.Length; i += 4)
            {
                bits += lengths[QuadIndex(values.Slice(i, 4), book)];
                if (book >= 3)
                {
                    bits += SignBits(values.Slice(i, 4));
                }
            }

            return bits;
        }

        for (var i = 0; i < values.Length; i += 2)
        {
            bits += lengths[PairIndex(values[i], values[i + 1], book)];
            if (book >= 7)
            {
                bits += SignBits(values.Slice(i, 2)) + EscapeBits(values[i], book) + EscapeBits(values[i + 1], book);
            }
        }

        return bits;
    }

    /// <summary>Writes values with a codebook.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="values">The values, a multiple of four long; every magnitude must fit the codebook.</param>
    /// <param name="book">The codebook, 1 to 11.</param>
    public static void Write(ref BitstreamWriter writer, ReadOnlySpan<int> values, int book)
    {
        GetCodebook(book, out var lengths, out var codes);
        if (book <= 4)
        {
            for (var i = 0; i < values.Length; i += 4)
            {
                var quad = values.Slice(i, 4);
                var index = QuadIndex(quad, book);
                writer.Write(codes[index], lengths[index]);
                if (book >= 3)
                {
                    WriteSigns(ref writer, quad);
                }
            }

            return;
        }

        for (var i = 0; i < values.Length; i += 2)
        {
            var index = PairIndex(values[i], values[i + 1], book);
            writer.Write(codes[index], lengths[index]);
            if (book >= 7)
            {
                WriteSigns(ref writer, values.Slice(i, 2));
                WriteEscape(ref writer, values[i], book);
                WriteEscape(ref writer, values[i + 1], book);
            }
        }
    }

    /// <summary>Gets the code and length tables of a codebook.</summary>
    /// <param name="book">The codebook, 1 to 11.</param>
    /// <param name="lengths">The codeword lengths by symbol.</param>
    /// <param name="codes">The codewords by symbol.</param>
    private static void GetCodebook(int book, out ReadOnlySpan<byte> lengths, out ReadOnlySpan<uint> codes)
    {
        switch (book)
        {
            case 1: lengths = AacHuffmanData.Book1Lengths; codes = AacHuffmanData.Book1Codes; break;
            case 2: lengths = AacHuffmanData.Book2Lengths; codes = AacHuffmanData.Book2Codes; break;
            case 3: lengths = AacHuffmanData.Book3Lengths; codes = AacHuffmanData.Book3Codes; break;
            case 4: lengths = AacHuffmanData.Book4Lengths; codes = AacHuffmanData.Book4Codes; break;
            case 5: lengths = AacHuffmanData.Book5Lengths; codes = AacHuffmanData.Book5Codes; break;
            case 6: lengths = AacHuffmanData.Book6Lengths; codes = AacHuffmanData.Book6Codes; break;
            case 7: lengths = AacHuffmanData.Book7Lengths; codes = AacHuffmanData.Book7Codes; break;
            case 8: lengths = AacHuffmanData.Book8Lengths; codes = AacHuffmanData.Book8Codes; break;
            case 9: lengths = AacHuffmanData.Book9Lengths; codes = AacHuffmanData.Book9Codes; break;
            case 10: lengths = AacHuffmanData.Book10Lengths; codes = AacHuffmanData.Book10Codes; break;
            case 11: lengths = AacHuffmanData.Book11Lengths; codes = AacHuffmanData.Book11Codes; break;
            default: throw new ArgumentOutOfRangeException(nameof(book), book, "Spectral codebooks are 1 to 11.");
        }
    }

    /// <summary>The symbol of a quadruple: 27 w + 9 x + 3 y + z of the values (books 1, 2, offset by 1) or magnitudes.</summary>
    /// <param name="quad">Four values.</param>
    /// <param name="book">The codebook, 1 to 4.</param>
    /// <returns>The symbol.</returns>
    private static int QuadIndex(ReadOnlySpan<int> quad, int book) => book <= 2
        ? (27 * (quad[0] + 1)) + (9 * (quad[1] + 1)) + (3 * (quad[2] + 1)) + quad[3] + 1
        : (27 * Math.Abs(quad[0])) + (9 * Math.Abs(quad[1])) + (3 * Math.Abs(quad[2])) + Math.Abs(quad[3]);

    /// <summary>The symbol of a pair: y * modulus + z of the values (books 5, 6, offset by 4) or (clamped) magnitudes.</summary>
    /// <param name="y">The first value.</param>
    /// <param name="z">The second value.</param>
    /// <param name="book">The codebook, 5 to 11.</param>
    /// <returns>The symbol.</returns>
    private static int PairIndex(int y, int z, int book) => book switch
    {
        5 or 6 => (9 * (y + 4)) + z + 4,
        7 or 8 => (8 * Math.Abs(y)) + Math.Abs(z),
        9 or 10 => (13 * Math.Abs(y)) + Math.Abs(z),
        _ => (17 * Math.Min(Math.Abs(y), 16)) + Math.Min(Math.Abs(z), 16),
    };

    /// <summary>Counts non-zero values, each of which carries a sign bit.</summary>
    /// <param name="values">The values.</param>
    /// <returns>The count.</returns>
    private static int SignBits(ReadOnlySpan<int> values)
    {
        var count = 0;
        foreach (var value in values)
        {
            count += value != 0 ? 1 : 0;
        }

        return count;
    }

    /// <summary>Writes one sign bit per non-zero value.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="values">The values.</param>
    private static void WriteSigns(ref BitstreamWriter writer, ReadOnlySpan<int> values)
    {
        foreach (var value in values)
        {
            if (value != 0)
            {
                writer.WriteBit(value < 0);
            }
        }
    }

    /// <summary>The escape_sequence length for a value: 2N + 5 bits for magnitudes of 16 and above in codebook 11.</summary>
    /// <param name="value">The value.</param>
    /// <param name="book">The codebook.</param>
    /// <returns>The bit count.</returns>
    private static int EscapeBits(int value, int book)
    {
        var magnitude = Math.Abs(value);
        return book != 11 || magnitude < 16 ? 0 : (2 * (int.Log2(magnitude) - 4)) + 5;
    }

    /// <summary>Writes the escape_sequence for a value, if it needs one.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="book">The codebook.</param>
    private static void WriteEscape(ref BitstreamWriter writer, int value, int book)
    {
        var magnitude = Math.Abs(value);
        if (book != 11 || magnitude < 16)
        {
            return;
        }

        var prefix = int.Log2(magnitude) - 4;
        for (var i = 0; i < prefix; i++)
        {
            writer.WriteBit(true);
        }

        writer.WriteBit(false);
        writer.Write((uint)(magnitude - (1 << (prefix + 4))), prefix + 4);
    }
}
