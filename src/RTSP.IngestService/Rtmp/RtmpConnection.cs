// Runs one RTMP connection from acceptance to close: handshake under the handshake timeout, then a receive loop that
// decodes chunks, applies protocol control messages, dispatches commands, data and media to the session, and sends
// acknowledgements. Every termination path (disconnect, timeout, protocol error, handler failure, shutdown) ends the
// connection's publishing sessions, releases pooled buffers and completes the transport. Failures stay inside the
// connection; RunAsync never throws.

using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Rtmp.Publishing;

namespace RTSP.IngestService.Rtmp;

/// <summary>
/// One RTMP connection over a duplex pipe.
/// </summary>
/// <remarks>
/// <para>
/// Reads happen only from the receive loop, which awaits each message's handling before decoding the next, so a slow
/// <see cref="IPublishSessionHandler"/> stops the connection reading (backpressure). The inactivity timer runs only
/// while the loop waits for data or for a flush to complete.
/// </para>
/// <para>
/// <see cref="State"/>, <see cref="CloseReason"/> and <see cref="BytesReceived"/> may be read from any thread.
/// <see cref="RunAsync"/> releases all resources itself; <see cref="Dispose"/> covers a connection that never ran.
/// </para>
/// </remarks>
internal sealed partial class RtmpConnection : IDisposable
{
    /// <summary>The transport; completed by the connection when it closes.</summary>
    private readonly IDuplexPipe _transport;

    /// <summary>Shared server dependencies.</summary>
    private readonly RtmpServerContext _context;

    /// <summary>Logger.</summary>
    private readonly ILogger _logger;

    /// <summary>Inbound chunk decoder.</summary>
    private readonly ChunkDecoder _decoder;

    /// <summary>Outbound messages.</summary>
    private readonly RtmpMessageWriter _writer;

    /// <summary>Current <see cref="RtmpConnectionState"/> as an integer for atomic access.</summary>
    private int _state;

    /// <summary>Bytes received, including the handshake.</summary>
    private long _bytesReceived;

    /// <summary>Value of <see cref="_bytesReceived"/> when the last Acknowledgement was sent.</summary>
    private long _acknowledgedBytes;

    /// <summary>The peer's acknowledgement window; 0 until it sends Window Acknowledgement Size.</summary>
    private uint _peerWindowSize;

    /// <summary>Close reason, stored as an integer + 1 so that 0 means "not closed".</summary>
    private int _closeReason;

    /// <summary>Initializes a new instance of the <see cref="RtmpConnection"/> class in the <see cref="RtmpConnectionState.Accepted"/> state.</summary>
    /// <param name="id">The connection identifier.</param>
    /// <param name="remoteEndPoint">The client's address.</param>
    /// <param name="transport">The transport; the connection completes both pipes when it closes but does not dispose the transport.</param>
    /// <param name="context">Shared server dependencies.</param>
    public RtmpConnection(long id, EndPoint? remoteEndPoint, IDuplexPipe transport, RtmpServerContext context)
    {
        Id = id;
        RemoteEndPoint = remoteEndPoint;
        _transport = transport;
        _context = context;
        _logger = context.LoggerFactory.CreateLogger<RtmpConnection>();
        AcceptedAt = context.TimeProvider.GetUtcNow();

        var options = context.Options;
        _decoder = new ChunkDecoder(options.InboundChunkSize, options.MaxMessageSize, options.MaxBufferedBytes, context.BufferPool);
        _writer = new RtmpMessageWriter(transport.Output);
        Session = new RtmpSession(id, remoteEndPoint, AcceptedAt, _writer, context);
    }

    /// <summary>Gets the connection identifier.</summary>
    public long Id { get; }

    /// <summary>Gets the client's address.</summary>
    public EndPoint? RemoteEndPoint { get; }

    /// <summary>Gets when the connection was accepted.</summary>
    public DateTimeOffset AcceptedAt { get; }

    /// <summary>Gets the command and stream state.</summary>
    public RtmpSession Session { get; }

    /// <summary>Gets the lifecycle state.</summary>
    public RtmpConnectionState State => (RtmpConnectionState)Volatile.Read(ref _state);

    /// <summary>Gets why the connection closed, or <see langword="null"/> while it is open.</summary>
    public RtmpCloseReason? CloseReason => Volatile.Read(ref _closeReason) is var value and > 0 ? (RtmpCloseReason)(value - 1) : null;

