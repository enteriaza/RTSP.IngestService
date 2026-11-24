// Minimal RTMP publishing client for tests. It performs the client handshake, sends commands and media through the
// production ChunkEncoder (chunk-level correctness is covered separately by byte-exact decoder tests), and decodes
// everything the server sends into a queue so tests can wait for specific responses.

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RTSP.IngestService.Rtmp.Amf;
using RTSP.IngestService.Rtmp.Protocol;

namespace RTSP.IngestService.Tests.Infrastructure;

/// <summary>A message received by <see cref="RtmpTestClient"/>.</summary>
/// <param name="ChunkStreamId">The chunk stream.</param>
/// <param name="MessageStreamId">The message stream.</param>
/// <param name="Type">The message type.</param>
/// <param name="Payload">The payload.</param>
internal sealed record ReceivedMessage(int ChunkStreamId, uint MessageStreamId, RtmpMessageType Type, byte[] Payload)
{
    /// <summary>Decodes the payload as AMF0 command values.</summary>
    /// <returns>The values.</returns>
    public List<AmfValue> Values() => Amf0Reader.ReadAll(Payload);
}

/// <summary>Test RTMP client over a duplex pipe.</summary>
/// <param name="transport">The client end of the transport.</param>
/// <param name="owned">A resource disposed with the client, such as a socket stream.</param>
internal sealed class RtmpTestClient(IDuplexPipe transport, IAsyncDisposable? owned = null) : IAsyncDisposable
{
    /// <summary>Encodes outbound chunks.</summary>
    private readonly ChunkEncoder _encoder = new();

    /// <summary>Decodes inbound chunks.</summary>
    private readonly ChunkDecoder _decoder = new(ChunkDecoder.DefaultChunkSize, 16 * 1024 * 1024, 64 * 1024 * 1024, ArrayPool<byte>.Shared);

    /// <summary>Decoded but unread messages.</summary>
    private readonly Queue<ReceivedMessage> _received = new();

    /// <summary>Next transaction ID.</summary>
    private double _transactionId = 1;

    /// <summary>Gets the transport.</summary>
    public IDuplexPipe Transport => transport;

    /// <summary>Connects a client to a TCP endpoint.</summary>
    /// <param name="endPoint">The server endpoint.</param>
    /// <returns>The client.</returns>
    public static async Task<RtmpTestClient> ConnectAsync(EndPoint endPoint)
    {
        var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(endPoint, TestContext.Current.CancellationToken);
        var stream = new NetworkStream(socket, ownsSocket: true);
        return new RtmpTestClient(new StreamDuplexPipe(stream), stream);
    }

    /// <summary>Performs the client handshake and verifies S0 and S2.</summary>
    /// <returns>A task that completes after C2 is sent.</returns>
    public async Task HandshakeAsync()
    {
        var c0c1 = new byte[1 + RtmpHandshake.PacketSize];
        c0c1[0] = 3;
        RandomNumberGenerator.Fill(c0c1.AsSpan(9));
        await SendRawAsync(c0c1);

        var response = await ReadRawAsync(1 + (2 * RtmpHandshake.PacketSize));
        Assert.Equal(3, response[0]);
        Assert.True(response.AsSpan(1 + RtmpHandshake.PacketSize + 8).SequenceEqual(c0c1.AsSpan(9)), "S2 must echo the C1 random data");
        await SendRawAsync(response.AsMemory(1, RtmpHandshake.PacketSize));
    }

    /// <summary>Handshakes, connects to <paramref name="application"/>, creates a stream and publishes <paramref name="streamName"/>.</summary>
    /// <param name="streamName">The stream name (key).</param>
    /// <param name="application">The application.</param>
    /// <returns>The message stream ID and the onStatus code.</returns>
    public async Task<(uint StreamId, string Code)> PublishFlowAsync(string streamName, string application = "live")
    {
        await HandshakeAsync();
        var connect = await ConnectAsync(application);
        Assert.Equal("_result", connect[0].StringValue);
        var streamId = await CreateStreamAsync();
        return (streamId, await PublishAsync(streamId, streamName));
    }

