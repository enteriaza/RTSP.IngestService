// Tests for the bounded in-memory segment store: atomic publication and unchanged state on failure, stream ownership,
// session limits and key numbering, RFC 8216 availability (deadlines from the longest published playlist, playlist
// withdrawal, ended-stream retention), storage pressure that rejects instead of releasing available segments, both
// per stream and globally, initialization segment release, reads that outlive expiry, and concurrent readers while a
// writer publishes and segments expire. Time is a FakeTimeProvider; there are no delays.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Hls;

/// <summary>
/// Tests for <see cref="InMemoryHlsSegmentStore"/>.
/// </summary>
public sealed class InMemoryHlsSegmentStoreTests : IDisposable
{
    /// <summary>A segment size that fits three times in one megabyte.</summary>
    private const int Large = 300 * 1024;

    /// <summary>Owns the meter factory.</summary>
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();

    /// <summary>The clock.</summary>
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

    /// <inheritdoc />
    public void Dispose() => _services.Dispose();

    /// <summary>Verifies a published playlist lists only stored segments, which are returned unchanged.</summary>
    [Fact]
    public void Append_PublishesAtomically()
    {
        var store = Create(new HlsOptions());
        var session = Open(store);
        Assert.Null(store.GetPlaylist("live", "cam"));
        Assert.Throws<InvalidOperationException>(() => store.AppendSegment(session, Media(0, 10), HlsPlaylistTests.Entry(0, 1.0)));

        Assert.Equal(HlsAppendResult.Published, store.AddInitialization(session, Init(0)));
        Assert.Null(store.GetPlaylist("live", "cam"));
        var segment = Media(0, 10);
        Assert.Equal(HlsAppendResult.Published, store.AppendSegment(session, segment, HlsPlaylistTests.Entry(0, 1.0)));

        var playlist = store.GetPlaylist("live", "cam")!;
        Assert.Equal((0L, false), (playlist.MediaSequence, playlist.Ended));
        Assert.Equal(["segment-0.m4s"], playlist.Segments.Select(static s => s.Name));
        Assert.Contains($"#EXT-X-MAP:URI=\"{store.Generation}/init-0.mp4\"", playlist.Text, StringComparison.Ordinal);
        Assert.Contains($"\n{store.Generation}/segment-0.m4s\n", playlist.Text, StringComparison.Ordinal);
        Assert.Same(segment, store.GetSegment("live", "cam", "segment-0.m4s"));
        Assert.NotNull(store.GetSegment("live", "cam", "init-0.mp4"));
        Assert.Null(store.GetSegment("live", "cam", "segment-1.m4s"));
        Assert.Null(store.GetSegment("live", "other", "segment-0.m4s"));
        Assert.Equal(10 + 100, store.TotalBytes);
        Assert.Throws<InvalidOperationException>(() => store.AppendSegment(Guid.NewGuid(), Media(1, 10), HlsPlaylistTests.Entry(1, 1.0)));
    }

