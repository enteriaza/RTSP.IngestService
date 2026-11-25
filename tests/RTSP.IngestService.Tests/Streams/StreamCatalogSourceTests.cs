// Tests of the stream catalog over the real authoritative sources: the registry's monotonic generation, the HLS
// store's inventory (live, ended, withdrawn, drained; incarnation and generation recorded at open), and a catalog
// reconciled from the registry, the media stage and the store through each stage of a publication's lifecycle,
// including a replacement while the previous incarnation drains and the lifecycle events applied incrementally.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Streams;
using RTSP.IngestService.Tests.Hls;

namespace RTSP.IngestService.Tests.Streams;

/// <summary>Tests for <see cref="RuntimeStreamInventorySource"/>, <see cref="IHlsStreamInventory"/> and registry generations.</summary>
public sealed class StreamCatalogSourceTests : IDisposable
{
    /// <summary>The media and HLS stages on a fake clock.</summary>
    private readonly HlsHarness _harness = new(new HlsOptions
    {
        TargetSegmentDuration = TimeSpan.FromSeconds(1),
        MaxSegmentDuration = TimeSpan.FromSeconds(2),
        EndedStreamRetention = TimeSpan.FromSeconds(30),
    });

    /// <summary>The registry.</summary>
    private readonly PublishedStreamRegistry _registry = new();

    /// <summary>Owns the meter factory.</summary>
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();

    /// <summary>The catalog under test.</summary>
    private readonly StreamCatalog _catalog;

    /// <summary>Initializes a new instance of the <see cref="StreamCatalogSourceTests"/> class.</summary>
    public StreamCatalogSourceTests() => _catalog = new StreamCatalog(
        new RuntimeStreamInventorySource(_registry, _harness.Processor, _harness.Store),
        new StreamCatalogMetrics(_services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()),
        _harness.Time,
        NullLogger<StreamCatalog>.Instance);

    /// <inheritdoc />
    public void Dispose()
    {
        _catalog.Dispose();
        _harness.Dispose();
        _services.Dispose();
    }

    /// <summary>Verifies every registration gets a larger generation, including re-registration of the same identity.</summary>
    [Fact]
    public void Registry_AssignsIncreasingGenerations()
    {
        var first = NewSession("camera1");
        var other = NewSession("camera2");
        var again = NewSession("camera1");
        Assert.Equal(0, first.Generation);

        Assert.True(_registry.TryAdd(first));
        Assert.True(_registry.TryAdd(other));
        Assert.False(_registry.TryAdd(again));
        _registry.Remove(first);
        Assert.True(_registry.TryAdd(again));

        Assert.True(first.Generation < other.Generation);
        Assert.True(other.Generation < again.Generation);
    }

    /// <summary>Verifies the store inventory lists instances with their incarnation, generation and state, and copies them.</summary>
    [Fact]
    public void StoreInventory_ListsInstancesImmutably()
    {
        Assert.Empty(_harness.Store.GetStreams());
        Assert.True(_harness.Store.TryOpenStream(Guid.NewGuid(), "live", "anonymous", 2, out _));
        Assert.True(_harness.Store.TryOpenStream(Guid.NewGuid(), "live", "cam", 2, out _, "0123456789abcdef0123456789abcdef", 7));

        var streams = _harness.Store.GetStreams();

        var status = Assert.Single(streams);
        Assert.Equal(new HlsStreamStatus("live", "cam", "0123456789abcdef0123456789abcdef", 7, HlsStreamState.Live, false), status);
        Assert.IsAssignableFrom<IReadOnlyList<HlsStreamStatus>>(streams);
        Assert.Throws<NotSupportedException>(() => ((IList<HlsStreamStatus>)streams).Add(status));
    }

