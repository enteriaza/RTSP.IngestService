// Unit tests of the stream catalog over a scriptable source: initialization and reconciliation, the distinct RTMP and
// HLS states, incarnation safety against stale, duplicate and out-of-order events and snapshots, media descriptions,
// request coalescing, failure handling, loss detection and atomic publication under concurrency. Concurrent changes
// during snapshot acquisition are injected deterministically through the source; nothing sleeps.

using Microsoft.Extensions.Logging;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Streams;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Streams;

/// <summary>Tests for <see cref="StreamCatalog"/>.</summary>
public sealed class StreamCatalogTests : IDisposable
{
    /// <summary>An incarnation whose identifier sorts last but whose generation is older.</summary>
    private const string Old = "ffffffffffffffffffffffffffffffff";

    /// <summary>An incarnation whose identifier sorts first but whose generation is newer.</summary>
    private const string New = "00000000000000000000000000000000";

    /// <summary>A third incarnation, of another stream.</summary>
    private const string Third = "11111111111111111111111111111111";

    /// <summary>The fixture.</summary>
    private readonly CatalogFixture _f = new();

    /// <summary>Gets the catalog.</summary>
    private StreamCatalog Catalog => _f.Catalog;

    /// <inheritdoc />
    public void Dispose() => _f.Dispose();

    /// <summary>Verifies the catalog is uninitialized until its first reconciliation, then initialized even if empty.</summary>
    [Fact]
    public void Reconcile_InitializesCatalog()
    {
        Assert.False(Catalog.Current.IsInitialized);
        Assert.Null(Catalog.Current.ReconciledAt);

        Assert.True(Catalog.Reconcile());

        Assert.True(Catalog.Current.IsInitialized);
        Assert.Empty(Catalog.Current.Streams);
        Assert.Equal(_f.Time.GetUtcNow(), Catalog.Current.ReconciledAt);
        Assert.Contains(_f.Logs.GetSnapshot(), static r => r.Id.Id == 9201);
        Assert.Equal("success", _f.Reconciliations.LastMeasurement!.Tags["result"]);
    }

    /// <summary>Verifies startup with streams already active reports them completely.</summary>
    [Fact]
    public void Reconcile_ReportsAlreadyActiveStreams()
    {
        var media = new StreamMediaDescription { Video = TestMedia.Video(), Audio = TestMedia.Audio() };
        _f.Source.Publish(Old, 1, "a", media).Store(Old, 1, HlsStreamState.Live, "a").Publish(New, 2, "b");

        Catalog.Reconcile();

        var a = Catalog.Current.Find("live", "a")!;
        Assert.Equal((StreamPublishingState.Publishing, StreamHlsState.Live, true), (a.Publishing.State, a.Hls.State, a.Hls.PlaylistAvailable));
        Assert.Equal(media, a.Media);
        Assert.Equal(DateTimeOffset.UnixEpoch, a.Publishing.StartedAt);
        var b = Catalog.Current.Find("live", "b")!;
        Assert.Equal((StreamPublishingState.Publishing, StreamHlsState.Pending, false), (b.Publishing.State, b.Hls.State, b.Hls.PlaylistAvailable));
        Assert.Null(b.Media);
    }

    /// <summary>Verifies the HLS state is reported independently of the RTMP state in every combination.</summary>
    /// <param name="publishing">Whether the session is registered and publishing.</param>
    /// <param name="store">The HLS store state, or <see langword="null"/> if absent.</param>
    /// <param name="expectedPublishing">The expected publishing state.</param>
    /// <param name="expectedHls">The expected HLS state, or <see langword="null"/> for no entry.</param>
    [Theory]
    [InlineData(true, null, StreamPublishingState.Publishing, StreamHlsState.Pending)]
    [InlineData(true, HlsStreamState.Live, StreamPublishingState.Publishing, StreamHlsState.Live)]
    [InlineData(true, HlsStreamState.Withdrawn, StreamPublishingState.Publishing, StreamHlsState.Withdrawn)]
    [InlineData(false, HlsStreamState.Ended, StreamPublishingState.Ended, StreamHlsState.Ended)]
    [InlineData(false, HlsStreamState.Withdrawn, StreamPublishingState.Ended, StreamHlsState.Withdrawn)]
    [InlineData(false, HlsStreamState.Live, StreamPublishingState.Ended, StreamHlsState.Live)]
    [InlineData(false, null, StreamPublishingState.Ended, null)]
    public void Reconcile_ReportsDistinctRtmpAndHlsStates(bool publishing, HlsStreamState? store, StreamPublishingState expectedPublishing, StreamHlsState? expectedHls)
    {
        if (publishing)
        {
            _f.Source.Publish(Old, 1);
        }

        if (store is { } state)
        {
            _f.Source.Store(Old, 1, state);
        }

        Catalog.Reconcile();

        var entry = Catalog.Current.Find("live", "cam");
        if (expectedHls is null)
        {
            Assert.Null(entry);
            return;
        }

        Assert.Equal((expectedPublishing, expectedHls.Value), (entry!.Publishing.State, entry.Hls.State));
        Assert.Equal(expectedHls is StreamHlsState.Live or StreamHlsState.Ended, entry.Hls.PlaylistAvailable);
    }

