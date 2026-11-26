// Building blocks for transcoding tests that do not need FFmpeg: a scripted in-memory transcoder process (echoing its
// FLV input as its output, crashing, exiting at once, stalling or failing to start), and a harness that drives
// TranscodingPublishSessionHandler directly with pooled messages and fixture FLV tags.

using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Transcoding;
using FixtureTag = RTSP.IngestService.Tests.Media.FlvTag;

namespace RTSP.IngestService.Tests.Transcoding;

/// <summary>How a <see cref="FakeTranscoder"/> behaves.</summary>
internal enum FakeTranscoderBehavior
{
    /// <summary>Copies its input to its output and exits with 0 when the input closes.</summary>
    Echo,

    /// <summary>Reads input until <see cref="FakeTranscoderFactory.CrashAfterBytes"/>, then exits with 1 and decoder diagnostics.</summary>
    Crash,

    /// <summary>Exits with 1 immediately, as a decoder that rejects the stream configuration.</summary>
    ExitAtStart,

    /// <summary>Never reads its input and never exits until killed.</summary>
    Stall,

    /// <summary>The process cannot be started.</summary>
    FailToStart,
}

/// <summary>An in-memory transcoder process.</summary>
internal sealed class FakeTranscoder : ITranscoderProcess
{
    /// <summary>The input pipe; small, so a process that does not read blocks its writer quickly.</summary>
    private readonly Pipe _input = new(new PipeOptions(pauseWriterThreshold: 16 * 1024, resumeWriterThreshold: 8 * 1024, useSynchronizationContext: false));

    /// <summary>The output pipe.</summary>
    private readonly Pipe _output = new(new PipeOptions(useSynchronizationContext: false));

    /// <summary>Cancelled by <see cref="Kill"/>.</summary>
    private readonly CancellationTokenSource _kill = new();

    /// <summary>The behaviour.</summary>
    private readonly FakeTranscoderBehavior _behavior;

    /// <summary>Input bytes after which <see cref="FakeTranscoderBehavior.Crash"/> exits.</summary>
    private readonly long _crashAfter;

    /// <summary>Input bytes read.</summary>
    private long _bytesRead;

    /// <summary>The diagnostics.</summary>
    private volatile string _diagnostics = string.Empty;

    /// <summary>1 once killed.</summary>
    private int _killed;

    /// <summary>Initializes a new instance of the <see cref="FakeTranscoder"/> class.</summary>
    /// <param name="arguments">The argument list it was started with.</param>
    /// <param name="behavior">The behaviour.</param>
    /// <param name="crashAfter">Input bytes after which a crashing process exits.</param>
    public FakeTranscoder(IReadOnlyList<string> arguments, FakeTranscoderBehavior behavior, long crashAfter)
    {
        Arguments = arguments;
        _behavior = behavior;
        _crashAfter = crashAfter;
        Input = _input.Writer.AsStream();
        Output = _output.Reader.AsStream();
        Exited = Task.Run(RunAsync);
    }

    /// <summary>Gets the argument list.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <inheritdoc />
    public Stream Input { get; }

    /// <inheritdoc />
    public Stream Output { get; }

    /// <inheritdoc />
    public Task<int> Exited { get; }

    /// <inheritdoc />
    public string Diagnostics => _diagnostics;

    /// <summary>Gets the input bytes read.</summary>
    public long BytesRead => Interlocked.Read(ref _bytesRead);

    /// <summary>Gets a value indicating whether the process was killed.</summary>
    public bool Killed => Volatile.Read(ref _killed) == 1;

    /// <summary>Gets a value indicating whether the input was closed for a flush.</summary>
    public bool InputCompleted { get; private set; }

    /// <inheritdoc />
    public void CompleteInput()
    {
        InputCompleted = true;
        Input.Dispose();
    }