    /// <summary>
    /// Verifies the store's inventory reports playlist availability exactly as <see cref="IHlsSegmentStore.GetPlaylist"/>
    /// serves it: not when opened, not before the first segment is published, then while live and ended; never for a
    /// withdrawn instance, including one replaced while the new instance has not yet published a playlist.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task StoreInventory_PlaylistAvailabilityMatchesGetPlaylist()
    {
        void AssertServed(PublisherSession session, HlsStreamState state, bool available)
        {
            var status = Assert.Single(_harness.Store.GetStreams(), s => s.IncarnationId == session.IncarnationId);
            Assert.Equal((state, available), (status.State, status.PlaylistAvailable));
        }

        var first = NewSession("camera1");
        await _harness.Processor.OnSessionStartedAsync(first, TestContext.Current.CancellationToken);
        AssertServed(first, HlsStreamState.Live, available: false);
        await _harness.ConfigureAsync(first, video: true, audio: false);
        await _harness.VideoAsync(first, 0, idr: true);
        Assert.Null(_harness.Playlist());
        AssertServed(first, HlsStreamState.Live, available: false);

        await _harness.VideoAsync(first, 1000, idr: true);
        Assert.NotNull(_harness.Playlist());
        AssertServed(first, HlsStreamState.Live, available: true);

        await _harness.EndAsync(first);
        Assert.NotNull(_harness.Playlist());
        AssertServed(first, HlsStreamState.Ended, available: true);

        var second = NewSession("camera1");
        await _harness.Processor.OnSessionStartedAsync(second, TestContext.Current.CancellationToken);
        Assert.Null(_harness.Playlist());
        AssertServed(first, HlsStreamState.Withdrawn, available: false);
        AssertServed(second, HlsStreamState.Live, available: false);
    }

    /// <summary>
    /// Verifies the catalog over the real sources through a publication's lifecycle: registered before HLS opens
    /// (pending), live with its media description, ended with the ended playlist retained, withdrawn while its
    /// segments drain after retention, and gone once drained.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Lifecycle_IsReconciledFromRealSources()
    {
        var session = NewSession("camera1");
        Assert.True(_registry.TryAdd(session));
        Assert.True(_catalog.Reconcile());
        Assert.Equal(StreamHlsState.Pending, Entry().Hls.State);
        Assert.False(Entry().Hls.PlaylistAvailable);
        Assert.Null(Entry().Media);

        await _harness.Processor.OnSessionStartedAsync(session, TestContext.Current.CancellationToken);
        await _harness.ConfigureAsync(session, video: true, audio: false);
        _catalog.Reconcile();
        var live = Entry();
        Assert.Equal((session.IncarnationId, StreamPublishingState.Publishing, StreamHlsState.Live), (live.IncarnationId, live.Publishing.State, live.Hls.State));
        Assert.False(live.Hls.PlaylistAvailable);
        Assert.Null(_harness.Playlist());
        Assert.NotNull(live.Media!.Video);
        Assert.Null(live.Media.Audio);
        Assert.Equal(session.Generation, live.Generation);

        await _harness.ConfigureAsync(session, video: false, audio: true);
        _catalog.Reconcile();
        Assert.NotNull(Entry().Media!.Audio);
        Assert.False(Entry().Hls.PlaylistAvailable);

        await SegmentsAsync(session);
        _catalog.Reconcile();
        Assert.Equal((StreamHlsState.Live, true), (Entry().Hls.State, Entry().Hls.PlaylistAvailable));
        Assert.NotNull(_harness.Playlist());
        _registry.Remove(session);
        await _harness.EndAsync(session);
        _catalog.Reconcile();
        var ended = Entry();
        Assert.Equal((StreamPublishingState.Ended, StreamHlsState.Ended, true), (ended.Publishing.State, ended.Hls.State, ended.Hls.PlaylistAvailable));
        Assert.Null(ended.Publishing.EndReason);
        Assert.NotNull(ended.Media);

        await _catalog.ConsumeAsync(StreamEventFactory.Create(StreamEventType.Ended, session, session.EndedAt!.Value, StreamEventReasons.Unpublished), TestContext.Current.CancellationToken);
        Assert.Equal((session.EndedAt, StreamEventReasons.Unpublished), (Entry().Publishing.EndedAt, Entry().Publishing.EndReason));
        _catalog.Reconcile();
        Assert.Equal(StreamEventReasons.Unpublished, Entry().Publishing.EndReason);

        _harness.Time.Advance(TimeSpan.FromMinutes(10));
        _catalog.Reconcile();
        Assert.Equal((StreamPublishingState.Ended, StreamHlsState.Withdrawn, false), (Entry().Publishing.State, Entry().Hls.State, Entry().Hls.PlaylistAvailable));

        _harness.Time.Advance(TimeSpan.FromMinutes(10));
        _catalog.Reconcile();
        Assert.Empty(_catalog.Current.Streams);
    }

