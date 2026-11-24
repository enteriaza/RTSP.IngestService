// Big-endian ISO base media file format (ISO/IEC 14496-12) writer over a fixed span. Boxes are opened and closed
// explicitly; closing a box patches its 32-bit size. Every write is bounds-checked against the span, so output can
// never exceed the buffer it was given, and box sizes are checked for 32-bit overflow.

using System.Buffers.Binary;

namespace RTSP.IngestService.Hls;

/// <summary>The MP4 output would exceed its size limit or a field's range.</summary>
internal sealed class Mp4SizeException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="Mp4SizeException"/> class.</summary>
    public Mp4SizeException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Mp4SizeException"/> class.</summary>
    /// <param name="message">The message.</param>
    public Mp4SizeException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Mp4SizeException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public Mp4SizeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Writes ISO BMFF boxes into a span.</summary>
/// <param name="buffer">The destination.</param>
internal ref struct Mp4Writer(Span<byte> buffer)
{
    /// <summary>The destination.</summary>
    private readonly Span<byte> _buffer = buffer;

    /// <summary>Gets the number of bytes written.</summary>
    public int Position { get; private set; }

    /// <summary>Opens a box.</summary>
    /// <param name="type">The four-character type.</param>
    /// <returns>The box start, for <see cref="End"/>.</returns>
    public int Begin(string type)
    {
        var start = Position;
        UInt32(0);
        FourCC(type);
        return start;
    }

    /// <summary>Opens a full box (with version and flags).</summary>
    /// <param name="type">The four-character type.</param>
    /// <param name="version">The version.</param>
    /// <param name="flags">The 24-bit flags.</param>
    /// <returns>The box start, for <see cref="End"/>.</returns>
    public int BeginFull(string type, byte version, uint flags)
    {
        var start = Begin(type);
        UInt8(version);
        UInt24(flags);
        return start;
    }

    /// <summary>Closes a box by writing its size.</summary>
    /// <param name="start">The value returned when it was opened.</param>
    public readonly void End(int start) => BinaryPrimitives.WriteUInt32BigEndian(_buffer[start..], checked((uint)(Position - start)));

    /// <summary>Writes a byte.</summary>
    /// <param name="value">The value.</param>
    public void UInt8(byte value) => Reserve(1)[0] = value;

    /// <summary>Writes a 16-bit value.</summary>
    /// <param name="value">The value.</param>
    public void UInt16(ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Reserve(2), value);

    /// <summary>Writes a 24-bit value.</summary>
    /// <param name="value">The value; must be below 2^24.</param>
    public void UInt24(uint value)
    {
        if (value > 0xFFFFFF)
        {
            throw new Mp4SizeException($"The value {value} does not fit in 24 bits.");
        }

        var span = Reserve(3);
        span[0] = (byte)(value >> 16);
        span[1] = (byte)(value >> 8);
        span[2] = (byte)value;
    }

    /// <summary>Writes a 32-bit value.</summary>
    /// <param name="value">The value.</param>
    public void UInt32(uint value) => BinaryPrimitives.WriteUInt32BigEndian(Reserve(4), value);

    /// <summary>Writes a signed 32-bit value.</summary>
    /// <param name="value">The value.</param>
    public void Int32(int value) => BinaryPrimitives.WriteInt32BigEndian(Reserve(4), value);

    /// <summary>Writes a 64-bit value.</summary>
    /// <param name="value">The value.</param>
    public void UInt64(ulong value) => BinaryPrimitives.WriteUInt64BigEndian(Reserve(8), value);

    /// <summary>Writes a four-character code.</summary>
    /// <param name="code">Four ASCII characters.</param>
    public void FourCC(string code)
    {
        var span = Reserve(4);
        for (var i = 0; i < 4; i++)
        {
            span[i] = (byte)code[i];
        }
    }

    /// <summary>Writes bytes.</summary>
    /// <param name="data">The bytes.</param>
    public void Bytes(ReadOnlySpan<byte> data) => data.CopyTo(Reserve(data.Length));

    /// <summary>Writes zero bytes.</summary>
    /// <param name="count">How many.</param>
    public void Zeros(int count) => Reserve(count).Clear();

    /// <summary>Writes the unity transformation matrix of <c>mvhd</c> and <c>tkhd</c>.</summary>
    public void UnityMatrix()
    {
        UInt32(0x00010000);
        Zeros(12);
        UInt32(0x00010000);
        Zeros(12);
        UInt32(0x40000000);
    }

    /// <summary>Advances over <paramref name="count"/> bytes, checking the remaining space.</summary>
    /// <param name="count">The byte count.</param>
    /// <returns>The reserved span.</returns>
    private Span<byte> Reserve(int count)
    {
        if (count > _buffer.Length - Position)
        {
            throw new Mp4SizeException($"The MP4 output exceeds its limit of {_buffer.Length} bytes.");
        }

        var span = _buffer.Slice(Position, count);
        Position += count;
        return span;
    }
}
