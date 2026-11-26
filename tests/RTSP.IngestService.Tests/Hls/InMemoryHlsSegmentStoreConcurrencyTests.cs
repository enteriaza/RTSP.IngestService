// High-contention and index tests for the segment store: many writers and lock-free readers across many streams while
// segments expire (no segment released before its own duration has passed since a reader saw it listed), stream churn with exactly-once PlaylistWithdrawn and Drained per instance, expiry observed by
// readers alone, stale retention deadlines after replacement, storage pressure that withdraws only ended streams, and
// complete cleanup of thousands of streams through the deadline index. Time is a FakeTimeProvider.

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Streams;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Hls;

/// <summary>Concurrency and indexing tests for <see cref="InMemoryHlsSegmentStore"/>.</summary>
public sealed class InMemoryHlsSegmentStoreConcurrencyTests : IDisposable
{
    /// <summary>Owns the meter factory.</summary>
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();

    /// <summary>The clock.</summary>
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

    /// <summary>Records lifecycle events.</summary>
    private readonly RecordingStreamEventPublisher _events = new();

    /// <inheritdoc />
    public void Dispose() => _services.Dispose();

    /// <summary>
    /// Verifies concurrent writers on many streams and readers on all of them: every playlist a reader gets lists
    /// segments that are retrievable unless their earliest deadline has passed, segment bytes match their entry, and
    /// accounting is exact once everything has expired.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ManyWritersAndReaders_StayConsistent()
    {
        const int Streams = 64;
        var store = Create(new HlsOptions { PlaylistWindowSize = 3, MaxSessions = Streams });
        var sessions = Enumerable.Range(0, Streams).Select(i => Open(store, $"cam{i}")).ToArray();
        foreach (var session in sessions)
        {
            Assert.Equal(HlsAppendResult.Published, store.AddInitialization(session, Init(0)));
        }

        using var done = new CancellationTokenSource();
        var failures = new ConcurrentQueue<string>();
        var reads = 0L;
        var readers = Enumerable.Range(0, 6).Select(r => Task.Run(
            () =>
            {
                var random = new Random(r);
                while (!done.IsCancellationRequested)
                {
                    var identity = $"cam{random.Next(Streams)}";
                    var observed = _time.GetUtcNow();
                    if (store.GetPlaylist("live", identity) is not { } playlist)
                    {
                        continue;
                    }

                    foreach (var entry in playlist.Segments)
                    {
                        using var lease = store.AcquireSegment("live", identity, entry.Name);
                        if (lease is not null)
                        {
                            if (lease.GetPart(0).Span[0] != (byte)entry.Sequence)
                            {
                                failures.Enqueue($"{identity}/{entry.Name} has the wrong bytes");
                            }
                        }
                        else if (_time.GetUtcNow() < observed + entry.Duration)
                        {
                            failures.Enqueue($"{identity}/{entry.Name} was released before its deadline");
                        }
                    }

                    if (!store.ContainsStream("live", identity))
                    {
                        failures.Enqueue($"{identity} is unknown while live");
                    }

                    Interlocked.Increment(ref reads);
                }
            },
            TestContext.Current.CancellationToken)).ToList();

        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(
            () =>
            {
                for (var sequence = 0; sequence < 200; sequence++)
                {
                    for (var i = w; i < Streams; i += 4)
                    {
                        var result = store.AppendSegment(sessions[i], Media(sequence, 32), HlsPlaylistTests.Entry(sequence, 1.0));
                        if (result != HlsAppendResult.Published)
                        {
                            failures.Enqueue($"cam{i} segment {sequence}: {result}");
                        }
                    }
                }
            },
            TestContext.Current.CancellationToken)).ToList();

        for (var i = 0; i < 200 && writers.Any(static w => !w.IsCompleted); i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        await Task.WhenAll(writers);
        await Eventually.WaitUntilAsync(() => Interlocked.Read(ref reads) > 1000, "readers complete reads");
        await done.CancelAsync();
        await Task.WhenAll(readers);
        Assert.Empty(failures);

        foreach (var session in sessions)
        {
            store.CloseStream(session, completed: false);
        }

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal((0L, 0, 0), (store.TotalBytes, store.StreamCount, store.WithdrawnStreamCount));
        Assert.All(Enumerable.Range(0, Streams), i => Assert.False(store.ContainsStream("live", $"cam{i}")));
    }