    /// <summary>
    /// Verifies a replacement over the real sources: the previous incarnation drains (withdrawn) while the new one is
    /// current, and the recorded lifecycle events applied to the catalog agree with reconciliation.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Replacement_IsReconciledAndEventsAgree()
    {
        var first = NewSession("camera1");
        Assert.True(_registry.TryAdd(first));
        await _harness.Processor.OnSessionStartedAsync(first, TestContext.Current.CancellationToken);
        await _harness.ConfigureAsync(first);
        await SegmentsAsync(first);
        _catalog.Reconcile();
        _registry.Remove(first);
        await _harness.EndAsync(first, PublishEndReason.ClientDisconnected);

        var second = NewSession("camera1");
        Assert.True(_registry.TryAdd(second));
        await _harness.Processor.OnSessionStartedAsync(second, TestContext.Current.CancellationToken);
        foreach (var streamEvent in _harness.StreamEvents.Events)
        {
            await _catalog.ConsumeAsync(streamEvent, TestContext.Current.CancellationToken);
        }

        var incremental = _catalog.Current.FindIncarnation(first.IncarnationId)!;
        Assert.Equal(StreamHlsState.Withdrawn, incremental.Hls.State);

        _catalog.Reconcile();
        Assert.Equal(second.IncarnationId, _catalog.Current.Find("live", "camera1")!.IncarnationId);
        var previous = _catalog.Current.FindIncarnation(first.IncarnationId)!;
        Assert.False(previous.Current);
        Assert.Equal((StreamPublishingState.Ended, StreamHlsState.Withdrawn), (previous.Publishing.State, previous.Hls.State));
    }

    /// <summary>
    /// Test A: an active publisher's HLS stream is withdrawn and fully drained, and the Drained event is never
    /// delivered to the catalog (the store's events are only recorded). Reconciliation alone reports it withdrawn,
    /// still publishing and current, both while draining and after the store has forgotten it.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task LostDrainedEvent_ReconciliationReportsWithdrawn()
    {
        var session = await StartLiveAsync();
        _catalog.Reconcile();
        Assert.Equal((StreamPublishingState.Publishing, StreamHlsState.Live), (Entry().Publishing.State, Entry().Hls.State));

        _harness.Store.CloseStream(session.SessionId, completed: false);
        _catalog.Reconcile();
        AssertActiveWithdrawn(session);
        Assert.Single(_harness.Store.GetStreams(), static s => s.State == HlsStreamState.Withdrawn);

        _harness.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Empty(_harness.Store.GetStreams());
        Assert.Contains(StreamEventType.Drained, _harness.StreamEvents.Types);
        _catalog.Reconcile();
        AssertActiveWithdrawn(session);
    }

    /// <summary>
    /// Test A, without help from the catalog's history: the stream opens, is withdrawn and drains entirely between two
    /// reconciliations (the catalog last saw it pending) and Drained is never delivered; reconciliation still reports
    /// it withdrawn, not pending.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task LostDrainedEvent_DrainedBetweenReconciliations_ReportsWithdrawn()
    {
        var session = NewSession("camera1");
        Assert.True(_registry.TryAdd(session));
        _catalog.Reconcile();
        Assert.Equal(StreamHlsState.Pending, Entry().Hls.State);

        await OpenLiveAsync(session);
        _harness.Store.CloseStream(session.SessionId, completed: false);
        _harness.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Empty(_harness.Store.GetStreams());

        _catalog.Reconcile();
        AssertActiveWithdrawn(session);
    }

