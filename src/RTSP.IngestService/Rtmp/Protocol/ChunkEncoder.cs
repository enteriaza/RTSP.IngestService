// RTMP chunk stream encoder (RTMP specification 1.0, section 5.3). It splits each message into chunks of the current
// outbound chunk size and compresses message headers against the previous message on the same chunk stream, using
// the same header rules as ChunkDecoder so that the two are symmetric.

using System.Buffers;
using System.Buffers.Binary;

namespace RTSP.IngestService.Rtmp.Protocol;

/// <summary>
/// Encodes outbound messages as RTMP chunks.
/// </summary>
/// <remarks>
/// <para>Header selection for each message, relative to the previous message on the same chunk stream:</para>
/// <list type="bullet">
///   <item><description>Type 0 for the first message, a different message stream, or a timestamp lower than the previous one.</description></item>
///   <item><description>Type 1 when the length or type changed.</description></item>
///   <item><description>Type 2 when only the timestamp delta changed, or the previous header used an extended timestamp.</description></item>
///   <item><description>Type 3 when the header and delta are unchanged.</description></item>
/// </list>
/// <para>
/// Continuation chunks use type 3 and repeat the extended timestamp when the message header has one. Not thread-safe;
/// one instance belongs to one connection's single writer.
/// </para>
/// </remarks>
internal sealed class ChunkEncoder
{
    /// <summary>Lowest chunk stream ID available to messages (0 and 1 select longer basic headers).</summary>
    public const int MinChunkStreamId = 2;

    /// <summary>Highest chunk stream ID expressible with a 3-byte basic header.</summary>
    public const int MaxChunkStreamId = 65599;

    /// <summary>Largest message length expressible in a message header.</summary>
    public const int MaxMessageLength = 0xFFFFFF;

    /// <summary>Timestamp field value signalling that an extended timestamp follows.</summary>
    private const uint ExtendedTimestampMarker = 0xFFFFFF;

    /// <summary>Header state per chunk stream ID.</summary>
    private readonly Dictionary<int, OutboundState> _chunkStreams = [];

    /// <summary>Gets the outbound chunk size. Starts at 128.</summary>
    public int ChunkSize { get; private set; } = ChunkDecoder.DefaultChunkSize;

    /// <summary>Changes the chunk size used for subsequent messages.</summary>
    /// <param name="chunkSize">The new chunk size, 1 to 2^31-1.</param>
    /// <remarks>The peer must be told with a Set Chunk Size message sent before any message using the new size.</remarks>
    public void SetChunkSize(int chunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        ChunkSize = chunkSize;
    }

    /// <summary>Writes one message as one or more chunks.</summary>
    /// <param name="output">Destination of the chunk bytes.</param>
    /// <param name="chunkStreamId">Chunk stream ID, 2 to 65599.</param>
    /// <param name="messageStreamId">Message stream ID.</param>
    /// <param name="messageTypeId">Message type ID.</param>
    /// <param name="timestamp">Absolute timestamp in milliseconds.</param>
    /// <param name="payload">Message payload, at most 16777215 bytes.</param>
    public void WriteMessage(IBufferWriter<byte> output, int chunkStreamId, uint messageStreamId, byte messageTypeId, uint timestamp, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkStreamId, MinChunkStreamId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkStreamId, MaxChunkStreamId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, MaxMessageLength);

        _chunkStreams.TryGetValue(chunkStreamId, out var previous);
        int format;
        uint field;
        if (previous is null || previous.MessageStreamId != messageStreamId || timestamp < previous.Timestamp)
        {
            format = 0;
            field = timestamp;
        }
        else
        {
            field = timestamp - previous.Timestamp;
            if (previous.MessageLength != payload.Length || previous.MessageTypeId != messageTypeId)
            {
                format = 1;
            }
            else if (field != previous.TimestampDelta || previous.HasExtendedTimestamp)
            {
                format = 2;
            }
            else
            {
                format = 3;
            }
        }

        var extended = format != 3 && field >= ExtendedTimestampMarker;

