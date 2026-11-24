// Tests for the RTMP chunk decoder and encoder. Decoder inputs are hand-built byte sequences following the RTMP 1.0
// specification (section 5.3), so the decoder is verified independently of the encoder; encoder tests then check
// header selection and round trips through the decoder.

using System.Buffers;
using System.Buffers.Binary;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Rtmp;

/// <summary>
/// Tests for <see cref="ChunkDecoder"/> and <see cref="ChunkEncoder"/>.
/// </summary>
public sealed class ChunkProtocolTests
{
    /// <summary>Pool whose outstanding rentals are asserted.</summary>
    private readonly CountingArrayPool _pool = new();

    /// <summary>Verifies that a single type 0 chunk yields a message with every header field and the payload.</summary>
    [Fact]
    public void Type0Chunk_DecodesAllHeaderFields()
    {
        var bytes = Concat(Header(0, 3, 1000, 5, 9, 0x01020304), [1, 2, 3, 4, 5]);

        var messages = DecodeAll(NewDecoder(), bytes);

        var message = Assert.Single(messages);
        Assert.Equal(3, message.ChunkStreamId);
        Assert.Equal(1000u, message.Timestamp);
        Assert.Equal(9, message.TypeId);
        Assert.Equal(0x01020304u, message.StreamId);
        Assert.Equal([1, 2, 3, 4, 5], message.Payload);
    }

    /// <summary>
    /// Verifies every basic header length: one byte for IDs 2–63, two bytes for 64–319 and three bytes for
    /// 320–65599, decoding hand-built headers and checking that the encoder emits the same bytes.
    /// </summary>
    /// <param name="chunkStreamId">The chunk stream ID.</param>
    /// <param name="expectedLength">The expected basic header length.</param>
    [Theory]
    [InlineData(2, 1)]
    [InlineData(63, 1)]
    [InlineData(64, 2)]
    [InlineData(319, 2)]
    [InlineData(320, 3)]
    [InlineData(65599, 3)]
    public void BasicHeader_AllEncodingsDecodeAndMatchEncoder(int chunkStreamId, int expectedLength)
    {
        var header = Header(0, chunkStreamId, 7, 1, 8, 1);
        Assert.Equal(expectedLength + 11, header.Length);

        var message = Assert.Single(DecodeAll(NewDecoder(), Concat(header, [42])));
        Assert.Equal(chunkStreamId, message.ChunkStreamId);

        var encoded = new ArrayBufferWriter<byte>();
        new ChunkEncoder().WriteMessage(encoded, chunkStreamId, 1, 8, 7, [42]);
        Assert.Equal(Concat(header, [42]), encoded.WrittenSpan.ToArray());
    }

    /// <summary>
    /// Verifies the compressed header formats: type 1 reuses the stream ID and adds a delta, type 2 also reuses
    /// length and type, and a type 3 header starting a new message reuses the previous delta.
    /// </summary>
    [Fact]
    public void CompressedHeaders_Type1Type2Type3_ExpandFromPreviousHeader()
    {
        var bytes = Concat(
            Header(0, 4, 100, 3, 8, 1), [1, 1, 1],
            Header(1, 4, 20, 4, 9), [2, 2, 2, 2],
            Header(2, 4, 30), [3, 3, 3, 3],
            Header(3, 4), [4, 4, 4, 4]);

        var messages = DecodeAll(NewDecoder(), bytes);

        Assert.Equal([100u, 120u, 150u, 180u], messages.Select(static m => m.Timestamp));
        Assert.Equal([8, 9, 9, 9], messages.Select(static m => (int)m.TypeId));
        Assert.All(messages, static m => Assert.Equal(1u, m.StreamId));
        Assert.Equal([4, 4, 4, 4], messages[3].Payload);
    }