    /// <summary>
    /// Verifies the complete transition pending → live without a playlist → live with a playlist: availability is
    /// taken only from the store, a live stream without a playlist is unsettled (so the service re-reconciles), and it
    /// settles once the store reports the playlist.
    /// </summary>
    [Fact]
    public void LiveWithoutPlaylist_IsUnavailableUntilStoreReportsIt()
    {
        _f.Source.Publish(Old, 1);
        Catalog.Reconcile();
        Assert.Equal((StreamHlsState.Pending, false), Hls());
        Assert.False(Catalog.HasUnsettled);

        _f.Source.Store(Old, 1, HlsStreamState.Live, playlistAvailable: false);
        Catalog.Reconcile();
        Assert.Equal((StreamHlsState.Live, false), Hls());
        Assert.True(Catalog.HasUnsettled);
        Catalog.Reconcile();
        Assert.Equal((StreamHlsState.Live, false), Hls());
        Assert.Empty(_f.RequestReasons());

        _f.Source.Hls.Clear();
        _f.Source.Store(Old, 1, HlsStreamState.Live, playlistAvailable: true);
        Catalog.Reconcile();
        Assert.Equal((StreamHlsState.Live, true), Hls());
        Assert.False(Catalog.HasUnsettled);
    }

    /// <summary>
    /// Verifies availability is never inferred from registration, an HLS open, the entry's existence, media
    /// descriptions or lifecycle events: events applied to a live stream without a playlist keep it unavailable, and
    /// only a reconciliation that reads the store's availability changes it, keeping the event's media.
    /// </summary>
    [Fact]
    public void Events_NeverMakePlaylistAvailable()
    {
        var media = new StreamMediaDescription { Video = TestMedia.Video(), Audio = TestMedia.Audio() };
        _f.Source.Publish(Old, 1).Store(Old, 1, HlsStreamState.Live, playlistAvailable: false);
        Catalog.Reconcile();

        _f.Deliver(_f.Event(StreamEventType.Published, Old));
        _f.Deliver(_f.Event(StreamEventType.MediaDescribed, Old, media: media));
        Assert.Equal((StreamHlsState.Live, false), Hls());
        Assert.Equal(media, Catalog.Current.Find("live", "cam")!.Media);
        _f.Source.Publishing[0] = _f.Source.Publishing[0] with { Media = media };

        _f.Source.DuringAcquire = () => _f.Source.Hls[0] = _f.Source.Hls[0] with { PlaylistAvailable = true };
        Catalog.Reconcile();
        Assert.Equal((StreamHlsState.Live, false), Hls());
        Catalog.Reconcile();
        Assert.Equal((StreamHlsState.Live, true), Hls());
        Assert.Equal(media, Catalog.Current.Find("live", "cam")!.Media);

        _f.Deliver(_f.Event(StreamEventType.Ended, Old, StreamEventReasons.Unpublished));
        Assert.Equal((StreamPublishingState.Ended, StreamHlsState.Live, true), (Catalog.Current.Find("live", "cam")!.Publishing.State, Hls().State, Hls().Available));
    }

    /// <summary>
    /// Verifies a snapshot still showing the playlist available cannot undo a withdrawal applied while it was being
    /// acquired: the entry stays withdrawn and unavailable.
    /// </summary>
    [Fact]
    public void StaleAvailableSnapshot_CannotUndoWithdrawal()
    {
        _f.Source.Publish(Old, 1).Store(Old, 1, HlsStreamState.Live, playlistAvailable: true);
        Catalog.Reconcile();
        _f.Source.DuringAcquire = () => _f.Deliver(_f.Event(StreamEventType.PlaylistWithdrawn, Old, StreamEventReasons.Aborted));

        Catalog.Reconcile();

        Assert.Equal((StreamHlsState.Withdrawn, false), Hls());
    }

