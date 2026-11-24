// Tests that lifecycle events are emitted at the authoritative transitions, once each and in order: Published and
// Ended from RTMP sessions (every termination path, no event for a rejected publish), MediaDescribed from the media
// stage, and PlaylistWithdrawn and Drained from the HLS store (every withdrawal reason, lazily evaluated expiry,
// publication outside the store lock). Time is driven by a fake clock.

using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Rtmp.Amf;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Streams;
using RTSP.IngestService.Tests.Hls;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Streams;

/// <summary>Tests for the emission points of stream lifecycle events.</summary>
public sealed class StreamLifecycleEventTests : IDisposable
{
    /// <summary>The RTMP server.</summary>
    private readonly RtmpTestServer _server = new();

    /// <inheritdoc />
    public void Dispose() => _server.Dispose();

    /// <summary>
    /// Verifies Published follows an accepted publish with the session's identity and incarnation, and closeStream
    /// then a disconnect produce exactly one Ended with reason unpublished.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task CloseStream_PublishesPublishedThenOneEnded()
    {
        var (_, run, client) = _server.Start();
        var (streamId, code) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);
        Assert.Equal("NetStream.Publish.Start", code);
        var session = (await _server.Handler.NextAsync("started")).Session;

        var published = Assert.Single(_server.StreamEvents.Events);
        Assert.Equal((StreamEventType.Published, "live", RtmpTestServer.StreamIdentity, session.IncarnationId, null), (published.Type, published.Application, published.Stream, published.IncarnationId, published.Reason));
        Assert.Equal(TimeSpan.Zero, published.Timestamp.Offset);

        await client.SendCommandAsync(streamId, "closeStream", AmfValue.Null);
        Assert.Equal("NetStream.Unpublish.Success", await client.ReadStatusCodeAsync());
        await client.DisposeAsync();
        await run.WithinSafetyTimeout();