    /// <summary>Verifies that a type 3 header following a type 0 header uses the type 0 timestamp as the delta.</summary>
    [Fact]
    public void Type3AfterType0_UsesAbsoluteTimestampAsDelta()
    {
        var bytes = Concat(Header(0, 5, 40, 1, 8, 1), [1], Header(3, 5), [2]);

        Assert.Equal([40u, 80u], DecodeAll(NewDecoder(), bytes).Select(static m => m.Timestamp));
    }

    /// <summary>
    /// Verifies extended timestamps on a type 0 header and that type 3 continuation chunks of the same message carry
    /// the 4-byte extended field too.
    /// </summary>
    [Fact]
    public void ExtendedTimestamp_Type0AndContinuationChunk()
    {
        var payload = Enumerable.Range(0, 200).Select(static i => (byte)i).ToArray();
        var bytes = Concat(
            Header(0, 6, 0xFFFFFF, 200, 9, 1, extended: 0x01000000), payload[..128],
            Header(3, 6, extended: 0x01000000), payload[128..]);

        var message = Assert.Single(DecodeAll(NewDecoder(), bytes));
        Assert.Equal(0x01000000u, message.Timestamp);
        Assert.Equal(payload, message.Payload);
    }

    /// <summary>Verifies an extended timestamp delta on a type 1 header and that timestamps wrap at 2^32.</summary>
    [Fact]
    public void ExtendedTimestampDelta_Type1_WrapsAt32Bits()
    {
        var bytes = Concat(Header(0, 4, 0xFFFFFF, 1, 8, 1, extended: 0xFFFFFFF0), [1], Header(1, 4, 0xFFFFFF, 1, 8, extended: 0x20), [2]);

        Assert.Equal([0xFFFFFFF0u, 0x10u], DecodeAll(NewDecoder(), bytes).Select(static m => m.Timestamp));
    }

    /// <summary>Verifies that after a chunk size change a long message arrives in a single chunk.</summary>
    [Fact]
    public void ChunkSizeChange_AppliesToSubsequentChunks()
    {
        var decoder = NewDecoder();
        decoder.SetChunkSize(4096);
        var payload = new byte[1000];
        payload.AsSpan().Fill(7);

        var message = Assert.Single(DecodeAll(decoder, Concat(Header(0, 4, 0, 1000, 9, 1), payload)));
        Assert.Equal(payload, message.Payload);
    }

    /// <summary>
    /// Verifies that chunks of different chunk streams interleave: a message on chunk stream 6 completes between the
    /// two chunks of a message on chunk stream 4.
    /// </summary>
    [Fact]
    public void InterleavedChunkStreams_AreReassembledIndependently()
    {
        var audio = Enumerable.Repeat((byte)0xAA, 200).ToArray();
        var bytes = Concat(
            Header(0, 4, 10, 200, 8, 1), audio[..128],
            Header(0, 6, 20, 2, 9, 1), [0xBB, 0xBB],
            Header(3, 4), audio[128..]);

        var messages = DecodeAll(NewDecoder(), bytes);

        Assert.Equal([6, 4], messages.Select(static m => m.ChunkStreamId));
        Assert.Equal(audio, messages[1].Payload);
    }

    /// <summary>Verifies that several complete messages in one buffer are all decoded, one per call.</summary>
    [Fact]
    public void MultipleMessagesInOneBuffer_AreAllDecoded()
    {
        var bytes = Concat(Header(0, 3, 0, 1, 20, 0), [1], Header(0, 4, 0, 1, 8, 1), [2], Header(0, 6, 0, 1, 9, 1), [3]);

        Assert.Equal(3, DecodeAll(NewDecoder(), bytes).Count);
    }