    /// <summary>
    /// Verifies replacement keeps availability per incarnation: the new incarnation is current and live without a
    /// playlist while the previous one's ended playlist is still reported available, and withdrawing the previous one
    /// does not touch the new one.
    /// </summary>
    [Fact]
    public void Replacement_AvailabilityIsPerIncarnation()
    {
        _f.Source.Store(Old, 1, HlsStreamState.Ended).Publish(New, 2).Store(New, 2, HlsStreamState.Live, playlistAvailable: false);
        Catalog.Reconcile();

        Assert.Equal((New, StreamHlsState.Live, false), (Catalog.Current.Find("live", "cam")!.IncarnationId, Hls().State, Hls().Available));
        var previous = Catalog.Current.FindIncarnation(Old)!;
        Assert.Equal((false, StreamHlsState.Ended, true), (previous.Current, previous.Hls.State, previous.Hls.PlaylistAvailable));

        _f.Deliver(_f.Event(StreamEventType.PlaylistWithdrawn, Old, StreamEventReasons.Replaced));
        Assert.Equal((StreamHlsState.Withdrawn, false), (Catalog.Current.FindIncarnation(Old)!.Hls.State, Catalog.Current.FindIncarnation(Old)!.Hls.PlaylistAvailable));
        Assert.Equal((StreamHlsState.Live, false), Hls());
    }

    /// <summary>
    /// Verifies recovery from lost events reports actual availability: the Ended, PlaylistWithdrawn and Drained
    /// events are dropped, and the reconciliation requested by the loss reports the incarnation withdrawn and
    /// unavailable while draining, then removes it once drained.
    /// </summary>
    [Fact]
    public void LostEvents_ReconciliationReportsActualAvailability()
    {
        _f.Source.Publish(Old, 1).Store(Old, 1, HlsStreamState.Live, playlistAvailable: true);
        Catalog.Reconcile();
        _f.Source.Publishing.Clear();
        _f.Source.Hls.Clear();
        _f.Source.Store(Old, 1, HlsStreamState.Withdrawn, playlistAvailable: false);

        _f.Dropped = 3;
        _f.Deliver(_f.Event(StreamEventType.Published, New));
        Assert.Contains("loss", _f.RequestReasons());
        Catalog.Reconcile();
        Assert.Equal((StreamPublishingState.Ended, StreamHlsState.Withdrawn, false), (Catalog.Current.Find("live", "cam")!.Publishing.State, Hls().State, Hls().Available));

        _f.Source.Hls.Clear();
        Catalog.Reconcile();
        Assert.Empty(Catalog.Current.Streams);
    }

    /// <summary>
    /// Verifies an ended RTMP session whose HLS is still live is reported unsettled (for the service's settle timer)
    /// without requesting anything itself, and settles once the store shows the stream ended.
    /// </summary>
    [Fact]
    public void Reconcile_UnsettledSnapshot_IsReportedUntilSettled()
    {
        _f.Source.Store(Old, 1, HlsStreamState.Live);

        Catalog.Reconcile();

        Assert.True(Catalog.HasUnsettled);
        Assert.Empty(_f.RequestReasons());
        _f.Source.Forget(Old);
        _f.Source.Store(Old, 1, HlsStreamState.Ended);
        Catalog.Reconcile();
        Assert.False(Catalog.HasUnsettled);
        Assert.Equal(StreamHlsState.Ended, Catalog.Current.Find("live", "cam")!.Hls.State);
    }

    /// <summary>Verifies entries absent from a later snapshot are removed, and a failed reconciliation keeps the previous catalog.</summary>
    [Fact]
    public void Reconcile_RemovesVanishedEntries_AndFailureKeepsPreviousCatalog()
    {
        _f.Source.Publish(Old, 1);
        Catalog.Reconcile();
        var good = Catalog.Current;

        _f.Source.Forget(Old);
        _f.Source.Fail = true;
        Assert.False(Catalog.Reconcile());
        Assert.Same(good, Catalog.Current);
        Assert.Equal("failure", _f.Reconciliations.LastMeasurement!.Tags["result"]);
        var failure = Assert.Single(_f.Logs.GetSnapshot(), static r => r.Id.Id == 9200);
        Assert.Equal(LogLevel.Warning, failure.Level);

        _f.Source.Fail = false;
        Assert.True(Catalog.Reconcile());
        Assert.Empty(Catalog.Current.Streams);
    }