    /// <summary>
    /// Verifies a live playlist at the entry cap that cannot shrink refuses a segment with <see cref="HlsAppendResult.PlaylistFull"/>
    /// and changes nothing, accepts one long enough to keep three target durations, and when withdrawn keeps every
    /// segment until its deadline.
    /// </summary>
    [Fact]
    public void EntryCap_RefusesNonCompliantRemoval()
    {
        var store = Create(new HlsOptions());
        var session = Open(store, targetDuration: 1);
        store.AddInitialization(session, Init(0));
        for (var i = 0; i < HlsPlaylist.MaxEntries; i++)
        {
            Assert.Equal(HlsAppendResult.Published, store.AppendSegment(session, Media(i, 10), HlsPlaylistTests.Entry(i, 0.002)));
        }

        var before = store.GetPlaylist("live", "cam")!;
        Assert.Equal((HlsPlaylist.MaxEntries, TimeSpan.FromSeconds(2)), (before.Segments.Count, before.Segments.Aggregate(TimeSpan.Zero, static (t, s) => t + s.Duration)));
        var bytes = store.TotalBytes;
        using var evictions = Evictions();

        Assert.Equal(HlsAppendResult.PlaylistFull, store.AppendSegment(session, Media(1000, 10), HlsPlaylistTests.Entry(1000, 0.002)));
        Assert.Equal(HlsAppendResult.PlaylistFull, store.AppendSegment(session, Media(1000, 10), HlsPlaylistTests.Entry(1000, 1.0019999)));
        Assert.Same(before, store.GetPlaylist("live", "cam"));
        Assert.Equal(bytes, store.TotalBytes);
        Assert.Null(store.GetSegment("live", "cam", "segment-1000.m4s"));
        Assert.All(before.Segments, s => Assert.NotNull(store.GetSegment("live", "cam", s.Name)));
        Assert.Empty(evictions.GetMeasurementSnapshot());

        Assert.Equal(HlsAppendResult.Published, store.AppendSegment(session, Media(1000, 10), HlsPlaylistTests.Entry(1000, 1.002)));
        var after = store.GetPlaylist("live", "cam")!;
        Assert.Equal((HlsPlaylist.MaxEntries, 1L, TimeSpan.FromSeconds(3)), (after.Segments.Count, after.MediaSequence, after.Segments.Aggregate(TimeSpan.Zero, static (t, s) => t + s.Duration)));
        Assert.Equal(bytes + 10, store.TotalBytes);

        store.CloseStream(session, completed: false);
        Assert.Null(store.GetPlaylist("live", "cam"));
        AssertAvailableUntil(store, "segment-0.m4s", TimeSpan.FromMilliseconds(2002));
        AssertAvailableUntil(store, "segment-1.m4s", TimeSpan.FromMilliseconds(3002));
        AssertAvailableUntil(store, "segment-1000.m4s", TimeSpan.FromMilliseconds(4002));
        Assert.Equal(0L, store.TotalBytes);
    }