    /// <summary>Sends connect and returns the response command values.</summary>
    /// <param name="application">The application.</param>
    /// <returns>The <c>_result</c> or <c>_error</c> values.</returns>
    public async Task<List<AmfValue>> ConnectAsync(string application = "live")
    {
        await SendCommandAsync(0, "connect", AmfValue.CreateObject(
        [
            new("app", AmfValue.FromString(application)),
            new("type", AmfValue.FromString("nonprivate")),
            new("tcUrl", AmfValue.FromString($"rtmp://localhost/{application}")),
        ]));
        return await ReadCommandAsync("_result", "_error");
    }

    /// <summary>Sends createStream and returns the new stream ID.</summary>
    /// <returns>The stream ID.</returns>
    public async Task<uint> CreateStreamAsync()
    {
        await SendCommandAsync(0, "createStream", AmfValue.Null);
        var result = await ReadCommandAsync("_result", "_error");
        Assert.Equal("_result", result[0].StringValue);
        return (uint)result[3].NumberValue;
    }

    /// <summary>Sends releaseStream, FCPublish and publish and returns the onStatus code.</summary>
    /// <param name="streamId">The message stream.</param>
    /// <param name="streamName">The stream name.</param>
    /// <returns>The status code.</returns>
    public async Task<string> PublishAsync(uint streamId, string streamName)
    {
        await SendCommandAsync(0, "releaseStream", AmfValue.Null, AmfValue.FromString(streamName));
        await SendCommandAsync(0, "FCPublish", AmfValue.Null, AmfValue.FromString(streamName));
        await SendCommandAsync(streamId, "publish", 0, AmfValue.Null, AmfValue.FromString(streamName), AmfValue.FromString("live"));
        return await ReadStatusCodeAsync();
    }

    /// <summary>Reads command messages until an <c>onStatus</c> arrives and returns its code.</summary>
    /// <returns>The status code.</returns>
    public async Task<string> ReadStatusCodeAsync()
    {
        var status = await ReadCommandAsync("onStatus");
        return status[3].GetStringProperty("code")!;
    }

    /// <summary>Sends a command with an automatically assigned transaction ID.</summary>
    /// <param name="streamId">The message stream.</param>
    /// <param name="name">The command name.</param>
    /// <param name="arguments">The command object and arguments.</param>
    /// <returns>A task that completes when flushed.</returns>
    public Task SendCommandAsync(uint streamId, string name, params AmfValue[] arguments) => SendCommandAsync(streamId, name, _transactionId++, arguments);

    /// <summary>Sends a command with an explicit transaction ID.</summary>
    /// <param name="streamId">The message stream.</param>
    /// <param name="name">The command name.</param>
    /// <param name="transactionId">The transaction ID.</param>
    /// <param name="arguments">The command object and arguments.</param>
    /// <returns>A task that completes when flushed.</returns>
    public Task SendCommandAsync(uint streamId, string name, double transactionId, params AmfValue[] arguments)
    {
        var body = new ArrayBufferWriter<byte>();
        Amf0Writer.WriteValue(body, AmfValue.FromString(name));
        Amf0Writer.WriteValue(body, AmfValue.FromNumber(transactionId));
        foreach (var argument in arguments)
        {
            Amf0Writer.WriteValue(body, argument);
        }

        return SendMessageAsync(3, streamId, RtmpMessageType.CommandAmf0, 0, body.WrittenMemory);
    }