    /// <summary>
    /// Verifies a reconciliation keeps the entries its snapshot did not change, replaces the changed one, removes the
    /// vanished one, and still publishes a new reconciled state.
    /// </summary>
    [Fact]
    public void Reconcile_ReplacesOnlyChangedEntries()
    {
        _f.Source.Publish(Old, 1, "a").Store(Old, 1, HlsStreamState.Live, "a")
            .Publish(New, 2, "b").Store(New, 2, HlsStreamState.Live, "b")
            .Publish(Third, 3, "c");
        Catalog.Reconcile();
        var before = Catalog.Current;

        _f.Source.Hls.RemoveAll(static h => h.IncarnationId == New);
        _f.Source.Store(New, 2, HlsStreamState.Ended, "b");
        _f.Source.Forget(Third);
        _f.Time.Advance(TimeSpan.FromSeconds(1));
        Catalog.Reconcile();
        var after = Catalog.Current;

        Assert.Same(before.FindIncarnation(Old), after.FindIncarnation(Old));
        Assert.Equal(StreamHlsState.Ended, after.FindIncarnation(New)!.Hls.State);
        Assert.Equal(StreamHlsState.Live, before.FindIncarnation(New)!.Hls.State);
        Assert.Null(after.FindIncarnation(Third));
        Assert.Equal([Old, New], after.Streams.Select(static s => s.IncarnationId));
        Assert.True(after.Version > before.Version);
        Assert.Equal(_f.Time.GetUtcNow(), after.ReconciledAt);
    }

    /// <summary>Verifies a failed first reconciliation leaves the catalog uninitialized.</summary>
    [Fact]
    public void Reconcile_FailedFirst_LeavesUninitialized()
    {
        _f.Source.Fail = true;
        Assert.False(Catalog.Reconcile());
        Assert.Same(StreamCatalogSnapshot.Empty, Catalog.Current);
    }

    /// <summary>
    /// Verifies the newest incarnation is chosen by generation, not by identifier, and both are listed while the
    /// older one drains.
    /// </summary>
    [Fact]
    public void Replacement_NewestGenerationIsCurrent()
    {
        _f.Source.Store(Old, 1, HlsStreamState.Withdrawn).Publish(New, 2).Store(New, 2, HlsStreamState.Live);

        Catalog.Reconcile();

        Assert.Equal(New, Catalog.Current.Find("live", "cam")!.IncarnationId);
        Assert.Equal([New, Old], Catalog.Current.Streams.Select(static s => s.IncarnationId));
        Assert.Equal([true, false], Catalog.Current.Streams.Select(static s => s.Current));
    }

    /// <summary>Verifies events about an older incarnation never mutate the newer one of the same stream.</summary>
    [Fact]
    public void StaleEvents_DoNotAffectNewerIncarnation()
    {
        _f.Source.Store(Old, 1, HlsStreamState.Withdrawn).Publish(New, 2).Store(New, 2, HlsStreamState.Live);
        Catalog.Reconcile();

        _f.Deliver(_f.Event(StreamEventType.Ended, Old, StreamEventReasons.Unpublished));
        _f.Deliver(_f.Event(StreamEventType.PlaylistWithdrawn, Old, StreamEventReasons.Replaced));
        _f.Deliver(_f.Event(StreamEventType.MediaDescribed, Old, media: new StreamMediaDescription { Audio = TestMedia.Audio() }));
        _f.Deliver(_f.Event(StreamEventType.Drained, Old));

        var current = Assert.Single(Catalog.Current.Streams);
        Assert.Equal(New, current.IncarnationId);
        Assert.Equal((StreamPublishingState.Publishing, StreamHlsState.Live), (current.Publishing.State, current.Hls.State));
        Assert.Null(current.Media);
    }

