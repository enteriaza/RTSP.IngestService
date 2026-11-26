// Tests for the RTMP command workflow and publishing sessions, run through RtmpConnection over in-memory pipes with
// a test client: connect, createStream, publish, authentication, metadata and media delivery, closeStream and
// deleteStream, and cleanup on every way a connection can end.

using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Rtmp;
using RTSP.IngestService.Rtmp.Amf;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Rtmp;

/// <summary>
/// Tests for <see cref="RtmpSession"/> and <see cref="RtmpConnection"/>.
/// </summary>
public sealed class RtmpSessionTests : IDisposable
{
    /// <summary>The server under test.</summary>
    private readonly RtmpTestServer _server = new();

    /// <summary>
    /// Verifies the full publish flow: connect sends window, bandwidth and chunk size control messages and a success
    /// result; createStream returns stream 1; publish authenticates, registers the stream, notifies the handler,
    /// sends StreamBegin and NetStream.Publish.Start, and moves the connection to Publishing.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task PublishFlow_StartsSession()
    {
        var (connection, run, client) = _server.Start();
        await client.HandshakeAsync();

        await client.SendCommandAsync(0, "connect", AmfValue.CreateObject([new("app", AmfValue.FromString("live/")), new("tcUrl", AmfValue.FromString("rtmp://h/live"))]));
        var control = new List<RtmpMessageType>();
        List<AmfValue> connect;
        while (true)
        {
            var message = await client.ReadMessageAsync();
            if (message.Type == RtmpMessageType.CommandAmf0)
            {
                connect = message.Values();
                break;
            }

            control.Add(message.Type);
        }

        Assert.Equal([RtmpMessageType.WindowAcknowledgementSize, RtmpMessageType.SetPeerBandwidth, RtmpMessageType.SetChunkSize], control);
        Assert.Equal("_result", connect[0].StringValue);
        Assert.Equal("NetConnection.Connect.Success", connect[3].GetStringProperty("code"));

        var streamId = await client.CreateStreamAsync();
        Assert.Equal(1u, streamId);

        await client.SendCommandAsync(streamId, "publish", 0, AmfValue.Null, AmfValue.FromString(RtmpTestServer.StreamKey + "?token=abc"), AmfValue.FromString("live"));
        var streamBegin = await client.ReadMessageAsync();
        Assert.Equal(RtmpMessageType.UserControl, streamBegin.Type);
        Assert.Equal((ushort)UserControlEventType.StreamBegin, BinaryPrimitives.ReadUInt16BigEndian(streamBegin.Payload));
        Assert.Equal(streamId, BinaryPrimitives.ReadUInt32BigEndian(streamBegin.Payload.AsSpan(2)));
        Assert.Equal("NetStream.Publish.Start", await client.ReadStatusCodeAsync());

        var started = await _server.Handler.NextAsync("started");
        Assert.Equal("live", started.Session.Application);
        Assert.Equal(RtmpTestServer.StreamIdentity, started.Session.StreamIdentity);
        Assert.Equal(PublisherSessionState.Publishing, started.Session.State);
        Assert.Same(started.Session, _server.Registry.Find("live", RtmpTestServer.StreamIdentity));
        var request = Assert.Single(_server.Authenticator.Requests);
        Assert.Equal(RtmpTestServer.StreamKey, request.StreamName);
        Assert.Equal("token=abc", request.StreamQuery);
        Assert.Equal("rtmp://h/live", request.TcUrl);
        await Eventually.WaitUntilAsync(() => connection.State == RtmpConnectionState.Publishing, "the connection is publishing");

        await client.DisposeAsync();
        await run.WithinSafetyTimeout();
    }

    /// <summary>Verifies that metadata, audio and video reach the handler in order with their payloads and timestamps, and are counted.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task PublishedMessages_AreDeliveredInOrder()
    {
        using var messages = new MetricCollector<long>(_server.MeterFactory, RtmpMetrics.MeterName, "rtmp.messages_received");
        var (_, run, client) = _server.Start();
        var (streamId, code) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);
        Assert.Equal("NetStream.Publish.Start", code);

        await client.SendMessageAsync(4, streamId, RtmpMessageType.DataAmf0, 0, Metadata(("width", 1280), ("height", 720)));
        await client.SendMessageAsync(4, streamId, RtmpMessageType.Audio, 10, new byte[] { 0xAF, 0x00, 0x12, 0x10 });
        var video = Enumerable.Range(0, 5000).Select(static i => (byte)i).ToArray();
        await client.SendMessageAsync(6, streamId, RtmpMessageType.Video, 33, video);

