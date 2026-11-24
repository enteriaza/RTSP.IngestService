// Tests for the HTTP HLS origin over real HTTP on an ephemeral loopback port: media types, cache headers, entity
// tags and conditional requests, HEAD, ranges, unsupported methods, invalid paths, the active / ended / withdrawn
// lifecycle with segments served until their availability deadlines, concurrent readers while a writer publishes,
// expiry during delivery, client disconnect, a stalled viewer, bind failure and graceful shutdown. Store time is a
// FakeTimeProvider; there are no delays.

using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Http;
using RTSP.IngestService.Tests.Hls;
using RTSP.IngestService.Tests.Infrastructure;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace RTSP.IngestService.Tests.Http;

/// <summary>Tests for <see cref="HlsHttpOrigin"/> and <see cref="HlsHttpApplication"/>.</summary>
public sealed class HlsHttpOriginTests : IAsyncDisposable
{
    /// <summary>Size of segments large enough to outlast socket buffers.</summary>
    private const int Large = 24 * 1024 * 1024;

    /// <summary>Owns the meter factory.</summary>
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();

    /// <summary>The store clock.</summary>
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

    /// <summary>The store.</summary>
    private readonly InMemoryHlsSegmentStore _store;

    /// <summary>The HLS options.</summary>
    private readonly HlsOptions _hlsOptions = new() { MaxSessionStorageMegabytes = 512, MaxTotalStorageMegabytes = 1024 };

    /// <summary>Started origins, stopped on dispose.</summary>
    private readonly List<HlsHttpOrigin> _origins = [];

    /// <summary>The client.</summary>
    private readonly HttpClient _client = new();

    /// <summary>Initializes a new instance of the <see cref="HlsHttpOriginTests"/> class.</summary>
    public HlsHttpOriginTests()
    {
        _store = new InMemoryHlsSegmentStore(MsOptions.Create(_hlsOptions), _time, new HlsMetrics(MeterFactory));
    }

    /// <summary>Gets the relative path of <c>live/cam</c>'s segments in the store's generation.</summary>
    private string SegmentBase => $"hls/live/cam/{_store.Generation}";

