// Table-driven decoder for an arbitrary (not necessarily canonical) prefix code given as codeword and length per
// symbol, as codec specifications publish their Huffman tables. One root lookup of RootBits bits resolves short
// codewords; longer ones go through a second-level table per root prefix. Construction rejects codes that are not
// prefix-free, so a mistyped table fails at startup instead of mis-decoding. Codes may be incomplete: unused bit
// patterns decode as invalid data.

using RTSP.IngestService.Media.Codecs.Common.Bitstreams;

namespace RTSP.IngestService.Media.Codecs.Common.Entropy;

/// <summary>Decodes symbols of a prefix code.</summary>
internal sealed class PrefixCodeDecoder
{
    /// <summary>Longest supported codeword.</summary>
    public const int MaxCodeLength = 32;

    /// <summary>Flag of an entry that points to a second-level table.</summary>
    private const int LinkFlag = 1 << 6;

    /// <summary>Mask of the length field (codeword length, or second-level index width for links).</summary>
    private const int LengthMask = 0x3F;

    /// <summary>Bits resolved by the root table.</summary>
    private readonly int _rootBits;

    /// <summary>
    /// Root table followed by second-level tables. Entry: <c>symbol &lt;&lt; 7 | length</c> (length 0 = unused bit
    /// pattern), or <c>offset &lt;&lt; 7 | LinkFlag | subBits</c>.
    /// </summary>
    private readonly int[] _table;

    /// <summary>Initializes a new instance of the <see cref="PrefixCodeDecoder"/> class.</summary>
    /// <param name="codes">Codeword per symbol, right-aligned.</param>
    /// <param name="lengths">Codeword length per symbol, 1 to <see cref="MaxCodeLength"/>.</param>
    /// <param name="rootBits">Root table index width, 1 to 16.</param>
    /// <exception cref="ArgumentException">The code is malformed or not prefix-free.</exception>
    public PrefixCodeDecoder(ReadOnlySpan<uint> codes, ReadOnlySpan<byte> lengths, int rootBits = 9)
    {
        ArgumentOutOfRangeException.ThrowIfZero(codes.Length);
        ArgumentOutOfRangeException.ThrowIfNotEqual(lengths.Length, codes.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(rootBits, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rootBits, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(codes.Length, 1 << 24);
        _rootBits = rootBits;
        SymbolCount = codes.Length;

        var subBits = new Dictionary<uint, int>();
        for (var symbol = 0; symbol < codes.Length; symbol++)
        {
            int length = lengths[symbol];
            if (length is < 1 or > MaxCodeLength || (length < 32 && codes[symbol] >> length != 0))
            {
                throw new ArgumentException($"Symbol {symbol} has an invalid codeword or length {length}.", nameof(codes));
            }

            if (length > rootBits)
            {
                var prefix = codes[symbol] >> (length - rootBits);
                subBits[prefix] = Math.Max(subBits.GetValueOrDefault(prefix), length - rootBits);
            }
        }

        var size = 1 << rootBits;
        var offsets = new Dictionary<uint, int>();
        foreach (var (prefix, bits) in subBits.OrderBy(static pair => pair.Key))
        {
            offsets[prefix] = size;
            size += 1 << bits;
        }

        _table = new int[size];
        foreach (var (prefix, offset) in offsets)
        {
            _table[prefix] = (offset << 7) | LinkFlag | subBits[prefix];
        }

        for (var symbol = 0; symbol < codes.Length; symbol++)
        {
            int length = lengths[symbol];
            var code = codes[symbol];
            if (length <= rootBits)
            {
                Fill(0, (int)(code << (rootBits - length)), 1 << (rootBits - length), (symbol << 7) | length, symbol);
            }
            else
            {
                var prefix = code >> (length - rootBits);
                var bits = subBits[prefix];
                var rest = length - rootBits;
                var suffix = code & ((1u << rest) - 1);
                Fill(offsets[prefix], (int)(suffix << (bits - rest)), 1 << (bits - rest), (symbol << 7) | rest, symbol);
            }
        }
    }

    /// <summary>Gets the number of symbols.</summary>
    public int SymbolCount { get; }

    /// <summary>Decodes one symbol.</summary>
    /// <param name="reader">The reader, advanced past the codeword.</param>
    /// <returns>The symbol index.</returns>
    /// <exception cref="CodecException">The bits are not a codeword, or the payload ends inside one.</exception>
    public int Decode(ref BitstreamReader reader)
    {
        var entry = _table[reader.Peek(_rootBits)];
        if ((entry & LinkFlag) != 0)
        {
            reader.Skip(_rootBits);
            entry = _table[(entry >> 7) + (int)reader.Peek(entry & LengthMask)];
        }

        var length = entry & LengthMask;
        if (length == 0)
        {
            CodecException.InvalidData("The bitstream contains an invalid codeword.");
        }

        reader.Skip(length);
        return entry >> 7;
    }

    /// <summary>Fills a run of entries, failing if any is already taken (the code is not prefix-free).</summary>
    /// <param name="table">Start of the table being filled.</param>
    /// <param name="first">First index relative to <paramref name="table"/>.</param>
    /// <param name="count">Number of entries.</param>
    /// <param name="value">The entry.</param>
    /// <param name="symbol">The symbol, for the error message.</param>
    private void Fill(int table, int first, int count, int value, int symbol)
    {
        for (var i = 0; i < count; i++)
        {
            ref var slot = ref _table[table + first + i];
            if (slot != 0)
            {
                throw new ArgumentException($"The codeword of symbol {symbol} is a prefix of, or prefixed by, another codeword.");
            }

            slot = value;
        }
    }
}