    /// <summary>Verifies events never create entries; an unknown incarnation requests reconciliation instead.</summary>
    [Fact]
    public void UnknownIncarnationEvent_RequestsReconciliationWithoutCreatingEntry()
    {
        Catalog.Reconcile();

        _f.Deliver(_f.Event(StreamEventType.Published, New));
        _f.Deliver(_f.Event(StreamEventType.MediaDescribed, New, media: new StreamMediaDescription { Video = TestMedia.Video() }));

        Assert.Empty(Catalog.Current.Streams);
        Assert.Equal(["event", "event"], _f.RequestReasons());
    }

    /// <summary>Verifies duplicate events are idempotent: the second leaves the published state untouched.</summary>
    [Fact]
    public void DuplicateEvents_AreIdempotent()
    {
        _f.Source.Publish(Old, 1).Store(Old, 1, HlsStreamState.Live);
        Catalog.Reconcile();
        var ended = _f.Event(StreamEventType.Ended, Old, StreamEventReasons.ClientDisconnected);
        var withdrawn = _f.Event(StreamEventType.PlaylistWithdrawn, Old, StreamEventReasons.Aborted);

        _f.Deliver(ended);
        _f.Deliver(withdrawn);
        var after = Catalog.Current;
        _f.Deliver(ended);
        _f.Deliver(withdrawn);
        _f.Deliver(_f.Event(StreamEventType.Published, Old));

        Assert.Same(after, Catalog.Current);
        var entry = Assert.Single(after.Streams);
        Assert.Equal((StreamPublishingState.Ended, StreamHlsState.Withdrawn), (entry.Publishing.State, entry.Hls.State));
        Assert.Equal((ended.Timestamp, StreamEventReasons.ClientDisconnected), (entry.Publishing.EndedAt, entry.Publishing.EndReason));
    }

    /// <summary>
    /// Verifies out-of-order and late events cannot resurrect a drained incarnation, and delayed Ended or Withdrawn
    /// never move a state backwards.
    /// </summary>
    [Fact]
    public void OutOfOrderEvents_CannotResurrectOrRegress()
    {
        _f.Source.Publish(Old, 1).Store(Old, 1, HlsStreamState.Live);
        Catalog.Reconcile();

        _f.Deliver(_f.Event(StreamEventType.Ended, Old, StreamEventReasons.Unpublished));
        _f.Deliver(_f.Event(StreamEventType.Drained, Old));
        Assert.Empty(Catalog.Current.Streams);
        var requests = _f.RequestReasons().Count();

        _f.Deliver(_f.Event(StreamEventType.PlaylistWithdrawn, Old, StreamEventReasons.Aborted));
        _f.Deliver(_f.Event(StreamEventType.Published, Old));
        _f.Deliver(_f.Event(StreamEventType.MediaDescribed, Old, media: new StreamMediaDescription { Video = TestMedia.Video() }));

        Assert.Empty(Catalog.Current.Streams);
        Assert.Equal(requests, _f.RequestReasons().Count());
    }

    /// <summary>
    /// Verifies a snapshot whose acquisition began before a Drained event cannot reintroduce the drained incarnation,
    /// and the tombstone is released by the next reconciliation.
    /// </summary>
    [Fact]
    public void StaleSnapshot_CannotResurrectDrainedIncarnation()
    {
        _f.Source.Store(Old, 1, HlsStreamState.Withdrawn);
        Catalog.Reconcile();
        _f.Source.DuringAcquire = () =>
        {
            _f.Deliver(_f.Event(StreamEventType.Drained, Old));
            _f.Source.Forget(Old);
        };

        Catalog.Reconcile();
        Assert.Empty(Catalog.Current.Streams);

        Catalog.Reconcile();
        Assert.Empty(Catalog.Current.Streams);
    }

    /// <summary>Verifies an event applied during acquisition wins over the older snapshot that does not show it.</summary>
    [Fact]
    public void EventDuringAcquisition_IsNotUndoneBySnapshot()
    {
        var video = new StreamMediaDescription { Video = TestMedia.Video() };
        var both = new StreamMediaDescription { Video = TestMedia.Video(), Audio = TestMedia.Audio() };
        _f.Source.Publish(Old, 1, media: video).Store(Old, 1, HlsStreamState.Live);
        Catalog.Reconcile();
        _f.Source.DuringAcquire = () =>
        {
            _f.Deliver(_f.Event(StreamEventType.MediaDescribed, Old, media: both));
            _f.Deliver(_f.Event(StreamEventType.Ended, Old, StreamEventReasons.Unpublished));
            _f.Deliver(_f.Event(StreamEventType.PlaylistWithdrawn, Old, StreamEventReasons.Aborted));
        };

        Catalog.Reconcile();

        var entry = Catalog.Current.Find("live", "cam")!;
        Assert.Equal(both, entry.Media);
        Assert.Equal((StreamPublishingState.Ended, StreamHlsState.Withdrawn), (entry.Publishing.State, entry.Hls.State));
        Assert.False(entry.Hls.PlaylistAvailable);
        Assert.Equal(StreamEventReasons.Unpublished, entry.Publishing.EndReason);
    }

