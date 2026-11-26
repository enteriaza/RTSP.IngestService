// Most-significant-bit-first reader for codec payloads (MPEG audio/video bit order). Peeking past the end yields zero
// bits, which lets prefix-code lookups read a fixed window near the end of a payload; consuming past the end throws,
// so a truncated payload always fails rather than decoding padding.

using System.Buffers.Binary;

namespace RTSP.IngestService.Media.Codecs.Common.Bitstreams;

/// <summary>Reads bits and Exp-Golomb codes from a span.</summary>
internal ref struct BitstreamReader
{
    /// <summary>The payload.</summary>
    private readonly ReadOnlySpan<byte> _data;

    /// <summary>Bits consumed.</summary>
    private int _position;

    /// <summary>Initializes a new instance of the <see cref="BitstreamReader"/> struct.</summary>
    /// <param name="data">The payload; at most 256 MiB.</param>
    public BitstreamReader(ReadOnlySpan<byte> data)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(data.Length, int.MaxValue / 8);
        _data = data;
    }

    /// <summary>Gets the number of bits consumed.</summary>
    public readonly int Position => _position;

    /// <summary>Gets the payload length in bits.</summary>
    public readonly int Length => _data.Length * 8;

    /// <summary>Gets the number of bits not yet consumed.</summary>
    public readonly int Remaining => Length - _position;

    /// <summary>Returns the next bits without consuming them, padding with zeros past the end.</summary>
    /// <param name="count">Number of bits, 0 to 32.</param>
    /// <returns>The bits, right-aligned.</returns>
    public readonly uint Peek(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 32);
        if (count == 0)
        {
            return 0;
        }

        var index = _position >> 3;
        ulong window;
        if (index + 8 <= _data.Length)
        {
            window = BinaryPrimitives.ReadUInt64BigEndian(_data[index..]);
        }
        else
        {
            window = 0;
            for (var i = 0; i < 8; i++)
            {
                window = (window << 8) | (index + i < _data.Length ? _data[index + i] : 0UL);
            }
        }

        return (uint)((window << (_position & 7)) >> (64 - count));
    }

    /// <summary>Reads bits.</summary>
    /// <param name="count">Number of bits, 0 to 32.</param>
    /// <returns>The bits, right-aligned.</returns>
    /// <exception cref="CodecException">Fewer than <paramref name="count"/> bits remain.</exception>
    public uint Read(int count)
    {
        var value = Peek(count);
        Skip(count);
        return value;
    }

    /// <summary>Reads one bit.</summary>
    /// <returns><see langword="true"/> for 1.</returns>
    /// <exception cref="CodecException">The payload is exhausted.</exception>
    public bool ReadBit() => Read(1) != 0;

    /// <summary>Consumes bits.</summary>
    /// <param name="count">Number of bits, non-negative.</param>
    /// <exception cref="CodecException">Fewer than <paramref name="count"/> bits remain.</exception>
    public void Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > Remaining)
        {
            CodecException.InvalidData("The bitstream is truncated.");
        }

        _position += count;
    }

    /// <summary>Skips to the next byte boundary.</summary>
    public void AlignToByte() => _position = (_position + 7) & ~7;

    /// <summary>Reads an unsigned Exp-Golomb code, <c>ue(v)</c> (ITU-T H.264 9.1).</summary>
    /// <returns>The value, 0 to 2^32 - 2.</returns>
    /// <exception cref="CodecException">Truncated, or more than 31 leading zero bits.</exception>
    public uint ReadExpGolomb()
    {
        var leadingZeros = 0;
        while (!ReadBit())
        {
            if (++leadingZeros > 31)
            {
                CodecException.InvalidData("An Exp-Golomb code has more than 31 leading zero bits.");
            }
        }

        return (uint)((1UL << leadingZeros) - 1 + Read(leadingZeros));
    }

    /// <summary>Reads a signed Exp-Golomb code, <c>se(v)</c> (ITU-T H.264 9.1.1).</summary>
    /// <returns>The value; code k maps to (-1)^(k+1) * Ceil(k / 2).</returns>
    /// <exception cref="CodecException">Truncated, or more than 31 leading zero bits.</exception>
    public int ReadSignedExpGolomb()
    {
        var code = ReadExpGolomb();
        var magnitude = (long)((code + 1UL) >> 1);
        return (int)((code & 1) != 0 ? magnitude : -magnitude);
    }
}
