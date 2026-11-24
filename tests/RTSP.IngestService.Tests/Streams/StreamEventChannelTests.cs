// Tests for the bounded drop-oldest stream event channel and its dispatcher: publishing without a consumer, capacity,
// drop-oldest order, exact drop accounting (including under concurrent producers), rejection after completion, a
// blocked consumer never blocking producers, consumer failure isolation, cancellation and the bounded stop budget.
// Synchronisation uses gates and a fake clock, never delays.

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Streams;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Streams;

/// <summary>Tests for <see cref="StreamEventChannel"/> and <see cref="StreamEventDispatcher"/>.</summary>
public sealed class StreamEventChannelTests : IDisposable
{
    /// <summary>Owns the meter factory.</summary>
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();

    /// <summary>Captured logs.</summary>
    private readonly FakeLogCollector _logs = new();

    /// <summary>The logger factory.</summary>
    private readonly LoggerFactory _loggerFactory;

    /// <summary>The metrics.</summary>
    private readonly StreamEventMetrics _metrics;

    /// <summary>Initializes a new instance of the <see cref="StreamEventChannelTests"/> class.</summary>
    public StreamEventChannelTests()
    {
        _loggerFactory = new LoggerFactory([new FakeLoggerProvider(_logs)]);
        _metrics = new StreamEventMetrics(MeterFactory);
    }

    /// <summary>Gets the meter factory.</summary>
    private System.Diagnostics.Metrics.IMeterFactory MeterFactory => _services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();

    /// <inheritdoc />
    public void Dispose()
    {
        _loggerFactory.Dispose();
        _services.Dispose();
    }

    /// <summary>Verifies publishing works with nobody reading, up to the capacity, without loss.</summary>
    [Fact]
    public void Publish_WithoutConsumer_BuffersUpToCapacity()
    {
        using var channel = CreateChannel(16);

        for (var i = 0; i < 16; i++)
        {
            channel.Publish(Event(i));
        }

        Assert.Equal((16, 16L, 0L, 0L), (channel.Count, channel.PublishedCount, channel.DroppedCount, channel.RejectedCount));
    }

