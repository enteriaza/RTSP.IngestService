// Integration tests of the stream catalog's playlistAvailable against the real HLS playlist route: the real registry,
// media stage, HLS packaging and store feed both a catalog reconciled through RuntimeStreamInventorySource and an
// HTTP origin on an ephemeral loopback port. At every lifecycle phase (pending, live before and after the first
// segment, ended, withdrawn and draining, replacement) the catalog's state and availability are compared with what the
// playlist route actually returns. Store time is a FakeTimeProvider; there are no delays.

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Http;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Streams;
using RTSP.IngestService.Tests.Hls;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace RTSP.IngestService.Tests.Streams;

/// <summary>Tests that <see cref="StreamHlsInfo.PlaylistAvailable"/> agrees with the real playlist route.</summary>
public sealed class StreamCatalogPlaylistAvailabilityTests : IAsyncDisposable
{
    /// <summary>The playlist path of <c>live/camera1</c>.</summary>
    private const string PlaylistPath = "/hls/live/camera1/index.m3u8";

    /// <summary>The ended-playlist retention.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(30);

    /// <summary>The media and HLS stages on a fake clock.</summary>
    private readonly HlsHarness _harness = new(new HlsOptions
    {
        TargetSegmentDuration = TimeSpan.FromSeconds(1),
        MaxSegmentDuration = TimeSpan.FromSeconds(2),
        EndedStreamRetention = Retention,
    });

    /// <summary>The registry.</summary>
    private readonly PublishedStreamRegistry _registry = new();

    /// <summary>Owns the meter factory and serves the origin.</summary>
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();

    /// <summary>The client.</summary>
    private readonly HttpClient _client = new();

    /// <summary>The catalog under test.</summary>
    private readonly StreamCatalog _catalog;

    /// <summary>The origin, once started.</summary>
    private HlsHttpOrigin? _origin;

    /// <summary>Initializes a new instance of the <see cref="StreamCatalogPlaylistAvailabilityTests"/> class.</summary>
    public StreamCatalogPlaylistAvailabilityTests() => _catalog = new StreamCatalog(
        new RuntimeStreamInventorySource(_registry, _harness.Processor, _harness.Store),
        new StreamCatalogMetrics(_services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()),
        _harness.Time,
        NullLogger<StreamCatalog>.Instance);

    /// <summary>Gets the test cancellation token.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_origin is not null)
        {
            await _origin.StopAsync(CancellationToken.None);
            await _origin.DisposeAsync();
        }

