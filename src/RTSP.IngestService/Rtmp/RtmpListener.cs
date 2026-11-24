// RTMP ingest TCP listener, hosted as a BackgroundService. StartAsync binds the socket, so a bind failure fails host
// startup and readiness (ApplicationStarted) is never reported before the port is open. ExecuteAsync accepts
// connections, enforces the connection limit and runs each connection as an owned task; on shutdown it stops
// accepting, cancels every connection and waits for all of them to finish cleanup.

using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Streams;

namespace RTSP.IngestService.Rtmp;

/// <summary>
/// Accepts RTMP publisher connections on <see cref="RtmpOptions.ListenAddress"/>:<see cref="RtmpOptions.Port"/>.
/// </summary>
/// <remarks>
/// <para>
/// Connections run as asynchronous tasks on the thread pool; there is no thread per connection. Connections beyond
/// <see cref="RtmpOptions.MaxConnections"/> are closed as soon as they are accepted, and the operating system's
/// listen backlog is capped at <see cref="ListenBacklog"/>, so no connection queue grows without bound.
/// </para>
/// <para>
/// Shutdown policy: when the host stops, the listening socket is closed first, then every active connection is
/// cancelled. Each connection ends its publishing sessions with <see cref="PublishEndReason.ServerShutdown"/>,
/// notifies the <see cref="IPublishSessionHandler"/> and closes its socket. <see cref="BackgroundService.StopAsync"/>
/// waits for this within the host shutdown timeout; publishers are disconnected rather than drained because a
/// live publisher never stops on its own.
/// </para>
/// <para>
/// A failure in one connection never affects the listener or other connections. An unexpected failure of the
/// accept loop itself faults <see cref="BackgroundService.ExecuteTask"/>, which stops the host and is reported as a
/// failed lifecycle.
/// </para>
/// </remarks>
public sealed partial class RtmpListener : BackgroundService
{
    /// <summary>Maximum length of the operating system's pending-connection queue.</summary>
    public const int ListenBacklog = 128;

    /// <summary>Pause after an accept failure that is not a per-connection error, to avoid a tight failure loop.</summary>
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>Active connections by identifier.</summary>
    private readonly ConcurrentDictionary<long, ConnectionEntry> _connections = new();

    /// <summary>The RTMP options.</summary>
    private readonly IOptions<RtmpOptions> _options;

    /// <summary>Publisher authenticator.</summary>
    private readonly IPublisherAuthenticator _authenticator;

    /// <summary>Publish session handler.</summary>
    private readonly IPublishSessionHandler _handler;

    /// <summary>Active session registry.</summary>
    private readonly PublishedStreamRegistry _registry;

    /// <summary>Metrics.</summary>
    private readonly RtmpMetrics _metrics;

    /// <summary>Clock.</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>Logger factory for connections.</summary>
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Stream lifecycle event publisher, if any.</summary>
    private readonly IStreamEventPublisher? _events;

    /// <summary>Listener logger.</summary>
    private readonly ILogger<RtmpListener> _logger;

    /// <summary>The bound socket, set by <see cref="StartAsync"/>.</summary>
    private Socket? _socket;

    /// <summary>Shared connection dependencies, set by <see cref="StartAsync"/>.</summary>
    private RtmpServerContext? _context;

    /// <summary>Last assigned connection identifier.</summary>
    private long _lastConnectionId;

    /// <summary>Admitted connections that have not finished.</summary>
    private int _activeConnections;

    /// <summary>Whether the socket is bound and accepting; see <see cref="IsListening"/>.</summary>
    private volatile bool _listening;

    /// <summary>Initializes a new instance of the <see cref="RtmpListener"/> class.</summary>
    /// <param name="options">The RTMP options.</param>
    /// <param name="authenticator">Publisher authenticator.</param>
    /// <param name="handler">Publish session handler.</param>
    /// <param name="registry">Active session registry.</param>
    /// <param name="metrics">Metrics.</param>
    /// <param name="timeProvider">Clock for timeouts.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="events">Receives stream lifecycle events, or <see langword="null"/> for none.</param>
    internal RtmpListener(
        IOptions<RtmpOptions> options,
        IPublisherAuthenticator authenticator,
        IPublishSessionHandler handler,
        PublishedStreamRegistry registry,
        RtmpMetrics metrics,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        IStreamEventPublisher? events = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options;
        _authenticator = authenticator;
        _handler = handler;
        _registry = registry;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory;
        _events = events;
        _logger = loggerFactory.CreateLogger<RtmpListener>();
    }