    /// <summary>
    /// Verifies drop-oldest: the channel stays at capacity, keeps the newest events in order, and counts exactly the
    /// overwritten ones (each with its type tag), although every TryWrite succeeded.
    /// </summary>
    [Fact]
    public void Publish_WhenFull_OverwritesOldestAndCountsEachDrop()
    {
        using var dropped = new MetricCollector<long>(MeterFactory, StreamEventMetrics.MeterName, "stream_events.dropped");
        using var published = new MetricCollector<long>(MeterFactory, StreamEventMetrics.MeterName, "stream_events.published");
        using var channel = CreateChannel(16);

        for (var i = 0; i < 100; i++)
        {
            channel.Publish(Event(i));
        }

        Assert.Equal((16, 100L, 84L, 0L), (channel.Count, channel.PublishedCount, channel.DroppedCount, channel.RejectedCount));
        Assert.Equal(Enumerable.Range(84, 16), Drain(channel).Select(static e => int.Parse(e.Stream, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(84, dropped.GetMeasurementSnapshot().Sum(static m => m.Value));
        Assert.All(dropped.GetMeasurementSnapshot(), static m => Assert.Equal("published", m.Tags["type"]));
        Assert.Equal(100, published.GetMeasurementSnapshot().Sum(static m => m.Value));
    }

    /// <summary>Verifies concurrent producers never block and the accounting stays exact: published = delivered + dropped.</summary>
    [Fact]
    public async Task Publish_ConcurrentProducers_AccountingIsExact()
    {
        const int producers = 8;
        const int perProducer = 5000;
        using var channel = CreateChannel(64);
        using var start = new Barrier(producers);

        await Task.WhenAll(Enumerable.Range(0, producers).Select(p => Task.Run(
            () =>
            {
                start.SignalAndWait(TestContext.Current.CancellationToken);
                for (var i = 0; i < perProducer; i++)
                {
                    channel.Publish(Event((p * perProducer) + i));
                }
            },
            TestContext.Current.CancellationToken))).WithinSafetyTimeout();

        var remaining = Drain(channel);
        Assert.Equal(producers * perProducer, channel.PublishedCount);
        Assert.Equal(64, remaining.Count);
        Assert.Equal(channel.PublishedCount, remaining.Count + channel.DroppedCount);
        Assert.Equal(remaining.Count, remaining.Distinct().Count());
    }

    /// <summary>Verifies that after completion events are rejected and counted separately, never as drops, and Publish does not throw.</summary>
    [Fact]
    public void Publish_AfterComplete_IsRejected()
    {
        using var rejected = new MetricCollector<long>(MeterFactory, StreamEventMetrics.MeterName, "stream_events.rejected");
        using var channel = CreateChannel(16);
        channel.Publish(Event(0));

        Assert.True(channel.Complete());
        Assert.False(channel.Complete());
        channel.Publish(Event(1));

        Assert.Equal((1L, 0L, 1L), (channel.PublishedCount, channel.DroppedCount, channel.RejectedCount));
        Assert.Equal("published", Assert.Single(rejected.GetMeasurementSnapshot()).Tags["type"]);
    }

    /// <summary>Verifies the dispatcher delivers every event in order to the consumer, on a task other than the producer's.</summary>
    [Fact]
    public async Task Dispatcher_DeliversInOrder()
    {
        using var channel = CreateChannel(1024);
        var consumer = new GatedConsumer();
        using var dispatcher = CreateDispatcher(channel, consumer);
        await dispatcher.StartAsync(TestContext.Current.CancellationToken);

        var producer = Environment.CurrentManagedThreadId;
        for (var i = 0; i < 100; i++)
        {
            channel.Publish(Event(i));
        }

        await Eventually.WaitUntilAsync(() => consumer.Received.Count == 100, "all events are delivered");
        Assert.Equal(Enumerable.Range(0, 100).Select(static i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), consumer.Received.Select(static e => e.Stream));
        Assert.DoesNotContain(producer, consumer.Threads);
        await dispatcher.StopAsync(TestContext.Current.CancellationToken).WithinSafetyTimeout();
    }

    /// <summary>
    /// Verifies a stalled consumer never blocks producers: while it is held on its first event, thousands of
    /// publishes return immediately and the overflow is counted as drops.
    /// </summary>
    [Fact]
    public async Task Dispatcher_StalledConsumer_DoesNotBlockProducers()
    {
        using var channel = CreateChannel(16);
        var consumer = new GatedConsumer { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var dispatcher = CreateDispatcher(channel, consumer);
        await dispatcher.StartAsync(TestContext.Current.CancellationToken);
        channel.Publish(Event(0));
        await consumer.Blocked.Task.WithinSafetyTimeout();

        for (var i = 1; i <= 10_000; i++)
        {
            channel.Publish(Event(i));
        }

        Assert.Equal(16, channel.Count);
        Assert.Equal(10_000 - 16, channel.DroppedCount);
        consumer.Gate.SetResult();
        await Eventually.WaitUntilAsync(() => consumer.Received.Count == 17, "the consumer catches up with the retained events");
        Assert.Equal(Enumerable.Range(9985, 16).Prepend(0).Select(static i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), consumer.Received.Select(static e => e.Stream));
        await dispatcher.StopAsync(TestContext.Current.CancellationToken).WithinSafetyTimeout();
    }

    /// <summary>Verifies a failing consumer is logged and counted, and later events are still delivered.</summary>
    [Fact]
    public async Task Dispatcher_ConsumerFailure_IsIsolated()
    {
        using var failures = new MetricCollector<long>(MeterFactory, StreamEventMetrics.MeterName, "stream_events.consumer_failures");
        using var channel = CreateChannel(16);
        var consumer = new GatedConsumer { FailOn = "1" };
        using var dispatcher = CreateDispatcher(channel, consumer);
        await dispatcher.StartAsync(TestContext.Current.CancellationToken);

        channel.Publish(Event(0));
        channel.Publish(Event(1));
        channel.Publish(Event(2));

        await Eventually.WaitUntilAsync(() => consumer.Received.Count == 2, "the events after the failure are delivered");
        Assert.Equal(["0", "2"], consumer.Received.Select(static e => e.Stream));
        Assert.Single(_logs.GetSnapshot(), static r => r.Id.Id == 9100);
        Assert.Equal(1, failures.GetMeasurementSnapshot().Sum(static m => m.Value));
        Assert.False(dispatcher.ExecuteTask!.IsCompleted);
        await dispatcher.StopAsync(TestContext.Current.CancellationToken).WithinSafetyTimeout();
    }

    /// <summary>Verifies stopping cancels a consumer waiting on its token, completes the channel and ends delivery.</summary>
    [Fact]
    public async Task Dispatcher_Stop_CancelsConsumerAndCompletesChannel()
    {
        using var channel = CreateChannel(16);
        var consumer = new GatedConsumer { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var dispatcher = CreateDispatcher(channel, consumer);
        await dispatcher.StartAsync(TestContext.Current.CancellationToken);
        channel.Publish(Event(0));
        channel.Publish(Event(1));
        await consumer.Blocked.Task.WithinSafetyTimeout();

        await dispatcher.StopAsync(TestContext.Current.CancellationToken).WithinSafetyTimeout();

        Assert.True(consumer.Cancelled);
        Assert.True(dispatcher.ExecuteTask!.IsCompletedSuccessfully);
        channel.Publish(Event(2));
        Assert.Equal(1, channel.RejectedCount);
        Assert.Contains(_logs.GetSnapshot(), static r => r.Id.Id == 9101);
        Assert.DoesNotContain(_logs.GetSnapshot(), static r => r.Id.Id == 9102);
    }

    /// <summary>Verifies a consumer that ignores cancellation is abandoned once the stop budget passes on the clock.</summary>
    [Fact]
    public async Task Dispatcher_Stop_AbandonsUncooperativeConsumerAfterBudget()
    {
        var time = new FakeTimeProvider();
        using var channel = CreateChannel(16);
        var consumer = new GatedConsumer { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), IgnoreCancellation = true };
        using var dispatcher = CreateDispatcher(channel, consumer, time);
        await dispatcher.StartAsync(TestContext.Current.CancellationToken);
        channel.Publish(Event(0));
        await consumer.Blocked.Task.WithinSafetyTimeout();

        var stop = dispatcher.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(stop.IsCompleted);
        time.Advance(StreamEventDispatcher.StopTimeout);
        await stop.WithinSafetyTimeout();

        Assert.Single(_logs.GetSnapshot(), static r => r.Id.Id == 9102);
        consumer.Gate.SetResult();
        await dispatcher.ExecuteTask!.WithinSafetyTimeout();
    }

    /// <summary>Verifies the host's shutdown token also bounds the stop, before the dispatcher's own budget.</summary>
    [Fact]
    public async Task Dispatcher_Stop_HonoursHostShutdownToken()
    {
        using var channel = CreateChannel(16);
        var consumer = new GatedConsumer { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), IgnoreCancellation = true };
        using var dispatcher = CreateDispatcher(channel, consumer, new FakeTimeProvider());
        await dispatcher.StartAsync(TestContext.Current.CancellationToken);
        channel.Publish(Event(0));
        await consumer.Blocked.Task.WithinSafetyTimeout();

        using var shutdown = new CancellationTokenSource();
        var stop = dispatcher.StopAsync(shutdown.Token);
        await shutdown.CancelAsync();
        await stop.WithinSafetyTimeout();

        consumer.Gate.SetResult();
        await dispatcher.ExecuteTask!.WithinSafetyTimeout();
    }

    /// <summary>Verifies that with no consumer the dispatcher reads nothing and stops at once.</summary>
    [Fact]
    public async Task Dispatcher_WithoutConsumer_LeavesEventsAndStops()
    {
        using var channel = CreateChannel(16);
        using var dispatcher = CreateDispatcher(channel, null);
        await dispatcher.StartAsync(TestContext.Current.CancellationToken);
        channel.Publish(Event(0));

        await dispatcher.ExecuteTask!.WithinSafetyTimeout();
        Assert.Equal(1, channel.Count);
        await dispatcher.StopAsync(TestContext.Current.CancellationToken).WithinSafetyTimeout();
        channel.Publish(Event(1));
        Assert.Equal(1, channel.RejectedCount);
    }

    /// <summary>Verifies the capacity option's range and default.</summary>
    /// <param name="capacity">The capacity.</param>
    /// <param name="valid">Whether it is valid.</param>
    [Theory]
    [InlineData(15, false)]
    [InlineData(16, true)]
    [InlineData(1024, true)]
    [InlineData(65536, true)]
    [InlineData(65537, false)]
    public void Options_CapacityRange(int capacity, bool valid)
    {
        Assert.Equal(1024, new StreamEventOptions().QueueCapacity);
        var result = new StreamEventOptionsValidator().Validate(null, new StreamEventOptions { QueueCapacity = capacity });
        Assert.Equal(valid, result.Succeeded);
    }

    /// <summary>Creates an event whose stream name is <paramref name="index"/>.</summary>
    /// <param name="index">The index.</param>
    /// <returns>The event.</returns>
    private static StreamEvent Event(int index) => StreamEventFactory.Create(
        StreamEventType.Published, "live", index.ToString(System.Globalization.CultureInfo.InvariantCulture), "0123456789abcdef0123456789abcdef", DateTimeOffset.UnixEpoch);

    /// <summary>Reads every buffered event.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>The events.</returns>
    private static List<StreamEvent> Drain(StreamEventChannel channel)
    {
        var events = new List<StreamEvent>();
        while (channel.Reader.TryRead(out var streamEvent))
        {
            events.Add(streamEvent);
        }

        return events;
    }

    /// <summary>Creates a channel.</summary>
    /// <param name="capacity">The capacity.</param>
    /// <returns>The channel.</returns>
    private StreamEventChannel CreateChannel(int capacity) =>
        new(Microsoft.Extensions.Options.Options.Create(new StreamEventOptions { QueueCapacity = capacity }), _metrics);

    /// <summary>Creates a dispatcher.</summary>
    /// <param name="channel">The channel.</param>
    /// <param name="consumer">The consumer.</param>
    /// <param name="time">The clock; the system clock when <see langword="null"/>.</param>
    /// <returns>The dispatcher, not started.</returns>
    private StreamEventDispatcher CreateDispatcher(StreamEventChannel channel, IStreamEventConsumer? consumer, TimeProvider? time = null) =>
        new(channel, consumer, _metrics, time ?? TimeProvider.System, _loggerFactory.CreateLogger<StreamEventDispatcher>());

    /// <summary>A consumer that records events and can block, fail or ignore cancellation.</summary>
    private sealed class GatedConsumer : IStreamEventConsumer
    {
        /// <summary>Gets the events handled, in order.</summary>
        public ConcurrentQueue<StreamEvent> Received { get; } = new();

        /// <summary>Gets the threads the consumer ran on.</summary>
        public ConcurrentBag<int> Threads { get; } = [];

        /// <summary>Gets or sets a gate awaited on the first event; <see langword="null"/> for none.</summary>
        public TaskCompletionSource? Gate { get; set; }

        /// <summary>Gets a signal completed when the consumer is waiting at <see cref="Gate"/>.</summary>
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets or sets the stream name of an event that makes the consumer throw.</summary>
        public string? FailOn { get; set; }

        /// <summary>Gets or sets a value indicating whether the gate wait ignores cancellation.</summary>
        public bool IgnoreCancellation { get; set; }

        /// <summary>Gets a value indicating whether a gate wait was cancelled.</summary>
        public bool Cancelled { get; private set; }

        /// <inheritdoc />
        public async ValueTask ConsumeAsync(StreamEvent streamEvent, CancellationToken cancellationToken)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            if (Gate is { } gate && !Blocked.Task.IsCompleted)
            {
                Blocked.TrySetResult();
                try
                {
                    await (IgnoreCancellation ? gate.Task : gate.Task.WaitAsync(cancellationToken));
                }
                catch (OperationCanceledException)
                {
                    Cancelled = true;
                    throw;
                }
            }

            if (streamEvent.Stream == FailOn)
            {
                throw new InvalidOperationException("Simulated consumer failure");
            }

            Received.Enqueue(streamEvent);
        }
    }
}