    /// <summary>Gets the meter factory.</summary>
    private IMeterFactory MeterFactory => _services.GetRequiredService<IMeterFactory>();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var origin in _origins)
        {
            await origin.StopAsync(CancellationToken.None);
            await origin.DisposeAsync();
        }

        _client.Dispose();
        await _services.DisposeAsync();
    }

    /// <summary>Verifies the playlist media type, cache policy, entity tag, body and HEAD.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Playlist_IsServedUnchangedAndNotStaleCached()
    {
        var baseUri = await StartAsync();
        var session = Open();
        Append(session, 0, 100);

        using var response = await _client.GetAsync(new Uri(baseUri, "hls/live/cam/index.m3u8"), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HlsHttpApplication.PlaylistContentType, response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.CacheControl!.NoCache);
        Assert.False(response.Headers.CacheControl.Public);
        Assert.Null(response.Headers.CacheControl.MaxAge);
        Assert.NotNull(response.Headers.ETag);
        Assert.Equal(_store.GetPlaylist("live", "cam")!.Text, await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(["none"], response.Headers.AcceptRanges);
        Assert.False(response.Headers.Contains("Server"));

        using var head = await SendAsync(HttpMethod.Head, new Uri(baseUri, "hls/live/cam/index.m3u8"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(response.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
        Assert.Equal(response.Headers.ETag, head.Headers.ETag);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync(Token));
    }

    /// <summary>Verifies segment media types, cache policy, exact bytes, HEAD and that ranges are ignored.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Segments_AreServedWithBoundedCachingAndNoRanges()
    {
        var baseUri = await StartAsync();
        var session = Open();
        Append(session, 0, 200_000);

        using var init = await _client.GetAsync(new Uri(baseUri, $"{SegmentBase}/init-0.mp4"), Token);
        Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        Assert.Equal(HlsHttpApplication.InitializationContentType, init.Content.Headers.ContentType!.MediaType);
        Assert.Equal(_store.GetSegment("live", "cam", "init-0.mp4")!.Data.ToArray(), await init.Content.ReadAsByteArrayAsync(Token));

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, $"{SegmentBase}/segment-0.m4s"));
        request.Headers.Range = new RangeHeaderValue(10, 19);
        using var segment = await _client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.OK, segment.StatusCode);
        Assert.Equal(HlsHttpApplication.SegmentContentType, segment.Content.Headers.ContentType!.MediaType);
        Assert.Equal(_store.GetSegment("live", "cam", "segment-0.m4s")!.Data.ToArray(), await segment.Content.ReadAsByteArrayAsync(Token));
        Assert.Equal(["none"], segment.Headers.GetValues("Accept-Ranges"));
        Assert.True(segment.Headers.CacheControl!.Public);
        Assert.Equal(HlsHttpApplication.SegmentCacheLifetime(_hlsOptions), segment.Headers.CacheControl.MaxAge);
        Assert.Equal(["nosniff"], segment.Headers.GetValues("X-Content-Type-Options"));

        using var head = await SendAsync(HttpMethod.Head, new Uri(baseUri, $"{SegmentBase}/segment-0.m4s"));
        Assert.Equal(200_000, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync(Token));
    }

    /// <summary>Verifies the segment cache lifetime bounds how long the origin can serve a segment.</summary>
    [Fact]
    public void SegmentCacheLifetime_CoversListingAndAvailability()
    {
        Assert.Equal(TimeSpan.FromSeconds(30 + (10 * 13)), HlsHttpApplication.SegmentCacheLifetime(new HlsOptions()));
        Assert.Equal(TimeSpan.FromSeconds(30 + (10 * 9)), HlsHttpApplication.SegmentCacheLifetime(new HlsOptions { PlaylistWindowSize = 3 }));
    }

    /// <summary>Verifies If-None-Match: 304 for the current representation, 200 once the playlist changes.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ConditionalRequests_RevalidatePlaylistsAndSegments()
    {
        var baseUri = await StartAsync();
        var session = Open();
        Append(session, 0, 100);
        var playlistUri = new Uri(baseUri, "hls/live/cam/index.m3u8");
        using var first = await _client.GetAsync(playlistUri, Token);
        var etag = first.Headers.ETag!;

        using (var notModified = await GetIfNoneMatchAsync(playlistUri, etag))
        {
            Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
            Assert.Equal(etag, notModified.Headers.ETag);
            Assert.Empty(await notModified.Content.ReadAsByteArrayAsync(Token));
        }

        Append(session, 1, 100, init: false);
        using (var changed = await GetIfNoneMatchAsync(playlistUri, etag))
        {
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            Assert.NotEqual(etag, changed.Headers.ETag);
            Assert.Contains("segment-1.m4s", await changed.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        }

        var segmentUri = new Uri(baseUri, $"{SegmentBase}/segment-0.m4s");
        using var segment = await _client.GetAsync(segmentUri, Token);
        using var segmentNotModified = await GetIfNoneMatchAsync(segmentUri, segment.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, segmentNotModified.StatusCode);

        using var otherSegment = await GetIfNoneMatchAsync(new Uri(baseUri, $"{SegmentBase}/segment-1.m4s"), segment.Headers.ETag!);
        Assert.Equal(HttpStatusCode.OK, otherSegment.StatusCode);
    }

    /// <summary>Verifies 404 (never 500) and its reason for every kind of missing resource.</summary>
    /// <param name="path">The request path.</param>
    /// <param name="resource">The expected resource tag.</param>
    /// <param name="reason">The expected reason tag.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData("hls/live/other/index.m3u8", "playlist", "unknown_stream")]
    [InlineData("hls/live/other/{generation}/segment-0.m4s", "segment", "unknown_stream")]
    [InlineData("hls/live/cam/00000000000000000000000000000000/segment-0.m4s", "segment", "segment_unavailable")]
    [InlineData("hls/live/cam/00000000000000000000000000000000/init-0.mp4", "initialization", "segment_unavailable")]
    [InlineData("hls/live/cam/segment-0.m4s", "other", "invalid_path")]
    [InlineData("hls/live/cam/init-0.mp4", "other", "invalid_path")]
    [InlineData("hls/live/cam/ABCDEF00000000000000000000000000/segment-0.m4s", "other", "invalid_path")]
    [InlineData("hls/live/cam/{generation}/index.m3u8", "other", "invalid_path")]
    [InlineData("hls/live/cam/{generation}/segment-7.m4s", "segment", "segment_unavailable")]
    [InlineData("hls/live/cam/{generation}/init-7.mp4", "initialization", "segment_unavailable")]
    [InlineData("hls/live/cam/other.txt", "other", "invalid_path")]
    [InlineData("hls/live/cam/{generation}/segment-0.mp4", "other", "invalid_path")]
    [InlineData("hls/live/cam/../cam/index.m3u8x", "other", "invalid_path")]
    [InlineData("hls/live/.cam/index.m3u8", "other", "invalid_path")]
    [InlineData("hls/live/cam/sub/index.m3u8", "other", "invalid_path")]
    [InlineData("hls/live/cam%2Findex.m3u8", "other", "invalid_path")]
    [InlineData("index.m3u8", "other", "invalid_path")]
    [InlineData("", "other", "invalid_path")]
    public async Task MissingResources_Are404WithReason(string path, string resource, string reason)
    {
        using var missing = Collect<long>("http.origin.missing");
        var baseUri = await StartAsync();
        Append(Open(), 0, 100);

        using var response = await _client.GetAsync(new Uri(baseUri, path.Replace("{generation}", _store.Generation, StringComparison.Ordinal)), Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var measurement = Assert.Single(missing.GetMeasurementSnapshot());
        Assert.Equal(resource, measurement.Tags["resource"]);
        Assert.Equal(reason, measurement.Tags["reason"]);
    }

    /// <summary>Verifies unsupported methods get 405 with Allow.</summary>
    /// <param name="method">The method.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public async Task UnsupportedMethods_Are405(string method)
    {
        var baseUri = await StartAsync();
        Append(Open(), 0, 100);

        using var response = await SendAsync(new HttpMethod(method), new Uri(baseUri, "hls/live/cam/index.m3u8"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(["GET", "HEAD"], response.Content.Headers.Allow.OrderBy(static m => m, StringComparer.Ordinal));
    }

    /// <summary>
    /// Verifies the ended lifecycle: ENDLIST is served for the retention period, then the playlist is withdrawn while
    /// its segments stay available until their deadline.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task EndedStream_ServesEndListThenWithdrawsAndDrains()
    {
        using var missing = Collect<long>("http.origin.missing");
        var baseUri = await StartAsync();
        var session = Open();
        Append(session, 0, 100);
        _store.CloseStream(session, completed: true);

        var ended = await _client.GetStringAsync(new Uri(baseUri, "hls/live/cam/index.m3u8"), Token);
        Assert.Contains("#EXT-X-ENDLIST", ended, StringComparison.Ordinal);

        _time.Advance(_hlsOptions.EndedStreamRetention);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(baseUri, "index.m3u8")).StatusCode);
        Assert.Equal("playlist_withdrawn", Assert.Single(missing.GetMeasurementSnapshot()).Tags["reason"]);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(baseUri, "segment-0.m4s")).StatusCode);

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(baseUri, "segment-0.m4s")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(baseUri, "init-0.mp4")).StatusCode);
        Assert.Equal("unknown_stream", missing.LastMeasurement!.Tags["reason"]);
    }

    /// <summary>
    /// Verifies the aborted lifecycle: the playlist is withdrawn at once, segments are served until exactly their
    /// deadline, and a replacement stream never reuses an old URL.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task AbortedStream_WithdrawsPlaylistAndReplacementNeverReusesUrls()
    {
        var baseUri = await StartAsync();
        var session = Open();
        Append(session, 0, 100);
        var original = await _client.GetByteArrayAsync(new Uri(baseUri, $"{SegmentBase}/segment-0.m4s"), Token);
        _store.CloseStream(session, completed: false);

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(baseUri, "index.m3u8")).StatusCode);

        var replacement = Open(expected: new HlsStreamNumbering(1, 1));
        _store.AddInitialization(replacement, new HlsSegment(HlsSegmentKind.Initialization, "init-1.mp4", new byte[50]));
        _store.AppendSegment(replacement, Media(1, 100), HlsPlaylistTests.Entry(1, 1.0, "init-1.mp4"));
        Assert.DoesNotContain("segment-0.m4s", await _client.GetStringAsync(new Uri(baseUri, "hls/live/cam/index.m3u8"), Token), StringComparison.Ordinal);
        Assert.Equal(original, await _client.GetByteArrayAsync(new Uri(baseUri, $"{SegmentBase}/segment-0.m4s"), Token));

        _time.Advance(TimeSpan.FromSeconds(2) - TimeSpan.FromTicks(1));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(baseUri, "segment-0.m4s")).StatusCode);
        _time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(baseUri, "segment-0.m4s")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(baseUri, "segment-1.m4s")).StatusCode);
    }

    /// <summary>
    /// Verifies concurrent viewers of the same and different segments and playlist refreshes while a writer publishes
    /// and old segments expire: every response is complete and matches the stored bytes, and none fails.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ConcurrentViewers_WhilePublishingAndExpiring_GetCompleteResponses()
    {
        using var failures = Collect<long>("http.origin.failures");
        var baseUri = await StartAsync();
        var session = Open(targetDuration: 1);
        Append(session, 0, 300_000);
        var published = 1L;
        var rounds = 0L;
        using var stop = new CancellationTokenSource();

        var viewers = Enumerable.Range(0, 8).Select(viewer => Task.Run(
            async () =>
            {
                var checkedSegments = 0;
                while (!stop.IsCancellationRequested)
                {
                    var playlist = await _client.GetStringAsync(new Uri(baseUri, "hls/live/cam/index.m3u8"), Token);
                    Assert.StartsWith("#EXTM3U", playlist, StringComparison.Ordinal);
                    var latest = Interlocked.Read(ref published) - 1;
                    var sequence = viewer % 2 == 0 ? latest : Math.Max(0, latest - (viewer % 3));
                    using var response = await _client.GetAsync(new Uri(baseUri, $"{SegmentBase}/segment-{sequence}.m4s"), Token);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        var body = await response.Content.ReadAsByteArrayAsync(Token);
                        Assert.Equal(300_000, body.Length);
                        Assert.Equal((byte)sequence, body[0]);
                        checkedSegments++;
                    }
                    else
                    {
                        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                    }

                    Interlocked.Increment(ref rounds);
                }

                return checkedSegments;
            },
            Token)).ToArray();

        for (var sequence = 1; sequence < 40; sequence++)
        {
            var target = Interlocked.Read(ref rounds) + viewers.Length;
            await Eventually.WaitUntilAsync(() => Interlocked.Read(ref rounds) >= target || viewers.Any(static v => v.IsFaulted), "viewers progressed");
            Append(session, sequence, 300_000, init: false);
            Interlocked.Exchange(ref published, sequence + 1);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        await stop.CancelAsync();
        var counts = await Task.WhenAll(viewers).WithinSafetyTimeout();
        Assert.All(counts, static count => Assert.True(count > 0));
        Assert.Empty(failures.GetMeasurementSnapshot());
    }

    /// <summary>
    /// Simulates a process restart on the same address: two independent store and origin lifetimes publish the same
    /// application and stream with the same initialization and media numbers. The stable playlist URL serves the
    /// current lifetime, whose generation-qualified URLs differ from the first lifetime's; a cache holding the first
    /// lifetime's URLs gets 404 for them, never the second lifetime's media.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Restart_SameStreamAndNumbers_OldUrlsNeverResolveToNewMedia()
    {
        var first = CreateOrigin();
        await first.StartAsync(Token);
        var port = first.LocalEndPoint!.Port;
        var playlistUri = new Uri($"http://127.0.0.1:{port}/hls/live/cam/index.m3u8");
        Append(Open(expected: new HlsStreamNumbering(0, 0)), 0, 1000);
        var cache = new Dictionary<Uri, (byte[] Body, EntityTagHeaderValue Tag)>();
        using (var firstClient = new HttpClient())
        {
            var firstPlaylist = await firstClient.GetStringAsync(playlistUri, Token);
            foreach (var uri in SegmentUris(playlistUri, firstPlaylist))
            {
                using var response = await firstClient.GetAsync(uri, Token);
                cache[uri] = (await response.Content.ReadAsByteArrayAsync(Token), response.Headers.ETag!);
            }
        }

        await first.StopAsync(Token);
        await first.DisposeAsync();

        var secondStore = new InMemoryHlsSegmentStore(MsOptions.Create(_hlsOptions), new FakeTimeProvider(DateTimeOffset.UnixEpoch), new HlsMetrics(MeterFactory));
        var second = CreateOrigin(port, secondStore);
        await second.StartAsync(Token);
        _origins.Add(second);
        var session = Guid.NewGuid();
        Assert.True(secondStore.TryOpenStream(session, "live", "cam", 10, out var numbering));
        Assert.Equal(new HlsStreamNumbering(0, 0), numbering);
        secondStore.AddInitialization(session, new HlsSegment(HlsSegmentKind.Initialization, "init-0.mp4", [9, 8, 7, 6, 5]));
        secondStore.AppendSegment(session, Media(0, 1000), HlsPlaylistTests.Entry(0, 1.0));
        Assert.NotEqual(_store.Generation, secondStore.Generation);

        using var secondClient = new HttpClient();
        var secondPlaylist = await secondClient.GetStringAsync(playlistUri, Token);
        Assert.Equal(secondStore.GetPlaylist("live", "cam")!.Text, secondPlaylist);
        var secondUris = SegmentUris(playlistUri, secondPlaylist);
        Assert.Equal(["init-0.mp4", "segment-0.m4s"], cache.Keys.Select(static u => u.Segments[^1]));
        Assert.Equal(["init-0.mp4", "segment-0.m4s"], secondUris.Select(static u => u.Segments[^1]));
        Assert.All(secondUris, uri => Assert.Equal($"/hls/live/cam/{secondStore.Generation}/{uri.Segments[^1]}", uri.AbsolutePath));
        Assert.Empty(secondUris.Intersect(cache.Keys));

        foreach (var (uri, cached) in cache)
        {
            using var stale = await secondClient.GetAsync(uri, Token);
            Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
            Assert.Empty(await stale.Content.ReadAsByteArrayAsync(Token));
            using var revalidation = await GetIfNoneMatchAsync(uri, cached.Tag);
            Assert.Equal(HttpStatusCode.NotFound, revalidation.StatusCode);
        }

        foreach (var uri in secondUris)
        {
            using var current = await secondClient.GetAsync(uri, Token);
            var body = await current.Content.ReadAsByteArrayAsync(Token);
            Assert.Equal(secondStore.GetSegment("live", "cam", uri.Segments[^1])!.Data.ToArray(), body);
            Assert.NotEqual(cache.Single(c => c.Key.Segments[^1] == uri.Segments[^1]).Value.Body, body);
            Assert.True(current.Headers.CacheControl!.Public);
            Assert.Equal(HlsHttpApplication.SegmentCacheLifetime(_hlsOptions), current.Headers.CacheControl.MaxAge);
            Assert.DoesNotContain(cache.Values, c => c.Tag.Equals(current.Headers.ETag));
        }
    }

    /// <summary>Verifies a response in progress completes with the full bytes after its segment expires.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ExpiryDuringDelivery_CompletesTheResponse()
    {
        var baseUri = await StartAsync();
        var session = Open();
        Append(session, 0, Large);
        var expected = _store.GetSegment("live", "cam", "segment-0.m4s")!.Data.ToArray();

        using var response = await _client.GetAsync(new Uri(baseUri, $"{SegmentBase}/segment-0.m4s"), HttpCompletionOption.ResponseHeadersRead, Token);
        await using var body = await response.Content.ReadAsStreamAsync(Token);
        var prefix = new byte[1024];
        await body.ReadExactlyAsync(prefix, Token);

        _store.CloseStream(session, completed: false);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(_store.GetSegment("live", "cam", "segment-0.m4s"));
        Assert.Equal(0, _store.TotalBytes);

        using var rest = new MemoryStream();
        await body.CopyToAsync(rest, Token);
        Assert.Equal(expected, prefix.Concat(rest.ToArray()).ToArray());
    }

    /// <summary>
    /// Verifies a viewer that disconnects mid-body is counted as cancelled and releases its request, and that a viewer
    /// that stops reading delays neither other viewers nor the publisher.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task StalledAndDisconnectedViewers_DoNotAffectOthers()
    {
        using var cancellations = Collect<long>("http.origin.cancellations");
        using var active = Collect<long>("http.origin.requests.active");
        var baseUri = await StartAsync();
        var session = Open(targetDuration: 1);
        Append(session, 0, Large);

        var segmentUri = new Uri(baseUri, $"{SegmentBase}/segment-0.m4s");
        using var stalled = await StartUnreadRequestAsync(segmentUri);
        using var leaving = await StartUnreadRequestAsync(segmentUri);

        for (var sequence = 1; sequence < 5; sequence++)
        {
            Append(session, sequence, 1000, init: false);
            Assert.Equal(1000, (await _client.GetByteArrayAsync(new Uri(baseUri, $"{SegmentBase}/segment-{sequence}.m4s"), Token)).Length);
            Assert.Contains($"segment-{sequence}.m4s", await _client.GetStringAsync(new Uri(baseUri, "hls/live/cam/index.m3u8"), Token), StringComparison.Ordinal);
        }

        await Eventually.WaitUntilAsync(() => active.GetMeasurementSnapshot().Sum(static m => m.Value) == 2, "only the two unread responses are in progress");
        Assert.Empty(cancellations.GetMeasurementSnapshot());

        leaving.Dispose();
        stalled.Dispose();
        await Eventually.WaitUntilAsync(() => cancellations.GetMeasurementSnapshot().Count == 2, "both large responses cancelled");
        await Eventually.WaitUntilAsync(() => active.GetMeasurementSnapshot().Sum(static m => m.Value) == 0, "no active requests");
        Assert.All(cancellations.GetMeasurementSnapshot(), static m => Assert.Equal("segment", m.Tags["resource"]));
    }

    /// <summary>Verifies request metrics carry only fixed-vocabulary tags.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Metrics_UseOnlyLowCardinalityTags()
    {
        using var requests = Collect<long>("http.origin.requests");
        using var bytes = Collect<long>("http.origin.response_bytes");
        using var duration = Collect<double>("http.origin.request.duration");
        var baseUri = await StartAsync();
        Append(Open(), 0, 150_000);

        await _client.GetByteArrayAsync(new Uri(baseUri, $"{SegmentBase}/segment-0.m4s"), Token);
        await _client.GetStringAsync(new Uri(baseUri, "hls/live/cam/index.m3u8"), Token);
        await GetAsync(baseUri, "segment-9.m4s");

        await Eventually.WaitUntilAsync(() => duration.GetMeasurementSnapshot().Count == 3, "three requests completed");
        var tags = requests.GetMeasurementSnapshot().Select(static m => ((string?)m.Tags["resource"], (string?)m.Tags["status_class"])).ToArray();
        Assert.Equal([("segment", "2xx"), ("playlist", "2xx"), ("segment", "4xx")], tags);
        Assert.Equal(150_000, bytes.GetMeasurementSnapshot().Where(static m => (string?)m.Tags["resource"] == "segment").Sum(static m => m.Value));
        Assert.Equal(3, duration.GetMeasurementSnapshot().Count);
        Assert.All(
            requests.GetMeasurementSnapshot().SelectMany(static m => m.Tags.Values),
            static value => Assert.DoesNotContain("cam", (string)value!, StringComparison.Ordinal));
    }

    /// <summary>Verifies a port conflict fails start with the bind failure as the inner exception.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task PortConflict_FailsStart()
    {
        using var occupier = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        occupier.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        occupier.Listen();

        var origin = CreateOrigin(((IPEndPoint)occupier.LocalEndPoint!).Port);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => origin.StartAsync(Token));

        Assert.NotNull(exception.InnerException);
        Assert.Null(origin.LocalEndPoint);
        await origin.DisposeAsync();
    }

    /// <summary>
    /// Verifies graceful shutdown: new connections are refused once stopping starts, and a response in progress
    /// completes before stop returns.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Stop_RefusesNewConnectionsAndDrainsInFlightResponses()
    {
        var baseUri = await StartAsync();
        var origin = _origins[^1];
        Append(Open(), 0, Large);
        var expected = _store.GetSegment("live", "cam", "segment-0.m4s")!.Data.ToArray();

        using var socket = await StartUnreadRequestAsync(new Uri(baseUri, $"{SegmentBase}/segment-0.m4s"));
        var stopping = origin.StopAsync(Token);

        using var lateClient = new HttpClient();
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => lateClient.GetAsync(new Uri(baseUri, "hls/live/cam/index.m3u8"), Token));
        Assert.False(stopping.IsCompleted);

        using var received = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = await socket.ReceiveAsync(buffer, Token)) > 0)
        {
            received.Write(buffer, 0, count);
        }

        var response = received.ToArray();
        var headerEnd = response.AsSpan().IndexOf("\r\n\r\n"u8) + 4;
        Assert.Contains($"Content-Length: {Large}\r\n", Encoding.ASCII.GetString(response, 0, headerEnd), StringComparison.Ordinal);
        Assert.Equal(expected, response[headerEnd..]);
        await stopping.WithinSafetyTimeout();
    }

    /// <summary>Gets the test cancellation token.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Creates a media segment whose first byte is its sequence number.</summary>
    /// <param name="sequence">The sequence number.</param>
    /// <param name="size">Its size.</param>
    /// <returns>The segment.</returns>
    private static HlsSegment Media(long sequence, int size)
    {
        var data = new byte[size];
        Random.Shared.NextBytes(data);
        data[0] = (byte)sequence;
        return new HlsSegment(HlsSegmentKind.Media, $"segment-{sequence}.m4s", data);
    }

    /// <summary>Opens <c>live/cam</c>.</summary>
    /// <param name="targetDuration">The target duration in seconds.</param>
    /// <param name="expected">The expected numbering, if checked.</param>
    /// <returns>The session.</returns>
    private Guid Open(int targetDuration = 10, HlsStreamNumbering? expected = null)
    {
        var session = Guid.NewGuid();
        Assert.True(_store.TryOpenStream(session, "live", "cam", targetDuration, out var numbering));
        if (expected is { } value)
        {
            Assert.Equal(value, numbering);
        }

        return session;
    }

    /// <summary>Stores a 1 s media segment, after <c>init-0.mp4</c> when <paramref name="init"/>.</summary>
    /// <param name="session">The session.</param>
    /// <param name="sequence">The sequence number.</param>
    /// <param name="size">The segment size.</param>
    /// <param name="init">Whether to store the initialization segment first.</param>
    private void Append(Guid session, long sequence, int size, bool init = true)
    {
        if (init)
        {
            _store.AddInitialization(session, new HlsSegment(HlsSegmentKind.Initialization, "init-0.mp4", [1, 2, 3, 4, 5]));
        }

        Assert.Equal(HlsAppendResult.Published, _store.AppendSegment(session, Media(sequence, size), HlsPlaylistTests.Entry(sequence, 1.0)));
    }

    /// <summary>
    /// Sends a GET on a raw socket with a 4 KiB receive buffer, which disables receive-window autotuning, reads the
    /// start of the 200 response and then stops reading, so the response stays in progress until the socket closes.
    /// </summary>
    /// <param name="uri">The resource.</param>
    /// <returns>The connected socket.</returns>
    private static async Task<Socket> StartUnreadRequestAsync(Uri uri)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ReceiveBufferSize = 4096 };
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, uri.Port), Token);
        await socket.SendAsync(Encoding.ASCII.GetBytes($"GET {uri.AbsolutePath} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"), Token);
        var start = new byte[12];
        for (var received = 0; received < start.Length;)
        {
            var count = await socket.ReceiveAsync(start.AsMemory(received), Token);
            Assert.NotEqual(0, count);
            received += count;
        }

        Assert.Equal("HTTP/1.1 200", Encoding.ASCII.GetString(start));
        return socket;
    }

    /// <summary>Returns the initialization and media segment URLs a playlist references, in order.</summary>
    /// <param name="playlistUri">The playlist URL, against which the references resolve.</param>
    /// <param name="playlist">The playlist text.</param>
    /// <returns>The distinct absolute URLs.</returns>
    private static List<Uri> SegmentUris(Uri playlistUri, string playlist)
    {
        const string map = "#EXT-X-MAP:URI=\"";
        return [.. playlist.Split('\n')
            .Where(static line => line.Length > 0 && (!line.StartsWith('#') || line.StartsWith(map, StringComparison.Ordinal)))
            .Select(static line => line.StartsWith(map, StringComparison.Ordinal) ? line[map.Length..^1] : line)
            .Distinct(StringComparer.Ordinal)
            .Select(reference => new Uri(playlistUri, reference))];
    }

    /// <summary>Creates an origin on a loopback port.</summary>
    /// <param name="port">The port; 0 for an ephemeral port.</param>
    /// <param name="store">The store to serve; the test's store by default.</param>
    /// <returns>The origin, not started.</returns>
    private HlsHttpOrigin CreateOrigin(int port = 0, InMemoryHlsSegmentStore? store = null)
    {
        var application = new HlsHttpApplication(store ?? _store, MsOptions.Create(_hlsOptions), new HttpOriginMetrics(MeterFactory), _time, NullLogger<HlsHttpApplication>.Instance);
        var options = new HttpOriginOptions { ListenAddress = "127.0.0.1", Port = port };
        return new HlsHttpOrigin(MsOptions.Create(options), application, _services, NullLoggerFactory.Instance);
    }

    /// <summary>Starts an origin on an ephemeral port.</summary>
    /// <returns>Its base URI.</returns>
    private async Task<Uri> StartAsync()
    {
        var origin = CreateOrigin();
        await origin.StartAsync(Token);
        _origins.Add(origin);
        return new Uri($"http://127.0.0.1:{origin.LocalEndPoint!.Port}/");
    }

    /// <summary>Sends a request without a body.</summary>
    /// <param name="method">The method.</param>
    /// <param name="uri">The URI.</param>
    /// <returns>The response.</returns>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri)
    {
        using var request = new HttpRequestMessage(method, uri);
        return await _client.SendAsync(request, Token);
    }

    /// <summary>Gets a resource of <c>live/cam</c>.</summary>
    /// <param name="baseUri">The origin.</param>
    /// <param name="file">The file name.</param>
    /// <returns>The response, with its body read.</returns>
    private async Task<HttpResponseMessage> GetAsync(Uri baseUri, string file)
    {
        var path = file == "index.m3u8" ? $"hls/live/cam/{file}" : $"{SegmentBase}/{file}";
        var response = await _client.GetAsync(new Uri(baseUri, path), Token);
        await response.Content.LoadIntoBufferAsync(Token);
        return response;
    }

    /// <summary>Sends a conditional GET.</summary>
    /// <param name="uri">The URI.</param>
    /// <param name="etag">The entity tag.</param>
    /// <returns>The response.</returns>
    private async Task<HttpResponseMessage> GetIfNoneMatchAsync(Uri uri, EntityTagHeaderValue etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.IfNoneMatch.Add(etag);
        return await _client.SendAsync(request, Token);
    }

    /// <summary>Collects an HTTP origin instrument.</summary>
    /// <typeparam name="T">The measurement type.</typeparam>
    /// <param name="instrument">The instrument name.</param>
    /// <returns>The collector.</returns>
    private MetricCollector<T> Collect<T>(string instrument)
        where T : struct => new(MeterFactory, HttpOriginMetrics.MeterName, instrument);
}