    /// <summary>
    /// Test B: after the active publisher's HLS stream has been withdrawn, drained and forgotten by the store, a new
    /// catalog with no event history or previous snapshot, initialized against the same sources, reports it
    /// publishing and withdrawn.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task FreshCatalogAfterDrain_ReportsWithdrawn()
    {
        var session = await StartLiveAsync();
        _harness.Store.CloseStream(session.SessionId, completed: false);
        _harness.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Empty(_harness.Store.GetStreams());

        using var fresh = CreateCatalog();
        Assert.False(fresh.Current.IsInitialized);
        Assert.True(fresh.Reconcile());

        var entry = Assert.Single(fresh.Current.Streams);
        Assert.Equal((session.IncarnationId, true), (entry.IncarnationId, entry.Current));
        Assert.Equal((StreamPublishingState.Publishing, StreamHlsState.Withdrawn, false), (entry.Publishing.State, entry.Hls.State, entry.Hls.PlaylistAvailable));
    }

    /// <summary>Test C: an active publisher whose HLS stream never opened is pending, in an existing and in a fresh catalog.</summary>
    [Fact]
    public void NeverOpenedHls_IsPending()
    {
        var session = NewSession("camera1");
        Assert.True(_registry.TryAdd(session));

        _catalog.Reconcile();
        using var fresh = CreateCatalog();
        fresh.Reconcile();

        Assert.False(session.HlsStreamOpened);
        Assert.Equal(StreamHlsState.Pending, Entry().Hls.State);
        Assert.Equal(StreamHlsState.Pending, fresh.Current.Find("live", "camera1")!.Hls.State);
    }