        _client.Dispose();
        _catalog.Dispose();
        _harness.Dispose();
        await _services.DisposeAsync();
    }

    /// <summary>
    /// Verifies a publication's complete lifecycle against the playlist route: pending (registered, HLS not opened),
    /// live without a playlist (opened, configured, first segment still open), live with the playlist once the first
    /// segment is published, ended with the ended playlist retained, withdrawn after retention while its segments
    /// drain, and gone once drained. The catalog never reports a playlist the route does not serve.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Lifecycle_PlaylistAvailabilityMatchesRoute()
    {
        await StartOriginAsync();
        var session = NewSession();
        Assert.True(_registry.TryAdd(session));
        await AssertAgreesAsync(session, StreamHlsState.Pending, available: false);

        await _harness.Processor.OnSessionStartedAsync(session, Token);
        Assert.True(session.HlsStreamOpened);
        await AssertAgreesAsync(session, StreamHlsState.Live, available: false);
        Assert.True(_catalog.HasUnsettled);

        await _harness.ConfigureAsync(session, video: true, audio: false);
        await _harness.VideoAsync(session, 0, idr: true);
        await _harness.VideoAsync(session, 500, idr: false);
        Assert.Null(_harness.Playlist());
        await AssertAgreesAsync(session, StreamHlsState.Live, available: false);
        Assert.True(_catalog.HasUnsettled);

        await _harness.VideoAsync(session, 1000, idr: true);
        Assert.NotNull(_harness.Playlist());
        await AssertAgreesAsync(session, StreamHlsState.Live, available: true);
        Assert.False(_catalog.HasUnsettled);

        for (uint ms = 2000; ms <= 3000; ms += 1000)
        {
            await _harness.VideoAsync(session, ms, idr: true);
        }

        var segment = $"/hls/live/camera1/{_harness.Store.Generation}/{_harness.Playlist()!.Segments[0].Name}";
        _registry.Remove(session);
        await _harness.EndAsync(session);
        await AssertAgreesAsync(session, StreamHlsState.Ended, available: true, publishing: StreamPublishingState.Ended);
        Assert.Contains("#EXT-X-ENDLIST", await _client.GetStringAsync(Uri(PlaylistPath), Token), StringComparison.Ordinal);

        _harness.Time.Advance(Retention);
        await AssertAgreesAsync(session, StreamHlsState.Withdrawn, available: false, publishing: StreamPublishingState.Ended);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(segment));

        _harness.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(segment));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(PlaylistPath));
        _catalog.Reconcile();
        Assert.Empty(_catalog.Current.Streams);
    }

    /// <summary>
    /// Verifies a replacement against the playlist route: when a new publication opens while the previous one's ended
    /// playlist is retained, the previous playlist is withdrawn and the new one is live but unavailable (the route
    /// returns 404) until its own first segment, after which the route serves the new playlist. Each incarnation keeps
    /// its own state and availability.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Replacement_NewIncarnationUnavailableUntilItsFirstSegment()
    {
        await StartOriginAsync();
        var first = NewSession();
        Assert.True(_registry.TryAdd(first));
        await _harness.Processor.OnSessionStartedAsync(first, Token);
        await _harness.ConfigureAsync(first, video: true, audio: false);
        await SegmentsAsync(first, 0);
        _registry.Remove(first);
        await _harness.EndAsync(first);
        await AssertAgreesAsync(first, StreamHlsState.Ended, available: true, publishing: StreamPublishingState.Ended);
        var endedPlaylist = await _client.GetStringAsync(Uri(PlaylistPath), Token);

        var second = NewSession();
        Assert.True(_registry.TryAdd(second));
        await _harness.Processor.OnSessionStartedAsync(second, Token);
        await _harness.ConfigureAsync(second, video: true, audio: false);
        await AssertAgreesAsync(second, StreamHlsState.Live, available: false);
        var previous = _catalog.Current.FindIncarnation(first.IncarnationId)!;
        Assert.Equal((false, StreamHlsState.Withdrawn, false), (previous.Current, previous.Hls.State, previous.Hls.PlaylistAvailable));
        Assert.True(_catalog.Current.Find("live", "camera1")!.Generation > first.Generation);

        await SegmentsAsync(second, 10_000);
        await AssertAgreesAsync(second, StreamHlsState.Live, available: true);
        Assert.NotEqual(endedPlaylist, await _client.GetStringAsync(Uri(PlaylistPath), Token));
        Assert.False(_catalog.Current.FindIncarnation(first.IncarnationId)!.Hls.PlaylistAvailable);
    }

    /// <summary>
    /// Verifies readers of the catalog never observe a false availability while a publication runs concurrently with
    /// reconciliations: availability is only ever reported for live or ended states, and once a reader has seen the
    /// playlist available it is never reported unavailable again while the stream is still live or ended.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ConcurrentPublicationAndReaders_NeverReportFalseAvailability()
    {
        var session = NewSession();
        Assert.True(_registry.TryAdd(session));
        using var done = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var violations = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var observedAvailable = 0;
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(
            () =>
            {
                var seen = false;
                while (!done.IsCancellationRequested)
                {
                    if (_catalog.Current.FindIncarnation(session.IncarnationId) is not { } entry)
                    {
                        continue;
                    }

                    var hls = entry.Hls;
                    if (hls.PlaylistAvailable && hls.State is not (StreamHlsState.Live or StreamHlsState.Ended))
                    {
                        violations.Enqueue($"available while {hls.State}");
                    }

                    if (seen && !hls.PlaylistAvailable && hls.State is StreamHlsState.Live or StreamHlsState.Ended)
                    {
                        violations.Enqueue($"unavailable again while {hls.State}");
                    }

                    if (hls.PlaylistAvailable && _harness.Playlist() is null)
                    {
                        violations.Enqueue("available before the store published a playlist");
                    }

                    seen |= hls.PlaylistAvailable;
                    if (seen)
                    {
                        Interlocked.Exchange(ref observedAvailable, 1);
                    }
                }
            },
            Token)).ToArray();
        var reconciler = Task.Run(
            () =>
            {
                while (!done.IsCancellationRequested)
                {
                    _catalog.Reconcile();
                }
            },
            Token);

        await _harness.Processor.OnSessionStartedAsync(session, Token);
        await _harness.ConfigureAsync(session, video: true, audio: false);
        for (uint ms = 0; ms <= 20_000; ms += 1000)
        {
            await _harness.VideoAsync(session, ms, idr: true);
        }

        await Infrastructure.Eventually.WaitUntilAsync(() => Volatile.Read(ref observedAvailable) == 1, "a reader observes the playlist available");
        await done.CancelAsync();
        await Task.WhenAll([.. readers, reconciler]);
        Assert.Empty(violations);
    }

    /// <summary>Creates a session for <c>live/camera1</c>.</summary>
    /// <returns>The session.</returns>
    private static PublisherSession NewSession() =>
        new(1, null, "live", "camera1", "live", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    /// <summary>Starts the HTTP origin over the harness store.</summary>
    /// <returns>A task.</returns>
    private async Task StartOriginAsync()
    {
        var application = new HlsHttpApplication(_harness.Store, MsOptions.Create(_harness.Options), new HttpOriginMetrics(_services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()), _harness.Time, NullLogger<HlsHttpApplication>.Instance);
        _origin = new HlsHttpOrigin(MsOptions.Create(new HttpOriginOptions { ListenAddress = "127.0.0.1", Port = 0 }), application, _services, NullLoggerFactory.Instance);
        await _origin.StartAsync(Token);
    }

    /// <summary>Returns the absolute URI of a path on the origin.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The URI.</returns>
    private Uri Uri(string path) => new($"http://127.0.0.1:{_origin!.LocalEndPoint!.Port}{path}");

    /// <summary>Returns the status of a GET.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The status.</returns>
    private async Task<HttpStatusCode> StatusAsync(string path)
    {
        using var response = await _client.GetAsync(Uri(path), Token);
        return response.StatusCode;
    }

    /// <summary>
    /// Reconciles, then asserts the incarnation's catalog state and availability, and that the playlist route agrees:
    /// 200 exactly when the catalog reports the playlist available.
    /// </summary>
    /// <param name="session">The incarnation's session.</param>
    /// <param name="state">The expected HLS state.</param>
    /// <param name="available">The expected availability.</param>
    /// <param name="publishing">The expected publishing state.</param>
    /// <returns>A task.</returns>
    private async Task AssertAgreesAsync(PublisherSession session, StreamHlsState state, bool available, StreamPublishingState publishing = StreamPublishingState.Publishing)
    {
        Assert.True(_catalog.Reconcile());
        var entry = _catalog.Current.FindIncarnation(session.IncarnationId) ?? throw new InvalidOperationException("No entry.");
        Assert.Equal((publishing, state, available), (entry.Publishing.State, entry.Hls.State, entry.Hls.PlaylistAvailable));
        var status = await StatusAsync(PlaylistPath);
        Assert.Equal(available ? HttpStatusCode.OK : HttpStatusCode.NotFound, status);
    }

    /// <summary>Sends keyframes one second apart, completing three segments.</summary>
    /// <param name="session">The session.</param>
    /// <param name="start">The first timestamp.</param>
    /// <returns>A task.</returns>
    private async Task SegmentsAsync(PublisherSession session, uint start)
    {
        for (var ms = start; ms <= start + 3000; ms += 1000)
        {
            await _harness.VideoAsync(session, ms, idr: true);
        }
    }
}