    /// <summary>Gets the bound local endpoint (with the actual port when <see cref="RtmpOptions.Port"/> is 0), or <see langword="null"/> before start.</summary>
    public EndPoint? LocalEndPoint { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the listening socket is bound and accepting connections: <see langword="true"/>
    /// from a successful bind in <see cref="StartAsync"/> until shutdown begins closing the socket.
    /// </summary>
    public bool IsListening => _listening;

    /// <summary>Gets the number of admitted connections that have not finished.</summary>
    public int ActiveConnectionCount => Volatile.Read(ref _activeConnections);

    /// <summary>Binds and starts listening, then starts the accept loop.</summary>
    /// <param name="cancellationToken">The host startup token.</param>
    /// <returns>A task that completes once the socket is listening.</returns>
    /// <exception cref="InvalidOperationException">The socket could not be created or bound; the inner exception has the cause.</exception>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var endPoint = new IPEndPoint(IPAddress.Parse(options.ListenAddress), options.Port);
        Socket? socket = null;
        try
        {
            socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            if (endPoint.Address.Equals(IPAddress.IPv6Any))
            {
                socket.DualMode = true;
            }

            if (OperatingSystem.IsWindows())
            {
                socket.ExclusiveAddressUse = true;
            }

            socket.Bind(endPoint);
            socket.Listen(ListenBacklog);
        }
        catch (Exception ex) when (ex is SocketException or NotSupportedException)
        {
            socket?.Dispose();
            LogBindFailed(ex, endPoint);
            throw new InvalidOperationException($"The RTMP listener could not bind to {endPoint}: {ex.Message}", ex);
        }

        _socket = socket;
        LocalEndPoint = socket.LocalEndPoint;
        _context = new RtmpServerContext(options, _authenticator, _handler, _registry, _metrics, _timeProvider, _loggerFactory, ArrayPool<byte>.Shared, _events);
        LogListening(LocalEndPoint, options.MaxConnections);
        _listening = true;

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes the listening socket if the accept loop never ran, then disposes the base service.</summary>
    public override void Dispose()
    {
        _listening = false;
        _socket?.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Accepts connections until <paramref name="stoppingToken"/> is cancelled, then closes the listening socket,
    /// cancels all connections and waits for them to finish.
    /// </summary>
    /// <param name="stoppingToken">Cancelled when the host stops.</param>
    /// <returns>A task that completes when every connection has finished.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var socket = _socket ?? throw new InvalidOperationException("The listener has not been started.");
        var context = _context!;
        using var connectionsSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = await socket.AcceptAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
                {
                    continue;
                }
                catch (SocketException ex)
                {
                    LogAcceptFailed(ex.SocketErrorCode);
                    try
                    {
                        await Task.Delay(AcceptRetryDelay, _timeProvider, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                Admit(client, context, connectionsSource.Token);
            }
        }
        finally
        {
            _listening = false;
            socket.Dispose();
            var active = _connections.Values.Select(static e => e.Task).ToArray();
            LogStopping(active.Length);
            await connectionsSource.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(active).ConfigureAwait(false);
            LogStopped();
        }
    }

    /// <summary>Starts a connection, or closes the socket if the connection limit is reached.</summary>
    /// <param name="client">The accepted socket; ownership passes to this method.</param>
    /// <param name="context">Shared connection dependencies.</param>
    /// <param name="cancellationToken">Cancelled at shutdown.</param>
    private void Admit(Socket client, RtmpServerContext context, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _activeConnections) > context.Options.MaxConnections)
        {
            Interlocked.Decrement(ref _activeConnections);
            _metrics.ConnectionRejected();
            LogConnectionRejected(SafeRemoteEndPoint(client), context.Options.MaxConnections);
            client.Dispose();
            return;
        }

        _metrics.ConnectionAccepted();
        var id = Interlocked.Increment(ref _lastConnectionId);
        var entry = new ConnectionEntry();
        _connections[id] = entry;
        entry.Task = RunConnectionAsync(id, client, context, cancellationToken);
    }