    /// <summary>
    /// Verifies a stream opened after the snapshot's HLS copy was taken is pending, not misread as drained, although
    /// its flag is already set when the registry is copied.
    /// </summary>
    [Fact]
    public void HlsOpenedDuringAcquisition_IsPendingNotWithdrawn()
    {
        var session = NewSession("camera1");
        Assert.True(_registry.TryAdd(session));
        using var catalog = CreateCatalog(new OpenAfterCopyInventory(_harness.Store, () =>
            _harness.Processor.OnSessionStartedAsync(session, TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult()));

        catalog.Reconcile();

        Assert.True(session.HlsStreamOpened);
        Assert.Equal(StreamHlsState.Pending, catalog.Current.Find("live", "camera1")!.Hls.State);
        catalog.Reconcile();
        Assert.Equal(StreamHlsState.Live, catalog.Current.Find("live", "camera1")!.Hls.State);
    }

    /// <summary>
    /// Test D: the drained-HLS state goes with its RTMP incarnation. When the publisher ends, the incarnation leaves
    /// the catalog (existing and fresh); a new publisher of the same stream is independent: pending until its own HLS
    /// stream opens, then live, with a larger generation.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task RtmpEnd_CleansUpAndNextIncarnationIsIndependent()
    {
        var first = await StartLiveAsync();
        _harness.Store.CloseStream(first.SessionId, completed: false);
        _harness.Time.Advance(TimeSpan.FromMinutes(10));
        _catalog.Reconcile();
        AssertActiveWithdrawn(first);

        _registry.Remove(first);
        Assert.True(first.TryEnd(PublishEndReason.ClientDisconnected, _harness.Time.GetUtcNow()));
        _catalog.Reconcile();
        Assert.Empty(_catalog.Current.Streams);
        using (var fresh = CreateCatalog())
        {
            fresh.Reconcile();
            Assert.Empty(fresh.Current.Streams);
        }

        var second = NewSession("camera1");
        Assert.True(_registry.TryAdd(second));
        Assert.False(second.HlsStreamOpened);
        _catalog.Reconcile();
        var pending = Entry();
        Assert.Equal((second.IncarnationId, StreamPublishingState.Publishing, StreamHlsState.Pending), (pending.IncarnationId, pending.Publishing.State, pending.Hls.State));
        Assert.True(pending.Generation > first.Generation);

        await OpenLiveAsync(second);
        _catalog.Reconcile();
        Assert.Equal((second.IncarnationId, StreamHlsState.Live), (Entry().IncarnationId, Entry().Hls.State));
        Assert.Null(_catalog.Current.FindIncarnation(first.IncarnationId));
    }

    /// <summary>Creates a session for application <c>live</c>.</summary>
    /// <param name="identity">The stream identity.</param>
    /// <returns>The session.</returns>
    private static PublisherSession NewSession(string identity) =>
        new(1, null, "live", identity, "live", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    /// <summary>Registers <c>live/camera1</c> and opens its HLS stream with segments.</summary>
    /// <returns>The session.</returns>
    private async Task<PublisherSession> StartLiveAsync()
    {
        var session = NewSession("camera1");
        Assert.True(_registry.TryAdd(session));
        await OpenLiveAsync(session);
        return session;
    }

    /// <summary>Opens a registered session's HLS stream through the media and HLS stages and publishes segments.</summary>
    /// <param name="session">The session.</param>
    /// <returns>A task.</returns>
    private async Task OpenLiveAsync(PublisherSession session)
    {
        await _harness.Processor.OnSessionStartedAsync(session, TestContext.Current.CancellationToken);
        await _harness.ConfigureAsync(session, video: true, audio: false);
        await SegmentsAsync(session);
        Assert.True(session.HlsStreamOpened);
        Assert.Contains(_harness.Store.GetStreams(), s => s.IncarnationId == session.IncarnationId && s.State == HlsStreamState.Live);
    }

    /// <summary>Asserts <c>live/camera1</c> is the given incarnation, current, publishing and withdrawn.</summary>
    /// <param name="session">The session.</param>
    private void AssertActiveWithdrawn(PublisherSession session)
    {
        var entry = Assert.Single(_catalog.Current.Streams);
        Assert.Equal((session.IncarnationId, true), (entry.IncarnationId, entry.Current));
        Assert.Equal((StreamPublishingState.Publishing, StreamHlsState.Withdrawn, false), (entry.Publishing.State, entry.Hls.State, entry.Hls.PlaylistAvailable));
    }

    /// <summary>Creates another catalog over the same registry, media stage and store.</summary>
    /// <param name="inventory">The HLS inventory; the store when <see langword="null"/>.</param>
    /// <returns>The catalog, not yet reconciled.</returns>
    private StreamCatalog CreateCatalog(IHlsStreamInventory? inventory = null) => new(
        new RuntimeStreamInventorySource(_registry, _harness.Processor, inventory ?? _harness.Store),
        new StreamCatalogMetrics(_services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()),
        _harness.Time,
        NullLogger<StreamCatalog>.Instance);

    /// <summary>Returns the current entry of <c>live/camera1</c>.</summary>
    /// <returns>The entry.</returns>
    private StreamCatalogEntry Entry() => _catalog.Current.Find("live", "camera1") ?? throw new InvalidOperationException("No entry.");

    /// <summary>Sends enough keyframes to complete segments.</summary>
    /// <param name="session">The session.</param>
    /// <returns>A task.</returns>
    private async Task SegmentsAsync(PublisherSession session)
    {
        for (uint ms = 0; ms <= 3000; ms += 1000)
        {
            await _harness.VideoAsync(session, ms, idr: true);
        }
    }

    /// <summary>An inventory that runs an action once, right after taking its copy, to open a stream mid-acquisition.</summary>
    /// <param name="inner">The store.</param>
    /// <param name="afterCopy">The action.</param>
    private sealed class OpenAfterCopyInventory(IHlsStreamInventory inner, Action afterCopy) : IHlsStreamInventory
    {
        /// <summary>The action, until run.</summary>
        private Action? _afterCopy = afterCopy;

        /// <inheritdoc />
        public IReadOnlyList<HlsStreamStatus> GetStreams()
        {
            var copy = inner.GetStreams();
            Interlocked.Exchange(ref _afterCopy, null)?.Invoke();
            return copy;
        }
    }
}
