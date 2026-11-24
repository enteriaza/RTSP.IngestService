// Building blocks for RTMP tests: in-memory duplex pipes, an ArrayPool that counts outstanding rentals, a recording
// publish session handler with optional blocking, a key-set authenticator and a factory for RtmpServerContext. They
// let connections run deterministically without sockets.

using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Rtmp;
using RTSP.IngestService.Rtmp.Publishing;

namespace RTSP.IngestService.Tests.Infrastructure;

/// <summary>A pair of connected in-memory duplex pipes.</summary>
internal static class DuplexPipePair
{
    /// <summary>Creates a client and a server end; bytes written by one are read by the other.</summary>
    /// <param name="serverInputOptions">Options of the client-to-server pipe, for example a small pause threshold.</param>
    /// <returns>The two ends.</returns>
    public static (IDuplexPipe Client, IDuplexPipe Server) Create(PipeOptions? serverInputOptions = null)
    {
        var toServer = new Pipe(serverInputOptions ?? PipeOptions.Default);
        var toClient = new Pipe();
        return (new DuplexPipe(toClient.Reader, toServer.Writer), new DuplexPipe(toServer.Reader, toClient.Writer));
    }

    /// <summary>A duplex pipe from a reader and a writer.</summary>
    /// <param name="Input">The inbound reader.</param>
    /// <param name="Output">The outbound writer.</param>
    private sealed record DuplexPipe(PipeReader Input, PipeWriter Output) : IDuplexPipe;
}

/// <summary>An <see cref="ArrayPool{T}"/> that counts arrays rented and not yet returned.</summary>
internal sealed class CountingArrayPool : ArrayPool<byte>
{
    /// <summary>The underlying pool.</summary>
    private readonly ArrayPool<byte> _inner = Create();

    /// <summary>Arrays currently rented.</summary>
    private int _outstanding;

    /// <summary>Gets the number of arrays rented and not returned.</summary>
    public int Outstanding => Volatile.Read(ref _outstanding);

    /// <summary>Rents an array and counts it.</summary>
    /// <param name="minimumLength">Minimum length.</param>
    /// <returns>The array.</returns>
    public override byte[] Rent(int minimumLength)
    {
        Interlocked.Increment(ref _outstanding);
        return _inner.Rent(minimumLength);
    }

    /// <summary>Returns an array and uncounts it.</summary>
    /// <param name="array">The array.</param>
    /// <param name="clearArray">Whether to clear it.</param>
    public override void Return(byte[] array, bool clearArray = false)
    {
        Interlocked.Decrement(ref _outstanding);
        _inner.Return(array, clearArray);
    }
}

/// <summary>One callback observed by <see cref="RecordingPublishSessionHandler"/>.</summary>
/// <param name="Kind"><c>started</c>, <c>message</c> or <c>ended</c>.</param>
/// <param name="Session">The session.</param>
/// <param name="MessageKind">The message kind for <c>message</c> events.</param>
/// <param name="Timestamp">The message timestamp.</param>
/// <param name="Payload">A copy of the message payload.</param>
internal sealed record HandlerEvent(string Kind, PublisherSession Session, PublishedMessageKind? MessageKind = null, uint Timestamp = 0, byte[]? Payload = null);

/// <summary>
/// Records every handler callback, copies and disposes each message, and can block or fail on demand.
/// </summary>
internal sealed class RecordingPublishSessionHandler : IPublishSessionHandler
{
    /// <summary>Events in arrival order.</summary>
    private readonly Channel<HandlerEvent> _events = Channel.CreateUnbounded<HandlerEvent>();

    /// <summary>Gets all events recorded so far.</summary>
    public ConcurrentQueue<HandlerEvent> Events { get; } = new();

    /// <summary>Gets or sets a gate awaited before each message is recorded; <see langword="null"/> for no blocking.</summary>
    public TaskCompletionSource? MessageGate { get; set; }

    /// <summary>Gets a signal completed when a message is waiting at <see cref="MessageGate"/>.</summary>
    public TaskCompletionSource MessageBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets or sets a value indicating whether <see cref="OnMessageAsync"/> throws.</summary>
    public bool FailMessages { get; set; }

