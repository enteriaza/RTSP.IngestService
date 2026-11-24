// RTMP NetConnection/NetStream command processing for one connection: connect, createStream, publish, closeStream,
// deleteStream and the releaseStream/FCPublish/FCUnpublish calls OBS and FFmpeg send. It owns the connection's
// message streams, runs publisher authentication, creates publishing sessions and hands audio, video and data
// messages to the IPublishSessionHandler. Stream keys and tcUrl values are never logged.

using System.Net;
using RTSP.IngestService.Rtmp.Amf;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Streams;

namespace RTSP.IngestService.Rtmp;

/// <summary>
/// Command and stream state of one RTMP connection.
/// </summary>
/// <remarks>
/// <para>States: not connected → connected (after <c>connect</c>); each message stream created by
/// <c>createStream</c> is idle or publishing. Rules:</para>
/// <list type="bullet">
///   <item><description>Any command other than <c>connect</c> before <c>connect</c> succeeds is rejected and closes the connection.</description></item>
///   <item><description>A rejected <c>connect</c> (missing or invalid application) closes the connection.</description></item>
///   <item><description>A rejected publish authentication closes the connection; other publish failures (unknown stream, stream already publishing, identity already published elsewhere, invalid name) are reported with <c>onStatus</c> and leave the connection open.</description></item>
///   <item><description>Media and data messages on a message stream that is not publishing are ignored.</description></item>
/// </list>
/// <para>Used only from the connection's receive loop; not thread-safe.</para>
/// </remarks>
internal sealed partial class RtmpSession
{
    /// <summary>Maximum message streams one connection may hold.</summary>
    public const int MaxMessageStreams = 8;

    /// <summary>Window Acknowledgement Size and Set Peer Bandwidth sent on connect, as common servers do.</summary>
    private const uint WindowSize = 2_500_000;

    /// <summary>Longest accepted application name.</summary>
    private const int MaxApplicationLength = 128;

    /// <summary>Longest accepted stream name including any query string.</summary>
    private const int MaxStreamNameLength = 1024;

    /// <summary>Message streams by ID.</summary>
    private readonly Dictionary<uint, MessageStream> _streams = [];

    /// <summary>Shared server dependencies.</summary>
    private readonly RtmpServerContext _context;

    /// <summary>Outbound messages.</summary>
    private readonly RtmpMessageWriter _writer;

    /// <summary>Logger.</summary>
    private readonly ILogger _logger;

    /// <summary>Next message stream ID to allocate.</summary>
    private uint _nextStreamId = 1;

    /// <summary>Initializes a new instance of the <see cref="RtmpSession"/> class.</summary>
    /// <param name="connectionId">The connection identifier.</param>
    /// <param name="remoteEndPoint">The client's address.</param>
    /// <param name="connectedAt">When the connection was accepted.</param>
    /// <param name="writer">Outbound messages.</param>
    /// <param name="context">Shared server dependencies.</param>
    public RtmpSession(long connectionId, EndPoint? remoteEndPoint, DateTimeOffset connectedAt, RtmpMessageWriter writer, RtmpServerContext context)
    {
        ConnectionId = connectionId;
        RemoteEndPoint = remoteEndPoint;
        ConnectedAt = connectedAt;
        _writer = writer;
        _context = context;
        _logger = context.LoggerFactory.CreateLogger<RtmpSession>();
    }

    /// <summary>Gets the connection identifier.</summary>
    public long ConnectionId { get; }

    /// <summary>Gets the client's address.</summary>
    public EndPoint? RemoteEndPoint { get; }

    /// <summary>Gets when the connection was accepted.</summary>
    public DateTimeOffset ConnectedAt { get; }

    /// <summary>Gets a value indicating whether <c>connect</c> succeeded.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>Gets the application name from <c>connect</c>.</summary>
    public string? Application { get; private set; }

    /// <summary>Gets the reason the session asked the connection to close, or <see langword="null"/>.</summary>
    public RtmpCloseReason? CloseRequest { get; private set; }

