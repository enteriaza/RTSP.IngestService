// Tests for the bounded asynchronous log sink: ordered delivery, waiting instead of dropping when the queue is full,
// draining and disposing the wrapped sinks on disposal, the drain timeout, events logged after disposal, and the batch
// callback; and for the buffered console sink it drives.

using System.Collections.Concurrent;
using RTSP.IngestService.Logging;
using RTSP.IngestService.Tests.Infrastructure;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace RTSP.IngestService.Tests.Logging;

/// <summary>
/// Tests for <see cref="BoundedAsyncSink"/>.
/// </summary>
public sealed class BoundedAsyncSinkTests
{
    /// <summary>Verifies that events from one thread are written in order and all are written by disposal.</summary>
    [Fact]
    public void Events_AreWrittenInOrder()
    {
        var target = new RecordingSink();
        using (var sink = new BoundedAsyncSink(Wrap(target), capacity: 16))
        {
            for (var i = 0; i < 1000; i++)
            {
                sink.Emit(Event(i));
            }
        }

        Assert.Equal(Enumerable.Range(0, 1000), target.Numbers);
        Assert.True(target.Disposed);
    }

    /// <summary>
    /// Verifies the overflow policy: while the queue is full a log call waits, and once the sinks make progress every
    /// event, including the one that waited, is written.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task FullQueue_MakesTheCallerWaitAndDropsNothing()
    {
        using var gate = new ManualResetEventSlim();
        var target = new RecordingSink(gate);
        using var sink = new BoundedAsyncSink(Wrap(target), capacity: 2);

        sink.Emit(Event(0));
        await Eventually.WaitUntilAsync(() => target.Writing, "the writer is blocked in the sink");
        sink.Emit(Event(1));
        sink.Emit(Event(2));
        var waiting = Task.Run(() => sink.Emit(Event(3)), TestContext.Current.CancellationToken);

        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(2, sink.Count);

        gate.Set();
        await waiting.WithinSafetyTimeout();
        await Eventually.WaitUntilAsync(() => target.Numbers.Count == 4, "every event is written");
        Assert.Equal([0, 1, 2, 3], target.Numbers);
    }

    /// <summary>Verifies that disposal waits for the queued events to be written before disposing the wrapped sinks.</summary>
    [Fact]
    public void Dispose_WritesEveryQueuedEventFirst()
    {
        var target = new RecordingSink(delay: TimeSpan.FromMilliseconds(1));
        var sink = new BoundedAsyncSink(Wrap(target), capacity: 100);
        for (var i = 0; i < 100; i++)
        {
            sink.Emit(Event(i));
        }

        sink.Dispose();

        Assert.Equal(100, target.Numbers.Count);
        Assert.True(target.Disposed);
    }

    /// <summary>
    /// Verifies that disposal returns after the drain timeout when the sinks are blocked, reports the loss and still
    /// disposes the wrapped sinks.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Dispose_WithBlockedSinks_ReturnsAfterTheDrainTimeout()
    {
        using var gate = new ManualResetEventSlim();
        var target = new RecordingSink(gate);
        var sink = new BoundedAsyncSink(Wrap(target), capacity: 4, drainTimeout: TimeSpan.FromMilliseconds(100));
        sink.Emit(Event(0));
        await Eventually.WaitUntilAsync(() => target.Writing, "the writer is blocked in the sink");
        sink.Emit(Event(1));

        await Task.Run(sink.Dispose, TestContext.Current.CancellationToken).WithinSafetyTimeout();

        Assert.True(target.Disposed);
        gate.Set();
    }

    /// <summary>Verifies that an event logged after disposal is ignored without an exception.</summary>
    [Fact]
    public void Emit_AfterDispose_IsIgnored()
    {
        var target = new RecordingSink();
        var sink = new BoundedAsyncSink(Wrap(target));
        sink.Dispose();

        sink.Emit(Event(1));
        sink.Dispose();

        Assert.Empty(target.Numbers);
    }

