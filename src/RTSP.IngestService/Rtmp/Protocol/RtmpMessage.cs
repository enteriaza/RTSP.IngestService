// A complete RTMP message reassembled by the chunk decoder: the expanded message header plus a pooled payload. The
// message owns its payload until TakePayload transfers it, typically to a PublishedMessage handed to the consumer.

namespace RTSP.IngestService.Rtmp.Protocol;

/// <summary>
/// A reassembled inbound RTMP message.
/// </summary>
/// <remarks>
/// Owned by the connection loop, which disposes it after handling. Disposal returns the payload to the pool unless
/// <see cref="TakePayload"/> has transferred it. Not thread-safe.
/// </remarks>
internal sealed class RtmpMessage : IDisposable
{
    /// <summary>The payload, or <see langword="null"/> after transfer or disposal.</summary>
    private PooledBuffer? _payload;

    /// <summary>Initializes a new instance of the <see cref="RtmpMessage"/> class.</summary>
    /// <param name="chunkStreamId">Chunk stream the message arrived on.</param>
    /// <param name="messageStreamId">Message stream identifier from the header.</param>
    /// <param name="messageTypeId">Message type identifier from the header.</param>
    /// <param name="timestamp">Absolute timestamp in milliseconds, after delta expansion (wraps at 2^32).</param>
    /// <param name="payload">The assembled payload; ownership passes to the message.</param>
    public RtmpMessage(int chunkStreamId, uint messageStreamId, byte messageTypeId, uint timestamp, PooledBuffer payload)
    {
        ChunkStreamId = chunkStreamId;
        MessageStreamId = messageStreamId;
        MessageTypeId = messageTypeId;
        Timestamp = timestamp;
        Length = payload.Length;
        _payload = payload;
    }

    /// <summary>Gets the chunk stream the message arrived on.</summary>
    public int ChunkStreamId { get; }

    /// <summary>Gets the message stream identifier.</summary>
    public uint MessageStreamId { get; }

    /// <summary>Gets the raw message type identifier.</summary>
    public byte MessageTypeId { get; }

    /// <summary>Gets the message type; values outside <see cref="RtmpMessageType"/> are possible.</summary>
    public RtmpMessageType Type => (RtmpMessageType)MessageTypeId;

    /// <summary>Gets the absolute timestamp in milliseconds.</summary>
    public uint Timestamp { get; }

    /// <summary>Gets the payload length in bytes.</summary>
    public int Length { get; }

    /// <summary>Gets the payload. Valid until the message is disposed or the payload is taken.</summary>
    /// <exception cref="InvalidOperationException">The payload has been taken or the message disposed.</exception>
    public ReadOnlyMemory<byte> Payload => (_payload ?? throw new InvalidOperationException("The payload is no longer owned by this message.")).Memory;

    /// <summary>Transfers ownership of the payload to the caller, who must dispose it.</summary>
    /// <returns>The payload buffer.</returns>
    /// <exception cref="InvalidOperationException">The payload has already been taken or the message disposed.</exception>
    public PooledBuffer TakePayload()
    {
        var payload = _payload ?? throw new InvalidOperationException("The payload is no longer owned by this message.");
        _payload = null;
        return payload;
    }

    /// <summary>Returns the payload to the pool if it is still owned.</summary>
    public void Dispose()
    {
        _payload?.Dispose();
        _payload = null;
    }
}
