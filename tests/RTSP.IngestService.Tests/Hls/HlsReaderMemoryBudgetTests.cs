// Tests of the reader memory budget: reservations never exceed the limit (also under contention), a segment's
// footprint is reserved once by its first lease and returned by its last, an already-leased segment is admitted when
// the budget is full, released segments are reported as not stored, and the store keeps the budget for evicted
// segments that readers still hold.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;

namespace RTSP.IngestService.Tests.Hls;

/// <summary>Tests for <see cref="HlsReaderMemoryBudget"/> and budgeted segment leases.</summary>
public sealed class HlsReaderMemoryBudgetTests : IDisposable
{
    /// <summary>Owns the meter factory.</summary>
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();

    /// <summary>The clock.</summary>
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

    /// <inheritdoc />
    public void Dispose() => _services.Dispose();

    /// <summary>Verifies reservations stop at the limit and returned bytes can be reserved again.</summary>
    [Fact]
    public void Budget_ReservesUpToItsLimit()
    {
        var budget = new HlsReaderMemoryBudget(100);
        Assert.True(budget.TryReserve(60));
        Assert.True(budget.TryReserve(40));
        Assert.False(budget.TryReserve(1));
        Assert.Equal(100, budget.Held);
        budget.Return(40);
        Assert.False(budget.TryReserve(41));
        Assert.True(budget.TryReserve(40));
    }

    /// <summary>Verifies the first lease reserves the footprint, further leases are free and the last returns it.</summary>
    [Fact]
    public void Segment_ReservesItsFootprintOnceForAllLeases()
    {
        var budget = new HlsReaderMemoryBudget(100);
        var segment = new HlsSegment(HlsSegmentKind.Media, "segment-0.m4s", new byte[70]);
        var other = new HlsSegment(HlsSegmentKind.Media, "segment-1.m4s", new byte[40]);

        Assert.Equal(HlsSegmentAccess.Leased, segment.TryAcquire(budget, out var first));
        Assert.Equal(70, budget.Held);
        Assert.Equal(HlsSegmentAccess.ReaderMemoryExhausted, other.TryAcquire(budget, out var refused));
        Assert.Null(refused);
        Assert.Equal(HlsSegmentAccess.Leased, segment.TryAcquire(budget, out var second));
        Assert.Equal(70, budget.Held);

        first!.Dispose();
        first.Dispose();
        Assert.Equal(70, budget.Held);
        second!.Dispose();
        Assert.Equal(0, budget.Held);
        Assert.Equal(HlsSegmentAccess.Leased, other.TryAcquire(budget, out var third));
        third!.Dispose();
        Assert.Equal(0, budget.Held);
        Assert.False(segment.IsReleased);
    }

    /// <summary>Verifies a released segment is not stored, and costs no budget.</summary>
    [Fact]
    public void ReleasedSegment_IsNotStored()
    {
        var budget = new HlsReaderMemoryBudget(100);
        var segment = new HlsSegment(HlsSegmentKind.Media, "segment-0.m4s", new byte[10]);
        segment.Release();
        Assert.Equal(HlsSegmentAccess.NotStored, segment.TryAcquire(budget, out var lease));
        Assert.Null(lease);
        Assert.Equal(0, budget.Held);
    }

