// Incremental RTMP chunk stream decoder (RTMP specification 1.0, section 5.3). It consumes bytes as they arrive, keeps
// the per-chunk-stream header state needed to expand compressed (type 1, 2 and 3) headers, and reassembles interleaved
// chunks into complete messages. Chunk payload bytes are copied into the message's pooled buffer as soon as they
// arrive, so at most one incomplete chunk header (up to 18 bytes) is ever left unconsumed in the transport buffer,
// whatever chunk size the peer chooses. Memory held in partial messages is bounded by a per-connection limit.

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace RTSP.IngestService.Rtmp.Protocol;

/// <summary>
/// Decodes the inbound chunk stream of one connection into <see cref="RtmpMessage"/> instances.
/// </summary>
/// <remarks>
/// <para>
/// One instance belongs to one connection and is not thread-safe. Header rules:
/// </para>
/// <list type="bullet">
///   <item><description>The first chunk on a chunk stream must use a type 0 header.</description></item>
///   <item><description>A type 0, 1 or 2 header while a message on the same chunk stream is incomplete is a protocol error.</description></item>
///   <item><description>A type 3 header continues the incomplete message, or starts a new message that reuses the previous header with the previous timestamp delta (after a type 0 header the delta is its absolute timestamp, as the specification defines).</description></item>
///   <item><description>Type 3 chunks carry a 4-byte extended timestamp whenever the most recent type 0, 1 or 2 header on that chunk stream did.</description></item>
/// </list>
/// <para>
/// Limits: messages longer than the maximum message size, more than <see cref="MaxChunkStreams"/> distinct chunk
/// streams, or partial-message memory beyond the buffered-bytes limit raise <see cref="RtmpProtocolException"/>.
/// Memory is reserved as payload actually arrives (doubling, starting at 4 KiB), not when a header declares a length.
/// </para>
/// </remarks>
internal sealed class ChunkDecoder : IDisposable
{
    /// <summary>The chunk size every RTMP peer starts with.</summary>
    public const int DefaultChunkSize = 128;

    /// <summary>Maximum number of distinct chunk stream IDs tracked per connection.</summary>
    public const int MaxChunkStreams = 64;

    /// <summary>Longest chunk header: 3-byte basic header, 11-byte message header, 4-byte extended timestamp.</summary>
    private const int MaxHeaderLength = 18;

    /// <summary>Timestamp field value signalling that an extended timestamp follows.</summary>
    private const uint ExtendedTimestampMarker = 0xFFFFFF;

    /// <summary>Smallest allocation for a message payload.</summary>
    private const int MinimumAllocation = 4096;

    /// <summary>Header state per chunk stream ID.</summary>
    private readonly Dictionary<int, ChunkStreamState> _chunkStreams = [];

    /// <summary>Pool for message payloads.</summary>
    private readonly ArrayPool<byte> _pool;

    /// <summary>Largest accepted message length.</summary>
    private readonly int _maxMessageSize;

    /// <summary>Upper bound on <see cref="BufferedBytes"/>.</summary>
    private readonly long _maxBufferedBytes;

    /// <summary>Chunk stream whose chunk payload is being read, or <see langword="null"/> between chunks.</summary>
    private ChunkStreamState? _current;

    /// <summary>Payload bytes still to read for the current chunk.</summary>
    private int _remainingInChunk;

    /// <summary>Capacity currently held by partial messages.</summary>
    private long _bufferedBytes;