        Assert.Equal([StreamEventType.Published, StreamEventType.Ended], _server.StreamEvents.Types);
        var ended = _server.StreamEvents.Events[1];
        Assert.Equal((session.IncarnationId, StreamEventReasons.Unpublished, session.EndedAt), (ended.IncarnationId, ended.Reason, (DateTimeOffset?)ended.Timestamp));
    }

    /// <summary>Verifies deleteStream ends the publication with reason stream_deleted.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task DeleteStream_EndsWithStreamDeleted()
    {
        var (_, run, client) = _server.Start();
        var (streamId, _) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);

        await client.SendCommandAsync(0, "deleteStream", AmfValue.Null, AmfValue.FromNumber(streamId));
        await _server.Handler.NextAsync("ended");
        await client.DisposeAsync();
        await run.WithinSafetyTimeout();

        Assert.Equal([StreamEventType.Published, StreamEventType.Ended], _server.StreamEvents.Types);
        Assert.Equal(StreamEventReasons.StreamDeleted, _server.StreamEvents.Events[1].Reason);
    }

    /// <summary>Verifies a client disconnect ends the publication with reason client_disconnected.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ClientDisconnect_EndsWithClientDisconnected()
    {
        var (_, run, client) = _server.Start();
        await client.PublishFlowAsync(RtmpTestServer.StreamKey);

        await client.DisposeAsync();
        await run.WithinSafetyTimeout();

        Assert.Equal([StreamEventType.Published, StreamEventType.Ended], _server.StreamEvents.Types);
        Assert.Equal(StreamEventReasons.ClientDisconnected, _server.StreamEvents.Events[1].Reason);
    }

    /// <summary>Verifies server shutdown ends the publication with reason server_shutdown.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Shutdown_EndsWithServerShutdown()
    {
        using var shutdown = new CancellationTokenSource();
        var (_, run, client) = _server.StartWithShutdown(shutdown.Token);
        await client.PublishFlowAsync(RtmpTestServer.StreamKey);

        await shutdown.CancelAsync();
        await run.WithinSafetyTimeout();
        await client.DisposeAsync();

        Assert.Equal([StreamEventType.Published, StreamEventType.Ended], _server.StreamEvents.Types);
        Assert.Equal(StreamEventReasons.ServerShutdown, _server.StreamEvents.Events[1].Reason);
    }

    /// <summary>Verifies a processing failure ends the publication once with reason handler_failed.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task HandlerFailure_EndsWithHandlerFailed()
    {
        _server.Handler.FailMessages = true;
        var (_, run, client) = _server.Start();
        var (streamId, _) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);

        await client.SendMessageAsync(6, streamId, RtmpMessageType.Video, 0, new byte[] { 0x17, 0x01 });
        await run.WithinSafetyTimeout();
        await client.DisposeAsync();

        Assert.Equal([StreamEventType.Published, StreamEventType.Ended], _server.StreamEvents.Types);
        Assert.Equal(StreamEventReasons.HandlerFailed, _server.StreamEvents.Events[1].Reason);
    }

    /// <summary>Verifies rejected publishes (unknown key, failing start, duplicate stream) emit no event at all.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task RejectedPublishes_EmitNothing()
    {
        var (_, unknownRun, unknown) = _server.Start();
        Assert.Equal("NetStream.Publish.Unauthorized", (await unknown.PublishFlowAsync("not-a-key-000")).Code);
        await unknownRun.WithinSafetyTimeout();
        await unknown.DisposeAsync();

        _server.Handler.FailStarts = true;
        var (_, failedRun, failed) = _server.Start();
        Assert.Equal("NetStream.Publish.Failed", (await failed.PublishFlowAsync(RtmpTestServer.StreamKey)).Code);
        await failedRun.WithinSafetyTimeout();
        await failed.DisposeAsync();
        Assert.Empty(_server.StreamEvents.Events);

        _server.Handler.FailStarts = false;
        var (_, firstRun, first) = _server.Start();
        await first.PublishFlowAsync(RtmpTestServer.StreamKey);
        var (_, duplicateRun, duplicate) = _server.Start();
        Assert.Equal("NetStream.Publish.BadName", (await duplicate.PublishFlowAsync(RtmpTestServer.StreamKey)).Code);
        Assert.Equal([StreamEventType.Published], _server.StreamEvents.Types);

        await duplicate.DisposeAsync();
        await first.DisposeAsync();
        await Task.WhenAll(firstRun, duplicateRun).WithinSafetyTimeout();
        Assert.Equal([StreamEventType.Published, StreamEventType.Ended], _server.StreamEvents.Types);
    }

    /// <summary>
    /// Verifies republishing the same application and stream gets a new incarnation, and every event carries the
    /// incarnation of its own publication.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Republish_SameName_GetsNewIncarnation()
    {
        var (_, run, client) = _server.Start();
        var (streamId, _) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);
        await client.SendCommandAsync(streamId, "closeStream", AmfValue.Null);
        Assert.Equal("NetStream.Unpublish.Success", await client.ReadStatusCodeAsync());
        Assert.Equal("NetStream.Publish.Start", await client.PublishAsync(streamId, RtmpTestServer.StreamKey));
        await client.DisposeAsync();
        await run.WithinSafetyTimeout();

        var events = _server.StreamEvents.Events;
        Assert.Equal([StreamEventType.Published, StreamEventType.Ended, StreamEventType.Published, StreamEventType.Ended], events.Select(static e => e.Type));
        Assert.Equal(events[0].IncarnationId, events[1].IncarnationId);
        Assert.Equal(events[2].IncarnationId, events[3].IncarnationId);
        Assert.NotEqual(events[0].IncarnationId, events[2].IncarnationId);
        Assert.All(events, static e => Assert.Equal((("live", RtmpTestServer.StreamIdentity)), (e.Application, e.Stream)));
    }

    /// <summary>
    /// Verifies the full HLS lifecycle of a normally ended stream: MediaDescribed when each track is configured (not
    /// for a repeated header or metadata), no withdrawal while the ended playlist is retained, PlaylistWithdrawn
    /// (retention_expired) once retention passes, then Drained once the last segment's availability ends, all with
    /// the session's incarnation and no media buffer left rented.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Hls_CompletedStream_FullLifecycle()
    {
        using var harness = new HlsHarness();
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session);
        await harness.ConfigureAsync(session, audio: false);
        session.SetMetadata([new("width", AmfValue.FromNumber(640)), new("height", AmfValue.FromNumber(360))]);
        await harness.SendAsync(session, PublishedMessageKind.Data, 0, [0x02]);
        Assert.Contains(harness.Logs.GetSnapshot(), static r => r.Id.Id == 6009);

        var described = harness.StreamEvents.Events;
        Assert.Equal([StreamEventType.MediaDescribed, StreamEventType.MediaDescribed], described.Select(static e => e.Type));
        Assert.Null(described[0].Media!.Audio);
        Assert.Equal(1, described[0].Media!.Video!.ConfigurationVersion);
        Assert.StartsWith("avc1.", described[0].Media!.Video!.Codec, StringComparison.Ordinal);
        Assert.Equal(("mp4a.40.2", 1), (described[1].Media!.Audio!.Codec, described[1].Media!.Audio!.ConfigurationVersion));
        Assert.Equal(described[0].Media!.Video, described[1].Media!.Video);

        await SegmentsAsync(harness, session);
        await harness.EndAsync(session);
        Assert.NotNull(harness.Playlist());
        Assert.Equal(2, harness.StreamEvents.Events.Count);

        harness.Time.Advance(harness.Options.EndedStreamRetention);
        Assert.Null(harness.Playlist());
        Assert.Equal(StreamEventType.PlaylistWithdrawn, harness.StreamEvents.Events[^1].Type);
        Assert.Equal(StreamEventReasons.RetentionExpired, harness.StreamEvents.Events[^1].Reason);
        Assert.True(harness.Store.ContainsStream("live", "camera1"));

        harness.Time.Advance(TimeSpan.FromHours(1));
        Assert.False(harness.Store.ContainsStream("live", "camera1"));
        _ = harness.Store.StreamCount;

        var events = harness.StreamEvents.Events;
        Assert.Equal([StreamEventType.MediaDescribed, StreamEventType.MediaDescribed, StreamEventType.PlaylistWithdrawn, StreamEventType.Drained], events.Select(static e => e.Type));
        Assert.All(events, e => Assert.Equal(session.IncarnationId, e.IncarnationId));
        Assert.Equal(harness.Time.GetUtcNow(), events[^1].Timestamp);
        Assert.Null(events[^1].Reason);
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>Verifies an aborted stream's playlist is withdrawn at once (aborted) and drains when its segments expire.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Hls_AbortedStream_WithdrawnImmediatelyThenDrained()
    {
        using var harness = new HlsHarness();
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session);
        await SegmentsAsync(harness, session);

        await harness.EndAsync(session, PublishEndReason.ConnectionTimeout);

        Assert.Equal(StreamEventType.PlaylistWithdrawn, harness.StreamEvents.Events[^1].Type);
        Assert.Equal(StreamEventReasons.Aborted, harness.StreamEvents.Events[^1].Reason);
        harness.Time.Advance(TimeSpan.FromHours(1));
        _ = harness.Store.StreamCount;
        Assert.Equal(StreamEventType.Drained, harness.StreamEvents.Events[^1].Type);
        Assert.Single(harness.StreamEvents.Events, static e => e.Type == StreamEventType.Drained);
    }

    /// <summary>Verifies a stream that ends normally without a segment is withdrawn (empty) and drained in the same call.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Hls_EmptyStream_WithdrawnAndDrainedAtOnce()
    {
        using var harness = new HlsHarness();
        var session = await harness.StartAsync();

        await harness.EndAsync(session);

        Assert.Equal([StreamEventType.PlaylistWithdrawn, StreamEventType.Drained], harness.StreamEvents.Types);
        Assert.Equal(StreamEventReasons.Empty, harness.StreamEvents.Events[0].Reason);
    }

    /// <summary>Verifies zero retention withdraws a completed stream at once with reason retention_disabled.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Hls_ZeroRetention_WithdrawnWithRetentionDisabled()
    {
        using var harness = new HlsHarness(new HlsOptions { TargetSegmentDuration = TimeSpan.FromSeconds(1), MaxSegmentDuration = TimeSpan.FromSeconds(2), EndedStreamRetention = TimeSpan.Zero });
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session);
        await SegmentsAsync(harness, session);

        await harness.EndAsync(session);

        Assert.Equal(StreamEventReasons.RetentionDisabled, Assert.Single(harness.StreamEvents.Events, static e => e.Type == StreamEventType.PlaylistWithdrawn).Reason);
    }

    /// <summary>
    /// Verifies a new publication of the same stream replaces the retained ended playlist: the old incarnation is
    /// withdrawn (replaced) and the new one is distinct.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Hls_Republish_WithdrawsRetainedPlaylistAsReplaced()
    {
        using var harness = new HlsHarness();
        var first = await harness.StartAsync();
        await harness.ConfigureAsync(first);
        await SegmentsAsync(harness, first);
        await harness.EndAsync(first);

        var second = await harness.StartAsync();

        var withdrawn = Assert.Single(harness.StreamEvents.Events, static e => e.Type == StreamEventType.PlaylistWithdrawn);
        Assert.Equal((first.IncarnationId, StreamEventReasons.Replaced), (withdrawn.IncarnationId, withdrawn.Reason));
        Assert.NotEqual(first.IncarnationId, second.IncarnationId);
        await harness.EndAsync(second, PublishEndReason.ClientDisconnected);
    }

    /// <summary>Verifies global storage pressure withdraws ended playlists early with reason storage_pressure.</summary>
    [Fact]
    public void Store_StoragePressure_WithdrawsEndedPlaylist()
    {
        var (store, events, _) = CreateStore(new HlsOptions { MaxTotalStorageMegabytes = 1, MaxSessionStorageMegabytes = 1 });
        var ended = Guid.NewGuid();
        Assert.True(store.TryOpenStream(ended, "live", "old", 1, out _, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        Assert.Equal(HlsAppendResult.Published, store.AddInitialization(ended, new HlsSegment(HlsSegmentKind.Initialization, "init-0.mp4", new byte[600_000])));
        Assert.Equal(HlsAppendResult.Published, store.AppendSegment(ended, new HlsSegment(HlsSegmentKind.Media, "segment-0.m4s", new byte[100]), HlsPlaylistTests.Entry(0, 1.0)));
        store.CloseStream(ended, completed: true);
        Assert.Empty(events.Events);

        var live = Guid.NewGuid();
        Assert.True(store.TryOpenStream(live, "live", "new", 1, out _, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        store.AddInitialization(live, new HlsSegment(HlsSegmentKind.Initialization, "init-0.mp4", new byte[600_000]));

        var withdrawn = Assert.Single(events.Events);
        Assert.Equal((StreamEventType.PlaylistWithdrawn, "old", StreamEventReasons.StoragePressure), (withdrawn.Type, withdrawn.Stream, withdrawn.Reason));
    }

    /// <summary>
    /// Verifies events are published after the store lock is released: from inside Publish, another thread can use
    /// the store. Streams opened without an incarnation report nothing.
    /// </summary>
    [Fact]
    public void Store_PublishesOutsideItsLock()
    {
        var probe = new ProbingPublisher();
        var (store, _, _) = CreateStore(new HlsOptions(), probe);
        probe.Store = store;

        Assert.True(store.TryOpenStream(Guid.NewGuid(), "live", "anonymous", 1, out _));
        var session = Guid.NewGuid();
        Assert.True(store.TryOpenStream(session, "live", "camera1", 1, out _, "cccccccccccccccccccccccccccccccc"));
        store.CloseStream(session, completed: false);

        Assert.Equal([StreamEventType.PlaylistWithdrawn, StreamEventType.Drained], probe.Types);
        Assert.All(probe.ProbeResults, Assert.True);
    }

    /// <summary>Sends keyframes 1 s apart so the 1 s target produces two completed segments.</summary>
    /// <param name="harness">The harness.</param>
    /// <param name="session">The session.</param>
    /// <returns>A task.</returns>
    private static async Task SegmentsAsync(HlsHarness harness, PublisherSession session)
    {
        for (uint t = 0; t <= 3000; t += 1000)
        {
            await harness.VideoAsync(session, t, idr: true);
        }

        Assert.NotNull(harness.Playlist());
    }

    /// <summary>Creates a store on a fake clock.</summary>
    /// <param name="options">The options.</param>
    /// <param name="publisher">The event publisher; a recorder when <see langword="null"/>.</param>
    /// <returns>The store, the recorder and the clock.</returns>
    private (InMemoryHlsSegmentStore Store, RecordingStreamEventPublisher Events, FakeTimeProvider Time) CreateStore(HlsOptions options, IStreamEventPublisher? publisher = null)
    {
        var events = new RecordingStreamEventPublisher();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new InMemoryHlsSegmentStore(Microsoft.Extensions.Options.Options.Create(options), time, new HlsMetrics(_server.MeterFactory), publisher ?? events);
        return (store, events, time);
    }

    /// <summary>Records events and, for each, whether another thread could use the store meanwhile.</summary>
    private sealed class ProbingPublisher : IStreamEventPublisher
    {
        /// <summary>Gets or sets the store probed.</summary>
        public InMemoryHlsSegmentStore? Store { get; set; }

        /// <summary>Gets the event types.</summary>
        public List<StreamEventType> Types { get; } = [];

        /// <summary>Gets whether each probe completed.</summary>
        public List<bool> ProbeResults { get; } = [];

        /// <inheritdoc />
        public void Publish(StreamEvent streamEvent)
        {
            Types.Add(streamEvent.Type);
            var probe = Task.Run(() => Store!.StreamCount, TestContext.Current.CancellationToken);
            ProbeResults.Add(probe.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        }
    }
}