    /// <summary>Verifies an evicted segment keeps its budget until its last reader ends, and only then returns its bytes.</summary>
    [Fact]
    public void EvictedSegment_KeepsItsBudgetUntilReadersEnd()
    {
        var pool = new HlsSegmentBufferPool(16);
        using var store = new InMemoryHlsSegmentStore(Microsoft.Extensions.Options.Options.Create(new HlsOptions { MaxSessionStorageMegabytes = 1, MaxTotalStorageMegabytes = 1 }), _time, new HlsMetrics(_services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()));
        var session = Guid.NewGuid();
        Assert.True(store.TryOpenStream(session, "live", "cam", 1, out _));
        store.AddInitialization(session, new HlsSegment(HlsSegmentKind.Initialization, "init-0.mp4", [1]));
        var content = new byte[700 * 1024];
        Assert.Equal(HlsAppendResult.Published, store.AppendSegment(session, HlsSegmentStoreTestExtensions.Pooled(pool, HlsSegmentKind.Media, "segment-0.m4s", content), HlsPlaylistTests.Entry(0, 1.0)));
        Assert.Equal(HlsSegmentAccess.Leased, store.TryAcquireSegment("live", "cam", "segment-0.m4s", out var held));
        var footprint = HlsSegmentBufferPool.ChunksFor(content.Length) * (long)HlsSegmentBufferPool.ChunkSize;
        Assert.Equal(footprint, store.ReaderBudget.Held);

        store.CloseStream(session, completed: false);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, store.TotalBytes);
        Assert.Equal(HlsSegmentAccess.NotStored, store.TryAcquireSegment("live", "cam", "segment-0.m4s", out _));

        var other = Guid.NewGuid();
        Assert.True(store.TryOpenStream(other, "live", "other", 1, out _));
        store.AddInitialization(other, new HlsSegment(HlsSegmentKind.Initialization, "init-0.mp4", [1]));
        Assert.Equal(HlsAppendResult.Published, store.AppendSegment(other, new HlsSegment(HlsSegmentKind.Media, "segment-0.m4s", new byte[400 * 1024]), HlsPlaylistTests.Entry(0, 1.0)));
        Assert.Equal(HlsSegmentAccess.ReaderMemoryExhausted, store.TryAcquireSegment("live", "other", "segment-0.m4s", out _));
        Assert.Equal(footprint, pool.OutstandingChunks * (long)HlsSegmentBufferPool.ChunkSize);

        held!.Dispose();
        Assert.Equal(0, store.ReaderBudget.Held);
        Assert.Equal(0, pool.OutstandingChunks);
        Assert.Equal(HlsSegmentAccess.Leased, store.TryAcquireSegment("live", "other", "segment-0.m4s", out var lease));
        lease!.Dispose();
        Assert.Equal(0, store.ReaderBudget.Held);
    }

    /// <summary>
    /// Verifies concurrent readers leasing and releasing overlapping segments, while the owner releases some of them,
    /// never push the budget past its limit and leave nothing reserved.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ConcurrentLeases_StayWithinTheLimit()
    {
        var budget = new HlsReaderMemoryBudget(250);
        var segments = Enumerable.Range(0, 8).Select(static i => new HlsSegment(HlsSegmentKind.Media, $"segment-{i}.m4s", new byte[100])).ToArray();
        var exceeded = 0;
        var refusals = 0;
        using var start = new Barrier(8);
        var readers = Enumerable.Range(0, 8).Select(r => Task.Factory.StartNew(
            () =>
            {
                var random = new Random(r);
                HlsSegmentLease? previous = null;
                start.SignalAndWait(TestContext.Current.CancellationToken);
                for (var i = 0; i < 20_000; i++)
                {
                    var access = segments[random.Next(segments.Length)].TryAcquire(budget, out var lease);
                    if (budget.Held > budget.Limit)
                    {
                        Interlocked.Increment(ref exceeded);
                    }

                    if (access == HlsSegmentAccess.ReaderMemoryExhausted)
                    {
                        Interlocked.Increment(ref refusals);
                    }

                    previous?.Dispose();
                    previous = lease;
                }

                previous?.Dispose();
            },
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)).ToList();

        await Task.Delay(5, TestContext.Current.CancellationToken);
        segments[0].Release();
        segments[1].Release();
        await Task.WhenAll(readers);

        Assert.Equal(0, exceeded);
        Assert.True(refusals > 0);
        Assert.Equal(0, budget.Held);
        Assert.True(segments[0].IsReleased);
        Assert.Equal(HlsSegmentAccess.NotStored, segments[1].TryAcquire(budget, out _));
        Assert.All(segments[2..], static s => Assert.False(s.IsReleased));
    }
}