    /// <summary>Encodes and sends one message.</summary>
    /// <param name="chunkStreamId">The chunk stream.</param>
    /// <param name="streamId">The message stream.</param>
    /// <param name="type">The message type.</param>
    /// <param name="timestamp">The timestamp.</param>
    /// <param name="payload">The payload.</param>
    /// <returns>A task that completes when flushed.</returns>
    public async Task SendMessageAsync(int chunkStreamId, uint streamId, RtmpMessageType type, uint timestamp, ReadOnlyMemory<byte> payload)
    {
        _encoder.WriteMessage(transport.Output, chunkStreamId, streamId, (byte)type, timestamp, payload.Span);
        await transport.Output.FlushAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Sends Set Chunk Size and uses the new size for later messages.</summary>
    /// <param name="chunkSize">The chunk size.</param>
    /// <returns>A task that completes when flushed.</returns>
    public async Task SetChunkSizeAsync(int chunkSize)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)chunkSize);
        await SendMessageAsync(2, 0, RtmpMessageType.SetChunkSize, 0, payload);
        _encoder.SetChunkSize(chunkSize);
    }

    /// <summary>Sends raw bytes.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>A task that completes when flushed.</returns>
    public async Task SendRawAsync(ReadOnlyMemory<byte> data)
    {
        await transport.Output.WriteAsync(data, TestContext.Current.CancellationToken);
    }

    /// <summary>Reads command messages until one named in <paramref name="names"/> arrives.</summary>
    /// <param name="names">Accepted command names.</param>
    /// <returns>The command values.</returns>
    public async Task<List<AmfValue>> ReadCommandAsync(params string[] names)
    {
        while (true)
        {
            var message = await ReadMessageAsync();
            if (message.Type == RtmpMessageType.CommandAmf0)
            {
                var values = message.Values();
                if (names.Contains(values[0].StringValue))
                {
                    return values;
                }
            }
        }
    }

    /// <summary>Reads the next message, applying the server's Set Chunk Size.</summary>
    /// <returns>The message.</returns>
    /// <exception cref="EndOfStreamException">The server closed the connection.</exception>
    public async Task<ReceivedMessage> ReadMessageAsync()
    {
        while (_received.Count == 0)
        {
            if (!await FillAsync())
            {
                throw new EndOfStreamException("The server closed the connection.");
            }
        }

        return _received.Dequeue();
    }

    /// <summary>Reads until the server closes the connection, discarding data.</summary>
    /// <returns>A task that completes when the connection is closed.</returns>
    public async Task WaitForCloseAsync()
    {
        try
        {
            while (await FillAsync())
            {
                _received.Clear();
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
        }
    }

    /// <summary>Resets the TCP connection (RST instead of FIN) without completing the pipes.</summary>
    public void Abort()
    {
        var socket = Assert.IsType<NetworkStream>(owned).Socket;
        socket.LingerState = new LingerOption(enable: true, seconds: 0);
        socket.Close();
    }

    /// <summary>Completes the transport and disposes owned resources.</summary>
    /// <returns>A task that completes when closed.</returns>
    public async ValueTask DisposeAsync()
    {
        _decoder.Dispose();
        try
        {
            await transport.Input.CompleteAsync();
            await transport.Output.CompleteAsync();
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
        }

        if (owned is not null)
        {
            await owned.DisposeAsync();
        }
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes.</summary>
    /// <param name="count">Number of bytes.</param>
    /// <returns>The bytes.</returns>
    private async Task<byte[]> ReadRawAsync(int count)
    {
        while (true)
        {
            var result = await transport.Input.ReadAsync(TestContext.Current.CancellationToken).AsTask().WithinSafetyTimeout();
            if (result.Buffer.Length >= count)
            {
                var bytes = result.Buffer.Slice(0, count).ToArray();
                transport.Input.AdvanceTo(result.Buffer.GetPosition(count));
                return bytes;
            }

            transport.Input.AdvanceTo(result.Buffer.Start, result.Buffer.End);
            if (result.IsCompleted)
            {
                throw new EndOfStreamException("The server closed the connection during the handshake.");
            }
        }
    }

    /// <summary>Reads once and decodes all complete messages into the queue.</summary>
    /// <returns><see langword="false"/> if the server closed the connection.</returns>
    private async Task<bool> FillAsync()
    {
        var result = await transport.Input.ReadAsync(TestContext.Current.CancellationToken).AsTask().WithinSafetyTimeout();
        var buffer = result.Buffer;
        while (_decoder.TryDecode(ref buffer, out var message))
        {
            using (message)
            {
                if (message.Type == RtmpMessageType.SetChunkSize)
                {
                    _decoder.SetChunkSize((int)BinaryPrimitives.ReadUInt32BigEndian(message.Payload.Span));
                }

                _received.Enqueue(new ReceivedMessage(message.ChunkStreamId, message.MessageStreamId, message.Type, message.Payload.ToArray()));
            }
        }

        transport.Input.AdvanceTo(buffer.Start, result.Buffer.End);
        return !result.IsCompleted || _received.Count > 0;
    }

    /// <summary>Duplex pipe over a stream.</summary>
    /// <param name="stream">The stream.</param>
    private sealed class StreamDuplexPipe(Stream stream) : IDuplexPipe
    {
        /// <summary>Gets the reader.</summary>
        public PipeReader Input { get; } = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));

        /// <summary>Gets the writer.</summary>
        public PipeWriter Output { get; } = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
    }
}