    /// <summary>Gets or sets a value indicating whether <see cref="OnMessageAsync"/> keeps messages undisposed.</summary>
    public bool RetainMessages { get; set; }

    /// <summary>Gets the messages kept when <see cref="RetainMessages"/> is set.</summary>
    public ConcurrentQueue<PublishedMessage> Retained { get; } = new();

    /// <summary>Records a started session.</summary>
    /// <param name="session">The session.</param>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>A completed task.</returns>
    public ValueTask OnSessionStartedAsync(PublisherSession session, CancellationToken cancellationToken)
    {
        Record(new HandlerEvent("started", session));
        return ValueTask.CompletedTask;
    }

    /// <summary>Optionally blocks, then copies, records and disposes the message.</summary>
    /// <param name="session">The session.</param>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancels a blocked wait.</param>
    /// <returns>A task that completes when the message has been recorded.</returns>
    public async ValueTask OnMessageAsync(PublisherSession session, PublishedMessage message, CancellationToken cancellationToken)
    {
        if (MessageGate is { } gate)
        {
            MessageBlocked.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (FailMessages)
        {
            throw new InvalidOperationException("Simulated handler failure");
        }

        Record(new HandlerEvent("message", session, message.Kind, message.Timestamp, message.Payload.ToArray()));
        if (RetainMessages)
        {
            Retained.Enqueue(message);
        }
        else
        {
            message.Dispose();
        }
    }

    /// <summary>Records an ended session.</summary>
    /// <param name="session">The session.</param>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>A completed task.</returns>
    public ValueTask OnSessionEndedAsync(PublisherSession session, CancellationToken cancellationToken)
    {
        Record(new HandlerEvent("ended", session));
        return ValueTask.CompletedTask;
    }

    /// <summary>Waits for the next event of <paramref name="kind"/>, skipping others.</summary>
    /// <param name="kind">The event kind.</param>
    /// <returns>The event.</returns>
    public async Task<HandlerEvent> NextAsync(string kind)
    {
        while (true)
        {
            var next = await _events.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WithinSafetyTimeout();
            if (next.Kind == kind)
            {
                return next;
            }
        }
    }

    /// <summary>Records an event.</summary>
    /// <param name="handlerEvent">The event.</param>
    private void Record(HandlerEvent handlerEvent)
    {
        Events.Enqueue(handlerEvent);
        _events.Writer.TryWrite(handlerEvent);
    }
}

/// <summary>Authenticator accepting a fixed set of keys, each mapped to an identity; can be made to throw.</summary>
/// <param name="keys">Accepted stream keys and their identities.</param>
internal sealed class TestAuthenticator(IReadOnlyDictionary<string, string> keys) : IPublisherAuthenticator
{
    /// <summary>Gets the requests received.</summary>
    public ConcurrentQueue<PublishAuthenticationRequest> Requests { get; } = new();

    /// <summary>Gets or sets a value indicating whether authentication throws.</summary>
    public bool Throw { get; set; }

    /// <summary>Accepts known keys.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>The result.</returns>
    public ValueTask<PublishAuthenticationResult> AuthenticateAsync(PublishAuthenticationRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        if (Throw)
        {
            throw new InvalidOperationException("Simulated authenticator failure");
        }

        return ValueTask.FromResult(keys.TryGetValue(request.StreamName, out var identity)
            ? PublishAuthenticationResult.Authorize(identity)
            : PublishAuthenticationResult.Reject("unknown key"));
    }
}

/// <summary>Builds <see cref="RtmpServerContext"/> instances and runs connections over in-memory pipes.</summary>
internal sealed class RtmpTestServer : IDisposable
{
    /// <summary>Owns the meter factory.</summary>
    private readonly ServiceProvider _services;