    /// <summary>Whether <see cref="Dispose"/> has run.</summary>
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ChunkDecoder"/> class.</summary>
    /// <param name="initialChunkSize">Chunk size assumed until the peer sends Set Chunk Size.</param>
    /// <param name="maxMessageSize">Largest accepted message length in bytes.</param>
    /// <param name="maxBufferedBytes">Maximum capacity held by partial messages across all chunk streams.</param>
    /// <param name="pool">Pool for message payloads.</param>
    public ChunkDecoder(int initialChunkSize, int maxMessageSize, long maxBufferedBytes, ArrayPool<byte> pool)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialChunkSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessageSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBufferedBytes, maxMessageSize);
        ArgumentNullException.ThrowIfNull(pool);

        ChunkSize = initialChunkSize;
        _maxMessageSize = maxMessageSize;
        _maxBufferedBytes = maxBufferedBytes;
        _pool = pool;
    }

    /// <summary>Gets the current inbound chunk size.</summary>
    public int ChunkSize { get; private set; }

    /// <summary>Gets the pooled capacity currently held by partial messages.</summary>
    public long BufferedBytes => _bufferedBytes;

    /// <summary>Gets a value indicating whether a chunk or message is partially received.</summary>
    public bool HasPartialMessage => _current is not null || _chunkStreams.Values.Any(static s => s.Payload is not null);

    /// <summary>Applies a Set Chunk Size value received from the peer to subsequent chunks.</summary>
    /// <param name="chunkSize">The new chunk size, already validated as 1 to 2^31-1.</param>
    public void SetChunkSize(int chunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        ChunkSize = chunkSize;
    }

    /// <summary>Discards the partial message on <paramref name="chunkStreamId"/>, if any (Abort Message).</summary>
    /// <param name="chunkStreamId">The chunk stream to abort.</param>
    public void Abort(int chunkStreamId)
    {
        if (!_chunkStreams.TryGetValue(chunkStreamId, out var state) || state.Payload is null)
        {
            return;
        }

        if (ReferenceEquals(_current, state))
        {
            _current = null;
            _remainingInChunk = 0;
        }

        Release(state);
    }

    /// <summary>
    /// Consumes bytes from <paramref name="buffer"/> until a message completes or more data is needed.
    /// </summary>
    /// <param name="buffer">
    /// Received bytes. On return it is sliced past everything consumed; unconsumed bytes are an incomplete chunk
    /// header and must be presented again with more data.
    /// </param>
    /// <param name="message">The completed message, owned by the caller, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if a message completed; <see langword="false"/> if more data is needed.</returns>
    /// <exception cref="RtmpProtocolException">The data is malformed or exceeds a limit; the decoder must not be used again.</exception>
    public bool TryDecode(ref ReadOnlySequence<byte> buffer, [NotNullWhen(true)] out RtmpMessage? message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        message = null;

        while (true)
        {
            if (_current is null && !TryReadHeader(ref buffer))
            {
                return false;
            }

            var state = _current!;
            if (_remainingInChunk > 0)
            {
                if (buffer.IsEmpty)
                {
                    return false;
                }

                var take = (int)Math.Min(_remainingInChunk, buffer.Length);
                var payload = state.Payload!;
                Reserve(state, payload.Length + take);
                payload.Append(buffer.Slice(0, take));
                buffer = buffer.Slice(take);
                _remainingInChunk -= take;
                if (_remainingInChunk > 0)
                {
                    return false;
                }
            }

            _current = null;
            if (state.Payload!.Length == state.MessageLength)
            {
                message = Complete(state);
                return true;
            }
        }
    }

    /// <summary>Returns every partial message buffer to the pool.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _current = null;
        foreach (var state in _chunkStreams.Values)
        {
            Release(state);
        }
    }

    /// <summary>Reads a 24-bit big-endian value.</summary>
    /// <param name="source">At least three bytes.</param>
    /// <returns>The value.</returns>
    private static uint ReadUInt24(ReadOnlySpan<byte> source) => (uint)((source[0] << 16) | (source[1] << 8) | source[2]);

    /// <summary>
    /// Parses one complete chunk header from the front of <paramref name="buffer"/>, applies it to the chunk stream
    /// state and sets up reading of the chunk payload. Consumes nothing if the header is incomplete.
    /// </summary>
    /// <param name="buffer">Received bytes; sliced past the header on success.</param>
    /// <returns><see langword="true"/> if a header was consumed.</returns>
    /// <exception cref="RtmpProtocolException">The header is invalid in the current state or exceeds a limit.</exception>
    private bool TryReadHeader(ref ReadOnlySequence<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[MaxHeaderLength];
        var available = (int)Math.Min(buffer.Length, MaxHeaderLength);
        buffer.Slice(0, available).CopyTo(header);
        header = header[..available];

        var format = header[0] >> 6;
        int chunkStreamId;
        int position;
        switch (header[0] & 0x3F)
        {
            case 0:
                if (available < 2)
                {
                    return false;
                }

                chunkStreamId = header[1] + 64;
                position = 2;
                break;

            case 1:
                if (available < 3)
                {
                    return false;
                }

                chunkStreamId = header[1] + (header[2] << 8) + 64;
                position = 3;
                break;

            default:
                chunkStreamId = header[0] & 0x3F;
                position = 1;
                break;
        }

        var messageHeaderLength = format switch
        {
            0 => 11,
            1 => 7,
            2 => 3,
            _ => 0,
        };
        if (available < position + messageHeaderLength)
        {
            return false;
        }

        _chunkStreams.TryGetValue(chunkStreamId, out var state);
        if (state is null && format != 0)
        {
            throw new RtmpProtocolException($"Chunk stream {chunkStreamId} began with a type {format} header; the first chunk on a chunk stream must use a type 0 header.");
        }

        var field = format <= 2 ? ReadUInt24(header[position..]) : 0u;
        var hasExtendedTimestamp = format <= 2 ? field == ExtendedTimestampMarker : state!.HasExtendedTimestamp;
        var headerLength = position + messageHeaderLength + (hasExtendedTimestamp ? 4 : 0);
        if (available < headerLength)
        {
            return false;
        }

        var extendedTimestamp = hasExtendedTimestamp ? BinaryPrimitives.ReadUInt32BigEndian(header[(position + messageHeaderLength)..]) : 0u;

        if (state is null)
        {
            if (_chunkStreams.Count >= MaxChunkStreams)
            {
                throw new RtmpProtocolException($"The peer used more than {MaxChunkStreams} chunk streams.");
            }

            state = new ChunkStreamState(chunkStreamId);
            _chunkStreams.Add(chunkStreamId, state);
        }

        var startsMessage = state.Payload is null;
        if (format <= 2 && !startsMessage)
        {
            throw new RtmpProtocolException($"A type {format} chunk header arrived on chunk stream {chunkStreamId} before the previous message on it was complete.");
        }

        var timestampField = hasExtendedTimestamp ? extendedTimestamp : field;
        switch (format)
        {
            case 0:
                state.Timestamp = timestampField;
                state.TimestampDelta = timestampField;
                state.MessageLength = (int)ReadUInt24(header[(position + 3)..]);
                state.MessageTypeId = header[position + 6];
                state.MessageStreamId = BinaryPrimitives.ReadUInt32LittleEndian(header[(position + 7)..]);
                state.HasExtendedTimestamp = hasExtendedTimestamp;
                break;

            case 1:
                state.TimestampDelta = timestampField;
                state.Timestamp = unchecked(state.Timestamp + timestampField);
                state.MessageLength = (int)ReadUInt24(header[(position + 3)..]);
                state.MessageTypeId = header[position + 6];
                state.HasExtendedTimestamp = hasExtendedTimestamp;
                break;

            case 2:
                state.TimestampDelta = timestampField;
                state.Timestamp = unchecked(state.Timestamp + timestampField);
                state.HasExtendedTimestamp = hasExtendedTimestamp;
                break;

            default:
                if (startsMessage)
                {
                    var delta = hasExtendedTimestamp ? extendedTimestamp : state.TimestampDelta;
                    state.Timestamp = unchecked(state.Timestamp + delta);
                }

                break;
        }

        if (startsMessage && state.MessageLength > _maxMessageSize)
        {
            throw new RtmpProtocolException($"A message of {state.MessageLength} bytes on chunk stream {chunkStreamId} exceeds the maximum message size of {_maxMessageSize} bytes.");
        }

        buffer = buffer.Slice(headerLength);

        if (startsMessage)
        {
            state.Payload = new PooledBuffer(_pool);
            state.MessageTimestamp = state.Timestamp;
        }

        _current = state;
        _remainingInChunk = Math.Min(ChunkSize, state.MessageLength - state.Payload!.Length);
        return true;
    }

    /// <summary>
    /// Grows the payload of <paramref name="state"/> to hold <paramref name="required"/> bytes, enforcing the
    /// buffered-bytes limit before renting.
    /// </summary>
    /// <param name="state">The chunk stream being appended to.</param>
    /// <param name="required">Total payload bytes after the append.</param>
    /// <exception cref="RtmpProtocolException">The limit would be exceeded.</exception>
    private void Reserve(ChunkStreamState state, int required)
    {
        var payload = state.Payload!;
        var capacity = payload.Capacity;
        if (required <= capacity)
        {
            return;
        }

        var target = (int)Math.Min(state.MessageLength, Math.Max(required, Math.Max((long)capacity * 2, MinimumAllocation)));
        if (_bufferedBytes - capacity + target > _maxBufferedBytes)
        {
            throw new RtmpProtocolException($"Partially received messages would exceed the limit of {_maxBufferedBytes} buffered bytes.");
        }

        payload.EnsureCapacity(target);
        _bufferedBytes += payload.Capacity - capacity;
    }

    /// <summary>Detaches the completed payload of <paramref name="state"/> as a message.</summary>
    /// <param name="state">The chunk stream whose message completed.</param>
    /// <returns>The message, which owns the payload.</returns>
    private RtmpMessage Complete(ChunkStreamState state)
    {
        var payload = state.Payload!;
        state.Payload = null;
        _bufferedBytes -= payload.Capacity;
        return new RtmpMessage(state.ChunkStreamId, state.MessageStreamId, state.MessageTypeId, state.MessageTimestamp, payload);
    }

    /// <summary>Returns the partial payload of <paramref name="state"/> to the pool.</summary>
    /// <param name="state">The chunk stream.</param>
    private void Release(ChunkStreamState state)
    {
        if (state.Payload is { } payload)
        {
            _bufferedBytes -= payload.Capacity;
            payload.Dispose();
            state.Payload = null;
        }
    }

    /// <summary>Header state and partial message for one chunk stream ID.</summary>
    /// <param name="chunkStreamId">The chunk stream ID.</param>
    private sealed class ChunkStreamState(int chunkStreamId)
    {
        /// <summary>Gets the chunk stream ID.</summary>
        public int ChunkStreamId { get; } = chunkStreamId;

        /// <summary>Gets or sets the timestamp of the most recent header.</summary>
        public uint Timestamp { get; set; }

        /// <summary>Gets or sets the delta reused by type 3 headers that start a new message.</summary>
        public uint TimestampDelta { get; set; }

        /// <summary>Gets or sets the message length from the most recent type 0 or 1 header.</summary>
        public int MessageLength { get; set; }

        /// <summary>Gets or sets the message type from the most recent type 0 or 1 header.</summary>
        public byte MessageTypeId { get; set; }

        /// <summary>Gets or sets the message stream ID from the most recent type 0 header.</summary>
        public uint MessageStreamId { get; set; }

        /// <summary>Gets or sets a value indicating whether the most recent type 0, 1 or 2 header used an extended timestamp.</summary>
        public bool HasExtendedTimestamp { get; set; }

        /// <summary>Gets or sets the timestamp of the message being assembled.</summary>
        public uint MessageTimestamp { get; set; }

        /// <summary>Gets or sets the payload of the message being assembled, or <see langword="null"/> between messages.</summary>
        public PooledBuffer? Payload { get; set; }
    }
}
