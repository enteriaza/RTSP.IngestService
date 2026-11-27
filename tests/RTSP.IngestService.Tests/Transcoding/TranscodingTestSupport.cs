// Building blocks for transcoding tests: a harness that drives TranscodingPublishSessionHandler, with its managed
// video and audio stages, directly with pooled messages and fixture FLV tags, recording what reaches the downstream
// stage.

using System.Buffers;
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
/// <summary>Drives a <see cref="TranscodingPublishSessionHandler"/> with a recording downstream stage.</summary>
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
            new TranscodingMetrics(MeterFactory),
            _loggerFactory.CreateLogger<TranscodingPublishSessionHandler>());
    }

    /// <summary>Gets the transcoding options.</summary>
    public TranscodingOptions Options { get; } = new();

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