    /// <summary>Verifies every rejected publication leaves the playlist, segments and accounting unchanged.</summary>
    [Fact]
    public void FailedPublication_LeavesStateUnchanged()
    {
        var store = Create(new HlsOptions { MaxSessionStorageMegabytes = 1 });
        var session = Open(store);
        store.AddInitialization(session, Init(0));
        store.AppendSegment(session, Media(0, Large), HlsPlaylistTests.Entry(0, 1.0));
        var before = store.GetPlaylist("live", "cam");
        var bytes = store.TotalBytes;

        Assert.Equal(HlsAppendResult.StreamLimitExceeded, store.AppendSegment(session, Media(1, 3 * Large), HlsPlaylistTests.Entry(1, 1.0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.AppendSegment(session, Media(1, 10), HlsPlaylistTests.Entry(1, 10.5)));
        Assert.Throws<InvalidOperationException>(() => store.AppendSegment(session, Media(5, 10), HlsPlaylistTests.Entry(5, 1.0)));
        Assert.Throws<InvalidOperationException>(() => store.AppendSegment(session, Media(0, 10), HlsPlaylistTests.Entry(0, 1.0)));
        Assert.Throws<ArgumentException>(() => store.AppendSegment(session, Media(2, 10), HlsPlaylistTests.Entry(1, 1.0)));
        Assert.Equal(HlsAppendResult.StreamLimitExceeded, store.AddInitialization(session, new HlsSegment(HlsSegmentKind.Initialization, "init-1.mp4", new byte[3 * Large])));

        Assert.Same(before, store.GetPlaylist("live", "cam"));
        Assert.Equal(bytes, store.TotalBytes);
        Assert.Null(store.GetSegment("live", "cam", "segment-1.m4s"));
        Assert.NotNull(store.GetSegment("live", "cam", "init-0.mp4"));
        Assert.Equal(HlsAppendResult.Published, store.AppendSegment(session, Media(1, 10), HlsPlaylistTests.Entry(1, 1.0)));
    }

    /// <summary>
    /// Verifies one writer per key and the session limit, and that replacing an ended stream keeps its segments
    /// available for their deadline while the new stream continues the key's numbering.
    /// </summary>
    [Fact]
    public void Open_EnforcesOwnershipAndContinuesNumbering()
    {
        var store = Create(new HlsOptions { MaxSessions = 2 });
        var first = Open(store, expected: new HlsStreamNumbering(0, 0));
        Assert.False(store.TryOpenStream(Guid.NewGuid(), "live", "cam", 10, out _));
        Assert.False(store.TryOpenStream(first, "live", "cam2", 10, out _));
        var second = Open(store, "cam2");
        Assert.False(store.TryOpenStream(Guid.NewGuid(), "live", "cam3", 10, out _));

        store.AddInitialization(first, Init(0));
        store.AppendSegment(first, Media(0, 10), HlsPlaylistTests.Entry(0, 1.0));
        store.CloseStream(first, completed: true);
        Assert.True(store.GetPlaylist("live", "cam")!.Ended);

        var third = Open(store, expected: new HlsStreamNumbering(1, 1));
        Assert.Null(store.GetPlaylist("live", "cam"));
        Assert.NotNull(store.GetSegment("live", "cam", "segment-0.m4s"));
        Assert.Throws<InvalidOperationException>(() => store.AddInitialization(third, Init(0)));
        store.AddInitialization(third, Init(1));
        store.AppendSegment(third, Media(1, 10), HlsPlaylistTests.Entry(1, 1.0, "init-1.mp4"));
        Assert.Equal(1, store.GetPlaylist("live", "cam")!.MediaSequence);

        // Segment 0: 1 s plus the 1 s playlist that listed it.
        _time.Advance(TimeSpan.FromSeconds(2) - TimeSpan.FromTicks(1));
        Assert.NotNull(store.GetSegment("live", "cam", "segment-0.m4s"));
        _time.Advance(TimeSpan.FromTicks(1));
        Assert.Null(store.GetSegment("live", "cam", "segment-0.m4s"));
        Assert.Null(store.GetSegment("live", "cam", "init-0.mp4"));
        Assert.Equal(0, store.WithdrawnStreamCount);

        store.CloseStream(second, completed: false);
        store.CloseStream(third, completed: false);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal((0, 0, 0L), (store.StreamCount, store.WithdrawnStreamCount, store.TotalBytes));
        Assert.False(store.ContainsStream("live", "cam"));

        // Fully drained, the key still continues its numbering, so a cached or bookmarked URL never names other media.
        Open(store, expected: new HlsStreamNumbering(2, 2));
    }

    /// <summary>
    /// Verifies the generation: random per store instance (so per process), 32 lowercase hex digits, and fixed for the
    /// store's lifetime, including across a replacement stream after the key fully drained.
    /// </summary>
    [Fact]
    public void Generation_IsUniquePerStoreAndQualifiesEveryUri()
    {
        var store = Create(new HlsOptions());
        var other = Create(new HlsOptions());
        Assert.Matches("^[0-9a-f]{32}$", store.Generation);
        Assert.NotEqual(store.Generation, other.Generation);

        var first = Open(store, expected: new HlsStreamNumbering(0, 0));
        store.AddInitialization(first, Init(0));
        store.AppendSegment(first, Media(0, 10), HlsPlaylistTests.Entry(0, 1.0));
        var generation = store.Generation;
        Assert.Equal(
            $"#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:10\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-MAP:URI=\"{generation}/init-0.mp4\"\n#EXTINF:1.000,\n{generation}/segment-0.m4s\n",
            store.GetPlaylist("live", "cam")!.Text);

        store.CloseStream(first, completed: false);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(store.ContainsStream("live", "cam"));
        var second = Open(store, expected: new HlsStreamNumbering(1, 1));
        store.AddInitialization(second, Init(1));
        store.AppendSegment(second, Media(1, 10), HlsPlaylistTests.Entry(1, 1.0, "init-1.mp4"));
        Assert.Equal(generation, store.Generation);
        Assert.Contains($"\n{generation}/segment-1.m4s\n", store.GetPlaylist("live", "cam")!.Text, StringComparison.Ordinal);
        Assert.Equal(new HlsPlaylist(10, 6).Render(), new HlsPlaylist(10, 6).Render(string.Empty));
    }

    /// <summary>Verifies a retired segment stays available for exactly its duration plus the playlist duration.</summary>
    [Fact]
    public void RetiredSegment_StaysAvailableForTheRfcPeriod()
    {
        using var evictions = Evictions();
        var store = Create(new HlsOptions { PlaylistWindowSize = 3 });
        var session = Open(store, targetDuration: 1);
        store.AddInitialization(session, Init(0));
        for (var i = 0; i < 4; i++)
        {
            store.AppendSegment(session, Media(i, 10), HlsPlaylistTests.Entry(i, 1.0));
        }

        Assert.Equal(1, store.GetPlaylist("live", "cam")!.MediaSequence);
        _time.Advance(TimeSpan.FromSeconds(4) - TimeSpan.FromTicks(1));
        Assert.NotNull(store.GetSegment("live", "cam", "segment-0.m4s"));
        _time.Advance(TimeSpan.FromTicks(1));
        Assert.Null(store.GetSegment("live", "cam", "segment-0.m4s"));
        Assert.NotNull(store.GetSegment("live", "cam", "segment-1.m4s"));
        Assert.Equal((3 * 10) + 100, store.TotalBytes);
        Assert.Contains(evictions.GetMeasurementSnapshot(), static m => m.Tags["reason"] is "retired");
    }

    /// <summary>
    /// Verifies deadlines use each segment's own duration and the longest published playlist that listed it, not the
    /// playlist left after removal: three segments retired by one append get 1.4 + 4.3 s, 0.5 + 4.3 s and 0.5 + 4.3 s.
    /// </summary>
    [Fact]
    public void RetiredSegment_UsesLongestPublishedPlaylist()
    {
        var store = Create(new HlsOptions { PlaylistWindowSize = 3 });
        var session = Open(store, targetDuration: 1);
        store.AddInitialization(session, Init(0));
        double[] durations = [1.4, 0.5, 0.5, 0.5, 1.4];
        for (var i = 0; i < durations.Length; i++)
        {
            store.AppendSegment(session, Media(i, 10), HlsPlaylistTests.Entry(i, durations[i]));
        }

        Assert.Equal(TimeSpan.FromSeconds(4.3), store.GetPlaylist("live", "cam")!.Segments.Aggregate(TimeSpan.Zero, static (t, s) => t + s.Duration));
        store.AppendSegment(session, Media(5, 10), HlsPlaylistTests.Entry(5, 1.4));
        Assert.Equal(3, store.GetPlaylist("live", "cam")!.MediaSequence);

        AssertAvailableUntil(store, "segment-1.m4s", TimeSpan.FromSeconds(4.8));
        Assert.Null(store.GetSegment("live", "cam", "segment-2.m4s"));
        AssertAvailableUntil(store, "segment-0.m4s", TimeSpan.FromSeconds(5.7));
        Assert.NotNull(store.GetSegment("live", "cam", "segment-3.m4s"));
        Assert.Equal(TimeSpan.FromSeconds(5.7), InMemoryHlsSegmentStore.AvailabilityDeadline(TimeSpan.FromSeconds(1.4), TimeSpan.FromSeconds(4.3), DateTimeOffset.UnixEpoch) - DateTimeOffset.UnixEpoch);
    }

    /// <summary>
    /// Verifies per-stream pressure never releases listed or still-available retired segments: the segment is rejected,
    /// and accepted once retired segments pass their deadlines.
    /// </summary>
    [Fact]
    public void SessionLimit_RejectsWithoutReleasingAvailableSegments()
    {
        var store = Create(new HlsOptions { MaxSessionStorageMegabytes = 1, PlaylistWindowSize = 3 });
        var session = Open(store, targetDuration: 1);
        store.AddInitialization(session, Init(0));
        store.AppendSegment(session, Media(0, Large), HlsPlaylistTests.Entry(0, 1.0));
        for (var i = 1; i < 4; i++)
        {
            Assert.Equal(HlsAppendResult.Published, store.AppendSegment(session, Media(i, 64 * 1024), HlsPlaylistTests.Entry(i, 1.0)));
        }

        Assert.Equal(1, store.GetPlaylist("live", "cam")!.MediaSequence);
        var listed = store.GetPlaylist("live", "cam");
        Assert.Equal(HlsAppendResult.StorageFull, store.AppendSegment(session, Media(4, Large * 2), HlsPlaylistTests.Entry(4, 1.0)));

        Assert.Same(listed, store.GetPlaylist("live", "cam"));
        Assert.NotNull(store.GetSegment("live", "cam", "segment-0.m4s"));
        Assert.All(listed!.Segments, s => Assert.NotNull(store.GetSegment("live", "cam", s.Name)));

        _time.Advance(TimeSpan.FromSeconds(4));
        Assert.Null(store.GetSegment("live", "cam", "segment-0.m4s"));
        Assert.Equal(HlsAppendResult.Published, store.AppendSegment(session, Media(4, Large * 2), HlsPlaylistTests.Entry(4, 1.0)));
        Assert.True(store.TotalBytes <= 1024 * 1024);
    }

    /// <summary>
    /// Verifies global pressure never shrinks another publisher's playlist or releases available segments early;
    /// closing a stream frees its space only after its segments' deadlines.
    /// </summary>
    [Fact]
    public void GlobalLimit_RejectsWithoutAffectingOtherPublishers()
    {
        var store = Create(new HlsOptions { MaxSessionStorageMegabytes = 1, MaxTotalStorageMegabytes = 1, EndedStreamRetention = TimeSpan.FromMinutes(5) });
        var a = Open(store, "a", targetDuration: 1);
        var b = Open(store, "b", targetDuration: 1);
        store.AddInitialization(a, Init(0));
        store.AddInitialization(b, Init(0));
        for (var i = 0; i < 3; i++)
        {
            store.AppendSegment(a, Media(i, Large), HlsPlaylistTests.Entry(i, 1.0));
        }

        var aPlaylist = store.GetPlaylist("live", "a");
        Assert.Equal(HlsAppendResult.StorageFull, store.AppendSegment(b, Media(0, Large), HlsPlaylistTests.Entry(0, 1.0)));
        Assert.Same(aPlaylist, store.GetPlaylist("live", "a"));
        Assert.Null(store.GetPlaylist("live", "b"));

        // The ended playlist is withdrawn early under pressure; its segments still drain on their deadlines (1 s + 3 s).
        store.CloseStream(a, completed: true);
        Assert.Equal(HlsAppendResult.StorageFull, store.AppendSegment(b, Media(0, Large), HlsPlaylistTests.Entry(0, 1.0)));
        Assert.Null(store.GetPlaylist("live", "a"));
        _time.Advance(TimeSpan.FromSeconds(4) - TimeSpan.FromTicks(1));
        Assert.All(aPlaylist!.Segments, s => Assert.NotNull(store.GetSegment("live", "a", s.Name)));
        Assert.Equal(HlsAppendResult.StorageFull, store.AppendSegment(b, Media(0, Large), HlsPlaylistTests.Entry(0, 1.0)));

        _time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(HlsAppendResult.Published, store.AppendSegment(b, Media(0, Large), HlsPlaylistTests.Entry(0, 1.0)));
        Assert.Equal((1, 0), (store.StreamCount, store.WithdrawnStreamCount));
    }

    /// <summary>
    /// Verifies ended and aborted streams: ENDLIST then withdrawal after the retention, and immediate withdrawal on
    /// abort; in both cases the segments drain on their deadlines afterwards.
    /// </summary>
    [Fact]
    public void Close_WithdrawsPlaylistAndDrainsSegments()
    {
        var store = Create(new HlsOptions { EndedStreamRetention = TimeSpan.FromSeconds(30) });
        var completed = Open(store, "done");
        store.AddInitialization(completed, Init(0));
        store.AppendSegment(completed, Media(0, 10), HlsPlaylistTests.Entry(0, 1.0));
        store.CloseStream(completed, completed: true);
        store.CloseStream(completed, completed: true);

        var playlist = store.GetPlaylist("live", "done")!;
        Assert.True(playlist.Ended);
        Assert.EndsWith("#EXT-X-ENDLIST\n", playlist.Text, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => store.AppendSegment(completed, Media(1, 10), HlsPlaylistTests.Entry(1, 1.0)));
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(store.GetPlaylist("live", "done"));
        // Withdrawn at 30 s: 1 s segment + 1 s longest playlist.
        AssertAvailableUntil(store, "segment-0.m4s", TimeSpan.FromSeconds(32), "done");
        Assert.Equal((0, 0, 0L), (store.StreamCount, store.WithdrawnStreamCount, store.TotalBytes));

        var aborted = Open(store, "aborted");
        store.AddInitialization(aborted, Init(0));
        store.AppendSegment(aborted, Media(0, 10), HlsPlaylistTests.Entry(0, 1.0));
        store.CloseStream(aborted, completed: false);
        Assert.Null(store.GetPlaylist("live", "aborted"));
        Assert.Equal((0, 1), (store.StreamCount, store.WithdrawnStreamCount));
        Assert.NotNull(store.GetSegment("live", "aborted", "init-0.mp4"));
        AssertAvailableUntil(store, "segment-0.m4s", TimeSpan.FromSeconds(34), "aborted");
        Assert.Null(store.GetSegment("live", "aborted", "init-0.mp4"));
        Assert.Equal((0, 0, 0L), (store.StreamCount, store.WithdrawnStreamCount, store.TotalBytes));

        var empty = Open(store, "empty");
        store.AddInitialization(empty, Init(0));
        store.CloseStream(empty, completed: true);
        Assert.Equal((0, 0, 0L), (store.StreamCount, store.WithdrawnStreamCount, store.TotalBytes));
    }

    /// <summary>Verifies an initialization segment is released once no kept segment references it.</summary>
    [Fact]
    public void Initialization_IsReleasedWhenUnreferenced()
    {
        var store = Create(new HlsOptions { PlaylistWindowSize = 3 });
        var session = Open(store, targetDuration: 1);
        store.AddInitialization(session, Init(0));
        store.AddInitialization(session, Init(1));
        Assert.Null(store.GetSegment("live", "cam", "init-0.mp4"));

        store.AppendSegment(session, Media(0, 10), HlsPlaylistTests.Entry(0, 1.0, "init-1.mp4"));
        store.AddInitialization(session, Init(2));
        for (var i = 1; i < 5; i++)
        {
            store.AppendSegment(session, Media(i, 10), HlsPlaylistTests.Entry(i, 1.0, "init-2.mp4", discontinuity: i == 1));
        }

        Assert.NotNull(store.GetSegment("live", "cam", "init-1.mp4"));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(store.GetSegment("live", "cam", "init-1.mp4"));
        Assert.NotNull(store.GetSegment("live", "cam", "init-2.mp4"));
        Assert.Equal(3, store.GetPlaylist("live", "cam")!.Segments.Count);
    }

    /// <summary>Verifies a segment leased before it expires stays intact afterwards: expiry never mutates buffers.</summary>
    [Fact]
    public void Expiry_DoesNotInvalidateAnActiveRead()
    {
        var store = Create(new HlsOptions { PlaylistWindowSize = 3 });
        var session = Open(store, targetDuration: 1);
        store.AddInitialization(session, Init(0));
        store.AppendSegment(session, Media(0, 1000), HlsPlaylistTests.Entry(0, 1.0));
        using var lease = store.AcquireSegment("live", "cam", "segment-0.m4s")!;
        var copy = lease.ToArray();
        store.CloseStream(session, completed: false);
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.Null(store.GetSegment("live", "cam", "segment-0.m4s"));
        Assert.Null(store.AcquireSegment("live", "cam", "segment-0.m4s"));
        Assert.Equal(0L, store.TotalBytes);
        Assert.Equal(copy, lease.ToArray());
        Assert.Equal(1000, lease.Length);
    }

    /// <summary>
    /// Verifies concurrent readers while a writer publishes and the clock expires segments: every listed segment is
    /// retrievable unless its earliest possible deadline has passed, and accounting is exact afterwards.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ConcurrentReaders_SeeConsistentPlaylists()
    {
        var store = Create(new HlsOptions { PlaylistWindowSize = 3 });
        var session = Open(store, targetDuration: 1);
        store.AddInitialization(session, Init(0));
        using var done = new CancellationTokenSource();
        var reads = 0L;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(
            () =>
            {
                while (!done.IsCancellationRequested)
                {
                    var observed = _time.GetUtcNow();
                    if (store.GetPlaylist("live", "cam") is not { } playlist)
                    {
                        continue;
                    }

                    Assert.Equal(playlist.Segments[0].Sequence, playlist.MediaSequence);
                    var listedFor = playlist.Segments.Aggregate(TimeSpan.Zero, static (t, s) => t + s.Duration);
                    foreach (var entry in playlist.Segments)
                    {
                        using var segment = store.AcquireSegment("live", "cam", entry.Name);
                        if (segment is not null)
                        {
                            Assert.Equal((byte)entry.Sequence, segment.GetPart(0).Span[0]);
                        }
                        else
                        {
                            Assert.True(_time.GetUtcNow() >= observed + entry.Duration + listedFor, $"{entry.Name} was released before its deadline.");
                        }

                        Assert.NotNull(store.GetSegment("live", "cam", entry.InitializationName));
                    }

                    Interlocked.Increment(ref reads);
                }
            },
            TestContext.Current.CancellationToken)).ToList();

        for (var i = 0; i < 500; i++)
        {
            Assert.Equal(HlsAppendResult.Published, store.AppendSegment(session, Media(i, 64), HlsPlaylistTests.Entry(i, 1.0)));
            _time.Advance(TimeSpan.FromMilliseconds(250));
            if (i % 50 == 0)
            {
                await Task.Yield();
            }
        }

        await Eventually.WaitUntilAsync(() => Interlocked.Read(ref reads) > 100, "readers complete reads");
        await done.CancelAsync();
        await Task.WhenAll(readers);
        Assert.Equal(497, store.GetPlaylist("live", "cam")!.MediaSequence);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal((3 * 64) + 100, store.TotalBytes);
    }

    /// <summary>Asserts a segment is retrievable until exactly <paramref name="deadline"/> after the epoch, then released.</summary>
    /// <param name="store">The store.</param>
    /// <param name="name">The segment.</param>
    /// <param name="deadline">The deadline after the epoch.</param>
    /// <param name="identity">The stream identity.</param>
    private void AssertAvailableUntil(InMemoryHlsSegmentStore store, string name, TimeSpan deadline, string identity = "cam")
    {
        var at = DateTimeOffset.UnixEpoch + deadline;
        _time.Advance(at - TimeSpan.FromTicks(1) - _time.GetUtcNow());
        Assert.NotNull(store.GetSegment("live", identity, name));
        _time.Advance(TimeSpan.FromTicks(1));
        Assert.Null(store.GetSegment("live", identity, name));
    }

    /// <summary>Creates a store.</summary>
    /// <param name="options">The options.</param>
    /// <returns>The store.</returns>
    private InMemoryHlsSegmentStore Create(HlsOptions options) =>
        new(Microsoft.Extensions.Options.Options.Create(options), _time, new HlsMetrics(_services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()));

    /// <summary>Collects eviction measurements.</summary>
    /// <returns>The collector.</returns>
    private MetricCollector<long> Evictions() =>
        new(_services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(), "RTSP.IngestService.Hls", "hls.storage.evictions");

    /// <summary>Opens a stream.</summary>
    /// <param name="store">The store.</param>
    /// <param name="identity">The stream identity.</param>
    /// <param name="targetDuration">The target duration.</param>
    /// <param name="expected">The expected numbering, if checked.</param>
    /// <returns>The writer session.</returns>
    private static Guid Open(InMemoryHlsSegmentStore store, string identity = "cam", int targetDuration = 10, HlsStreamNumbering? expected = null)
    {
        var session = Guid.NewGuid();
        Assert.True(store.TryOpenStream(session, "live", identity, targetDuration, out var numbering));
        if (expected is { } value)
        {
            Assert.Equal(value, numbering);
        }

        return session;
    }

    /// <summary>Creates a 100-byte initialization segment.</summary>
    /// <param name="index">Its index.</param>
    /// <returns>The segment.</returns>
    private static HlsSegment Init(int index) => new(HlsSegmentKind.Initialization, $"init-{index}.mp4", new byte[100]);

    /// <summary>Creates a media segment whose first byte is its sequence number.</summary>
    /// <param name="sequence">The sequence number.</param>
    /// <param name="size">Its size.</param>
    /// <returns>The segment.</returns>
    private static HlsSegment Media(long sequence, int size)
    {
        var data = new byte[size];
        data[0] = (byte)sequence;
        return new HlsSegment(HlsSegmentKind.Media, $"segment-{sequence}.m4s", data);
    }
}