    /// <summary>
    /// Verifies that delivering the bytes in every fragment size from 1 to 23, as successive reads that retain the
    /// unconsumed remainder, decodes the same messages; this splits every header field and payload across reads.
    /// </summary>
    /// <param name="fragmentSize">Bytes per read.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(17)]
    [InlineData(23)]
    public void MessagesFragmentedAcrossReads_DecodeIdentically(int fragmentSize)
    {
        var payload = Enumerable.Range(0, 300).Select(static i => (byte)i).ToArray();
        var bytes = Concat(
            Header(0, 320, 0xFFFFFF, 300, 9, 1, extended: 0x12345678), payload[..128],
            Header(0, 64, 5, 2, 8, 1), [9, 9],
            Header(3, 320, extended: 0x12345678), payload[128..256],
            Header(3, 320, extended: 0x12345678), payload[256..]);

        var decoder = NewDecoder();
        var messages = new List<Decoded>();
        var retained = Array.Empty<byte>();
        for (var offset = 0; offset < bytes.Length; offset += fragmentSize)
        {
            var read = Concat(retained, bytes[offset..Math.Min(bytes.Length, offset + fragmentSize)]);
            var buffer = new ReadOnlySequence<byte>(read);
            while (decoder.TryDecode(ref buffer, out var message))
            {
                messages.Add(Decoded.From(message));
            }

            retained = buffer.ToArray();
        }

        Assert.Empty(retained);
        Assert.Equal([64, 320], messages.Select(static m => m.ChunkStreamId));
        Assert.Equal(payload, messages[1].Payload);
        Assert.Equal(0x12345678u, messages[1].Timestamp);
        Assert.Equal(0, decoder.BufferedBytes);
    }

    /// <summary>
    /// Verifies decoding from a multi-segment sequence with one-byte segments, so every header is split across
    /// segment boundaries within a single read.
    /// </summary>
    [Fact]
    public void HeadersSplitAcrossSegments_Decode()
    {
        var bytes = Concat(Header(0, 65599, 0xFFFFFF, 3, 18, 7, extended: 0xABCDEF01), [1, 2, 3], Header(2, 65599, 0xFFFFFF, extended: 1), [4, 5, 6]);

        var messages = new List<Decoded>();
        var buffer = OneByteSegments(bytes);
        var decoder = NewDecoder();
        while (decoder.TryDecode(ref buffer, out var message))
        {
            messages.Add(Decoded.From(message));
        }

        Assert.True(buffer.IsEmpty);
        Assert.Equal([0xABCDEF01u, 0xABCDEF02u], messages.Select(static m => m.Timestamp));
        Assert.Equal([4, 5, 6], messages[1].Payload);
    }

    /// <summary>Verifies that an incomplete header consumes nothing.</summary>
    [Fact]
    public void IncompleteHeader_ConsumesNothing()
    {
        var header = Header(0, 3, 0xFFFFFF, 10, 20, 0, extended: 5);
        var buffer = new ReadOnlySequence<byte>(header[..^1]);

        Assert.False(NewDecoder().TryDecode(ref buffer, out _));
        Assert.Equal(header.Length - 1, buffer.Length);
    }

    /// <summary>Verifies that a zero-length message completes immediately after its header.</summary>
    [Fact]
    public void ZeroLengthMessage_CompletesAfterHeader()
    {
        var message = Assert.Single(DecodeAll(NewDecoder(), Header(0, 4, 3, 0, 8, 1)));
        Assert.Empty(message.Payload);
    }

    /// <summary>Verifies that a header declaring more than the maximum message size is rejected before any memory is rented.</summary>
    [Fact]
    public void MessageLongerThanMaximum_IsRejectedBeforeAllocation()
    {
        var decoder = new ChunkDecoder(128, 1000, 4096, _pool);
        var buffer = new ReadOnlySequence<byte>(Header(0, 4, 0, 1001, 9, 1));

        Assert.Throws<RtmpProtocolException>(() => decoder.TryDecode(ref buffer, out _));
        Assert.Equal(0, _pool.Outstanding);
    }

    /// <summary>Verifies that a message of exactly the maximum size is accepted.</summary>
    [Fact]
    public void MessageOfMaximumSize_IsAccepted()
    {
        var decoder = new ChunkDecoder(128, 1000, 4096, _pool);
        decoder.SetChunkSize(1000);

        var message = Assert.Single(DecodeAll(decoder, Concat(Header(0, 4, 0, 1000, 9, 1), new byte[1000])));
        Assert.Equal(1000, message.Payload.Length);
    }