    /// <summary>
    /// Verifies concurrent open, append and close churn on distinct keys while readers run: each instance raises
    /// PlaylistWithdrawn and Drained exactly once, in that order, and every key is forgotten afterwards while keeping
    /// its numbering.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Churn_RaisesEachLifecycleEventOnce()
    {
        var store = Create(new HlsOptions { PlaylistWindowSize = 3, MaxSessions = 64, EndedStreamRetention = TimeSpan.FromSeconds(2) });
        using var done = new CancellationTokenSource();
        var reader = Task.Run(
            () =>
            {
                var random = new Random(1);
                while (!done.IsCancellationRequested)
                {
                    var identity = $"key{random.Next(16)}";
                    if (store.GetPlaylist("live", identity) is { } playlist)
                    {
                        using var lease = store.AcquireSegment("live", identity, playlist.Segments[^1].Name);
                    }
                }
            },
            TestContext.Current.CancellationToken);

        var opened = new ConcurrentBag<string>();
        var churn = Enumerable.Range(0, 16).Select(k => Task.Run(
            () =>
            {
                for (var round = 0; round < 20; round++)
                {
                    var incarnation = $"key{k}-{round}";
                    var session = Guid.NewGuid();
                    Assert.True(store.TryOpenStream(session, "live", $"key{k}", 1, out var numbering, incarnation, round + 1));
                    opened.Add(incarnation);
                    store.AddInitialization(session, Init(numbering.FirstInitialization));
                    for (var s = 0; s < 3; s++)
                    {
                        var sequence = numbering.FirstSequence + s;
                        Assert.Equal(HlsAppendResult.Published, store.AppendSegment(session, Media(sequence, 16), HlsPlaylistTests.Entry(sequence, 1.0, $"init-{numbering.FirstInitialization}.mp4")));
                    }

                    store.CloseStream(session, completed: round % 2 == 0);
                }
            },
            TestContext.Current.CancellationToken)).ToList();

        for (var i = 0; i < 400 && churn.Any(static t => !t.IsCompleted); i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        await Task.WhenAll(churn);
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal((0L, 0, 0), (store.TotalBytes, store.StreamCount, store.WithdrawnStreamCount));
        await done.CancelAsync();
        await reader;

        var byIncarnation = _events.Events.GroupBy(static e => e.IncarnationId).ToDictionary(static g => g.Key, static g => g.Select(static e => e.Type).ToArray());
        Assert.Equal(opened.Count, byIncarnation.Count);
        Assert.All(opened, incarnation => Assert.Equal([StreamEventType.PlaylistWithdrawn, StreamEventType.Drained], byIncarnation[incarnation]));

        var again = Guid.NewGuid();
        Assert.True(store.TryOpenStream(again, "live", "key0", 1, out var continued));
        Assert.Equal((60L, 20), (continued.FirstSequence, continued.FirstInitialization));
    }

    /// <summary>Verifies readers alone observe expiry: no writer call is needed to release a segment past its deadline.</summary>
    [Fact]
    public void Readers_RunDueExpiry()
    {
        var store = Create(new HlsOptions { PlaylistWindowSize = 3 });
        var session = Open(store, "cam", "cam");
        store.AddInitialization(session, Init(0));
        for (var i = 0; i < 4; i++)
        {
            store.AppendSegment(session, Media(i, 10), HlsPlaylistTests.Entry(i, 1.0));
        }

        store.CloseStream(session, completed: false);
        Assert.False(store.ContainsStream("other", "cam"));
        Assert.True(store.ContainsStream("live", "cam"));
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.Null(store.AcquireSegment("live", "cam", "segment-3.m4s"));
        Assert.False(store.ContainsStream("live", "cam"));
        Assert.Equal([StreamEventType.PlaylistWithdrawn, StreamEventType.Drained], _events.Types);
    }

    /// <summary>
    /// Verifies an ended playlist's retention deadline does not withdraw the stream that replaced it, and that the
    /// replaced instance's segments still drain on their own deadlines.
    /// </summary>
    [Fact]
    public void StaleRetentionDeadline_LeavesTheReplacementAlone()
    {
        var store = Create(new HlsOptions { PlaylistWindowSize = 3, EndedStreamRetention = TimeSpan.FromSeconds(30) });
        var first = Open(store, "cam", "first");
        store.AddInitialization(first, Init(0));
        store.AppendSegment(first, Media(0, 10), HlsPlaylistTests.Entry(0, 1.0));
        store.CloseStream(first, completed: true);

        var second = Open(store, "cam", "second");
        store.AddInitialization(second, Init(1));
        store.AppendSegment(second, Media(1, 10), HlsPlaylistTests.Entry(1, 1.0, "init-1.mp4"));
        _time.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal(["segment-1.m4s"], store.GetPlaylist("live", "cam")!.Segments.Select(static s => s.Name));
        Assert.Null(store.GetSegment("live", "cam", "segment-0.m4s"));
        Assert.Equal(
            [(StreamEventType.PlaylistWithdrawn, "first", StreamEventReasons.Replaced), (StreamEventType.Drained, "first", null)],
            _events.Events.Select(static e => (e.Type, e.IncarnationId, e.Reason)));
    }

    /// <summary>Verifies global storage pressure withdraws ended streams early but never a live one.</summary>
    [Fact]
    public void StoragePressure_WithdrawsOnlyEndedStreams()
    {
        var store = Create(new HlsOptions { MaxTotalStorageMegabytes = 1, MaxSessionStorageMegabytes = 1 });
        var ended = Open(store, "ended", "ended");
        store.AddInitialization(ended, Init(0));
        store.AppendSegment(ended, Media(0, 400 * 1024), HlsPlaylistTests.Entry(0, 1.0));
        store.CloseStream(ended, completed: true);
        var live = Open(store, "live1", "live1");
        store.AddInitialization(live, Init(0));
        store.AppendSegment(live, Media(0, 400 * 1024), HlsPlaylistTests.Entry(0, 1.0));

        var next = Open(store, "live2", "live2");
        store.AddInitialization(next, Init(0));
        Assert.Equal(HlsAppendResult.StorageFull, store.AppendSegment(next, Media(0, 400 * 1024), HlsPlaylistTests.Entry(0, 1.0)));

        Assert.Null(store.GetPlaylist("live", "ended"));
        Assert.NotNull(store.GetPlaylist("live", "live1"));
        Assert.Equal([(StreamEventType.PlaylistWithdrawn, "ended", StreamEventReasons.StoragePressure)], _events.Events.Select(static e => (e.Type, e.IncarnationId, e.Reason)));
    }

    /// <summary>Verifies thousands of streams are all released and forgotten through the deadline index.</summary>
    [Fact]
    public void ThousandsOfStreams_DrainCompletely()
    {
        const int Streams = 2000;
        var store = Create(new HlsOptions { PlaylistWindowSize = 3, MaxSessions = Streams });
        for (var i = 0; i < Streams; i++)
        {
            var session = Open(store, $"cam{i}", $"cam{i}");
            store.AddInitialization(session, Init(0));
            for (var s = 0; s < 5; s++)
            {
                Assert.Equal(HlsAppendResult.Published, store.AppendSegment(session, Media(s, 8), HlsPlaylistTests.Entry(s, 1.0)));
            }

            store.CloseStream(session, completed: i % 2 == 0);
        }

        Assert.Equal((Streams / 2, Streams / 2), (store.StreamCount, store.WithdrawnStreamCount));
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal((0, Streams / 2), (store.StreamCount, store.WithdrawnStreamCount));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal((0L, 0, 0), (store.TotalBytes, store.StreamCount, store.WithdrawnStreamCount));
        Assert.Equal(Streams * 2, _events.Events.Count);
        Assert.Equal(Streams, _events.Events.Count(static e => e.Type == StreamEventType.Drained));
    }

    /// <summary>Creates a store reporting to <see cref="_events"/>.</summary>
    /// <param name="options">The options.</param>
    /// <returns>The store.</returns>
    private InMemoryHlsSegmentStore Create(HlsOptions options) =>
        new(Microsoft.Extensions.Options.Options.Create(options), _time, new HlsMetrics(_services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()), _events);

    /// <summary>Opens a stream.</summary>
    /// <param name="store">The store.</param>
    /// <param name="identity">The stream identity.</param>
    /// <param name="incarnation">The incarnation identifier, or <see langword="null"/> for no lifecycle events.</param>
    /// <returns>The writer session.</returns>
    private static Guid Open(InMemoryHlsSegmentStore store, string identity, string? incarnation = null)
    {
        var session = Guid.NewGuid();
        Assert.True(store.TryOpenStream(session, "live", identity, 1, out _, incarnation));
        return session;
    }

    /// <summary>Creates an initialization segment.</summary>
    /// <param name="index">Its index.</param>
    /// <returns>The segment.</returns>
    private static HlsSegment Init(int index) => new(HlsSegmentKind.Initialization, $"init-{index}.mp4", new byte[10]);

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
