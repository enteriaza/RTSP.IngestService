// Integration tests of stream lifecycle events in the real service host: the dispatcher starts before the HTTP origin
// and RTMP listener, a real TCP publish delivers Published, Ended, PlaylistWithdrawn and Drained to a registered
// consumer, a failing consumer affects neither ingest nor later events, and a stalled consumer affects neither
// readiness, publishing nor shutdown. Synchronisation uses gates, never delays.

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Testing;
using RTSP.IngestService.Http;
using RTSP.IngestService.Lifecycle;
using RTSP.IngestService.Management;
using RTSP.IngestService.Rtmp;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Streams;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Streams;

/// <summary>Integration tests for <see cref="StreamEventServiceCollectionExtensions.AddStreamEvents"/> in the service host.</summary>
public sealed class StreamEventHostTests
{
    /// <summary>The configured test stream key.</summary>
    private const string StreamKey = "stream-events-key-0001";

    /// <summary>Captured host logs.</summary>
    private readonly FakeLogCollector _logs = new();

    /// <summary>Gets the test cancellation token.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Verifies the composition (one channel, the dispatcher hosted before the origin and the listener) and delivery
    /// of a real publication's lifecycle in order with one incarnation, even though the consumer throws on the
    /// first event.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Host_DeliversPublicationLifecycle_DespiteConsumerFailure()
    {
        var consumer = new HostConsumer { FailOn = StreamEventType.Published };
        var builder = TestHosts.CreateBuilder(_logs, overrides: Settings());
        builder.Services.AddSingleton<IStreamEventConsumer>(consumer);
        using var host = builder.Build();

        var hosted = host.Services.GetServices<IHostedService>().ToList();
        var dispatcher = hosted.FindIndex(static s => s is StreamEventDispatcher);
        Assert.True(dispatcher >= 0);
        Assert.True(dispatcher < hosted.FindIndex(static s => s is HlsHttpOrigin));
        Assert.True(dispatcher < hosted.FindIndex(static s => s is RtmpListener));
        Assert.Same(host.Services.GetRequiredService<StreamEventChannel>(), host.Services.GetRequiredService<IStreamEventPublisher>());

        await host.StartAsync(Token).WithinSafetyTimeout();
        try
        {
            await PublishAndDisconnectAsync(host);
            await Eventually.WaitUntilAsync(() => consumer.Received.Count == 3, "the remaining lifecycle events are delivered");

            var events = consumer.Received.ToList();
            Assert.Equal([StreamEventType.Ended, StreamEventType.PlaylistWithdrawn, StreamEventType.Drained], events.Select(static e => e.Type));
            Assert.Single(events.Select(static e => e.IncarnationId).Distinct(StringComparer.Ordinal));
            Assert.All(events, static e => Assert.Equal(("live", "camera"), (e.Application, e.Stream)));
            Assert.Equal(StreamEventReasons.ClientDisconnected, events[0].Reason);
            Assert.Single(_logs.GetSnapshot(), static r => r.Id.Id == 9100);
            Assert.Equal(ApplicationState.Running, host.Services.GetRequiredService<IApplicationLifecycle>().State);
            Assert.DoesNotContain(_logs.GetSnapshot(), r => r.Message.Contains(StreamKey, StringComparison.Ordinal));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0, host.Services.GetRequiredService<StreamEventChannel>().DroppedCount);
    }

    /// <summary>
    /// Verifies a consumer stalled on its first event does not affect readiness, further publishing or shutdown:
    /// readiness stays 200, a second publication still completes, and stopping cancels the consumer and closes the
    /// channel.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Host_StalledConsumer_DoesNotAffectIngestReadinessOrShutdown()
    {
        var consumer = new HostConsumer { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var builder = TestHosts.CreateBuilder(_logs, overrides: Settings());
        builder.Services.AddSingleton<IStreamEventConsumer>(consumer);
        using var host = builder.Build();
        var channel = host.Services.GetRequiredService<StreamEventChannel>();

        await host.StartAsync(Token).WithinSafetyTimeout();
        try
        {
            await PublishAndDisconnectAsync(host);
            await consumer.Blocked.Task.WithinSafetyTimeout();
            await PublishAndDisconnectAsync(host);
            await Eventually.WaitUntilAsync(() => channel.PublishedCount == 8, "both publications' events are published");

            Assert.Equal(200, host.Services.GetRequiredService<ManagementEndpoints>().Ready().Status);
            Assert.Empty(consumer.Received);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WithinSafetyTimeout();
        }

        Assert.True(consumer.Cancelled);
        channel.Publish(StreamEventFactory.Create(StreamEventType.Published, "live", "camera", "0123456789abcdef0123456789abcdef", DateTimeOffset.UnixEpoch));
        Assert.Equal(1, channel.RejectedCount);
    }

    /// <summary>Publishes over TCP to the host's RTMP listener and disconnects once the publication has ended.</summary>
    /// <param name="host">The started host.</param>
    /// <returns>A task that completes when the publication has ended.</returns>
    private static async Task PublishAndDisconnectAsync(IHost host)
    {
        var listener = host.Services.GetRequiredService<RtmpListener>();
        var channel = host.Services.GetRequiredService<StreamEventChannel>();
        var before = channel.PublishedCount;
        var client = await RtmpTestClient.ConnectAsync(listener.LocalEndPoint!);
        await using (client)
        {
            Assert.Equal("NetStream.Publish.Start", (await client.PublishFlowAsync(StreamKey)).Code);
        }

        await Eventually.WaitUntilAsync(() => channel.PublishedCount == before + 4, "the publication is published, ended, withdrawn and drained");
    }

    /// <summary>Host settings: one publisher <c>camera</c>.</summary>
    /// <returns>The overrides.</returns>
    private static Dictionary<string, string?> Settings() => new()
    {
        ["Rtmp:Authentication:Publishers:0:Name"] = "camera",
        ["Rtmp:Authentication:Publishers:0:Application"] = "live",
        ["Rtmp:Authentication:Publishers:0:KeyHash"] = PublisherKeyHash.Create(StreamKey),
    };

    /// <summary>A consumer that records events and can fail on a type or stall cooperatively on its first event.</summary>
    private sealed class HostConsumer : IStreamEventConsumer
    {
        /// <summary>Gets the events handled.</summary>
        public ConcurrentQueue<StreamEvent> Received { get; } = new();

        /// <summary>Gets or sets an event type that makes the consumer throw.</summary>
        public StreamEventType? FailOn { get; set; }

        /// <summary>Gets or sets a gate awaited, honouring cancellation, on the first event.</summary>
        public TaskCompletionSource? Gate { get; set; }

        /// <summary>Gets a signal completed when the consumer waits at <see cref="Gate"/>.</summary>
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets a value indicating whether the wait was cancelled.</summary>
        public bool Cancelled { get; private set; }

        /// <inheritdoc />
        public async ValueTask ConsumeAsync(StreamEvent streamEvent, CancellationToken cancellationToken)
        {
            if (Gate is { } gate)
            {
                Blocked.TrySetResult();
                try
                {
                    await gate.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Cancelled = true;
                    throw;
                }
            }

            if (streamEvent.Type == FailOn)
            {
                throw new InvalidOperationException("Simulated consumer failure");
            }

            Received.Enqueue(streamEvent);
        }
    }
}