    /// <summary>
    /// Verifies that partial messages across chunk streams cannot exceed the buffered-bytes limit, and that the limit
    /// counts memory actually reserved rather than declared lengths.
    /// </summary>
    [Fact]
    public void PartialMessagesBeyondBufferedLimit_AreRejected()
    {
        var decoder = new ChunkDecoder(4096, 8192, 10000, _pool);
        var first = Concat(Header(0, 4, 0, 8192, 9, 1), new byte[4096], Header(0, 5, 0, 8192, 9, 1), new byte[4096]);
        var buffer = new ReadOnlySequence<byte>(first);
        Assert.False(decoder.TryDecode(ref buffer, out _));
        Assert.Equal(8192, decoder.BufferedBytes);

        buffer = new ReadOnlySequence<byte>(Concat(Header(3, 4), new byte[4096]));
        Assert.Throws<RtmpProtocolException>(() => decoder.TryDecode(ref buffer, out _));
        decoder.Dispose();
        Assert.Equal(0, _pool.Outstanding);
    }

    /// <summary>Verifies that the first chunk on a chunk stream must use a type 0 header.</summary>
    /// <param name="format">A compressed header type.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FirstChunkWithCompressedHeader_IsRejected(int format)
    {
        var buffer = new ReadOnlySequence<byte>(Concat(Header(format, 4, 1, 1, 8), [0]));

        Assert.Throws<RtmpProtocolException>(() => NewDecoder().TryDecode(ref buffer, out _));
    }

    /// <summary>Verifies that a new message header on a chunk stream with an incomplete message is rejected.</summary>
    [Fact]
    public void NewHeaderBeforeMessageCompletes_IsRejected()
    {
        var bytes = Concat(Header(0, 4, 0, 200, 9, 1), new byte[128], Header(1, 4, 0, 1, 9), [0]);
        var buffer = new ReadOnlySequence<byte>(bytes);

        Assert.Throws<RtmpProtocolException>(() => NewDecoder().TryDecode(ref buffer, out _));
    }

    /// <summary>Verifies that using more than the permitted number of chunk streams is rejected.</summary>
    [Fact]
    public void TooManyChunkStreams_AreRejected()
    {
        var decoder = NewDecoder();
        for (var id = 2; id < 2 + ChunkDecoder.MaxChunkStreams; id++)
        {
            Assert.Single(DecodeAll(decoder, Concat(Header(0, id, 0, 1, 8, 1), [0])));
        }

        var buffer = new ReadOnlySequence<byte>(Concat(Header(0, 2 + ChunkDecoder.MaxChunkStreams, 0, 1, 8, 1), [0]));
        Assert.Throws<RtmpProtocolException>(() => decoder.TryDecode(ref buffer, out _));
    }

    /// <summary>Verifies that Abort discards a partial message and releases its memory, and the chunk stream can be reused.</summary>
    [Fact]
    public void Abort_DiscardsPartialMessage()
    {
        var decoder = new ChunkDecoder(128, 4096, 8192, _pool);
        var buffer = new ReadOnlySequence<byte>(Concat(Header(0, 4, 0, 200, 9, 1), new byte[128]));
        Assert.False(decoder.TryDecode(ref buffer, out _));
        Assert.True(decoder.HasPartialMessage);

        decoder.Abort(4);

        Assert.False(decoder.HasPartialMessage);
        Assert.Equal(0, decoder.BufferedBytes);
        Assert.Equal(0, _pool.Outstanding);
        Assert.Equal([5], Assert.Single(DecodeAll(decoder, Concat(Header(0, 4, 0, 1, 9, 1), [5]))).Payload);
    }