        var data = await _server.Handler.NextAsync("message");
        var audio = await _server.Handler.NextAsync("message");
        var frame = await _server.Handler.NextAsync("message");

        Assert.Equal(PublishedMessageKind.Data, data.MessageKind);
        Assert.Equal(1280, data.Session.Metadata.Single(static p => p.Key == "width").Value.NumberValue);
        Assert.Equal((PublishedMessageKind.Audio, 10u), (audio.MessageKind!.Value, audio.Timestamp));
        Assert.Equal([0xAF, 0x00, 0x12, 0x10], audio.Payload);
        Assert.Equal((PublishedMessageKind.Video, 33u), (frame.MessageKind!.Value, frame.Timestamp));
        Assert.Equal(video, frame.Payload);

        var session = frame.Session;
        Assert.Equal((1, 1, 1), (session.AudioMessageCount, session.VideoMessageCount, session.DataMessageCount));
        Assert.True(session.ReceivedPayloadBytes > 5004);
        Assert.Contains(messages.GetMeasurementSnapshot(), static m => (string?)m.Tags["type"] == "video");
        Assert.Contains(messages.GetMeasurementSnapshot(), static m => (string?)m.Tags["type"] == "audio");

        await client.DisposeAsync();
        await run.WithinSafetyTimeout();
        Assert.Equal(0, _server.Pool.Outstanding);
    }

    /// <summary>Verifies that onMetaData without @setDataFrame, as an ECMA array, is captured.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Metadata_WithoutSetDataFrame_IsCaptured()
    {
        var (_, run, client) = _server.Start();
        var (streamId, _) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);
        var body = new ArrayBufferWriter<byte>();
        Amf0Writer.WriteValue(body, AmfValue.FromString("onMetaData"));
        Amf0Writer.WriteValue(body, AmfValue.CreateEcmaArray([new("framerate", AmfValue.FromNumber(30))]));

        await client.SendMessageAsync(4, streamId, RtmpMessageType.DataAmf0, 0, body.WrittenMemory);
        var data = await _server.Handler.NextAsync("message");

        Assert.Equal(30, data.Session.Metadata.Single().Value.NumberValue);
        await client.DisposeAsync();
        await run.WithinSafetyTimeout();
    }

    /// <summary>Verifies that a client changing its chunk size can send a large message in few chunks.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ClientChunkSizeChange_IsApplied()
    {
        var (_, run, client) = _server.Start();
        var (streamId, _) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);
        await client.SetChunkSizeAsync(60000);
        var video = Enumerable.Range(0, 100_000).Select(static i => (byte)(i * 7)).ToArray();

        await client.SendMessageAsync(6, streamId, RtmpMessageType.Video, 0, video);

        Assert.Equal(video, (await _server.Handler.NextAsync("message")).Payload);
        await client.DisposeAsync();
        await run.WithinSafetyTimeout();
    }

    /// <summary>Verifies that media on a stream that is not publishing is ignored and released.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task MediaBeforePublish_IsIgnored()
    {
        var (connection, run, client) = _server.Start();
        await client.HandshakeAsync();
        await client.ConnectAsync();
        var streamId = await client.CreateStreamAsync();

        await client.SendMessageAsync(6, streamId, RtmpMessageType.Video, 0, new byte[100]);
        Assert.Equal("NetStream.Publish.Start", await client.PublishAsync(streamId, RtmpTestServer.StreamKey));
        await client.SendMessageAsync(6, streamId, RtmpMessageType.Video, 40, new byte[] { 1 });

        Assert.Equal(40u, (await _server.Handler.NextAsync("message")).Timestamp);
        Assert.Single(_server.Handler.Events, static e => e.Kind == "message");
        Assert.Equal(RtmpConnectionState.Publishing, connection.State);
        await client.DisposeAsync();
        await run.WithinSafetyTimeout();
    }

    /// <summary>Verifies that connect without a valid application is rejected and the connection closed.</summary>
    /// <param name="app">The app value; <see langword="null"/> to omit it.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("has space")]
    [InlineData("caf\u00e9")]
    public async Task Connect_InvalidApplication_IsRejected(string? app)
    {
        var (connection, run, client) = _server.Start();
        await client.HandshakeAsync();

        List<KeyValuePair<string, AmfValue>> properties = app is null ? [] : [new("app", AmfValue.FromString(app))];
        await client.SendCommandAsync(0, "connect", AmfValue.CreateObject(properties));
        var error = await client.ReadCommandAsync("_error", "_result");
        await run.WithinSafetyTimeout();

        Assert.Equal("_error", error[0].StringValue);
        Assert.Equal("NetConnection.Connect.Rejected", error[3].GetStringProperty("code"));
        Assert.Equal(RtmpCloseReason.Rejected, connection.CloseReason);
        Assert.Equal(RtmpConnectionState.Closed, connection.State);
        await client.DisposeAsync();
    }

    /// <summary>Verifies that a command before connect is answered with an error and closes the connection.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task CommandBeforeConnect_ClosesConnection()
    {
        var (connection, run, client) = _server.Start();
        await client.HandshakeAsync();

        await client.SendCommandAsync(0, "createStream", AmfValue.Null);
        var error = await client.ReadCommandAsync("_error");
        await run.WithinSafetyTimeout();

        Assert.Equal("NetConnection.Call.Failed", error[3].GetStringProperty("code"));
        Assert.Equal(RtmpCloseReason.Rejected, connection.CloseReason);
        await client.DisposeAsync();
    }

    /// <summary>
    /// Verifies that an unknown stream key is answered with NetStream.Publish.Unauthorized, closes the connection,
    /// starts no session, counts an authentication failure and never logs the key.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Publish_UnknownKey_IsUnauthorizedAndClosed()
    {
        using var failures = new MetricCollector<long>(_server.MeterFactory, RtmpMetrics.MeterName, "rtmp.authentication.failures");
        const string key = "not-a-valid-key-123";
        var (connection, run, client) = _server.Start();

        var (_, code) = await client.PublishFlowAsync(key);
        await run.WithinSafetyTimeout();

        Assert.Equal("NetStream.Publish.Unauthorized", code);
        Assert.Equal(RtmpCloseReason.Rejected, connection.CloseReason);
        Assert.DoesNotContain(_server.Handler.Events, static e => e.Kind == "started");
        Assert.Equal(0, _server.Registry.Count);
        Assert.Equal(1, failures.LastMeasurement?.Value);
        Assert.Contains(_server.Logs.GetSnapshot(), static r => r.Id.Id == 5205);
        AssertRtmpApplication(5200, 5205);
        Assert.DoesNotContain(_server.Logs.GetSnapshot(), r => r.Message.Contains(key, StringComparison.Ordinal));
        await client.DisposeAsync();
    }

    /// <summary>Verifies that a failing authenticator rejects the publish and closes the connection as a handler failure.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Publish_AuthenticatorThrows_ClosesConnection()
    {
        _server.Authenticator.Throw = true;
        var (connection, run, client) = _server.Start();

        var (_, code) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);
        await run.WithinSafetyTimeout();

        Assert.Equal("NetStream.Publish.Failed", code);
        Assert.Equal(RtmpCloseReason.HandlerFailed, connection.CloseReason);
        Assert.Contains(_server.Logs.GetSnapshot(), static r => r.Id.Id == 5212);
        await client.DisposeAsync();
    }

    /// <summary>Verifies publish errors that keep the connection open: an uncreated stream, an invalid name, and publishing twice.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Publish_InvalidRequests_AreRejectedWithBadName()
    {
        var (connection, run, client) = _server.Start();
        await client.HandshakeAsync();
        await client.ConnectAsync();

        Assert.Equal("NetStream.Publish.BadName", await client.PublishAsync(5, RtmpTestServer.StreamKey));
        var streamId = await client.CreateStreamAsync();
        Assert.Equal("NetStream.Publish.BadName", await client.PublishAsync(streamId, string.Empty));
        Assert.Equal("NetStream.Publish.BadName", await client.PublishAsync(streamId, "bad\u0001name"));
        Assert.Equal("NetStream.Publish.Start", await client.PublishAsync(streamId, RtmpTestServer.StreamKey));
        Assert.Equal("NetStream.Publish.BadName", await client.PublishAsync(streamId, "secret-key-0002"));

        Assert.Null(connection.CloseReason);
        Assert.Single(_server.Authenticator.Requests);
        await client.DisposeAsync();
        await run.WithinSafetyTimeout();
    }

    /// <summary>Verifies that a second connection cannot publish an identity that is already being published.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Publish_DuplicateIdentityAcrossConnections_IsRejected()
    {
        var (_, firstRun, first) = _server.Start();
        var (_, secondRun, second) = _server.Start();

        Assert.Equal("NetStream.Publish.Start", (await first.PublishFlowAsync(RtmpTestServer.StreamKey)).Code);
        Assert.Equal("NetStream.Publish.BadName", (await second.PublishFlowAsync(RtmpTestServer.StreamKey)).Code);

        Assert.Equal(1, _server.Registry.Count);
        Assert.Contains(_server.Logs.GetSnapshot(), static r => r.Id.Id == 5206);
        AssertRtmpApplication(5200, 5204, 5206);
        await first.DisposeAsync();
        await second.DisposeAsync();
        await Task.WhenAll(firstRun, secondRun).WithinSafetyTimeout();
        Assert.Equal(0, _server.Registry.Count);
    }

    /// <summary>Verifies that createStream beyond the per-connection limit is answered with an error.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task CreateStream_BeyondLimit_IsRejected()
    {
        var (_, run, client) = _server.Start();
        await client.HandshakeAsync();
        await client.ConnectAsync();
        for (var i = 0; i < RtmpSession.MaxMessageStreams; i++)
        {
            await client.CreateStreamAsync();
        }

        await client.SendCommandAsync(0, "createStream", AmfValue.Null);

        Assert.Equal("_error", (await client.ReadCommandAsync("_result", "_error"))[0].StringValue);
        await client.DisposeAsync();
        await run.WithinSafetyTimeout();
    }

    /// <summary>Verifies that unsupported commands with a transaction ID are answered with an error and the connection stays open.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task UnsupportedCommand_IsAnsweredWithError()
    {
        var (connection, run, client) = _server.Start();
        await client.HandshakeAsync();
        await client.ConnectAsync();

        await client.SendCommandAsync(0, "play", AmfValue.Null, AmfValue.FromString("x"));

        Assert.Equal("NetConnection.Call.Failed", (await client.ReadCommandAsync("_error"))[3].GetStringProperty("code"));
        Assert.Null(connection.CloseReason);
        await client.DisposeAsync();
        await run.WithinSafetyTimeout();
    }

    /// <summary>Verifies that closeStream ends the session as unpublished, and the stream can publish again.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task CloseStream_EndsSessionAndAllowsRepublish()
    {
        var (connection, run, client) = _server.Start();
        var (streamId, _) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);

        await client.SendCommandAsync(streamId, "closeStream", 0, AmfValue.Null);

        Assert.Equal("NetStream.Unpublish.Success", await client.ReadStatusCodeAsync());
        var ended = await _server.Handler.NextAsync("ended");
        Assert.Equal(PublishEndReason.Unpublished, ended.Session.EndReason);
        Assert.Equal(PublisherSessionState.Ended, ended.Session.State);
        Assert.Equal(0, _server.Registry.Count);
        await Eventually.WaitUntilAsync(() => connection.State == RtmpConnectionState.Connected, "the connection is no longer publishing");

        Assert.Equal("NetStream.Publish.Start", await client.PublishAsync(streamId, RtmpTestServer.StreamKey));
        await client.DisposeAsync();
        await run.WithinSafetyTimeout();
    }

    /// <summary>Verifies that FCUnpublish and deleteStream end the session as deleted and remove the stream.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task DeleteStream_EndsSessionAndRemovesStream()
    {
        var (_, run, client) = _server.Start();
        var (streamId, _) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);

        await client.SendCommandAsync(0, "FCUnpublish", AmfValue.Null, AmfValue.FromString(RtmpTestServer.StreamKey));
        await client.SendCommandAsync(0, "deleteStream", 0, AmfValue.Null, AmfValue.FromNumber(streamId));

        Assert.Equal(PublishEndReason.StreamDeleted, (await _server.Handler.NextAsync("ended")).Session.EndReason);
        Assert.Equal("NetStream.Publish.BadName", await client.PublishAsync(streamId, RtmpTestServer.StreamKey));
        await client.DisposeAsync();
        await run.WithinSafetyTimeout();
    }

    /// <summary>
    /// Verifies that a client disconnecting while publishing, mid-message, ends the session as a disconnect, empties
    /// the registry, releases every pooled buffer and records the publisher disconnect.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Disconnect_WhilePublishing_CleansUp()
    {
        using var disconnects = new MetricCollector<long>(_server.MeterFactory, RtmpMetrics.MeterName, "rtmp.publisher.disconnects");
        using var active = new MetricCollector<long>(_server.MeterFactory, RtmpMetrics.MeterName, "rtmp.sessions.active");
        var (connection, run, client) = _server.Start();
        var (streamId, _) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);
        await client.SendRawAsync(PartialVideoMessage(streamId));
        await Eventually.WaitUntilAsync(() => connection.BufferedBytes > 0, "the partial message is buffered");

        await client.DisposeAsync();
        await run.WithinSafetyTimeout();

        Assert.Equal(RtmpCloseReason.ClientDisconnected, connection.CloseReason);
        Assert.Equal(PublishEndReason.ClientDisconnected, (await _server.Handler.NextAsync("ended")).Session.EndReason);
        Assert.Equal(0, _server.Registry.Count);
        Assert.Equal(0, _server.Pool.Outstanding);
        Assert.Equal("client_disconnected", disconnects.LastMeasurement?.Tags["reason"]);
        Assert.Equal(0, active.GetMeasurementSnapshot().Sum(static m => m.Value));
        Assert.Contains(_server.Logs.GetSnapshot(), static r => r.Id.Id == 5107);
    }

    /// <summary>Verifies that malformed chunk data while publishing closes the connection as a protocol error and ends the session.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ProtocolError_WhilePublishing_EndsSession()
    {
        using var errors = new MetricCollector<long>(_server.MeterFactory, RtmpMetrics.MeterName, "rtmp.protocol_errors");
        var (connection, run, client) = _server.Start();
        await client.PublishFlowAsync(RtmpTestServer.StreamKey);

        await client.SendRawAsync(new byte[] { 0x7F, 0, 0, 0, 0, 0, 1, 9 });
        await run.WithinSafetyTimeout();

        Assert.Equal(RtmpCloseReason.ProtocolError, connection.CloseReason);
        Assert.Equal(RtmpConnectionState.Failed, connection.State);
        Assert.Equal(PublishEndReason.ProtocolError, (await _server.Handler.NextAsync("ended")).Session.EndReason);
        Assert.Equal(1, errors.LastMeasurement?.Value);
        Assert.Contains(_server.Logs.GetSnapshot(), static r => r.Id.Id == 5104);
        await client.DisposeAsync();
    }

    /// <summary>Verifies that invalid control messages are protocol errors.</summary>
    /// <param name="type">The control message type.</param>
    /// <param name="hex">Its payload in hexadecimal.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData(RtmpMessageType.SetChunkSize, "00000000")]
    [InlineData(RtmpMessageType.SetChunkSize, "80000000")]
    [InlineData(RtmpMessageType.SetChunkSize, "0001")]
    [InlineData(RtmpMessageType.WindowAcknowledgementSize, "00")]
    [InlineData(RtmpMessageType.SetPeerBandwidth, "00000001")]
    [InlineData(RtmpMessageType.UserControl, "00")]
    [InlineData(RtmpMessageType.UserControl, "000600")]
    public async Task InvalidControlMessage_IsProtocolError(RtmpMessageType type, string hex)
    {
        var (connection, run, client) = _server.Start();
        await client.HandshakeAsync();

        await client.SendMessageAsync(2, 0, type, 0, Convert.FromHexString(hex));
        await run.WithinSafetyTimeout();

        Assert.Equal(RtmpCloseReason.ProtocolError, connection.CloseReason);
        await client.DisposeAsync();
    }

    /// <summary>Verifies that a malformed command body is a protocol error.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task MalformedCommand_IsProtocolError()
    {
        var (connection, run, client) = _server.Start();
        await client.HandshakeAsync();

        await client.SendMessageAsync(3, 0, RtmpMessageType.CommandAmf0, 0, new byte[] { 0x02, 0x00, 0x09, 0x61 });
        await run.WithinSafetyTimeout();

        Assert.Equal(RtmpCloseReason.ProtocolError, connection.CloseReason);
        await client.DisposeAsync();
    }

    /// <summary>Verifies that a message longer than the configured maximum is rejected without buffering it.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task OversizedMessage_IsProtocolError()
    {
        using var server = new RtmpTestServer(new RtmpOptions { MaxMessageSize = 1024 });
        var (connection, run, client) = server.Start();
        var (streamId, _) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);

        await client.SendMessageAsync(6, streamId, RtmpMessageType.Video, 0, new byte[1025]);
        await run.WithinSafetyTimeout();

        Assert.Equal(RtmpCloseReason.ProtocolError, connection.CloseReason);
        Assert.DoesNotContain(server.Handler.Events, static e => e.Kind == "message");
        Assert.Equal(0, server.Pool.Outstanding);
        await client.DisposeAsync();
    }

    /// <summary>Verifies that ping requests are answered with ping responses carrying the same timestamp.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task PingRequest_IsAnswered()
    {
        var (_, run, client) = _server.Start();
        await client.HandshakeAsync();

        await client.SendMessageAsync(2, 0, RtmpMessageType.UserControl, 0, new byte[] { 0, 6, 0x12, 0x34, 0x56, 0x78 });
        var response = await client.ReadMessageAsync();

        Assert.Equal(RtmpMessageType.UserControl, response.Type);
        Assert.Equal([0, 7, 0x12, 0x34, 0x56, 0x78], response.Payload);
        await client.DisposeAsync();
        await run.WithinSafetyTimeout();
    }

    /// <summary>Verifies that after the client sets an acknowledgement window, the server acknowledges the bytes received.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task WindowAcknowledgement_IsSentWhenDue()
    {
        var (connection, run, client) = _server.Start();
        var (streamId, _) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);
        var window = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(window, 4000);
        await client.SendMessageAsync(2, 0, RtmpMessageType.WindowAcknowledgementSize, 0, window);

        await client.SendMessageAsync(6, streamId, RtmpMessageType.Video, 0, new byte[5000]);
        ReceivedMessage ack;
        do
        {
            ack = await client.ReadMessageAsync();
        }
        while (ack.Type != RtmpMessageType.Acknowledgement);

        Assert.True(BinaryPrimitives.ReadUInt32BigEndian(ack.Payload) >= 4000);
        Assert.True(BinaryPrimitives.ReadUInt32BigEndian(ack.Payload) <= connection.BytesReceived);
        await client.DisposeAsync();
        await run.WithinSafetyTimeout();
    }

    /// <summary>Verifies that Abort Message discards a partial message on the connection.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task AbortMessage_DiscardsPartialMessage()
    {
        var (connection, run, client) = _server.Start();
        var (streamId, _) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);
        await client.SendRawAsync(PartialVideoMessage(streamId));
        await Eventually.WaitUntilAsync(() => connection.BufferedBytes > 0, "the partial message is buffered");

        await client.SendMessageAsync(2, 0, RtmpMessageType.Abort, 0, new byte[] { 0, 0, 0, 6 });

        await Eventually.WaitUntilAsync(() => connection.BufferedBytes == 0, "the partial message is discarded");
        await client.DisposeAsync();
        await run.WithinSafetyTimeout();
        Assert.Equal(RtmpCloseReason.ClientDisconnected, connection.CloseReason);
    }

    /// <summary>Verifies that a handler failure ends the session and closes the connection, releasing the message.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task HandlerFailure_ClosesConnection()
    {
        _server.Handler.FailMessages = true;
        var (connection, run, client) = _server.Start();
        var (streamId, _) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);

        await client.SendMessageAsync(6, streamId, RtmpMessageType.Video, 0, new byte[10]);
        await run.WithinSafetyTimeout();

        Assert.Equal(RtmpCloseReason.HandlerFailed, connection.CloseReason);
        Assert.Equal(PublishEndReason.HandlerFailed, (await _server.Handler.NextAsync("ended")).Session.EndReason);
        Assert.Equal(0, _server.Pool.Outstanding);
        Assert.Contains(_server.Logs.GetSnapshot(), static r => r.Id.Id == 5208);
        await client.DisposeAsync();
    }

    /// <summary>Verifies that a delivered message owns its buffer until the handler disposes it, and is unusable afterwards.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task PublishedMessage_OwnsBufferUntilDisposed()
    {
        _server.Handler.RetainMessages = true;
        var (_, run, client) = _server.Start();
        var (streamId, _) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);

        await client.SendMessageAsync(6, streamId, RtmpMessageType.Video, 0, new byte[] { 1, 2, 3 });
        await _server.Handler.NextAsync("message");
        await client.DisposeAsync();
        await run.WithinSafetyTimeout();

        Assert.True(_server.Handler.Retained.TryDequeue(out var message));
        Assert.Equal(1, _server.Pool.Outstanding);
        Assert.Equal([1, 2, 3], message.Payload.ToArray());
        message.Dispose();
        message.Dispose();
        Assert.Equal(0, _server.Pool.Outstanding);
        Assert.Throws<ObjectDisposedException>(() => message.Payload);
    }

    /// <summary>Verifies that a connection receiving nothing for the connection timeout is closed and its session ends as timed out.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task IdleConnection_TimesOut()
    {
        var time = new FakeTimeProvider();
        using var server = new RtmpTestServer(new RtmpOptions { ConnectionTimeout = TimeSpan.FromSeconds(30) }, time);
        var (connection, run, client) = server.Start();
        await client.PublishFlowAsync(RtmpTestServer.StreamKey);

        await Eventually.WaitUntilAsync(
            () =>
            {
                time.Advance(TimeSpan.FromSeconds(30));
                return run.IsCompleted;
            },
            "the idle connection times out");

        Assert.Equal(RtmpCloseReason.IdleTimeout, connection.CloseReason);
        Assert.Equal(PublishEndReason.ConnectionTimeout, (await server.Handler.NextAsync("ended")).Session.EndReason);
        await client.DisposeAsync();
    }

    /// <summary>Verifies that a slow but active publisher is not timed out: each byte resets the inactivity timer.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task SlowPublisher_IsNotTimedOut()
    {
        var time = new FakeTimeProvider();
        using var server = new RtmpTestServer(new RtmpOptions { ConnectionTimeout = TimeSpan.FromSeconds(30) }, time);
        var (connection, run, client) = server.Start();
        var (streamId, _) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);
        var encoded = new ArrayBufferWriter<byte>();
        new ChunkEncoder().WriteMessage(encoded, 6, streamId, (byte)RtmpMessageType.Video, 0, new byte[300]);

        var bytes = encoded.WrittenSpan.ToArray();
        var baseline = connection.BytesReceived;
        for (var i = 0; i < bytes.Length; i++)
        {
            await client.SendRawAsync(bytes.AsMemory(i, 1));
            var expected = baseline + i + 1;
            await Eventually.WaitUntilAsync(() => connection.BytesReceived >= expected, "the byte is received");
            time.Advance(TimeSpan.FromSeconds(29));
        }

        Assert.Equal(300, (await server.Handler.NextAsync("message")).Payload!.Length);
        Assert.Null(connection.CloseReason);
        await client.DisposeAsync();
        await run.WithinSafetyTimeout();
    }

    /// <summary>
    /// Verifies backpressure: while the handler is blocked the connection stops reading, so the client's writes fill
    /// the bounded inbound pipe and its flush does not complete; releasing the handler resumes delivery of every
    /// message in order.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task SlowHandler_AppliesBackpressureToClient()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (connection, run, client) = _server.Start(new System.IO.Pipelines.PipeOptions(pauseWriterThreshold: 64 * 1024, resumeWriterThreshold: 32 * 1024, useSynchronizationContext: false));
        var (streamId, _) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);
        _server.Handler.MessageGate = gate;

        await client.SendMessageAsync(6, streamId, RtmpMessageType.Video, 0, new byte[10]);
        await _server.Handler.MessageBlocked.Task.WithinSafetyTimeout();
        var receivedWhileBlocked = connection.BytesReceived;

        var encoder = new ChunkEncoder();
        var output = client.Transport.Output;
        for (var i = 1; i <= 64; i++)
        {
            encoder.WriteMessage(output, 7, streamId, (byte)RtmpMessageType.Video, (uint)i, new byte[4096]);
        }

        var flush = output.FlushAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.False(flush.IsCompleted, "the client's flush must wait while the server is not reading");
        Assert.Equal(receivedWhileBlocked, connection.BytesReceived);

        gate.SetResult();
        await flush.WithinSafetyTimeout();
        for (var i = 0; i <= 64; i++)
        {
            Assert.Equal((uint)i, (await _server.Handler.NextAsync("message")).Timestamp);
        }

        await client.DisposeAsync();
        await run.WithinSafetyTimeout();
        Assert.Equal(0, _server.Pool.Outstanding);
    }

    /// <summary>Verifies that cancellation while the handler is blocked closes the connection as a shutdown and releases the message.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Shutdown_WhileHandlerBlocked_ReleasesMessage()
    {
        using var shutdown = new CancellationTokenSource();
        var (connection, run, client) = _server.StartWithShutdown(shutdown.Token);
        var (streamId, _) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);
        _server.Handler.MessageGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await client.SendMessageAsync(6, streamId, RtmpMessageType.Video, 0, new byte[10]);
        await _server.Handler.MessageBlocked.Task.WithinSafetyTimeout();
        await shutdown.CancelAsync();
        await run.WithinSafetyTimeout();

        Assert.Equal(RtmpCloseReason.ServerShutdown, connection.CloseReason);
        Assert.Equal(PublishEndReason.ServerShutdown, (await _server.Handler.NextAsync("ended")).Session.EndReason);
        Assert.Equal(0, _server.Pool.Outstanding);
        await client.DisposeAsync();
    }

    /// <summary>Verifies that cancellation while waiting for data closes a publishing connection as a shutdown.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Shutdown_WhileWaitingForData_EndsSessions()
    {
        using var shutdown = new CancellationTokenSource();
        var (connection, run, client) = _server.StartWithShutdown(shutdown.Token);
        await client.PublishFlowAsync(RtmpTestServer.StreamKey);

        await shutdown.CancelAsync();
        await run.WithinSafetyTimeout();
        await client.WaitForCloseAsync();

        Assert.Equal(RtmpCloseReason.ServerShutdown, connection.CloseReason);
        Assert.Equal(RtmpConnectionState.Closed, connection.State);
        Assert.Equal(PublishEndReason.ServerShutdown, (await _server.Handler.NextAsync("ended")).Session.EndReason);
        await client.DisposeAsync();
    }

    /// <summary>
    /// Verifies that cancellation while the server's own flush is blocked by a client that never reads closes the
    /// connection instead of hanging.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Shutdown_WhileWriteBlocked_ClosesConnection()
    {
        using var shutdown = new CancellationTokenSource();
        var (connection, run, client) = _server.StartWithShutdown(shutdown.Token);
        await client.HandshakeAsync();
        var encoder = new ChunkEncoder();
        var output = client.Transport.Output;
        for (var i = 0; i < 8000; i++)
        {
            encoder.WriteMessage(output, 2, 0, (byte)RtmpMessageType.UserControl, 0, [0, 6, 0, 0, 0, 1]);
        }

        var flush = output.FlushAsync(TestContext.Current.CancellationToken).AsTask();
        await Eventually.WaitUntilAsync(() => connection.BytesReceived > RtmpHandshake.ClientBytes, "the pings are being read");

        await shutdown.CancelAsync();
        await run.WithinSafetyTimeout();
        await flush.WithinSafetyTimeout();

        Assert.Equal(RtmpCloseReason.ServerShutdown, connection.CloseReason);
        await client.DisposeAsync();
    }

    /// <summary>Disposes the server.</summary>
    public void Dispose() => _server.Dispose();

    /// <summary>
    /// Asserts that the events, under their unchanged IDs and levels, carry the RTMP application as
    /// <c>RtmpApplication</c> and no <c>Application</c> property that could be taken for the service identity.
    /// </summary>
    /// <param name="eventIds">The event IDs.</param>
    private void AssertRtmpApplication(params int[] eventIds)
    {
        foreach (var id in eventIds)
        {
            var record = _server.Logs.GetSnapshot().First(r => r.Id.Id == id);
            Assert.Equal(id is 5200 or 5204 ? Microsoft.Extensions.Logging.LogLevel.Information : Microsoft.Extensions.Logging.LogLevel.Warning, record.Level);
            var state = record.StructuredState!;
            Assert.Contains(new KeyValuePair<string, string?>("RtmpApplication", "live"), state);
            Assert.DoesNotContain(state, static p => p.Key == "Application");
        }
    }

    /// <summary>Encodes an <c>@setDataFrame onMetaData</c> data message body.</summary>
    /// <param name="properties">Numeric metadata properties.</param>
    /// <returns>The body.</returns>
    private static ReadOnlyMemory<byte> Metadata(params (string Name, double Value)[] properties)
    {
        var body = new ArrayBufferWriter<byte>();
        Amf0Writer.WriteValue(body, AmfValue.FromString("@setDataFrame"));
        Amf0Writer.WriteValue(body, AmfValue.FromString("onMetaData"));
        Amf0Writer.WriteValue(body, AmfValue.CreateEcmaArray(properties.Select(static p => new KeyValuePair<string, AmfValue>(p.Name, AmfValue.FromNumber(p.Value)))));
        return body.WrittenMemory;
    }

    /// <summary>Builds the first 128-byte chunk of a 1000-byte video message on chunk stream 6.</summary>
    /// <param name="streamId">The message stream.</param>
    /// <returns>The bytes.</returns>
    private static byte[] PartialVideoMessage(uint streamId)
    {
        var encoded = new ArrayBufferWriter<byte>();
        new ChunkEncoder().WriteMessage(encoded, 6, streamId, (byte)RtmpMessageType.Video, 0, new byte[1000]);
        return encoded.WrittenSpan[..(12 + 128)].ToArray();
    }
}