    /// <inheritdoc />
    public void Kill()
    {
        if (Exited.IsCompleted)
        {
            return;
        }

        Volatile.Write(ref _killed, 1);
        try
        {
            _kill.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Kill();
        await Exited.ConfigureAwait(false);
        Input.Dispose();
        await Output.DisposeAsync().ConfigureAwait(false);
        _kill.Dispose();
    }

    /// <summary>Runs the behaviour.</summary>
    /// <returns>The exit code.</returns>
    private async Task<int> RunAsync()
    {
        var token = _kill.Token;
        try
        {
            switch (_behavior)
            {
                case FakeTranscoderBehavior.ExitAtStart:
                    _diagnostics = "Invalid data found when processing input";
                    return await ExitAsync(1).ConfigureAwait(false);
                case FakeTranscoderBehavior.Stall:
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                    return 0;
            }

            while (true)
            {
                var result = await _input.Reader.ReadAsync(token).ConfigureAwait(false);
                var buffer = result.Buffer;
                Interlocked.Add(ref _bytesRead, buffer.Length);
                if (_behavior == FakeTranscoderBehavior.Echo)
                {
                    foreach (var segment in buffer)
                    {
                        await _output.Writer.WriteAsync(segment, token).ConfigureAwait(false);
                    }
                }

                _input.Reader.AdvanceTo(buffer.End);
                if (_behavior == FakeTranscoderBehavior.Crash && BytesRead >= _crashAfter)
                {
                    _diagnostics = "Error while decoding stream #0:0: Invalid data found when processing input";
                    return await ExitAsync(1).ConfigureAwait(false);
                }

                if (result.IsCompleted)
                {
                    return await ExitAsync(0).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return await ExitAsync(-1).ConfigureAwait(false);
        }
    }

    /// <summary>Closes both pipes.</summary>
    /// <param name="code">The exit code.</param>
    /// <returns>The exit code.</returns>
    private async Task<int> ExitAsync(int code)
    {
        await _input.Reader.CompleteAsync().ConfigureAwait(false);
        await _output.Writer.CompleteAsync().ConfigureAwait(false);
        return code;
    }
}

/// <summary>Starts <see cref="FakeTranscoder"/> instances.</summary>
internal sealed class FakeTranscoderFactory : ITranscoderProcessFactory
{
    /// <summary>Starts so far.</summary>
    private int _starts;

    /// <summary>Gets the processes started, in order.</summary>
    public ConcurrentQueue<FakeTranscoder> Started { get; } = new();

    /// <summary>Gets or sets the behaviour of every process, unless <see cref="BehaviorForStart"/> is set.</summary>
    public FakeTranscoderBehavior Behavior { get; set; }

    /// <summary>Gets or sets the behaviour by zero-based start index.</summary>
    public Func<int, FakeTranscoderBehavior>? BehaviorForStart { get; set; }

    /// <summary>Gets or sets the input bytes after which a crashing process exits.</summary>
    public long CrashAfterBytes { get; set; }

    /// <summary>Gets the number of start attempts.</summary>
    public int Starts => Volatile.Read(ref _starts);

    /// <inheritdoc />
    public ITranscoderProcess Start(IReadOnlyList<string> arguments)
    {
        var index = Interlocked.Increment(ref _starts) - 1;
        var behavior = BehaviorForStart?.Invoke(index) ?? Behavior;
        if (behavior == FakeTranscoderBehavior.FailToStart)
        {
            throw new TranscodingFailedException(TranscodingStage.Process, true, "The fake transcoder could not be started.");
        }

        var process = new FakeTranscoder([.. arguments], behavior, CrashAfterBytes);
        Started.Enqueue(process);
        return process;
    }
}

/// <summary>Drives a <see cref="TranscodingPublishSessionHandler"/> with a fake transcoder and a recording downstream stage.</summary>
internal sealed class TranscodingHarness : IDisposable
{
    /// <summary>Connection IDs for new sessions.</summary>
    private static long _nextConnection;

    /// <summary>Owns the meter factory.</summary>
    private readonly ServiceProvider _services;

    /// <summary>The logger factory.</summary>
    private readonly LoggerFactory _loggerFactory;

    /// <summary>Initializes a new instance of the <see cref="TranscodingHarness"/> class.</summary>
    /// <param name="configure">Adjusts the transcoding options.</param>
    /// <param name="media">Media options; defaults when <see langword="null"/>.</param>
    public TranscodingHarness(Action<TranscodingOptions>? configure = null, MediaOptions? media = null)
    {
        configure?.Invoke(Options);
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        MeterFactory = _services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
        _loggerFactory = new LoggerFactory([new FakeLoggerProvider(Logs)]);
        Handler = new TranscodingPublishSessionHandler(
            Microsoft.Extensions.Options.Options.Create(Options),
            Microsoft.Extensions.Options.Options.Create(media ?? new MediaOptions()),
            Microsoft.Extensions.Options.Options.Create(new HlsOptions { TargetSegmentDuration = TimeSpan.FromSeconds(2) }),
            Downstream,
            Factory,
            new TranscodingMetrics(MeterFactory),
            _loggerFactory.CreateLogger<TranscodingPublishSessionHandler>());
    }

    /// <summary>Gets the transcoding options.</summary>
    public TranscodingOptions Options { get; } = new();

    /// <summary>Gets the transcoder factory.</summary>
    public FakeTranscoderFactory Factory { get; } = new();

    /// <summary>Gets the recording downstream stage.</summary>
    public RecordingPublishSessionHandler Downstream { get; } = new();

    /// <summary>Gets the handler under test.</summary>
    public TranscodingPublishSessionHandler Handler { get; }

    /// <summary>Gets the meter factory.</summary>
    public System.Diagnostics.Metrics.IMeterFactory MeterFactory { get; }

    /// <summary>Gets the captured logs.</summary>
    public FakeLogCollector Logs { get; } = new();

    /// <summary>Gets the pool payloads are rented from.</summary>
    public CountingArrayPool Pool { get; } = new();

    /// <summary>Creates a publisher session.</summary>
    /// <returns>The session.</returns>
    public static PublisherSession NewSession() =>
        new(Interlocked.Increment(ref _nextConnection), null, "live", "camera", "live", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    /// <summary>Creates a pooled message.</summary>
    /// <param name="kind">Its kind.</param>
    /// <param name="timestamp">Its RTMP timestamp.</param>
    /// <param name="payload">Its payload.</param>
    /// <returns>The message.</returns>
    public PublishedMessage Message(PublishedMessageKind kind, uint timestamp, ReadOnlySpan<byte> payload)
    {
        var buffer = new PooledBuffer(Pool);
        buffer.EnsureCapacity(Math.Max(payload.Length, 1));
        buffer.Append(new ReadOnlySequence<byte>(payload.ToArray()));
        var type = kind switch
        {
            PublishedMessageKind.Audio => (byte)8,
            PublishedMessageKind.Video => (byte)9,
            _ => (byte)18,
        };
        return new PublishedMessage(kind, type, timestamp, buffer);
    }

    /// <summary>Starts a session.</summary>
    /// <returns>The session.</returns>
    public async Task<PublisherSession> StartAsync()
    {
        var session = NewSession();
        await Handler.OnSessionStartedAsync(session, TestContext.Current.CancellationToken);
        return session;
    }

    /// <summary>Sends a message.</summary>
    /// <param name="session">The session.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="timestamp">The RTMP timestamp.</param>
    /// <param name="payload">The payload.</param>
    /// <param name="cancellationToken">Cancellation; the test's token when omitted.</param>
    /// <returns>A task.</returns>
    public Task SendAsync(PublisherSession session, PublishedMessageKind kind, uint timestamp, byte[] payload, CancellationToken? cancellationToken = null) =>
        Handler.OnMessageAsync(session, Message(kind, timestamp, payload), cancellationToken ?? TestContext.Current.CancellationToken).AsTask();

    /// <summary>Sends fixture tags in order.</summary>
    /// <param name="session">The session.</param>
    /// <param name="tags">The tags.</param>
    /// <param name="timestampShift">Added to every timestamp.</param>
    /// <returns>A task.</returns>
    public async Task SendTagsAsync(PublisherSession session, IEnumerable<FixtureTag> tags, uint timestampShift = 0)
    {
        foreach (var tag in tags)
        {
            await SendAsync(session, Kind(tag), tag.Timestamp + timestampShift, tag.Body);
        }
    }

    /// <summary>Ends a session.</summary>
    /// <param name="session">The session.</param>
    /// <param name="reason">Why it ended.</param>
    /// <returns>A task.</returns>
    public async Task EndAsync(PublisherSession session, PublishEndReason reason = PublishEndReason.Unpublished)
    {
        session.TryEnd(reason, DateTimeOffset.UnixEpoch);
        await Handler.OnSessionEndedAsync(session, TestContext.Current.CancellationToken).AsTask().WithinSafetyTimeout();
    }

    /// <summary>Gets the media messages delivered downstream for a session.</summary>
    /// <param name="session">The session.</param>
    /// <param name="kind">The kind.</param>
    /// <returns>The messages.</returns>
    public IReadOnlyList<HandlerEvent> Delivered(PublisherSession session, PublishedMessageKind kind) =>
        [.. Downstream.Events.Where(e => e.Kind == "message" && e.MessageKind == kind && ReferenceEquals(e.Session, session))];

    /// <summary>Gets the event IDs logged so far.</summary>
    /// <returns>The IDs.</returns>
    public IReadOnlyList<int> EventIds() => [.. Logs.GetSnapshot().Select(static r => r.Id.Id)];

    /// <summary>Disposes the logger and meter factories.</summary>
    public void Dispose()
    {
        _loggerFactory.Dispose();
        _services.Dispose();
    }

    /// <summary>Maps a fixture tag type to a message kind.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The kind.</returns>
    public static PublishedMessageKind Kind(FixtureTag tag) => tag.Type switch
    {
        8 => PublishedMessageKind.Audio,
        9 => PublishedMessageKind.Video,
        _ => PublishedMessageKind.Data,
    };
}