    /// <summary>Verifies that disposing the decoder returns the buffers of partial messages to the pool.</summary>
    [Fact]
    public void Dispose_ReturnsPartialBuffers()
    {
        var decoder = new ChunkDecoder(128, 4096, 8192, _pool);
        var buffer = new ReadOnlySequence<byte>(Concat(Header(0, 4, 0, 300, 9, 1), new byte[128], Header(0, 5, 0, 300, 8, 1), new byte[10]));
        Assert.False(decoder.TryDecode(ref buffer, out _));
        Assert.Equal(2, _pool.Outstanding);

        decoder.Dispose();

        Assert.Equal(0, _pool.Outstanding);
    }

    /// <summary>
    /// Verifies the encoder's header selection: type 0 first, type 1 for a new length, type 2 for a new delta,
    /// type 3 for an identical header and delta, and type 0 again when the timestamp goes backwards.
    /// </summary>
    [Fact]
    public void Encoder_SelectsMostCompactHeader()
    {
        var encoder = new ChunkEncoder();
        int Format(uint timestamp, int length)
        {
            var output = new ArrayBufferWriter<byte>();
            encoder.WriteMessage(output, 4, 1, 9, timestamp, new byte[length]);
            return output.WrittenSpan[0] >> 6;
        }

        Assert.Equal(0, Format(0, 10));
        Assert.Equal(1, Format(40, 20));
        Assert.Equal(2, Format(100, 20));
        Assert.Equal(3, Format(160, 20));
        Assert.Equal(0, Format(50, 20));
    }

    /// <summary>
    /// Verifies that encoder output decodes to the original messages for several chunk sizes, including
    /// extended timestamps, timestamp regressions, empty payloads and payloads spanning many chunks.
    /// </summary>
    /// <param name="chunkSize">The chunk size used by both sides.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(4096)]
    public void Encoder_RoundTripsThroughDecoder(int chunkSize)
    {
        (uint Timestamp, int Length, byte Type)[] messages =
        [
            (0, 10, 9), (33, 10, 9), (66, 10, 9), (66, 0, 8), (0x00FFFFFF, 300, 9), (0x01000021, 300, 9), (5, 1, 8), (0xFFFFFFFF, 5000, 9),
        ];
        var encoder = new ChunkEncoder();
        encoder.SetChunkSize(chunkSize);
        var output = new ArrayBufferWriter<byte>();
        foreach (var (timestamp, length, type) in messages)
        {
            encoder.WriteMessage(output, 6, 1, type, timestamp, Enumerable.Range(0, length).Select(i => (byte)(i + timestamp)).ToArray());
        }

        var decoder = new ChunkDecoder(128, 16 * 1024, 64 * 1024, _pool);
        decoder.SetChunkSize(chunkSize);
        var decoded = DecodeAll(decoder, output.WrittenSpan.ToArray());

        Assert.Equal(messages.Select(static m => m.Timestamp), decoded.Select(static m => m.Timestamp));
        Assert.Equal(messages.Select(static m => m.Length), decoded.Select(static m => m.Payload.Length));
        Assert.Equal(messages.Select(static m => m.Type), decoded.Select(static m => m.TypeId));
        for (var i = 0; i < messages.Length; i++)
        {
            Assert.Equal(Enumerable.Range(0, messages[i].Length).Select(b => (byte)(b + messages[i].Timestamp)), decoded[i].Payload);
        }

        Assert.Equal(0, _pool.Outstanding);
    }

