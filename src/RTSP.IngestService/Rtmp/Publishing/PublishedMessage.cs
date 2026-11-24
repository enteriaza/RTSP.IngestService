// The handoff unit between the RTMP ingest server and downstream media processing: one audio, video or data message
// from a published stream, with its RTMP timestamp and an opaque payload. Ownership of the pooled payload passes to
// the consumer, which must dispose the message exactly once when it no longer needs the payload.

using RTSP.IngestService.Rtmp.Protocol;

namespace RTSP.IngestService.Rtmp.Publishing;

/// <summary>The kind of a <see cref="PublishedMessage"/>.</summary>
public enum PublishedMessageKind
{
    /// <summary>RTMP audio message (type 8).</summary>
    Audio,

    /// <summary>RTMP video message (type 9).</summary>
    Video,

    /// <summary>RTMP data message (type 18 AMF0 or type 15 AMF3), for example <c>@setDataFrame</c>/<c>onMetaData</c>.</summary>
    Data,
}

/// <summary>
/// An audio, video or data message received on a published stream.
/// </summary>
/// <remarks>
/// <para>
/// The payload is the RTMP message body exactly as received (for example the FLV audio or video tag body); it is not
/// decoded. The payload memory is rented from a pool and owned by this instance. Whoever owns the message must call
/// <see cref="Dispose"/> once it no longer needs <see cref="Payload"/>; afterwards the memory is returned to the pool
/// and must not be read through any reference obtained earlier. Copy the bytes to retain them beyond disposal.
/// </para>
/// <para>
/// <see cref="Dispose"/> is thread-safe and idempotent. Reading <see cref="Payload"/> concurrently with disposal is
/// not safe.
/// </para>
/// </remarks>
public sealed class PublishedMessage : IDisposable
{
    /// <summary>The payload, or <see langword="null"/> once disposed.</summary>
    private PooledBuffer? _payload;

    /// <summary>Initializes a new instance of the <see cref="PublishedMessage"/> class, taking ownership of <paramref name="payload"/>.</summary>
    /// <param name="kind">The message kind.</param>
    /// <param name="messageTypeId">The RTMP message type ID.</param>
    /// <param name="timestamp">The RTMP timestamp in milliseconds.</param>
    /// <param name="payload">The payload buffer.</param>
    internal PublishedMessage(PublishedMessageKind kind, byte messageTypeId, uint timestamp, PooledBuffer payload)
    {
        Kind = kind;
        MessageTypeId = messageTypeId;
        Timestamp = timestamp;
        Length = payload.Length;
        _payload = payload;
    }

    /// <summary>Gets the message kind.</summary>
    public PublishedMessageKind Kind { get; }

    /// <summary>Gets the RTMP message type ID (8, 9, 15 or 18).</summary>
    public byte MessageTypeId { get; }

    /// <summary>Gets the RTMP timestamp in milliseconds; it wraps at 2^32 as RTMP timestamps do.</summary>
    public uint Timestamp { get; }

    /// <summary>Gets the payload length in bytes.</summary>
    public int Length { get; }

    /// <summary>Gets the opaque payload. Valid only until <see cref="Dispose"/> is called.</summary>
    /// <exception cref="ObjectDisposedException">The message has been disposed.</exception>
    public ReadOnlyMemory<byte> Payload => (Volatile.Read(ref _payload) ?? throw new ObjectDisposedException(nameof(PublishedMessage))).Memory;

    /// <summary>Returns the payload memory to the pool. Subsequent calls do nothing.</summary>
    public void Dispose() => Interlocked.Exchange(ref _payload, null)?.Dispose();
}