    /// <summary>Initializes a new instance of the <see cref="RtmpTestServer"/> class.</summary>
    /// <param name="options">RTMP options; defaults are used when <see langword="null"/>.</param>
    /// <param name="timeProvider">Clock; the system clock when <see langword="null"/>.</param>
    /// <param name="sessionHandler">
    /// Creates the publish session handler from the meter and logger factories; <see cref="Handler"/> when <see langword="null"/>.
    /// </param>
    public RtmpTestServer(RtmpOptions? options = null, TimeProvider? timeProvider = null, Func<System.Diagnostics.Metrics.IMeterFactory, ILoggerFactory, IPublishSessionHandler>? sessionHandler = null)
    {
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        MeterFactory = _services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
        Options = options ?? new RtmpOptions();
        TimeProvider = timeProvider ?? TimeProvider.System;
        LoggerFactory = new LoggerFactory([new FakeLoggerProvider(Logs)]);
        SessionHandler = sessionHandler?.Invoke(MeterFactory, LoggerFactory) ?? Handler;
        Context = new RtmpServerContext(Options, Authenticator, SessionHandler, Registry, new RtmpMetrics(MeterFactory), TimeProvider, LoggerFactory, Pool);
    }

    /// <summary>Gets the publish session handler the server uses.</summary>
    public IPublishSessionHandler SessionHandler { get; }

    /// <summary>The stream key accepted by <see cref="Authenticator"/>.</summary>
    public const string StreamKey = "secret-key-0001";

    /// <summary>The identity assigned to <see cref="StreamKey"/>.</summary>
    public const string StreamIdentity = "camera1";

    /// <summary>Gets the meter factory, for metric collectors.</summary>
    public System.Diagnostics.Metrics.IMeterFactory MeterFactory { get; }

    /// <summary>Gets the options.</summary>
    public RtmpOptions Options { get; }

    /// <summary>Gets the clock.</summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>Gets the captured logs.</summary>
    public FakeLogCollector Logs { get; } = new();

    /// <summary>Gets the logger factory.</summary>
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>
    /// Gets the authenticator, accepting <see cref="StreamKey"/>, <c>secret-key-0002</c> and <c>secret-key-0003</c>
    /// (identities <c>camera1</c> to <c>camera3</c>).
    /// </summary>
    public TestAuthenticator Authenticator { get; } = new(new Dictionary<string, string>
    {
        [StreamKey] = StreamIdentity,
        ["secret-key-0002"] = "camera2",
        ["secret-key-0003"] = "camera3",
    });

    /// <summary>Gets the handler.</summary>
    public RecordingPublishSessionHandler Handler { get; } = new();

    /// <summary>Gets the registry.</summary>
    public PublishedStreamRegistry Registry { get; } = new();

    /// <summary>Gets the counting pool used for payloads.</summary>
    public CountingArrayPool Pool { get; } = new();

    /// <summary>Gets the server context.</summary>
    public RtmpServerContext Context { get; }

    /// <summary>Starts a connection over in-memory pipes that shuts down when the test is cancelled.</summary>
    /// <param name="serverInputOptions">Options of the client-to-server pipe.</param>
    /// <returns>The connection, its run task and a client on the other end.</returns>
    public (RtmpConnection Connection, Task Run, RtmpTestClient Client) Start(PipeOptions? serverInputOptions = null) =>
        StartWithShutdown(TestContext.Current.CancellationToken, serverInputOptions);

    /// <summary>Starts a connection over in-memory pipes.</summary>
    /// <param name="shutdown">Cancels the connection (server shutdown).</param>
    /// <param name="serverInputOptions">Options of the client-to-server pipe.</param>
    /// <returns>The connection, its run task and a client on the other end.</returns>
    public (RtmpConnection Connection, Task Run, RtmpTestClient Client) StartWithShutdown(CancellationToken shutdown, PipeOptions? serverInputOptions = null)
    {
        var (client, server) = DuplexPipePair.Create(serverInputOptions);
        var connection = new RtmpConnection(1, null, server, Context);
        var run = connection.RunAsync(shutdown);
        return (connection, run, new RtmpTestClient(client));
    }

    /// <summary>Creates a TCP listener sharing this server's options and dependencies; payloads use the shared pool.</summary>
    /// <returns>The listener, not started.</returns>
    public RtmpListener CreateListener() =>
        new(Microsoft.Extensions.Options.Options.Create(Options), Authenticator, SessionHandler, Registry, Context.Metrics, TimeProvider, LoggerFactory);

    /// <summary>Disposes the logger factory and meter factory.</summary>
    public void Dispose()
    {
        LoggerFactory.Dispose();
        _services.Dispose();
    }
}