    /// <summary>Gets the number of message streams that are publishing.</summary>
    public int PublishingCount { get; private set; }

    /// <summary>Gets the sessions currently publishing on this connection.</summary>
    public IEnumerable<PublisherSession> ActiveSessions => _streams.Values.Where(static s => s.Session is not null).Select(static s => s.Session!);

    /// <summary>Handles an AMF0 or AMF3 command message.</summary>
    /// <param name="message">The message; not retained.</param>
    /// <param name="amf3">Whether it is an AMF3 command (type 17), whose body has a leading format byte.</param>
    /// <param name="cancellationToken">Cancelled when the connection closes.</param>
    /// <returns>A task that completes when the command has been processed and any response buffered.</returns>
    /// <exception cref="RtmpProtocolException">The command is malformed.</exception>
    public async ValueTask HandleCommandAsync(RtmpMessage message, bool amf3, CancellationToken cancellationToken)
    {
        var values = Amf0Reader.ReadAll(AmfBody(message.Payload.Span, amf3));
        if (values.Count == 0 || values[0].Kind != AmfValueKind.Text)
        {
            throw new RtmpProtocolException("A command message does not start with a command name.");
        }

        var name = values[0].StringValue;
        var transactionId = values.Count > 1 && values[1].Kind == AmfValueKind.Number ? values[1].NumberValue : 0;
        var commandObject = values.Count > 2 ? values[2] : AmfValue.Null;
        var arguments = values.Count > 3 ? values.GetRange(3, values.Count - 3) : [];

        if (!IsConnected && name != "connect")
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                var command = Truncate(name);
                LogCommandBeforeConnect(ConnectionId, command);
            }

            if (transactionId > 0)
            {
                WriteError(transactionId, "NetConnection.Call.Failed", "The connection is not connected.");
            }