    /// <summary>Gets the bytes received so far, including the handshake.</summary>
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <summary>Gets the pooled bytes held in partially received messages.</summary>
    public long BufferedBytes => _decoder.BufferedBytes;

    /// <summary>
    /// Runs the connection until the client disconnects, the server closes it, a limit or timeout is hit, or
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="cancellationToken">Cancelled at server shutdown.</param>
    /// <returns>A task that completes after cleanup; it never faults.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var options = _context.Options;
        var time = _context.TimeProvider;
        var startedAt = time.GetTimestamp();
        var handshakeCompleted = false;
        var reason = RtmpCloseReason.InternalError;
        Exception? failure = null;

        LogAccepted(Id, RemoteEndPoint);
        using var timeoutSource = new CancellationTokenSource(Timeout.InfiniteTimeSpan, time);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var token = linkedSource.Token;
        try
        {
            TransitionTo(RtmpConnectionState.Handshaking);
            timeoutSource.CancelAfter(options.HandshakeTimeout);
            await RtmpHandshake.PerformAsync(_transport, 0, token).ConfigureAwait(false);
            timeoutSource.CancelAfter(Timeout.InfiniteTimeSpan);
            handshakeCompleted = true;
            AddReceived(RtmpHandshake.ClientBytes);
            _context.Metrics.Handshake(succeeded: true);
            LogHandshakeCompleted(Id);

            TransitionTo(RtmpConnectionState.Connected);
            reason = await ReceiveAsync(timeoutSource, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            reason = RtmpCloseReason.ServerShutdown;
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            reason = handshakeCompleted ? RtmpCloseReason.IdleTimeout : RtmpCloseReason.HandshakeTimeout;
        }
        catch (RtmpHandshakeException ex)
        {
            reason = RtmpCloseReason.HandshakeFailed;
            failure = ex;
        }
        catch (RtmpProtocolException ex)
        {
            reason = RtmpCloseReason.ProtocolError;
            failure = ex;
        }
        catch (EndOfStreamException)
        {
            reason = RtmpCloseReason.ClientDisconnected;
        }
        catch (Exception ex) when (IsReset(ex))
        {
            reason = RtmpCloseReason.ClientDisconnected;
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            reason = RtmpCloseReason.TransportError;
            failure = ex;
        }
        catch (Exception ex)
        {
            reason = RtmpCloseReason.InternalError;
            failure = ex;
            LogUnexpectedError(ex, Id);
        }

        if (!handshakeCompleted)
        {
            _context.Metrics.Handshake(succeeded: false);
        }

        await CloseAsync(reason).ConfigureAwait(false);

        var duration = time.GetElapsedTime(startedAt);
        _context.Metrics.ConnectionClosed(duration, reason);
        if (failure is null)
        {
            LogClosed(Id, reason, duration, BytesReceived);
        }
        else
        {
            LogFailed(Id, reason, failure.Message, duration, BytesReceived);
        }
    }

    /// <summary>Returns any partial message buffers to the pool. Idempotent.</summary>
    public void Dispose() => _decoder.Dispose();

    /// <summary>Whether <paramref name="exception"/> reports a connection reset or abort by the peer.</summary>
    /// <param name="exception">The exception.</param>
    /// <returns><see langword="true"/> for resets.</returns>
    private static bool IsReset(Exception exception)
    {
        var socketException = exception as SocketException ?? exception.InnerException as SocketException;
        return socketException?.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown;
    }

    /// <summary>Maps a connection close reason to the end reason of its publishing sessions.</summary>
    /// <param name="reason">The close reason.</param>
    /// <returns>The session end reason.</returns>
    private static PublishEndReason ToEndReason(RtmpCloseReason reason) => reason switch
    {
        RtmpCloseReason.ClientDisconnected => PublishEndReason.ClientDisconnected,
        RtmpCloseReason.ServerShutdown => PublishEndReason.ServerShutdown,
        RtmpCloseReason.IdleTimeout => PublishEndReason.ConnectionTimeout,
        RtmpCloseReason.ProtocolError => PublishEndReason.ProtocolError,
        RtmpCloseReason.HandlerFailed => PublishEndReason.HandlerFailed,
        _ => PublishEndReason.ConnectionError,
    };