    /// <summary>Verifies a drained HLS stream of a still-publishing incarnation keeps the entry, as withdrawn.</summary>
    [Fact]
    public void DrainedWhilePublishing_KeepsEntryAsWithdrawn()
    {
        _f.Source.Publish(Old, 1).Store(Old, 1, HlsStreamState.Live);
        Catalog.Reconcile();

        _f.Deliver(_f.Event(StreamEventType.Drained, Old));
        Assert.Equal(StreamHlsState.Withdrawn, Catalog.Current.Find("live", "cam")!.Hls.State);

        _f.Source.Hls.Clear();
        Catalog.Reconcile();
        var entry = Catalog.Current.Find("live", "cam")!;
        Assert.Equal((StreamPublishingState.Publishing, StreamHlsState.Withdrawn), (entry.Publishing.State, entry.Hls.State));
        Assert.False(entry.Hls.PlaylistAvailable);
    }

    /// <summary>
    /// Verifies MediaDescribed replaces the whole description: video only, then audio added, then each track changing
    /// independently, without merging or inventing fields.
    /// </summary>
    [Fact]
    public void MediaDescribed_ReplacesCompleteDescription()
    {
        _f.Source.Publish(Old, 1);
        Catalog.Reconcile();
        Assert.Null(Catalog.Current.Find("live", "cam")!.Media);

        StreamMediaDescription[] sequence =
        [
            new() { Video = TestMedia.Video() },
            new() { Video = TestMedia.Video(), Audio = TestMedia.Audio() },
            new() { Video = TestMedia.Video(2), Audio = TestMedia.Audio() },
            new() { Video = TestMedia.Video(2), Audio = TestMedia.Audio(2) with { SampleRate = 44100, Channels = null } },
            new() { Audio = TestMedia.Audio(3) },
        ];
        foreach (var media in sequence)
        {
            _f.Deliver(_f.Event(StreamEventType.MediaDescribed, Old, media: media));
            Assert.Equal(media, Catalog.Current.Find("live", "cam")!.Media);
        }

        _f.Deliver(_f.Event(StreamEventType.MediaDescribed, Old));
        Assert.Equal(sequence[^1], Catalog.Current.Find("live", "cam")!.Media);
    }

    /// <summary>Verifies the snapshot's media wins when no event is newer, and a session's last media survives its end.</summary>
    [Fact]
    public void SnapshotMedia_IsAuthoritativeWhenNoNewerEvent()
    {
        _f.Source.Publish(Old, 1, media: new StreamMediaDescription { Video = TestMedia.Video() }).Store(Old, 1, HlsStreamState.Live);
        Catalog.Reconcile();
        _f.Source.Publishing.Clear();
        _f.Source.Publish(Old, 1, media: new StreamMediaDescription { Audio = TestMedia.Audio() });
        Catalog.Reconcile();
        Assert.Equal(new StreamMediaDescription { Audio = TestMedia.Audio() }, Catalog.Current.Find("live", "cam")!.Media);

        _f.Source.Publishing.Clear();
        _f.Source.Hls.Clear();
        _f.Source.Store(Old, 1, HlsStreamState.Ended);
        Catalog.Reconcile();
        Assert.Equal(new StreamMediaDescription { Audio = TestMedia.Audio() }, Catalog.Current.Find("live", "cam")!.Media);
    }

    /// <summary>Verifies an Ended event requests a reconciliation, because the HLS stream closes without an event.</summary>
    [Fact]
    public void EndedEvent_RequestsReconciliation()
    {
        _f.Source.Publish(Old, 1).Store(Old, 1, HlsStreamState.Live);
        Catalog.Reconcile();

        _f.Deliver(_f.Event(StreamEventType.Ended, Old, StreamEventReasons.Unpublished));

        Assert.Equal(["event"], _f.RequestReasons());
    }