        WriteBasicHeader(output, format, chunkStreamId);
        if (format <= 2)
        {
            var length = format switch { 0 => 11, 1 => 7, _ => 3 };
            var span = output.GetSpan(length);
            WriteUInt24(span, extended ? ExtendedTimestampMarker : field);
            if (format <= 1)
            {
                WriteUInt24(span[3..], (uint)payload.Length);
                span[6] = messageTypeId;
            }

            if (format == 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(span[7..], messageStreamId);
            }

            output.Advance(length);
        }

        if (extended)
        {
            WriteExtendedTimestamp(output, field);
        }

        var offset = Math.Min(ChunkSize, payload.Length);
        output.Write(payload[..offset]);
        while (offset < payload.Length)
        {
            WriteBasicHeader(output, 3, chunkStreamId);
            if (extended)
            {
                WriteExtendedTimestamp(output, field);
            }

            var count = Math.Min(ChunkSize, payload.Length - offset);
            output.Write(payload.Slice(offset, count));
            offset += count;
        }

        previous ??= _chunkStreams[chunkStreamId] = new OutboundState();
        previous.Timestamp = timestamp;
        previous.TimestampDelta = format == 0 ? timestamp : field;
        previous.MessageLength = payload.Length;
        previous.MessageTypeId = messageTypeId;
        previous.MessageStreamId = messageStreamId;
        if (format != 3)
        {
            previous.HasExtendedTimestamp = extended;
        }
    }

    /// <summary>Writes a basic header using the shortest encoding for <paramref name="chunkStreamId"/>.</summary>
    /// <param name="output">Destination.</param>
    /// <param name="format">Header type, 0 to 3.</param>
    /// <param name="chunkStreamId">Chunk stream ID, 2 to 65599.</param>
    private static void WriteBasicHeader(IBufferWriter<byte> output, int format, int chunkStreamId)
    {
        var span = output.GetSpan(3);
        if (chunkStreamId < 64)
        {
            span[0] = (byte)((format << 6) | chunkStreamId);
            output.Advance(1);
        }
        else if (chunkStreamId < 320)
        {
            span[0] = (byte)(format << 6);
            span[1] = (byte)(chunkStreamId - 64);
            output.Advance(2);
        }
        else
        {
            var value = chunkStreamId - 64;
            span[0] = (byte)((format << 6) | 1);
            span[1] = (byte)(value & 0xFF);
            span[2] = (byte)(value >> 8);
            output.Advance(3);
        }
    }

    /// <summary>Writes a 4-byte big-endian extended timestamp.</summary>
    /// <param name="output">Destination.</param>
    /// <param name="value">The timestamp or delta.</param>
    private static void WriteExtendedTimestamp(IBufferWriter<byte> output, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(output.GetSpan(4), value);
        output.Advance(4);
    }

    /// <summary>Writes a 24-bit big-endian value.</summary>
    /// <param name="destination">At least three bytes.</param>
    /// <param name="value">Value below 2^24.</param>
    private static void WriteUInt24(Span<byte> destination, uint value)
    {
        destination[0] = (byte)(value >> 16);
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)value;
    }

    /// <summary>Header of the previous message written on a chunk stream.</summary>
    private sealed class OutboundState
    {
        /// <summary>Gets or sets the previous absolute timestamp.</summary>
        public uint Timestamp { get; set; }

        /// <summary>Gets or sets the delta a decoder will reuse for a type 3 header.</summary>
        public uint TimestampDelta { get; set; }

        /// <summary>Gets or sets the previous message length.</summary>
        public int MessageLength { get; set; }

        /// <summary>Gets or sets the previous message type.</summary>
        public byte MessageTypeId { get; set; }

        /// <summary>Gets or sets the previous message stream ID.</summary>
        public uint MessageStreamId { get; set; }

        /// <summary>Gets or sets a value indicating whether the previous type 0, 1 or 2 header used an extended timestamp.</summary>
        public bool HasExtendedTimestamp { get; set; }
    }
}