    /// <summary>Returns the socket's remote endpoint, or <see langword="null"/> if it is no longer available.</summary>
    /// <param name="socket">The socket.</param>
    /// <returns>The remote endpoint.</returns>
    private static EndPoint? SafeRemoteEndPoint(Socket socket)
    {
        try
        {
            return socket.RemoteEndPoint;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Runs one connection and always releases its slot; never faults.</summary>
    /// <param name="id">The connection identifier.</param>
    /// <param name="client">The socket.</param>
    /// <param name="context">Shared connection dependencies.</param>
    /// <param name="cancellationToken">Cancelled at shutdown.</param>
    /// <returns>A task that completes when the connection has been cleaned up.</returns>
    private async Task RunConnectionAsync(long id, Socket client, RtmpServerContext context, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield();
            client.NoDelay = true;
            var transport = new SocketTransport(client);
            await using (transport.ConfigureAwait(false))
            {
                using var connection = new RtmpConnection(id, SafeRemoteEndPoint(client), transport, context);
                await connection.RunAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LogConnectionCrashed(ex, id);
        }
        finally
        {
            _connections.TryRemove(id, out _);
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    /// <summary>Logs that the listener is bound.</summary>
    /// <param name="endPoint">The bound endpoint.</param>
    /// <param name="maxConnections">The connection limit.</param>
    [LoggerMessage(EventId = 5000, Level = LogLevel.Information, Message = "RTMP listener bound to {EndPoint}; at most {MaxConnections} concurrent connections")]
    private partial void LogListening(EndPoint? endPoint, int maxConnections);

    /// <summary>Logs a bind failure.</summary>
    /// <param name="exception">The failure.</param>
    /// <param name="endPoint">The endpoint that could not be bound.</param>
    [LoggerMessage(EventId = 5001, Level = LogLevel.Error, Message = "RTMP listener could not bind to {EndPoint}")]
    private partial void LogBindFailed(Exception exception, IPEndPoint endPoint);

    /// <summary>Logs a connection closed by the connection limit.</summary>
    /// <param name="remoteEndPoint">The client's address.</param>
    /// <param name="maxConnections">The limit.</param>
    [LoggerMessage(EventId = 5002, Level = LogLevel.Warning, Message = "Rejected RTMP connection from {RemoteEndPoint}: the limit of {MaxConnections} concurrent connections is reached")]
    private partial void LogConnectionRejected(EndPoint? remoteEndPoint, int maxConnections);

    /// <summary>Logs an accept failure.</summary>
    /// <param name="socketError">The socket error.</param>
    [LoggerMessage(EventId = 5003, Level = LogLevel.Warning, Message = "Accepting an RTMP connection failed with socket error {SocketError}; retrying")]
    private partial void LogAcceptFailed(System.Net.Sockets.SocketError socketError);

    /// <summary>Logs the start of listener shutdown.</summary>
    /// <param name="activeConnections">Connections being closed.</param>
    [LoggerMessage(EventId = 5004, Level = LogLevel.Information, Message = "RTMP listener stopped accepting connections; closing {ActiveConnections} active connection(s)")]
    private partial void LogStopping(int activeConnections);

    /// <summary>Logs the end of listener shutdown.</summary>
    [LoggerMessage(EventId = 5005, Level = LogLevel.Information, Message = "RTMP listener stopped; all connections are closed")]
    private partial void LogStopped();

    /// <summary>Logs a failure outside the connection's own error handling.</summary>
    /// <param name="exception">The failure.</param>
    /// <param name="connectionId">The connection.</param>
    [LoggerMessage(EventId = 5006, Level = LogLevel.Error, Message = "RTMP connection {ConnectionId} terminated unexpectedly")]
    private partial void LogConnectionCrashed(Exception exception, long connectionId);

    /// <summary>The task of an admitted connection.</summary>
    private sealed class ConnectionEntry
    {
        /// <summary>Gets or sets the connection task; assigned immediately after the entry is registered.</summary>
        public Task Task { get; set; } = Task.CompletedTask;
    }
}