    /// <summary>Verifies an increase of the channel's dropped count requests a reconciliation once per increase.</summary>
    [Fact]
    public void DroppedEvents_RequestReconciliation()
    {
        Catalog.Reconcile();
        _f.Source.Publish(Old, 1);
        Catalog.Reconcile();

        _f.Dropped = 3;
        _f.Deliver(_f.Event(StreamEventType.Published, Old));
        _f.Deliver(_f.Event(StreamEventType.Published, Old));

        Assert.Equal(["loss"], _f.RequestReasons());
        Assert.Single(_f.Logs.GetSnapshot(), static r => r.Id.Id == 9202);
    }

    /// <summary>
    /// Verifies requests coalesce into one pending request: many requests release one wait, and a request after the
    /// wait was taken makes another pending rather than being lost.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Requests_CoalesceIntoOnePending()
    {
        for (var i = 0; i < 5; i++)
        {
            Catalog.RequestReconciliation(StreamCatalogTrigger.Periodic);
        }

        await Catalog.WaitForRequestAsync(TestContext.Current.CancellationToken).WithinSafetyTimeout();
        var second = Catalog.WaitForRequestAsync(TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);
        Assert.Equal(4, _f.Coalesced.GetMeasurementSnapshot().Sum(static m => m.Value));

        Catalog.RequestReconciliation(StreamCatalogTrigger.Event);
        await second.WithinSafetyTimeout();
        Assert.False(Catalog.WaitForRequestAsync(TestContext.Current.CancellationToken).IsCompleted);
    }

    /// <summary>Verifies a cancelled wait for a request ends with cancellation.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task WaitForRequest_HonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        var wait = Catalog.WaitForRequestAsync(cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    /// <summary>
    /// Verifies readers always see a complete, internally consistent snapshot while events and reconciliations
    /// publish concurrently: the listing and lookups agree, versions never decrease, and exactly one entry per stream
    /// is current.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ConcurrentReadersSeeConsistentSnapshots()
    {
        const int Streams = 20;
        for (var i = 0; i < Streams; i++)
        {
            _f.Source.Publish($"{i:x32}", i + 1, $"s{i}").Store($"{i:x32}", i + 1, HlsStreamState.Live, $"s{i}");
        }

        Catalog.Reconcile();
        using var stop = new CancellationTokenSource();
        var violations = 0;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            long version = 0;
            while (!stop.IsCancellationRequested)
            {
                var snapshot = Catalog.Current;
                if (snapshot.Version < version
                    || snapshot.Streams.Count != snapshot.Streams.Select(static s => s.IncarnationId).Distinct(StringComparer.Ordinal).Count()
                    || snapshot.Streams.Any(s => snapshot.FindIncarnation(s.IncarnationId) != s)
                    || snapshot.Streams.GroupBy(static s => s.Stream).Any(static g => g.Count(static s => s.Current) != 1))
                {
                    Interlocked.Increment(ref violations);
                }

                version = snapshot.Version;
            }
        })).ToArray();

        var events = Task.Run(() =>
        {
            for (var round = 0; round < 200; round++)
            {
                var i = round % Streams;
                _f.Deliver(_f.Event(StreamEventType.MediaDescribed, $"{i:x32}", media: new StreamMediaDescription { Video = TestMedia.Video(round + 1) }, stream: $"s{i}"));
            }
        }, TestContext.Current.CancellationToken);
        var reconciliations = Task.Run(() =>
        {
            for (var round = 0; round < 20; round++)
            {
                Assert.True(Catalog.Reconcile());
            }
        }, TestContext.Current.CancellationToken);
        await Task.WhenAll(events, reconciliations).WithinSafetyTimeout();
        await stop.CancelAsync();
        await Task.WhenAll(readers).WithinSafetyTimeout();

        Assert.Equal(0, violations);
        Assert.Equal(Streams, Catalog.Current.Streams.Count);
    }

    /// <summary>Returns the HLS state and availability of <c>live/cam</c>'s current entry.</summary>
    /// <returns>The state and availability.</returns>
    private (StreamHlsState State, bool Available) Hls()
    {
        var hls = Catalog.Current.Find("live", "cam")!.Hls;
        return (hls.State, hls.PlaylistAvailable);
    }
}
