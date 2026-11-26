// Tests for pooled segment memory ownership: the bounded chunk pool, exactly-once release of a segment's chunks by its
// last reference, leases that keep bytes valid across eviction, withdrawal, replacement and store disposal, writers
// keeping ownership of segments the store does not publish (rejection or exception), byte-identical pooled fragments
// across chunk boundaries, oversized fragments that rent nothing, and concurrent readers that would observe reused
// chunks if any were returned while still leased. Time is a FakeTimeProvider; there are no delays.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Hls;

/// <summary>Tests for <see cref="HlsSegmentBufferPool"/>, <see cref="HlsSegment"/> references and <see cref="HlsSegmentLease"/>.</summary>
public sealed class HlsSegmentOwnershipTests : IDisposable
{
    /// <summary>Owns the meter factory.</summary>
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();

    /// <summary>The clock.</summary>
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

    /// <inheritdoc />
    public void Dispose() => _services.Dispose();

    /// <summary>Verifies the pool reuses returned chunks and keeps no more free chunks than its bound.</summary>
    [Fact]
    public void Pool_ReusesChunksWithinItsBound()
    {
        var pool = new HlsSegmentBufferPool(2);
        var first = pool.Rent((3 * HlsSegmentBufferPool.ChunkSize) + 1);
        Assert.Equal(4, first.Length);
        Assert.All(first, static c => Assert.Equal(HlsSegmentBufferPool.ChunkSize, c.Length));
        Assert.Equal(4, pool.OutstandingChunks);

        pool.Return(first);
        Assert.Equal((0L, 2), (pool.OutstandingChunks, pool.RetainedChunks));

        var second = pool.Rent(HlsSegmentBufferPool.ChunkSize * 3);
        Assert.Equal(2, second.Count(c => first.Contains(c)));
        Assert.Equal((3L, 0), (pool.OutstandingChunks, pool.RetainedChunks));
        pool.Return(second);
        Assert.Equal(1, HlsSegmentBufferPool.ChunksFor(1));
        Assert.Equal(1, HlsSegmentBufferPool.ChunksFor(HlsSegmentBufferPool.ChunkSize));
        Assert.Equal(2, HlsSegmentBufferPool.ChunksFor(HlsSegmentBufferPool.ChunkSize + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(0));
    }

    /// <summary>Verifies the retained bound scales with the global storage limit.</summary>
    [Fact]
    public void Pool_RetainsAnEighthOfTheGlobalLimit() =>
        Assert.Equal(2048 / 8 * 16, HlsSegmentBufferPool.Create(new HlsOptions { MaxTotalStorageMegabytes = 2048 }).MaxRetainedChunks);

    /// <summary>
    /// Verifies the chunks return exactly once, when the last of the creator's and the leases' references is released;
    /// releasing again throws without returning anything, and no lease can be taken afterwards.
    /// </summary>
    [Fact]
    public void Segment_ReturnsItsChunksExactlyOnceAtTheLastRelease()
    {
        var pool = new HlsSegmentBufferPool(16);
        var segment = HlsSegmentStoreTestExtensions.Pooled(pool, HlsSegmentKind.Media, "segment-0.m4s", Pattern(200_000, 7));
        Assert.Equal((4L, 200_000, 4L * HlsSegmentBufferPool.ChunkSize), (pool.OutstandingChunks, segment.Length, segment.Footprint));

        var first = segment.TryAcquire()!;
        var second = segment.TryAcquire()!;
        segment.Release();
        first.Dispose();
        first.Dispose();
        Assert.False(segment.IsReleased);
        Assert.Equal(4, pool.OutstandingChunks);
        Assert.Equal(Pattern(200_000, 7), second.ToArray());

        second.Dispose();
        Assert.True(segment.IsReleased);
        Assert.Equal((0L, 4), (pool.OutstandingChunks, pool.RetainedChunks));
        Assert.Null(segment.TryAcquire());
        Assert.Throws<InvalidOperationException>(segment.Release);
        Assert.Equal((0L, 4), (pool.OutstandingChunks, pool.RetainedChunks));
        Assert.Throws<ObjectDisposedException>(second.ToArray);
        Assert.Throws<ObjectDisposedException>(() => second.PartCount);
    }

    /// <summary>Verifies a lease exposes the bytes in order as chunk-sized parts, the last one partial.</summary>
    [Fact]
    public void Lease_ExposesOrderedParts()
    {
        var pool = new HlsSegmentBufferPool(16);
        var content = Pattern((2 * HlsSegmentBufferPool.ChunkSize) + 10, 3);
        var segment = HlsSegmentStoreTestExtensions.Pooled(pool, HlsSegmentKind.Media, "segment-0.m4s", content);
        using (var lease = segment.TryAcquire()!)
        {
            Assert.Equal(3, lease.PartCount);
            Assert.Equal([HlsSegmentBufferPool.ChunkSize, HlsSegmentBufferPool.ChunkSize, 10], Enumerable.Range(0, 3).Select(i => lease.GetPart(i).Length));
            Assert.Equal(content, Enumerable.Range(0, 3).SelectMany(i => lease.GetPart(i).ToArray()));
            Assert.Throws<ArgumentOutOfRangeException>(() => lease.GetPart(3));
        }

        segment.Release();
        Assert.Equal(0, pool.OutstandingChunks);
    }

    /// <summary>Verifies pooled fragments are byte-identical to contiguous ones, including at chunk boundaries.</summary>
    /// <param name="sampleBytes">The video sample size.</param>
    [Theory]
    [InlineData(10)]
    [InlineData(HlsSegmentBufferPool.ChunkSize - 100)]
    [InlineData(HlsSegmentBufferPool.ChunkSize)]
    [InlineData((3 * HlsSegmentBufferPool.ChunkSize) + 17)]
    public void PooledFragment_MatchesTheContiguousFragment(int sampleBytes)
    {
        var tracks = new[]
        {
            new Mp4TrackFragment(Mp4VideoTrack.Id, 9000, true, [new Mp4Sample(3000, Mp4Sample.SyncFlags, 0, Pattern(sampleBytes, 1)), new Mp4Sample(3000, Mp4Sample.NonSyncFlags, 0, Pattern(sampleBytes / 2, 2))]),
            new Mp4TrackFragment(Mp4AudioTrack.Id, 1024, false, [new Mp4Sample(1024, 0, 0, Pattern(300, 3))]),
        };
        var expected = Mp4Segments.WriteFragment(4, tracks, 1 << 20);
        var pool = new HlsSegmentBufferPool(64);

        var segment = Mp4Segments.WriteFragment(4, tracks, 1 << 20, pool, "segment-3.m4s");
        using (var lease = segment.TryAcquire()!)
        {
            Assert.Equal((HlsSegmentKind.Media, "segment-3.m4s", expected.Length), (lease.Kind, lease.Name, lease.Length));
            Assert.Equal(expected, lease.ToArray());
            Assert.Equal(HlsSegmentBufferPool.ChunksFor(expected.Length), pool.OutstandingChunks);
        }

        segment.Release();
        Assert.Equal(0, pool.OutstandingChunks);
    }

    /// <summary>Verifies an oversized fragment is refused before anything is rented.</summary>
    [Fact]
    public void OversizedFragment_RentsNothing()
    {
        var pool = new HlsSegmentBufferPool(64);
        var tracks = new[] { new Mp4TrackFragment(Mp4VideoTrack.Id, 0, true, [new Mp4Sample(3000, Mp4Sample.SyncFlags, 0, new byte[HlsSegmentBufferPool.ChunkSize])]) };

        Assert.Throws<Mp4SizeException>(() => Mp4Segments.WriteFragment(1, tracks, HlsSegmentBufferPool.ChunkSize, pool, "segment-0.m4s"));
        Assert.Throws<ArgumentException>(() => Mp4Segments.WriteFragment(1, [new Mp4TrackFragment(1, 0, true, [])], 1 << 20, pool, "segment-0.m4s"));
        Assert.Equal((0L, 0), (pool.OutstandingChunks, pool.RetainedChunks));
    }

    /// <summary>
    /// Verifies a lease keeps a segment's bytes intact and its chunks out of the pool while the store withdraws the
    /// playlist, replaces the stream and expires the segment, and that the chunks return once the lease ends.
    /// </summary>
    [Fact]
    public void Lease_OutlivesWithdrawalReplacementAndExpiry()
    {
        var pool = new HlsSegmentBufferPool(1024);
        var store = Create(new HlsOptions { PlaylistWindowSize = 3, EndedStreamRetention = TimeSpan.FromSeconds(5) });
        var session = Open(store);
        Assert.Equal(HlsAppendResult.Published, store.AddInitialization(session, Init()));
        Assert.Equal(HlsAppendResult.Published, store.AppendSegment(session, Media(pool, 0, 300_000), HlsPlaylistTests.Entry(0, 1.0)));
        Assert.Equal(HlsAppendResult.Published, store.AppendSegment(session, Media(pool, 1, 300_000), HlsPlaylistTests.Entry(1, 1.0)));
        using var ended = store.AcquireSegment("live", "cam", "segment-0.m4s")!;
        store.CloseStream(session, completed: true);

        var replacement = Open(store);
        Assert.Null(store.GetPlaylist("live", "cam"));
        using var withdrawn = store.AcquireSegment("live", "cam", "segment-1.m4s")!;
        Assert.Equal(HlsAppendResult.Published, store.AddInitialization(replacement, Init(1)));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(store.GetSegment("live", "cam", "segment-0.m4s"));
        Assert.Null(store.AcquireSegment("live", "cam", "segment-1.m4s"));
        Assert.Equal(2 * HlsSegmentBufferPool.ChunksFor(300_000), pool.OutstandingChunks);

        for (var sequence = 2; sequence < 8; sequence++)
        {
            Assert.Equal(HlsAppendResult.Published, store.AppendSegment(replacement, Media(pool, sequence, 300_000), HlsPlaylistTests.Entry(sequence, 1.0, "init-1.mp4")));
            _time.Advance(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(Pattern(300_000, 0), ended.ToArray());
        Assert.Equal(Pattern(300_000, 1), withdrawn.ToArray());
        var held = pool.OutstandingChunks;
        ended.Dispose();
        withdrawn.Dispose();
        Assert.Equal(held - (2 * HlsSegmentBufferPool.ChunksFor(300_000)), pool.OutstandingChunks);

        store.Dispose();
        Assert.Equal(0, pool.OutstandingChunks);
    }

    /// <summary>
    /// Verifies disposing the store releases every stored segment but leaves leased bytes valid until each lease ends,
    /// and that a disposed store finds nothing and refuses writers.
    /// </summary>
    [Fact]
    public void StoreDispose_ReleasesEverythingButActiveLeases()
    {
        var pool = new HlsSegmentBufferPool(1024);
        var store = Create(new HlsOptions());
        var session = Open(store);
        store.AddInitialization(session, Init());
        store.AppendSegment(session, Media(pool, 0, 100_000), HlsPlaylistTests.Entry(0, 1.0));
        store.AppendSegment(session, Media(pool, 1, 100_000), HlsPlaylistTests.Entry(1, 1.0));
        var lease = store.AcquireSegment("live", "cam", "segment-1.m4s")!;

        store.Dispose();
        store.Dispose();
        Assert.Equal(HlsSegmentBufferPool.ChunksFor(100_000), pool.OutstandingChunks);
        Assert.Equal(Pattern(100_000, 1), lease.ToArray());
        Assert.Null(store.AcquireSegment("live", "cam", "segment-0.m4s"));
        Assert.Null(store.GetPlaylist("live", "cam"));
        Assert.Equal(0, store.TotalBytes);
        Assert.Throws<ObjectDisposedException>(() => store.AppendSegment(session, Media(pool, 2, 10), HlsPlaylistTests.Entry(2, 1.0)));
        Assert.Throws<ObjectDisposedException>(() => store.TryOpenStream(Guid.NewGuid(), "live", "other", 10, out _));
        store.CloseStream(session, completed: false);

        lease.Dispose();
        Assert.Equal(HlsSegmentBufferPool.ChunksFor(10), pool.OutstandingChunks);
    }

    /// <summary>Verifies a segment the store refuses stays owned by the caller, whose release returns its chunks.</summary>
    [Fact]
    public void RefusedSegment_StaysWithTheCaller()
    {
        var pool = new HlsSegmentBufferPool(1024);
        var store = Create(new HlsOptions { MaxSessionStorageMegabytes = 1 });
        var session = Open(store);
        store.AddInitialization(session, Init());
        var segment = Media(pool, 0, 2 * 1024 * 1024);

        Assert.Equal(HlsAppendResult.StreamLimitExceeded, store.AppendSegment(session, segment, HlsPlaylistTests.Entry(0, 1.0)));
        Assert.Throws<ArgumentException>(() => store.AppendSegment(session, segment, HlsPlaylistTests.Entry(1, 1.0)));
        Assert.False(segment.IsReleased);
        Assert.Null(store.GetSegment("live", "cam", "segment-0.m4s"));

        segment.Release();
        Assert.Equal(0, pool.OutstandingChunks);
        store.Dispose();
    }

    /// <summary>
    /// Verifies the storage limits count the whole chunks a pooled segment holds, so small segments cannot hold more
    /// memory than the limit.
    /// </summary>
    [Fact]
    public void StorageLimits_CountTheChunksSegmentsHold()
    {
        var pool = new HlsSegmentBufferPool(1024);
        var store = Create(new HlsOptions { MaxSessionStorageMegabytes = 1 });
        var session = Open(store);
        store.AddInitialization(session, Init());
        for (var i = 0; i < 15; i++)
        {
            Assert.Equal(HlsAppendResult.Published, store.AppendSegment(session, Media(pool, i, 100), HlsPlaylistTests.Entry(i, 1.0)));
        }

        Assert.Equal(100 + (15L * HlsSegmentBufferPool.ChunkSize), store.TotalBytes);
        var refused = Media(pool, 15, 100);
        Assert.Equal(HlsAppendResult.StreamLimitExceeded, store.AppendSegment(session, refused, HlsPlaylistTests.Entry(15, 1.0)));
        Assert.Equal(100 + (15L * HlsSegmentBufferPool.ChunkSize), store.TotalBytes);

        refused.Release();
        store.Dispose();
        Assert.Equal(0, pool.OutstandingChunks);
    }

    /// <summary>
    /// Verifies concurrent readers of pooled segments never see bytes change while a writer publishes, the clock
    /// expires segments and their chunks are immediately reused for new segments with different contents.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ConcurrentReaders_NeverObserveReusedChunks()
    {
        const int Size = (2 * HlsSegmentBufferPool.ChunkSize) + 1000;
        var pool = new HlsSegmentBufferPool(1024);
        var store = Create(new HlsOptions { PlaylistWindowSize = 3 });
        var session = Open(store, targetDuration: 1);
        store.AddInitialization(session, Init());
        using var done = new CancellationTokenSource();
        var reads = 0L;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(
            () =>
            {
                while (!done.IsCancellationRequested)
                {
                    if (store.GetPlaylist("live", "cam") is not { } playlist)
                    {
                        continue;
                    }

                    foreach (var entry in playlist.Segments)
                    {
                        using var lease = store.AcquireSegment("live", "cam", entry.Name);
                        if (lease is null)
                        {
                            continue;
                        }

                        var expected = (byte)(entry.Sequence % 251);
                        for (var i = 0; i < lease.PartCount; i++)
                        {
                            var part = lease.GetPart(i).Span;
                            Assert.True(part.IndexOfAnyExcept(expected) < 0, $"{entry.Name} changed while leased.");
                        }

                        Interlocked.Increment(ref reads);
                    }
                }
            },
            TestContext.Current.CancellationToken)).ToList();

        for (var i = 0; i < 400; i++)
        {
            Assert.Equal(HlsAppendResult.Published, store.AppendSegment(session, Filled(pool, i, Size), HlsPlaylistTests.Entry(i, 1.0)));
            _time.Advance(TimeSpan.FromMilliseconds(500));
            if (i % 20 == 0)
            {
                await Eventually.WaitUntilAsync(() => Interlocked.Read(ref reads) > i, "readers keep up");
            }
        }

        await done.CancelAsync();
        await Task.WhenAll(readers);
        Assert.True(pool.RetainedChunks > 0, "expired chunks were reused");
        store.Dispose();
        Assert.Equal(0, pool.OutstandingChunks);
    }

    /// <summary>Verifies a segment whose storage throws is released by the packager, once.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task StoreFailure_ReleasesTheSegment()
    {
        FaultingStore? faulting = null;
        using var harness = new HlsHarness(decorate: store => faulting = new FaultingStore(store));
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session, audio: false);
        await harness.VideoAsync(session, 0, idr: true);
        faulting!.FailAppends = true;

        var failure = await Record.ExceptionAsync(() => harness.VideoAsync(session, 1000, idr: true));
        Assert.IsType<IOException>(failure);
        Assert.Equal(0, harness.SegmentPool.OutstandingChunks);
        Assert.True(harness.SegmentPool.RetainedChunks > 0);

        faulting.FailAppends = false;
        await harness.EndAsync(session, PublishEndReason.HandlerFailed);
    }

    /// <summary>Returns <paramref name="length"/> bytes derived from <paramref name="seed"/>.</summary>
    /// <param name="length">The length.</param>
    /// <param name="seed">The seed.</param>
    /// <returns>The bytes.</returns>
    private static byte[] Pattern(int length, int seed)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 31) + seed);
        }

        return bytes;
    }

    /// <summary>Creates a pooled media segment with <see cref="Pattern"/> bytes seeded by its sequence number.</summary>
    /// <param name="pool">The pool.</param>
    /// <param name="sequence">The sequence number.</param>
    /// <param name="size">The size.</param>
    /// <returns>The segment.</returns>
    private static HlsSegment Media(HlsSegmentBufferPool pool, long sequence, int size) =>
        HlsSegmentStoreTestExtensions.Pooled(pool, HlsSegmentKind.Media, $"segment-{sequence}.m4s", Pattern(size, (int)sequence));

    /// <summary>Creates a pooled media segment whose every byte is its sequence number modulo 251.</summary>
    /// <param name="pool">The pool.</param>
    /// <param name="sequence">The sequence number.</param>
    /// <param name="size">The size.</param>
    /// <returns>The segment.</returns>
    private static HlsSegment Filled(HlsSegmentBufferPool pool, long sequence, int size)
    {
        var bytes = new byte[size];
        bytes.AsSpan().Fill((byte)(sequence % 251));
        return HlsSegmentStoreTestExtensions.Pooled(pool, HlsSegmentKind.Media, $"segment-{sequence}.m4s", bytes);
    }

    /// <summary>Creates an unpooled initialization segment.</summary>
    /// <param name="index">Its index.</param>
    /// <returns>The segment.</returns>
    private static HlsSegment Init(int index = 0) => new(HlsSegmentKind.Initialization, $"init-{index}.mp4", new byte[100]);

    /// <summary>Opens <c>live/cam</c>.</summary>
    /// <param name="store">The store.</param>
    /// <param name="targetDuration">The target duration.</param>
    /// <returns>The writer session.</returns>
    private static Guid Open(InMemoryHlsSegmentStore store, int targetDuration = 10)
    {
        var session = Guid.NewGuid();
        Assert.True(store.TryOpenStream(session, "live", "cam", targetDuration, out _));
        return session;
    }

    /// <summary>Creates a store.</summary>
    /// <param name="options">The options.</param>
    /// <returns>The store.</returns>
    private InMemoryHlsSegmentStore Create(HlsOptions options) =>
        new(Microsoft.Extensions.Options.Options.Create(options), _time, new HlsMetrics(_services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()));

    /// <summary>A store whose appends throw on demand, before reaching the inner store.</summary>
    /// <param name="inner">The store.</param>
    private sealed class FaultingStore(IHlsSegmentStore inner) : IHlsSegmentStore
    {
        /// <summary>Gets or sets a value indicating whether appends throw.</summary>
        public bool FailAppends { get; set; }

        /// <inheritdoc />
        public string Generation => inner.Generation;

        /// <inheritdoc />
        public bool TryOpenStream(Guid sessionId, string application, string streamIdentity, int targetDurationSeconds, out HlsStreamNumbering numbering, string? incarnationId = null, long generation = 0) =>
            inner.TryOpenStream(sessionId, application, streamIdentity, targetDurationSeconds, out numbering, incarnationId, generation);

        /// <inheritdoc />
        public HlsAppendResult AddInitialization(Guid sessionId, HlsSegment segment) => inner.AddInitialization(sessionId, segment);

        /// <inheritdoc />
        public HlsAppendResult AppendSegment(Guid sessionId, HlsSegment segment, HlsSegmentEntry entry) =>
            FailAppends ? throw new IOException("Injected storage failure.") : inner.AppendSegment(sessionId, segment, entry);

        /// <inheritdoc />
        public void CloseStream(Guid sessionId, bool completed) => inner.CloseStream(sessionId, completed);

        /// <inheritdoc />
        public HlsPlaylistSnapshot? GetPlaylist(string application, string streamIdentity) => inner.GetPlaylist(application, streamIdentity);

        /// <inheritdoc />
        public HlsSegment? GetSegment(string application, string streamIdentity, string name) => inner.GetSegment(application, streamIdentity, name);

        /// <inheritdoc />
        public HlsSegmentLease? AcquireSegment(string application, string streamIdentity, string name) => inner.AcquireSegment(application, streamIdentity, name);

        /// <inheritdoc />
        public HlsSegmentAccess TryAcquireSegment(string application, string streamIdentity, string name, out HlsSegmentLease? lease) =>
            inner.TryAcquireSegment(application, streamIdentity, name, out lease);

        /// <inheritdoc />
        public bool ContainsStream(string application, string streamIdentity) => inner.ContainsStream(application, streamIdentity);
    }
}