    /// <summary>Builds a chunk header byte-by-byte from the specification layout.</summary>
    /// <param name="format">Header type 0–3.</param>
    /// <param name="chunkStreamId">Chunk stream ID.</param>
    /// <param name="timestampField">The 3-byte timestamp or delta field (types 0–2).</param>
    /// <param name="length">Message length (types 0–1).</param>
    /// <param name="typeId">Message type (types 0–1).</param>
    /// <param name="streamId">Message stream ID, little-endian (type 0).</param>
    /// <param name="extended">Extended timestamp to append, if any.</param>
    /// <returns>The header bytes.</returns>
    private static byte[] Header(int format, int chunkStreamId, uint timestampField = 0, int length = 0, byte typeId = 0, uint streamId = 0, uint? extended = null)
    {
        var bytes = new List<byte>();
        if (chunkStreamId < 64)
        {
            bytes.Add((byte)((format << 6) | chunkStreamId));
        }
        else if (chunkStreamId < 320)
        {
            bytes.Add((byte)(format << 6));
            bytes.Add((byte)(chunkStreamId - 64));
        }
        else
        {
            bytes.Add((byte)((format << 6) | 1));
            bytes.Add((byte)((chunkStreamId - 64) & 0xFF));
            bytes.Add((byte)((chunkStreamId - 64) >> 8));
        }

        if (format <= 2)
        {
            bytes.AddRange([(byte)(timestampField >> 16), (byte)(timestampField >> 8), (byte)timestampField]);
        }

        if (format <= 1)
        {
            bytes.AddRange([(byte)(length >> 16), (byte)(length >> 8), (byte)length, typeId]);
        }

        if (format == 0)
        {
            var id = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(id, streamId);
            bytes.AddRange(id);
        }

        if (extended is { } value)
        {
            var ext = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(ext, value);
            bytes.AddRange(ext);
        }

        return [.. bytes];
    }

    /// <summary>Concatenates byte arrays.</summary>
    /// <param name="parts">The parts.</param>
    /// <returns>The concatenation.</returns>
    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(static p => p)];

    /// <summary>Builds a sequence with one segment per byte.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The sequence.</returns>
    private static ReadOnlySequence<byte> OneByteSegments(byte[] bytes)
    {
        var first = new Segment(bytes.AsMemory(0, 1), 0);
        var last = first;
        for (var i = 1; i < bytes.Length; i++)
        {
            last = last.Append(bytes.AsMemory(i, 1));
        }

        return new ReadOnlySequence<byte>(first, 0, last, 1);
    }

    /// <summary>Decodes every complete message in <paramref name="bytes"/> and checks nothing is left over.</summary>
    /// <param name="decoder">The decoder.</param>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The decoded messages.</returns>
    private static List<Decoded> DecodeAll(ChunkDecoder decoder, byte[] bytes)
    {
        var buffer = new ReadOnlySequence<byte>(bytes);
        var messages = new List<Decoded>();
        while (decoder.TryDecode(ref buffer, out var message))
        {
            messages.Add(Decoded.From(message));
        }

        Assert.True(buffer.IsEmpty, "every byte should be consumed");
        return messages;
    }

    /// <summary>Creates a decoder with generous limits over the counting pool.</summary>
    /// <returns>The decoder.</returns>
    private ChunkDecoder NewDecoder() => new(128, 1024 * 1024, 4 * 1024 * 1024, _pool);

    /// <summary>A decoded message copied out of its pooled buffer.</summary>
    /// <param name="ChunkStreamId">The chunk stream.</param>
    /// <param name="StreamId">The message stream.</param>
    /// <param name="TypeId">The message type.</param>
    /// <param name="Timestamp">The timestamp.</param>
    /// <param name="Payload">The payload.</param>
    private sealed record Decoded(int ChunkStreamId, uint StreamId, byte TypeId, uint Timestamp, byte[] Payload)
    {
        /// <summary>Copies and disposes <paramref name="message"/>.</summary>
        /// <param name="message">The message.</param>
        /// <returns>The copy.</returns>
        public static Decoded From(RtmpMessage message)
        {
            using (message)
            {
                return new Decoded(message.ChunkStreamId, message.MessageStreamId, message.MessageTypeId, message.Timestamp, message.Payload.ToArray());
            }
        }
    }

    /// <summary>A linked memory segment for building multi-segment sequences.</summary>
    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        /// <summary>Initializes a new instance of the <see cref="Segment"/> class.</summary>
        /// <param name="memory">The segment memory.</param>
        /// <param name="runningIndex">Offset of the segment in the sequence.</param>
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        /// <summary>Appends a segment after this one.</summary>
        /// <param name="memory">The next segment's memory.</param>
        /// <returns>The new segment.</returns>
        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
