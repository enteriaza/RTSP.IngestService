// Most-significant-bit-first RBSP writer for the encoder. Unlike the shared BitstreamWriter it is a class that grows
// its buffer (allocated at exact sizes, so the bound is the memory held) up to a fixed bound and can rewind to an earlier position, which the encoder uses to replace a
// macroblock that exceeded the size limit by I_PCM and to retry a picture at a higher QP.

using System.Buffers.Binary;

namespace RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

/// <summary>A rewindable, bounded bit writer.</summary>
internal sealed class H264BitWriter : IDisposable
{
    /// <summary>The most bytes the buffer may grow to.</summary>
    private readonly int _maxBytes;

    /// <summary>The buffer.</summary>
    private byte[] _buffer;

    /// <summary>Bits written.</summary>
    private long _position;

    /// <summary>Initializes a new instance of the <see cref="H264BitWriter"/> class.</summary>
    /// <param name="initialBytes">The initial capacity.</param>
    /// <param name="maxBytes">The hard capacity bound.</param>
    public H264BitWriter(int initialBytes, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, initialBytes);
        _maxBytes = maxBytes;
        _buffer = new byte[initialBytes];
    }

    /// <summary>Gets the number of bits written.</summary>
    public long Position => _position;

    /// <summary>Gets the written bytes, including a partial last byte.</summary>
    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, (int)((_position + 7) >> 3));

    /// <summary>Gets the capacity currently allocated, in bytes.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Gets whether the position is byte aligned.</summary>
    public bool IsAligned => (_position & 7) == 0;

    /// <summary>Discards everything written.</summary>
    public void Clear() => Rewind(0);

    /// <summary>Returns to an earlier position, zeroing the bits after it.</summary>
    /// <param name="position">The position, at most <see cref="Position"/>.</param>
    public void Rewind(long position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, _position);
        var first = (int)(position >> 3);
        var end = (int)((_position + 7) >> 3);
        if ((position & 7) != 0)
        {
            _buffer[first] &= (byte)(0xFF << (8 - (int)(position & 7)));
            first++;
        }

        if (end > first)
        {
            _buffer.AsSpan(first, end - first).Clear();
        }

        _position = position;
    }

    /// <summary>Writes the low <paramref name="count"/> bits of <paramref name="value"/>.</summary>
    /// <param name="value">The value; bits above <paramref name="count"/> must be zero.</param>
    /// <param name="count">0 to 32.</param>
    public void Write(uint value, int count)
    {
        if ((uint)count > 32 || (count < 32 && value >> count != 0))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The value does not fit the field width.");
        }

        Ensure(count);
        for (var i = count - 1; i >= 0; i--)
        {
            if (((value >> i) & 1) != 0)
            {
                _buffer[(int)(_position >> 3)] |= (byte)(0x80 >> (int)(_position & 7));
            }

            _position++;
        }
    }

    /// <summary>Writes one bit.</summary>
    /// <param name="bit">The bit.</param>
    public void WriteBit(bool bit) => Write(bit ? 1u : 0u, 1);

    /// <summary>Writes <c>ue(v)</c>.</summary>
    /// <param name="value">The value, below 2^31.</param>
    public void WriteUe(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        var code = (uint)value + 1;
        var bits = 32 - System.Numerics.BitOperations.LeadingZeroCount(code);
        Write(0, bits - 1);
        Write(code, bits);
    }

    /// <summary>Writes <c>se(v)</c>.</summary>
    /// <param name="value">The value, within ±2^30.</param>
    public void WriteSe(int value) => WriteUe(value > 0 ? (2 * value) - 1 : -2 * value);

    /// <summary>Writes the bytes of a byte-aligned run.</summary>
    /// <param name="bytes">The bytes.</param>
    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        if (!IsAligned)
        {
            throw new InvalidOperationException("Byte runs must start byte aligned.");
        }

        Ensure(bytes.Length * 8);
        bytes.CopyTo(_buffer.AsSpan((int)(_position >> 3)));
        _position += bytes.Length * 8L;
    }

    /// <summary>Appends a run of bits copied from a buffer at any bit offset, up to 32 bits at a time.</summary>
    /// <param name="source">The source bytes, most significant bit first.</param>
    /// <param name="offset">The bit offset of the first bit in <paramref name="source"/>.</param>
    /// <param name="count">The number of bits.</param>
    public void WriteBits(ReadOnlySpan<byte> source, long offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + count, source.Length * 8L, nameof(count));
        Ensure(count);
        while (count > 0)
        {
            var n = Math.Min(count, 32);
            var index = (int)(offset >> 3);
            ulong window;
            if (index + 8 <= source.Length)
            {
                window = BinaryPrimitives.ReadUInt64BigEndian(source[index..]);
            }
            else
            {
                window = 0;
                for (var i = 0; index + i < source.Length; i++)
                {
                    window |= (ulong)source[index + i] << (56 - (8 * i));
                }
            }

            // The bits after the position are zero, so the field is ORed in; at most 7 + 32 bits span the window.
            var bits = (window << (int)(offset & 7)) >> (64 - n) << (64 - n) >> (int)(_position & 7);
            var target = (int)(_position >> 3);
            if (target + 8 <= _buffer.Length)
            {
                var span = _buffer.AsSpan(target, 8);
                BinaryPrimitives.WriteUInt64BigEndian(span, BinaryPrimitives.ReadUInt64BigEndian(span) | bits);
            }
            else
            {
                for (var i = 0; target + i < _buffer.Length && i < 8; i++)
                {
                    _buffer[target + i] |= (byte)(bits >> (56 - (8 * i)));
                }
            }

            _position += n;
            offset += n;
            count -= n;
        }
    }

    /// <summary>Writes zero bits up to the next byte boundary.</summary>
    public void AlignZero()
    {
        while (!IsAligned)
        {
            Write(0, 1);
        }
    }

    /// <summary>Writes rbsp_trailing_bits(): a one bit, then zero bits to the byte boundary.</summary>
    public void WriteTrailingBits()
    {
        Write(1, 1);
        AlignZero();
    }

    /// <summary>Returns the number of bits of <c>ue(v)</c> for a value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The code length.</returns>
    public static int UeBits(int value) => (2 * (32 - System.Numerics.BitOperations.LeadingZeroCount((uint)value + 1))) - 1;

    /// <summary>Returns the number of bits of <c>se(v)</c> for a value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The code length.</returns>
    public static int SeBits(int value) => UeBits(value > 0 ? (2 * value) - 1 : -2 * value);

    /// <inheritdoc/>
    public void Dispose()
    {
        _buffer = [];
        _position = 0;
    }

    /// <summary>Grows the buffer for <paramref name="bits"/> more bits, within the bound.</summary>
    /// <param name="bits">The bits about to be written.</param>
    private void Ensure(int bits)
    {
        var needed = (_position + bits + 7) >> 3;
        if (needed <= _buffer.Length)
        {
            return;
        }

        ObjectDisposedException.ThrowIf(_buffer.Length == 0, this);

        if (needed > _maxBytes)
        {
            throw new CodecException(CodecErrorKind.ResourceLimit, "The coded picture exceeds the encoder's output bound.");
        }

        var grown = new byte[(int)Math.Min(_maxBytes, Math.Max(needed, (long)_buffer.Length * 2))];
        _buffer.AsSpan(0, (int)((_position + 7) >> 3)).CopyTo(grown);
        _buffer = grown;
    }
}