    /// <summary>
    /// Verifies that the batch callback runs after the events of each batch, with at most <see cref="BoundedAsyncSink.MaxBatch"/>
    /// events per batch, and after the last event.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task BatchCallback_FollowsEveryBatch()
    {
        using var gate = new ManualResetEventSlim();
        var target = new RecordingSink(gate);
        var batches = new ConcurrentQueue<int>();
        var sink = new BoundedAsyncSink(Wrap(target), capacity: 3 * BoundedAsyncSink.MaxBatch, batchWritten: () => batches.Enqueue(target.Numbers.Count));
        sink.Emit(Event(0));
        await Eventually.WaitUntilAsync(() => target.Writing, "the writer is blocked in the sink");
        for (var i = 1; i <= 2500; i++)
        {
            sink.Emit(Event(i));
        }

        gate.Set();
        sink.Dispose();

        Assert.Equal(2501, target.Numbers.Count);
        Assert.Equal(target.Numbers.Count, batches.Last());
        Assert.All(batches.Zip(batches.Skip(1)), static pair => Assert.InRange(pair.Second - pair.First, 1, BoundedAsyncSink.MaxBatch));
        Assert.True(batches.First() <= BoundedAsyncSink.MaxBatch);
    }

    /// <summary>Verifies that the buffered console sink writes nothing until flushed, then everything in order once.</summary>
    [Fact]
    public void BufferedConsoleSink_WritesOnFlush()
    {
        using var output = new StringWriter();
        var sink = new BufferedConsoleSink(new Serilog.Formatting.Display.MessageTemplateTextFormatter("{Message}{NewLine}"), () => output);
        sink.Emit(Event(1));
        sink.Emit(Event(2));
        Assert.Empty(output.ToString());

        sink.Flush();
        sink.Flush();
        Assert.Equal($"Event 1{Environment.NewLine}Event 2{Environment.NewLine}", output.ToString());

        sink.Emit(Event(3));
        sink.Dispose();
        Assert.EndsWith($"Event 3{Environment.NewLine}", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Verifies that a batch that cannot be written is discarded rather than kept growing.</summary>
    [Fact]
    public void BufferedConsoleSink_DiscardsABatchThatCannotBeWritten()
    {
        using var output = new StringWriter();
        var failing = true;
        var sink = new BufferedConsoleSink(new Serilog.Formatting.Display.MessageTemplateTextFormatter("{Message}{NewLine}"), () => failing ? throw new IOException("closed") : output);
        sink.Emit(Event(1));

        Assert.Throws<IOException>(sink.Flush);
        failing = false;
        sink.Emit(Event(2));
        sink.Flush();

        Assert.Equal($"Event 2{Environment.NewLine}", output.ToString());
    }

    /// <summary>Wraps <paramref name="target"/> in a logger that accepts every level.</summary>
    /// <param name="target">The sink.</param>
    /// <returns>The logger.</returns>
    private static Logger Wrap(ILogEventSink target) => new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(target).CreateLogger();

    /// <summary>Creates an event carrying <paramref name="number"/>.</summary>
    /// <param name="number">The number.</param>
    /// <returns>The event.</returns>
    private static LogEvent Event(int number) => new(
        DateTimeOffset.UnixEpoch,
        LogEventLevel.Information,
        exception: null,
        new MessageTemplateParser().Parse("Event {Number}"),
        [new LogEventProperty("Number", new ScalarValue(number))]);

    /// <summary>Records the numbers of the events it receives, optionally blocking on a gate or delaying each write.</summary>
    /// <param name="gate">When set, each write waits for it.</param>
    /// <param name="delay">A delay per write.</param>
    private sealed class RecordingSink(ManualResetEventSlim? gate = null, TimeSpan delay = default) : ILogEventSink, IDisposable
    {
        /// <summary>The received numbers.</summary>
        private readonly ConcurrentQueue<int> _numbers = new();

        /// <summary>Gets the received numbers in arrival order.</summary>
        public List<int> Numbers => [.. _numbers];

        /// <summary>Gets a value indicating whether a write is in progress or has started.</summary>
        public bool Writing { get; private set; }

        /// <summary>Gets a value indicating whether the sink has been disposed.</summary>
        public bool Disposed { get; private set; }

        /// <inheritdoc />
        public void Emit(LogEvent logEvent)
        {
            Writing = true;
            gate?.Wait(TestContext.Current.CancellationToken);
            if (delay > TimeSpan.Zero)
            {
                Thread.Sleep(delay);
            }

            _numbers.Enqueue((int)((ScalarValue)logEvent.Properties["Number"]).Value!);
        }

        /// <inheritdoc />
        public void Dispose() => Disposed = true;
    }
}
