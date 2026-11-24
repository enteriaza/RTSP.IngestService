// Outbound side of an RTMP connection: encodes protocol control messages, User Control events and AMF0 commands as
// chunks into the transport's PipeWriter. Writes are buffered until the connection flushes them, so a batch of
// responses to one read is sent together. Used only from the connection's receive loop.

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using RTSP.IngestService.Rtmp.Amf;
using RTSP.IngestService.Rtmp.Protocol;

namespace RTSP.IngestService.Rtmp;

/// <summary>
/// Writes RTMP messages to a connection's output.
/// </summary>
/// <remarks>Not thread-safe; the owning connection is the only caller.</remarks>
/// <param name="output">The transport output.</param>
internal sealed class RtmpMessageWriter(PipeWriter output)
{
    /// <summary>Chunk stream for protocol control and User Control messages.</summary>
    public const int ControlChunkStreamId = 2;

    /// <summary>Chunk stream for command responses on message stream 0.</summary>
    public const int CommandChunkStreamId = 3;

    /// <summary>Chunk stream for <c>onStatus</c> messages on published streams.</summary>
    public const int StatusChunkStreamId = 5;

    /// <summary>Encodes chunks.</summary>
    private readonly ChunkEncoder _encoder = new();

    /// <summary>Scratch buffer for AMF0 command bodies.</summary>
    private readonly ArrayBufferWriter<byte> _scratch = new(256);

    /// <summary>Whether unflushed data has been written.</summary>
    private bool _pending;

    /// <summary>Gets the outbound chunk size.</summary>
    public int ChunkSize => _encoder.ChunkSize;

    /// <summary>Writes Set Chunk Size, then uses <paramref name="chunkSize"/> for every later message.</summary>
    /// <param name="chunkSize">The new chunk size.</param>
    public void WriteSetChunkSize(int chunkSize)
    {
        WriteUInt32Control(RtmpMessageType.SetChunkSize, (uint)chunkSize);
        _encoder.SetChunkSize(chunkSize);
    }

    /// <summary>Writes an Acknowledgement.</summary>
    /// <param name="sequenceNumber">Bytes received so far, modulo 2^32.</param>
    public void WriteAcknowledgement(uint sequenceNumber) => WriteUInt32Control(RtmpMessageType.Acknowledgement, sequenceNumber);

    /// <summary>Writes Window Acknowledgement Size.</summary>
    /// <param name="windowSize">The window in bytes.</param>
    public void WriteWindowAcknowledgementSize(uint windowSize) => WriteUInt32Control(RtmpMessageType.WindowAcknowledgementSize, windowSize);

    /// <summary>Writes Set Peer Bandwidth.</summary>
    /// <param name="windowSize">The window in bytes.</param>
    /// <param name="limitType">0 hard, 1 soft, 2 dynamic.</param>
    public void WriteSetPeerBandwidth(uint windowSize, byte limitType)
    {
        Span<byte> payload = stackalloc byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(payload, windowSize);
        payload[4] = limitType;
        Write(ControlChunkStreamId, 0, RtmpMessageType.SetPeerBandwidth, payload);
    }

    /// <summary>Writes a User Control event with a 4-byte value (stream ID or timestamp).</summary>
    /// <param name="eventType">The event type.</param>
    /// <param name="value">The event data.</param>
    public void WriteUserControl(UserControlEventType eventType, uint value)
    {
        Span<byte> payload = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)eventType);
        BinaryPrimitives.WriteUInt32BigEndian(payload[2..], value);
        Write(ControlChunkStreamId, 0, RtmpMessageType.UserControl, payload);
    }

    /// <summary>Writes an AMF0 command message.</summary>
    /// <param name="chunkStreamId">The chunk stream.</param>
    /// <param name="messageStreamId">The message stream.</param>
    /// <param name="values">Command name, transaction ID and arguments, in order.</param>
    public void WriteCommand(int chunkStreamId, uint messageStreamId, params ReadOnlySpan<AmfValue> values)
    {
        _scratch.ResetWrittenCount();
        foreach (var value in values)
        {
            Amf0Writer.WriteValue(_scratch, value);
        }

        Write(chunkStreamId, messageStreamId, RtmpMessageType.CommandAmf0, _scratch.WrittenSpan);
    }

    /// <summary>Flushes buffered messages, if any.</summary>
    /// <param name="cancellationToken">Cancels the flush, for example when the write timeout expires.</param>
    /// <returns>A task that completes when the data has been handed to the transport.</returns>
    /// <exception cref="IOException">The peer no longer accepts data.</exception>
    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        if (!_pending)
        {
            return;
        }

        _pending = false;
        var result = await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsCompleted)
        {
            throw new IOException("The peer stopped accepting data.");
        }
    }

    /// <summary>Writes a protocol control message with a single 4-byte value.</summary>
    /// <param name="type">The message type.</param>
    /// <param name="value">The value.</param>
    private void WriteUInt32Control(RtmpMessageType type, uint value)
    {
        Span<byte> payload = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, value);
        Write(ControlChunkStreamId, 0, type, payload);
    }

    /// <summary>Encodes a message into the output buffer.</summary>
    /// <param name="chunkStreamId">The chunk stream.</param>
    /// <param name="messageStreamId">The message stream.</param>
    /// <param name="type">The message type.</param>
    /// <param name="payload">The payload.</param>
    private void Write(int chunkStreamId, uint messageStreamId, RtmpMessageType type, ReadOnlySpan<byte> payload)
    {
        _encoder.WriteMessage(output, chunkStreamId, messageStreamId, (byte)type, 0, payload);
        _pending = true;
    }
}
