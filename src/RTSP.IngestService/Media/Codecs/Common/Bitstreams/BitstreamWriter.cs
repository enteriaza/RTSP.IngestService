// Most-significant-bit-first writer into a caller-provided buffer, the counterpart of BitstreamReader. It never
// grows the buffer: running out of space is a ResourceLimit failure, so the caller sizes the buffer for the worst case.

namespace RTSP.IngestService.Media.Codecs.Common.Bitstreams;

/// <summary>Writes bits and Exp-Golomb codes into a span.</summary>
/// <param name="destination">The buffer. Bits not yet written are left as they are.</param>
internal ref struct BitstreamWriter(Span<byte> destination)
{
    /// <summary>The buffer.</summary>
    private readonly Span<byte> _destination = destination;

    /// <summary>Bits written.</summary>
    private int _position;

    /// <summary>Gets the number of bits written.</summary>
    public readonly int Position => _position;

    /// <summary>Gets the number of bytes started, including a partially written last byte.</summary>
    public readonly int BytesWritten => (_position + 7) >> 3;

    /// <summary>Writes the low <paramref name="count"/> bits of <paramref name="value"/>.</summary>
    /// <param name="value">The value; bits above <paramref name="count"/> must be zero.</param>
    /// <param name="count">Number of bits, 0 to 32.</param>
    /// <exception cref="CodecException">The buffer is full.</exception>
    public void Write(uint value, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 32);
        if (count < 32 && value >> count != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The value has bits above the field width.");
        }

        if ((long)_position + count > (long)_destination.Length * 8)
        {
            throw new CodecException(CodecErrorKind.ResourceLimit, "The bitstream buffer is full.");
        }

        for (var i = count - 1; i >= 0; i--)
        {
            var index = _position >> 3;
            var mask = (byte)(0x80 >> (_position & 7));
            _destination[index] = ((value >> i) & 1) != 0 ? (byte)(_destination[index] | mask) : (byte)(_destination[index] & ~mask);
            _position++;
        }
    }

    /// <summary>Writes one bit.</summary>
    /// <param name="bit">The bit.</param>
    public void WriteBit(bool bit) => Write(bit ? 1u : 0u, 1);

    /// <summary>Writes zero bits up to the next byte boundary.</summary>
    public void AlignToByte()
    {
        while ((_position & 7) != 0)
        {
            WriteBit(false);
        }
    }

    /// <summary>Writes an unsigned Exp-Golomb code, <c>ue(v)</c>.</summary>
    /// <param name="value">The value, at most 2^32 - 2.</param>
    public void WriteExpGolomb(uint value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, uint.MaxValue);
        var code = (ulong)value + 1;
        var bits = 64 - System.Numerics.BitOperations.LeadingZeroCount(code);
        Write(0, bits - 1);
        Write((uint)(code >> 32), Math.Max(bits - 32, 0));
        Write((uint)code & (bits >= 32 ? uint.MaxValue : (1u << bits) - 1), Math.Min(bits, 32));
    }

    /// <summary>Writes a signed Exp-Golomb code, <c>se(v)</c>.</summary>
    /// <param name="value">The value; <see cref="int.MinValue"/> is not representable.</param>
    public void WriteSignedExpGolomb(int value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, int.MinValue);
        WriteExpGolomb(value > 0 ? (uint)((2L * value) - 1) : (uint)(-2L * value));
    }
}