    /// <summary>The receive loop.</summary>
    /// <param name="timeoutSource">Armed with the connection timeout while waiting for data or a flush.</param>
    /// <param name="token">Cancelled at shutdown or by the timeout.</param>
    /// <returns>Why the loop ended normally.</returns>
    private async Task<RtmpCloseReason> ReceiveAsync(CancellationTokenSource timeoutSource, CancellationToken token)
    {
        var reader = _transport.Input;
        var timeout = _context.Options.ConnectionTimeout;
        long retained = 0;
        while (true)
        {
            timeoutSource.CancelAfter(timeout);
            var result = await reader.ReadAsync(token).ConfigureAwait(false);
            timeoutSource.CancelAfter(Timeout.InfiniteTimeSpan);

            var buffer = result.Buffer;
            AddReceived(buffer.Length - retained);
            try
            {
                while (Session.CloseRequest is null && _decoder.TryDecode(ref buffer, out var message))
                {
                    using (message)
                    {
                        await HandleMessageAsync(message, token).ConfigureAwait(false);
                    }

                    UpdatePublishingState();
                }
            }
            finally
            {
                reader.AdvanceTo(buffer.Start, result.Buffer.End);
            }

            retained = buffer.Length;
            AcknowledgeIfDue();

            timeoutSource.CancelAfter(timeout);
            await _writer.FlushAsync(token).ConfigureAwait(false);
            timeoutSource.CancelAfter(Timeout.InfiniteTimeSpan);

            if (Session.CloseRequest is { } closeReason)
            {
                return closeReason;
            }

            if (result.IsCompleted)
            {
                if (retained > 0 || _decoder.HasPartialMessage)
                {
                    LogIncompleteMessage(Id);
                }

                return RtmpCloseReason.ClientDisconnected;
            }
        }
    }

    /// <summary>Dispatches one message.</summary>
    /// <param name="message">The message.</param>
    /// <param name="token">Cancelled at shutdown or by the timeout.</param>
    /// <returns>A task that completes when the message has been handled.</returns>
    /// <exception cref="RtmpProtocolException">A control message is malformed.</exception>
    private ValueTask HandleMessageAsync(RtmpMessage message, CancellationToken token)
    {
        var payload = message.Payload.Span;
        switch (message.Type)
        {
            case RtmpMessageType.SetChunkSize:
                Control();
                var chunkSize = ReadUInt32(payload, "Set Chunk Size");
                if (chunkSize is 0 or > int.MaxValue)
                {
                    throw new RtmpProtocolException($"Set Chunk Size {chunkSize} is outside the valid range 1 to 2147483647.");
                }

                _decoder.SetChunkSize((int)chunkSize);
                LogPeerChunkSize(Id, (int)chunkSize);
                return ValueTask.CompletedTask;

            case RtmpMessageType.Abort:
                Control();
                var chunkStreamId = ReadUInt32(payload, "Abort Message");
                if (chunkStreamId <= ChunkEncoder.MaxChunkStreamId)
                {
                    _decoder.Abort((int)chunkStreamId);
                }

                return ValueTask.CompletedTask;

            case RtmpMessageType.Acknowledgement:
                Control();
                ReadUInt32(payload, "Acknowledgement");
                return ValueTask.CompletedTask;

            case RtmpMessageType.WindowAcknowledgementSize:
                Control();
                _peerWindowSize = ReadUInt32(payload, "Window Acknowledgement Size");
                return ValueTask.CompletedTask;

            case RtmpMessageType.SetPeerBandwidth:
                Control();
                if (payload.Length < 5)
                {
                    throw new RtmpProtocolException("Set Peer Bandwidth is shorter than 5 bytes.");
                }

                return ValueTask.CompletedTask;

            case RtmpMessageType.UserControl:
                Control();
                HandleUserControl(payload);
                return ValueTask.CompletedTask;

            case RtmpMessageType.CommandAmf0:
            case RtmpMessageType.CommandAmf3:
                _context.Metrics.MessageReceived("command");
                return Session.HandleCommandAsync(message, message.Type == RtmpMessageType.CommandAmf3, token);

            case RtmpMessageType.DataAmf0:
            case RtmpMessageType.DataAmf3:
                _context.Metrics.MessageReceived("data");
                return Session.HandleDataAsync(message, message.Type == RtmpMessageType.DataAmf3, token);

            case RtmpMessageType.Audio:
                _context.Metrics.MessageReceived("audio");
                return Session.HandleMediaAsync(message, PublishedMessageKind.Audio, token);

            case RtmpMessageType.Video:
                _context.Metrics.MessageReceived("video");
                return Session.HandleMediaAsync(message, PublishedMessageKind.Video, token);

            default:
                _context.Metrics.MessageReceived("other");
                LogMessageTypeIgnored(Id, message.MessageTypeId);
                return ValueTask.CompletedTask;
        }

        void Control() => _context.Metrics.MessageReceived("control");
    }