            CloseRequest = RtmpCloseReason.Rejected;
            return;
        }

        switch (name)
        {
            case "connect":
                HandleConnect(transactionId, commandObject);
                break;

            case "createStream":
                HandleCreateStream(transactionId);
                break;

            case "releaseStream":
            case "FCPublish":
            case "FCUnpublish":
                if (transactionId > 0)
                {
                    WriteResult(transactionId, AmfValue.Null, AmfValue.Undefined);
                }

                break;

            case "publish":
                await HandlePublishAsync(message.MessageStreamId, arguments, cancellationToken).ConfigureAwait(false);
                break;

            case "closeStream":
                await HandleCloseStreamAsync(message.MessageStreamId).ConfigureAwait(false);
                break;

            case "deleteStream":
                await HandleDeleteStreamAsync(arguments).ConfigureAwait(false);
                break;

            default:
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    var command = Truncate(name);
                    LogUnsupportedCommand(ConnectionId, command);
                }

                if (transactionId > 0)
                {
                    WriteError(transactionId, "NetConnection.Call.Failed", "The command is not supported.");
                }

                break;
        }
    }

    /// <summary>Delivers a data message on a publishing stream and captures <c>onMetaData</c>.</summary>
    /// <param name="message">The message; its payload is transferred when delivered.</param>
    /// <param name="amf3">Whether it is an AMF3 data message (type 15).</param>
    /// <param name="cancellationToken">Cancelled when the connection closes.</param>
    /// <returns>A task that completes when the handler has accepted the message.</returns>
    public ValueTask HandleDataAsync(RtmpMessage message, bool amf3, CancellationToken cancellationToken)
    {
        if (!TryGetPublishing(message, out var stream))
        {
            return ValueTask.CompletedTask;
        }

        CaptureMetadata(stream.Session!, message.Payload.Span, amf3);
        return DeliverAsync(stream, message, PublishedMessageKind.Data, cancellationToken);
    }

    /// <summary>Delivers an audio or video message on a publishing stream.</summary>
    /// <param name="message">The message; its payload is transferred when delivered.</param>
    /// <param name="kind">Audio or video.</param>
    /// <param name="cancellationToken">Cancelled when the connection closes.</param>
    /// <returns>A task that completes when the handler has accepted the message.</returns>
    public ValueTask HandleMediaAsync(RtmpMessage message, PublishedMessageKind kind, CancellationToken cancellationToken) =>
        TryGetPublishing(message, out var stream) ? DeliverAsync(stream, message, kind, cancellationToken) : ValueTask.CompletedTask;

    /// <summary>Ends every publishing session on the connection.</summary>
    /// <param name="reason">Why the sessions end.</param>
    /// <returns>A task that completes when the handler has been told about each ended session.</returns>
    public async Task EndAllAsync(PublishEndReason reason)
    {
        foreach (var stream in _streams.Values)
        {
            await EndPublishingAsync(stream, reason).ConfigureAwait(false);
        }

        _streams.Clear();
    }

    /// <summary>Returns the AMF0 portion of a command or data body.</summary>
    /// <param name="payload">The message body.</param>
    /// <param name="amf3">Whether the message type is AMF3.</param>
    /// <returns>The AMF0-encoded bytes.</returns>
    /// <exception cref="RtmpProtocolException">An AMF3 command body does not start with the AMF0 format byte.</exception>
    private static ReadOnlySpan<byte> AmfBody(ReadOnlySpan<byte> payload, bool amf3)
    {
        if (!amf3)
        {
            return payload;
        }

        if (payload.IsEmpty || payload[0] != 0)
        {
            throw new RtmpProtocolException("An AMF3 message body does not start with the AMF0 format selector; AMF3-encoded values are not supported.");
        }

        return payload[1..];
    }

    /// <summary>Creates an <c>onStatus</c>/<c>_error</c> information object.</summary>
    /// <param name="level"><c>status</c> or <c>error</c>.</param>
    /// <param name="code">The status code.</param>
    /// <param name="description">Human-readable description.</param>
    /// <returns>The object.</returns>
    private static AmfValue Status(string level, string code, string description) => AmfValue.CreateObject(
    [
        new("level", AmfValue.FromString(level)),
        new("code", AmfValue.FromString(code)),
        new("description", AmfValue.FromString(description)),
    ]);

    /// <summary>Shortens peer-supplied text for logging.</summary>
    /// <param name="value">The text.</param>
    /// <returns>At most 64 characters.</returns>
    private static string Truncate(string value) => value.Length <= 64 ? value : value[..64];

    /// <summary>
    /// Normalises the <c>app</c> property: removes any query string and trailing slashes and checks that the rest is
    /// 1 to 128 printable ASCII characters without spaces.
    /// </summary>
    /// <param name="app">The raw value.</param>
    /// <param name="application">The normalised name.</param>
    /// <returns><see langword="true"/> if valid.</returns>
    private static bool TryNormalizeApplication(string? app, out string application)
    {
        application = string.Empty;
        if (app is null)
        {
            return false;
        }

        var query = app.IndexOf('?', StringComparison.Ordinal);
        var name = (query >= 0 ? app[..query] : app).TrimEnd('/');
        if (name.Length is 0 or > MaxApplicationLength || name.Any(static c => c is <= ' ' or > '~'))
        {
            return false;
        }

        application = name;
        return true;
    }

    /// <summary>Splits a publish name into the name and the query string after the first <c>?</c>.</summary>
    /// <param name="raw">The raw name.</param>
    /// <param name="streamName">The name.</param>
    /// <param name="query">The query string, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the name is 1 to 1024 characters without control characters.</returns>
    private static bool TrySplitStreamName(string? raw, out string streamName, out string? query)
    {
        streamName = string.Empty;
        query = null;
        if (raw is null || raw.Length > MaxStreamNameLength || raw.Any(char.IsControl))
        {
            return false;
        }

        var separator = raw.IndexOf('?', StringComparison.Ordinal);
        streamName = separator >= 0 ? raw[..separator] : raw;
        query = separator >= 0 ? raw[(separator + 1)..] : null;
        return streamName.Length > 0;
    }

    /// <summary>Handles <c>connect</c>.</summary>
    /// <param name="transactionId">The transaction ID.</param>
    /// <param name="commandObject">The command object.</param>
    private void HandleConnect(double transactionId, AmfValue commandObject)
    {
        if (IsConnected)
        {
            WriteError(transactionId, "NetConnection.Connect.Rejected", "The connection is already connected.");
            return;
        }

        if (commandObject.Kind != AmfValueKind.AnonymousObject || !TryNormalizeApplication(commandObject.GetStringProperty("app"), out var application))
        {
            LogConnectRejected(ConnectionId);
            WriteError(transactionId, "NetConnection.Connect.Rejected", "The application name is missing or invalid.");
            CloseRequest = RtmpCloseReason.Rejected;
            return;
        }

        Application = application;
        TcUrl = commandObject.GetStringProperty("tcUrl");
        IsConnected = true;

        _writer.WriteWindowAcknowledgementSize(WindowSize);
        _writer.WriteSetPeerBandwidth(WindowSize, 2);
        _writer.WriteSetChunkSize(_context.Options.OutboundChunkSize);
        WriteResult(
            transactionId,
            AmfValue.CreateObject(
            [
                new("fmsVer", AmfValue.FromString("FMS/3,0,1,123")),
                new("capabilities", AmfValue.FromNumber(31)),
            ]),
            AmfValue.CreateObject(
            [
                new("level", AmfValue.FromString("status")),
                new("code", AmfValue.FromString("NetConnection.Connect.Success")),
                new("description", AmfValue.FromString("Connection succeeded.")),
                new("objectEncoding", AmfValue.FromNumber(0)),
            ]));

        LogConnected(ConnectionId, application);
    }

    /// <summary>Gets the <c>tcUrl</c> from <c>connect</c>; may contain secrets and is never logged.</summary>
    private string? TcUrl { get; set; }

    /// <summary>Handles <c>createStream</c>.</summary>
    /// <param name="transactionId">The transaction ID.</param>
    private void HandleCreateStream(double transactionId)
    {
        if (_streams.Count >= MaxMessageStreams)
        {
            WriteError(transactionId, "NetConnection.Call.Failed", $"A connection may have at most {MaxMessageStreams} streams.");
            return;
        }

        var streamId = _nextStreamId++;
        _streams.Add(streamId, new MessageStream(streamId));
        WriteResult(transactionId, AmfValue.Null, AmfValue.FromNumber(streamId));
    }

    /// <summary>Handles <c>publish</c>: validates, authenticates, registers and starts a session.</summary>
    /// <param name="messageStreamId">The message stream the command arrived on.</param>
    /// <param name="arguments">The command arguments after the command object.</param>
    /// <param name="cancellationToken">Cancelled when the connection closes.</param>
    /// <returns>A task that completes when the outcome has been buffered.</returns>
    private async ValueTask HandlePublishAsync(uint messageStreamId, List<AmfValue> arguments, CancellationToken cancellationToken)
    {
        if (!_streams.TryGetValue(messageStreamId, out var stream))
        {
            LogPublishInvalid(ConnectionId, messageStreamId, "the message stream was not created");
            WriteStatus(messageStreamId, "error", "NetStream.Publish.BadName", "The stream does not exist.");
            return;
        }

        if (stream.Session is not null)
        {
            LogPublishInvalid(ConnectionId, messageStreamId, "the message stream is already publishing");
            WriteStatus(messageStreamId, "error", "NetStream.Publish.BadName", "The stream is already publishing.");
            return;
        }

        var rawName = arguments.Count > 0 && arguments[0].Kind == AmfValueKind.Text ? arguments[0].StringValue : null;
        var publishType = arguments.Count > 1 && arguments[1].Kind == AmfValueKind.Text ? Truncate(arguments[1].StringValue) : "live";
        if (!TrySplitStreamName(rawName, out var streamName, out var query))
        {
            LogPublishInvalid(ConnectionId, messageStreamId, "the stream name is missing or invalid");
            WriteStatus(messageStreamId, "error", "NetStream.Publish.BadName", "The stream name is missing or invalid.");
            return;
        }

        var request = new PublishAuthenticationRequest(ConnectionId, RemoteEndPoint, Application!, streamName, query, TcUrl, publishType);
        PublishAuthenticationResult result;
        try
        {
            result = await _context.Authenticator.AuthenticateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogAuthenticatorFailed(ex, ConnectionId);
            WriteStatus(messageStreamId, "error", "NetStream.Publish.Failed", "Publishing could not be authorised.");
            CloseRequest = RtmpCloseReason.HandlerFailed;
            return;
        }

        if (!result.IsAuthorized)
        {
            _context.Metrics.AuthenticationFailed();
            LogPublishRejected(ConnectionId, Application!, result.RejectionReason);
            WriteStatus(messageStreamId, "error", "NetStream.Publish.Unauthorized", "Publishing is not authorised.");
            CloseRequest = RtmpCloseReason.Rejected;
            return;
        }

        var time = _context.TimeProvider;
        var session = new PublisherSession(ConnectionId, RemoteEndPoint, Application!, result.StreamIdentity, publishType, messageStreamId, ConnectedAt, time.GetUtcNow());
        if (!_context.Registry.TryAdd(session))
        {
            LogStreamAlreadyPublished(ConnectionId, Application!, result.StreamIdentity);
            WriteStatus(messageStreamId, "error", "NetStream.Publish.BadName", "The stream is already being published.");
            return;
        }

        try
        {
            await _context.Handler.OnSessionStartedAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _context.Registry.Remove(session);
            session.TryEnd(PublishEndReason.HandlerFailed, time.GetUtcNow());
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            LogHandlerFailed(ex, session.SessionId);
            WriteStatus(messageStreamId, "error", "NetStream.Publish.Failed", "Publishing could not be started.");
            CloseRequest = RtmpCloseReason.HandlerFailed;
            return;
        }

        stream.Session = session;
        PublishingCount++;
        _context.Metrics.SessionStarted();
        _writer.WriteUserControl(UserControlEventType.StreamBegin, messageStreamId);
        WriteStatus(messageStreamId, "status", "NetStream.Publish.Start", "Publishing started.");
        LogPublishStarted(session.SessionId, ConnectionId, session.Application, session.StreamIdentity, publishType);
        _context.Events?.Publish(StreamEventFactory.Create(StreamEventType.Published, session, time.GetUtcNow()));
    }

    /// <summary>Handles <c>closeStream</c>: ends publishing on the stream and keeps the stream.</summary>
    /// <param name="messageStreamId">The message stream the command arrived on.</param>
    /// <returns>A task that completes when the session has ended.</returns>
    private async ValueTask HandleCloseStreamAsync(uint messageStreamId)
    {
        if (_streams.TryGetValue(messageStreamId, out var stream) && stream.Session is not null)
        {
            await EndPublishingAsync(stream, PublishEndReason.Unpublished).ConfigureAwait(false);
            WriteStatus(messageStreamId, "status", "NetStream.Unpublish.Success", "Publishing stopped.");
        }
    }

    /// <summary>Handles <c>deleteStream</c>: ends publishing on the stream and removes it. Sends no response.</summary>
    /// <param name="arguments">The arguments; the first is the stream ID.</param>
    /// <returns>A task that completes when the stream has been removed.</returns>
    private async ValueTask HandleDeleteStreamAsync(List<AmfValue> arguments)
    {
        if (arguments.Count == 0 || arguments[0].Kind != AmfValueKind.Number)
        {
            return;
        }

        var value = arguments[0].NumberValue;
        if (value is < 1 or > uint.MaxValue || value != Math.Floor(value))
        {
            return;
        }

        var streamId = (uint)value;
        if (_streams.Remove(streamId, out var stream))
        {
            await EndPublishingAsync(stream, PublishEndReason.StreamDeleted).ConfigureAwait(false);
        }
    }

    /// <summary>Finds the publishing stream a media or data message belongs to.</summary>
    /// <param name="message">The message.</param>
    /// <param name="stream">The stream, when publishing.</param>
    /// <returns><see langword="true"/> if the message's stream is publishing.</returns>
    private bool TryGetPublishing(RtmpMessage message, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MessageStream? stream)
    {
        if (_streams.TryGetValue(message.MessageStreamId, out stream) && stream.Session is not null)
        {
            return true;
        }

        LogMessageIgnored(ConnectionId, message.MessageTypeId, message.MessageStreamId);
        stream = null;
        return false;
    }

    /// <summary>Stores <c>onMetaData</c> properties on the session; unparsable metadata is logged and skipped.</summary>
    /// <param name="session">The session.</param>
    /// <param name="payload">The data message body.</param>
    /// <param name="amf3">Whether the message type is AMF3.</param>
    private void CaptureMetadata(PublisherSession session, ReadOnlySpan<byte> payload, bool amf3)
    {
        try
        {
            var body = amf3 && !payload.IsEmpty && payload[0] == 0 ? payload[1..] : payload;
            var values = Amf0Reader.ReadAll(body);
            var offset = values.Count > 0 && values[0] is { Kind: AmfValueKind.Text, StringValue: "@setDataFrame" } ? 1 : 0;
            if (values.Count > offset + 1
                && values[offset] is { Kind: AmfValueKind.Text, StringValue: "onMetaData" }
                && values[offset + 1].Kind is AmfValueKind.AnonymousObject or AmfValueKind.EcmaArray)
            {
                session.SetMetadata(values[offset + 1].Properties);
            }
        }
        catch (RtmpProtocolException ex)
        {
            LogMetadataUnparsable(session.SessionId, ex.Message);
        }
    }

    /// <summary>Transfers the payload to a <see cref="PublishedMessage"/> and awaits the handler.</summary>
    /// <param name="stream">The publishing stream.</param>
    /// <param name="message">The message whose payload is transferred.</param>
    /// <param name="kind">The message kind.</param>
    /// <param name="cancellationToken">Cancelled when the connection closes.</param>
    /// <returns>A task that completes when the handler has accepted the message.</returns>
    private async ValueTask DeliverAsync(MessageStream stream, RtmpMessage message, PublishedMessageKind kind, CancellationToken cancellationToken)
    {
        var session = stream.Session!;
        var published = new PublishedMessage(kind, message.MessageTypeId, message.Timestamp, message.TakePayload());
        session.RecordMessage(kind, published.Length);
        try
        {
            await _context.Handler.OnMessageAsync(session, published, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            published.Dispose();
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            LogHandlerFailed(ex, session.SessionId);
            await EndPublishingAsync(stream, PublishEndReason.HandlerFailed).ConfigureAwait(false);
            CloseRequest = RtmpCloseReason.HandlerFailed;
        }
    }

    /// <summary>Ends the stream's session, if any, and notifies the handler.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="reason">Why the session ends.</param>
    /// <returns>A task that completes when the handler has been notified.</returns>
    private async Task EndPublishingAsync(MessageStream stream, PublishEndReason reason)
    {
        if (stream.Session is not { } session)
        {
            return;
        }

        stream.Session = null;
        PublishingCount--;
        _context.Registry.Remove(session);
        if (session.TryEnd(reason, _context.TimeProvider.GetUtcNow()))
        {
            _context.Events?.Publish(StreamEventFactory.Create(StreamEventType.Ended, session, session.EndedAt!.Value, StreamEventFactory.EndReason(reason)));
        }

        _context.Metrics.SessionEnded(reason);
        LogPublishEnded(
            session.SessionId,
            ConnectionId,
            reason,
            session.EndedAt!.Value - session.StartedAt,
            session.AudioMessageCount,
            session.VideoMessageCount,
            session.DataMessageCount,
            session.ReceivedPayloadBytes);

        try
        {
            await _context.Handler.OnSessionEndedAsync(session, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogHandlerFailed(ex, session.SessionId);
        }
    }

    /// <summary>Buffers a <c>_result</c> response.</summary>
    /// <param name="transactionId">The transaction ID.</param>
    /// <param name="properties">The command object.</param>
    /// <param name="information">The response information.</param>
    private void WriteResult(double transactionId, AmfValue properties, AmfValue information) =>
        _writer.WriteCommand(RtmpMessageWriter.CommandChunkStreamId, 0, AmfValue.FromString("_result"), AmfValue.FromNumber(transactionId), properties, information);

    /// <summary>Buffers an <c>_error</c> response.</summary>
    /// <param name="transactionId">The transaction ID.</param>
    /// <param name="code">The status code.</param>
    /// <param name="description">The description.</param>
    private void WriteError(double transactionId, string code, string description) =>
        _writer.WriteCommand(RtmpMessageWriter.CommandChunkStreamId, 0, AmfValue.FromString("_error"), AmfValue.FromNumber(transactionId), AmfValue.Null, Status("error", code, description));

    /// <summary>Buffers an <c>onStatus</c> message on a message stream.</summary>
    /// <param name="messageStreamId">The message stream.</param>
    /// <param name="level"><c>status</c> or <c>error</c>.</param>
    /// <param name="code">The status code.</param>
    /// <param name="description">The description.</param>
    private void WriteStatus(uint messageStreamId, string level, string code, string description) =>
        _writer.WriteCommand(RtmpMessageWriter.StatusChunkStreamId, messageStreamId, AmfValue.FromString("onStatus"), AmfValue.FromNumber(0), AmfValue.Null, Status(level, code, description));

    /// <summary>Logs a successful <c>connect</c>.</summary>
    /// <param name="connectionId">The connection.</param>
    /// <param name="application">The application name.</param>
    [LoggerMessage(EventId = 5200, Level = LogLevel.Information, Message = "RTMP connection {ConnectionId} connected to application {Application}")]
    private partial void LogConnected(long connectionId, string application);

    /// <summary>Logs a rejected <c>connect</c>.</summary>
    /// <param name="connectionId">The connection.</param>
    [LoggerMessage(EventId = 5201, Level = LogLevel.Warning, Message = "RTMP connection {ConnectionId} sent connect with a missing or invalid application name; closing")]
    private partial void LogConnectRejected(long connectionId);

    /// <summary>Logs a command received before <c>connect</c>.</summary>
    /// <param name="connectionId">The connection.</param>
    /// <param name="command">The command name, truncated to 64 characters.</param>
    [LoggerMessage(EventId = 5202, Level = LogLevel.Warning, Message = "RTMP connection {ConnectionId} sent {Command} before connect; closing")]
    private partial void LogCommandBeforeConnect(long connectionId, string command);

    /// <summary>Logs an unsupported command.</summary>
    /// <param name="connectionId">The connection.</param>
    /// <param name="command">The command name, truncated to 64 characters.</param>
    [LoggerMessage(EventId = 5203, Level = LogLevel.Debug, Message = "RTMP connection {ConnectionId} sent unsupported command {Command}")]
    private partial void LogUnsupportedCommand(long connectionId, string command);

    /// <summary>Logs a started publishing session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="connectionId">The connection.</param>
    /// <param name="application">The application.</param>
    /// <param name="streamIdentity">The non-secret stream identity.</param>
    /// <param name="publishType">The publishing type.</param>
    [LoggerMessage(EventId = 5204, Level = LogLevel.Information, Message = "Publishing started: session {SessionId} on RTMP connection {ConnectionId}, application {Application}, stream {StreamIdentity} ({PublishType})")]
    private partial void LogPublishStarted(Guid sessionId, long connectionId, string application, string streamIdentity, string publishType);

    /// <summary>Logs a publish request rejected by the authenticator.</summary>
    /// <param name="connectionId">The connection.</param>
    /// <param name="application">The application.</param>
    /// <param name="reason">The authenticator's non-secret reason.</param>
    [LoggerMessage(EventId = 5205, Level = LogLevel.Warning, Message = "Publish rejected by authentication on RTMP connection {ConnectionId}, application {Application}: {Reason}; closing")]
    private partial void LogPublishRejected(long connectionId, string application, string reason);

    /// <summary>Logs a publish for an identity that another session is already publishing.</summary>
    /// <param name="connectionId">The connection.</param>
    /// <param name="application">The application.</param>
    /// <param name="streamIdentity">The identity.</param>
    [LoggerMessage(EventId = 5206, Level = LogLevel.Warning, Message = "Publish rejected on RTMP connection {ConnectionId}: stream {StreamIdentity} in application {Application} is already being published")]
    private partial void LogStreamAlreadyPublished(long connectionId, string application, string streamIdentity);

    /// <summary>Logs an ended publishing session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="connectionId">The connection.</param>
    /// <param name="reason">Why it ended.</param>
    /// <param name="duration">How long it published.</param>
    /// <param name="audioMessages">Audio messages received.</param>
    /// <param name="videoMessages">Video messages received.</param>
    /// <param name="dataMessages">Data messages received.</param>
    /// <param name="payloadBytes">Payload bytes received.</param>
    [LoggerMessage(EventId = 5207, Level = LogLevel.Information, Message = "Publishing ended: session {SessionId} on RTMP connection {ConnectionId}, reason {Reason}, duration {Duration}, {AudioMessages} audio, {VideoMessages} video and {DataMessages} data messages, {PayloadBytes} payload bytes")]
    private partial void LogPublishEnded(Guid sessionId, long connectionId, PublishEndReason reason, TimeSpan duration, long audioMessages, long videoMessages, long dataMessages, long payloadBytes);

    /// <summary>Logs a publish session handler failure.</summary>
    /// <param name="exception">The failure.</param>
    /// <param name="sessionId">The session.</param>
    [LoggerMessage(EventId = 5208, Level = LogLevel.Error, Message = "The publish session handler failed for session {SessionId}")]
    private partial void LogHandlerFailed(Exception exception, Guid sessionId);

    /// <summary>Logs metadata that could not be decoded; the message is still delivered.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="detail">The decoding error.</param>
    [LoggerMessage(EventId = 5209, Level = LogLevel.Debug, Message = "Metadata for session {SessionId} could not be decoded: {Detail}")]
    private partial void LogMetadataUnparsable(Guid sessionId, string detail);

    /// <summary>Logs a media or data message on a stream that is not publishing.</summary>
    /// <param name="connectionId">The connection.</param>
    /// <param name="messageTypeId">The message type.</param>
    /// <param name="messageStreamId">The message stream.</param>
    [LoggerMessage(EventId = 5210, Level = LogLevel.Debug, Message = "RTMP connection {ConnectionId} sent message type {MessageTypeId} on message stream {MessageStreamId}, which is not publishing; ignored")]
    private partial void LogMessageIgnored(long connectionId, byte messageTypeId, uint messageStreamId);

    /// <summary>Logs a publish request that is invalid in the current stream state.</summary>
    /// <param name="connectionId">The connection.</param>
    /// <param name="messageStreamId">The message stream.</param>
    /// <param name="reason">What is wrong.</param>
    [LoggerMessage(EventId = 5211, Level = LogLevel.Warning, Message = "Publish rejected on RTMP connection {ConnectionId}, message stream {MessageStreamId}: {Reason}")]
    private partial void LogPublishInvalid(long connectionId, uint messageStreamId, string reason);

    /// <summary>Logs an authenticator failure.</summary>
    /// <param name="exception">The failure.</param>
    /// <param name="connectionId">The connection.</param>
    [LoggerMessage(EventId = 5212, Level = LogLevel.Error, Message = "The publisher authenticator failed for RTMP connection {ConnectionId}; closing")]
    private partial void LogAuthenticatorFailed(Exception exception, long connectionId);

    /// <summary>A message stream created by <c>createStream</c>.</summary>
    /// <param name="id">The message stream ID.</param>
    private sealed class MessageStream(uint id)
    {
        /// <summary>Gets the message stream ID.</summary>
        public uint Id { get; } = id;

        /// <summary>Gets or sets the publishing session, or <see langword="null"/> while idle.</summary>
        public PublisherSession? Session { get; set; }
    }
}