    /// <summary>Answers ping requests; other User Control events are accepted and ignored.</summary>
    /// <param name="payload">The event payload.</param>
    /// <exception cref="RtmpProtocolException">The payload is shorter than its event type requires.</exception>
    private void HandleUserControl(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            throw new RtmpProtocolException("A User Control message is shorter than 2 bytes.");
        }

        if ((UserControlEventType)BinaryPrimitives.ReadUInt16BigEndian(payload) == UserControlEventType.PingRequest)
        {
            _writer.WriteUserControl(UserControlEventType.PingResponse, ReadUInt32(payload[2..], "Ping Request"));
        }
    }

    /// <summary>Reads the 4-byte big-endian value of a control message.</summary>
    /// <param name="payload">The payload.</param>
    /// <param name="name">Message name for the error.</param>
    /// <returns>The value.</returns>
    /// <exception cref="RtmpProtocolException">The payload is shorter than 4 bytes.</exception>
    private static uint ReadUInt32(ReadOnlySpan<byte> payload, string name) =>
        payload.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(payload) : throw new RtmpProtocolException($"{name} is shorter than 4 bytes.");

    /// <summary>Sends an Acknowledgement once the peer's window has been received since the last one.</summary>
    private void AcknowledgeIfDue()
    {
        var received = BytesReceived;
        if (_peerWindowSize > 0 && received - _acknowledgedBytes >= _peerWindowSize)
        {
            _writer.WriteAcknowledgement(unchecked((uint)received));
            _acknowledgedBytes = received;
        }
    }

    /// <summary>Moves between Connected and Publishing as streams start and stop publishing.</summary>
    private void UpdatePublishingState()
    {
        var state = State;
        if (Session.PublishingCount > 0 && state == RtmpConnectionState.Connected)
        {
            TransitionTo(RtmpConnectionState.Publishing);
        }
        else if (Session.PublishingCount == 0 && state == RtmpConnectionState.Publishing)
        {
            TransitionTo(RtmpConnectionState.Connected);
        }
    }

    /// <summary>Ends sessions, releases buffers, completes the transport and enters the terminal state.</summary>
    /// <param name="reason">Why the connection closes.</param>
    /// <returns>A task that completes after cleanup.</returns>
    private async Task CloseAsync(RtmpCloseReason reason)
    {
        TransitionTo(RtmpConnectionState.Closing);
        try
        {
            await Session.EndAllAsync(ToEndReason(reason)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogUnexpectedError(ex, Id);
        }

        _decoder.Dispose();
        await CompleteQuietlyAsync(_transport.Input.CompleteAsync()).ConfigureAwait(false);
        await CompleteQuietlyAsync(_transport.Output.CompleteAsync()).ConfigureAwait(false);

        Volatile.Write(ref _closeReason, (int)reason + 1);
        TransitionTo(reason is RtmpCloseReason.ClientDisconnected or RtmpCloseReason.Rejected or RtmpCloseReason.ServerShutdown
            ? RtmpConnectionState.Closed
            : RtmpConnectionState.Failed);
    }

    /// <summary>Awaits a pipe completion, ignoring transport errors raised by flushing on completion.</summary>
    /// <param name="completion">The completion task.</param>
    /// <returns>A task that completes when the pipe is completed.</returns>
    private static async Task CompleteQuietlyAsync(ValueTask completion)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    /// <summary>Counts received bytes.</summary>
    /// <param name="count">Newly received bytes.</param>
    private void AddReceived(long count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _bytesReceived, count);
            _context.Metrics.BytesReceived(count);
        }
    }

    /// <summary>Changes <see cref="State"/> and logs the change at Debug.</summary>
    /// <param name="state">The new state.</param>
    private void TransitionTo(RtmpConnectionState state)
    {
        Volatile.Write(ref _state, (int)state);
        LogStateChanged(Id, state);
    }

    /// <summary>Logs an accepted connection.</summary>
    /// <param name="connectionId">The connection.</param>
    /// <param name="remoteEndPoint">The client's address.</param>
    [LoggerMessage(EventId = 5100, Level = LogLevel.Information, Message = "RTMP connection {ConnectionId} accepted from {RemoteEndPoint}")]
    private partial void LogAccepted(long connectionId, EndPoint? remoteEndPoint);

    /// <summary>Logs a state change.</summary>
    /// <param name="connectionId">The connection.</param>
    /// <param name="state">The new state.</param>
    [LoggerMessage(EventId = 5101, Level = LogLevel.Debug, Message = "RTMP connection {ConnectionId} is {State}")]
    private partial void LogStateChanged(long connectionId, RtmpConnectionState state);

    /// <summary>Logs a completed handshake.</summary>
    /// <param name="connectionId">The connection.</param>
    [LoggerMessage(EventId = 5102, Level = LogLevel.Debug, Message = "RTMP connection {ConnectionId} completed the handshake")]
    private partial void LogHandshakeCompleted(long connectionId);

    /// <summary>Logs a normal close.</summary>
    /// <param name="connectionId">The connection.</param>
    /// <param name="closeReason">Why it closed.</param>
    /// <param name="duration">How long it was open.</param>
    /// <param name="bytesReceived">Bytes received.</param>
    [LoggerMessage(EventId = 5103, Level = LogLevel.Information, Message = "RTMP connection {ConnectionId} closed ({CloseReason}) after {Duration}; {BytesReceived} bytes received")]
    private partial void LogClosed(long connectionId, RtmpCloseReason closeReason, TimeSpan duration, long bytesReceived);

    /// <summary>Logs a close caused by a failure.</summary>
    /// <param name="connectionId">The connection.</param>
    /// <param name="closeReason">Why it closed.</param>
    /// <param name="detail">The failure message; never contains payload data.</param>
    /// <param name="duration">How long it was open.</param>
    /// <param name="bytesReceived">Bytes received.</param>
    [LoggerMessage(EventId = 5104, Level = LogLevel.Warning, Message = "RTMP connection {ConnectionId} closed ({CloseReason}: {Detail}) after {Duration}; {BytesReceived} bytes received")]
    private partial void LogFailed(long connectionId, RtmpCloseReason closeReason, string detail, TimeSpan duration, long bytesReceived);

    /// <summary>Logs a Set Chunk Size from the peer.</summary>
    /// <param name="connectionId">The connection.</param>
    /// <param name="chunkSize">The new inbound chunk size.</param>
    [LoggerMessage(EventId = 5105, Level = LogLevel.Debug, Message = "RTMP connection {ConnectionId} set its chunk size to {ChunkSize}")]
    private partial void LogPeerChunkSize(long connectionId, int chunkSize);

    /// <summary>Logs an ignored message type.</summary>
    /// <param name="connectionId">The connection.</param>
    /// <param name="messageTypeId">The message type.</param>
    [LoggerMessage(EventId = 5106, Level = LogLevel.Debug, Message = "RTMP connection {ConnectionId} sent unsupported message type {MessageTypeId}; ignored")]
    private partial void LogMessageTypeIgnored(long connectionId, byte messageTypeId);

    /// <summary>Logs a disconnect in the middle of a chunk or message.</summary>
    /// <param name="connectionId">The connection.</param>
    [LoggerMessage(EventId = 5107, Level = LogLevel.Debug, Message = "RTMP connection {ConnectionId} ended in the middle of a message; the partial message was discarded")]
    private partial void LogIncompleteMessage(long connectionId);

    /// <summary>Logs an unexpected error.</summary>
    /// <param name="exception">The error.</param>
    /// <param name="connectionId">The connection.</param>
    [LoggerMessage(EventId = 5108, Level = LogLevel.Error, Message = "RTMP connection {ConnectionId} failed unexpectedly")]
    private partial void LogUnexpectedError(Exception exception, long connectionId);
}
