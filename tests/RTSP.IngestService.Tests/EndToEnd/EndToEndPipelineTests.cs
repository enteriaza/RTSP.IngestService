// End-to-end verification of one real FFmpeg publication through the production host: RTMP accept, authentication and
// registration, media description, HLS packaging and HTTP playback (live refresh, segments, ENDLIST, ffprobe and a
// strict decode, withdrawal at the end of retention and the final drain), the stream catalog's state sequence and
// media description through /health/streams, the lifecycle events delivered to a consumer attached to the real
// dispatcher, and OTLP export to a loopback collector with in-memory measurements that grow with the workload and
// carry no identifying tags.

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using OpenTelemetry.Metrics;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Http;
using RTSP.IngestService.Lifecycle;
using RTSP.IngestService.Streams;
using RTSP.IngestService.Tests.Hls;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Tests.Telemetry;

namespace RTSP.IngestService.Tests.EndToEnd;

/// <summary>End-to-end tests of a single publication.</summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class EndToEndPipelineTests : IDisposable
{
    /// <summary>Meters that must appear in the OTLP payload.</summary>
    private static readonly string[] Meters = ["RTSP.IngestService.Rtmp", "RTSP.IngestService.Media", "RTSP.IngestService.Hls", "RTSP.IngestService.Http", "RTSP.IngestService.Streams"];

    /// <summary>Instruments whose totals must grow with the workload.</summary>
    private static readonly string[] Growing = ["rtmp.connections.accepted", "rtmp.bytes_received", "media.samples", "hls.segments", "http.origin.requests", "stream_events.published", "stream_catalog.reconciliations"];

    /// <summary>Captured host logs.</summary>
    private readonly FakeLogCollector _logs = new();

    /// <summary>The client.</summary>
    private readonly HttpClient _client = new();

    /// <summary>Gets the test cancellation token.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    /// <summary>Verifies the whole pipeline for one H.264 and AAC publication, from RTMP accept to the final drain.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task FfmpegPublication_FlowsThroughEveryStage()
    {
        var tools = HlsFfmpegInteropTests.Tools();
        var evidence = new Evidence("pipeline");
        await using var collector = new FakeCollector(respond: true);
        var overrides = EndToEndHost.Settings();
        foreach (var (key, value) in TelemetryTestSupport.Enabled(collector.Endpoint, interval: "00:00:01"))
        {
            overrides[key] = value;
        }

        overrides["Hls:EndedStreamRetention"] = "00:00:03";
        overrides["StreamCatalog:ReconciliationInterval"] = "00:00:01";
        var exported = new List<MetricSnapshot>();
        using var host = EndToEndHost.Build(_logs, overrides, exported);
        await host.StartAsync(Token).WithinSafetyTimeout();
        var origin = host.Services.GetRequiredService<HlsHttpOrigin>();
        var catalog = host.Services.GetRequiredService<StreamCatalog>();
        var consumer = host.Services.GetRequiredService<RecordingEventConsumer>();
        var provider = host.Services.GetRequiredService<MeterProvider>();
        var rtmpPort = EndToEndHost.RtmpPort(host);
        _origin = origin.LocalEndPoint!;
        var httpPort = origin.LocalEndPoint!.Port;
        Uri Url(string path) => new($"http://127.0.0.1:{httpPort}{path}");
        provider.ForceFlush(10_000);
        var baseline = Totals(exported);
        evidence.Add($"host started: rtmp port {rtmpPort}, http port {httpPort}, store generation {host.Services.GetRequiredService<IHlsSegmentStore>().Generation}");

        using var sampling = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var states = new List<string>();
        var sampler = Task.Run(async () =>
        {
            string? last = null;
            void Sample()
            {
                var entry = catalog.Current.Find("live", "camera1");
                var state = entry is null ? "absent" : $"{entry.Publishing.State}/{entry.Hls.State}/media={(entry.Media is null ? "none" : "described")}";
                if (state != last)
                {
                    states.Add(state);
                    evidence.Add($"catalog live/camera1: {state}{(entry is null ? string.Empty : $" incarnation={entry.IncarnationId} generation={entry.Generation}")}");
                    last = state;
                }
            }

            while (!sampling.IsCancellationRequested)
            {
                Sample();
                await Task.Delay(5, Token);
            }

            // Cancellation can arrive before the polling loop observes the final state.
            Sample();
        }, Token);

        string incarnation;
        string segmentPath;
        try
        {
            var publisher = new FfmpegPublisher(tools.Ffmpeg, rtmpPort, EndToEndHost.Key(1), seconds: 8);
            await using (publisher)
            {
                await Eventually.WaitUntilAsync(() => catalog.Current.Find("live", "camera1") is { Media.Video: not null, Media.Audio: not null }, "the publication is described in the catalog");
                var live = await WaitForPlaylistAsync("/hls/live/camera1/index.m3u8", static t => Segments(t).Count >= 2);
                Assert.DoesNotContain("#EXT-X-ENDLIST", live, StringComparison.Ordinal);
                var refreshed = await WaitForPlaylistAsync("/hls/live/camera1/index.m3u8", t => Segments(t).Count > Segments(live).Count);
                evidence.Add($"live playlist refreshed from {Segments(live).Count} to {Segments(refreshed).Count} segments");
                var map = refreshed.Split('\n').Single(static l => l.StartsWith("#EXT-X-MAP:URI=", StringComparison.Ordinal))["#EXT-X-MAP:URI=\"".Length..].TrimEnd('"', '\r');
                await AssertResourceAsync($"/hls/live/camera1/{map}", HlsHttpApplication.InitializationContentType);
                foreach (var segment in Segments(refreshed))
                {
                    await AssertResourceAsync($"/hls/live/camera1/{segment}", HlsHttpApplication.SegmentContentType);
                }

                segmentPath = $"/hls/live/camera1/{Segments(refreshed)[0]}";
                evidence.Add($"fetched {map} and {Segments(refreshed).Count} listed segments with 200");

                var entry = catalog.Current.Find("live", "camera1")!;
                incarnation = entry.IncarnationId;
                Assert.Equal(32, incarnation.Length);
                Assert.True(entry.Current);
                Assert.Equal((StreamPublishingState.Publishing, StreamHlsState.Live), (entry.Publishing.State, entry.Hls.State));
                Assert.StartsWith("avc1.64", entry.Media!.Video!.Codec, StringComparison.Ordinal);
                Assert.Equal((320, 240), (entry.Media.Video.Width, entry.Media.Video.Height));
                Assert.Equal(("mp4a.40.2", 48000, 2), (entry.Media.Audio!.Codec, entry.Media.Audio.SampleRate, entry.Media.Audio.Channels));
                evidence.Add($"catalog media: video {entry.Media.Video.Codec} {entry.Media.Video.Width}x{entry.Media.Video.Height} @{entry.Media.Video.FrameRate}, audio {entry.Media.Audio.Codec} {entry.Media.Audio.SampleRate} Hz x{entry.Media.Audio.Channels}");

                await AssertManagementAsync(Url, incarnation, evidence);

                var exit = await publisher.WaitAsync();
                Assert.True(exit.Code == 0, $"FFmpeg exited with {exit.Code}: {exit.Errors}");
            }

            var ended = await WaitForPlaylistAsync("/hls/live/camera1/index.m3u8", static t => t.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal));
            evidence.Add($"ended playlist: {Segments(ended).Count} segments, {Duration(ended):F3} s, ENDLIST");
            var probe = await HlsFfmpegInteropTests.ProbeAsync(tools.Probe, Url("/hls/live/camera1/index.m3u8").AbsoluteUri);
            var video = probe.Streams.Single(static s => s.CodecType == "video");
            var audio = probe.Streams.Single(static s => s.CodecType == "audio");
            Assert.Equal(("h264", 320, 240), (video.CodecName, video.Width, video.Height));
            Assert.Equal(("aac", "48000", 2), (audio.CodecName, audio.SampleRate, audio.Channels));
            Assert.Equal(200, video.Frames);
            Assert.Equal(Duration(ended), probe.Duration, 0.0015);
            await HlsFfmpegInteropTests.DecodeAsync(tools.Ffmpeg, Url("/hls/live/camera1/index.m3u8").AbsoluteUri);
            evidence.Add($"ffprobe over HTTP: video {video.CodecName} {video.Width}x{video.Height} {video.Frames} frames, audio {audio.CodecName} {audio.SampleRate} Hz x{audio.Channels} {audio.Frames} frames, duration {probe.Duration:F3} s; -xerror decode clean");
            await Eventually.WaitUntilAsync(() => catalog.Current.Find("live", "camera1") is { Publishing.State: StreamPublishingState.Ended, Hls.State: StreamHlsState.Ended }, "the catalog reports the ended playlist");

            await Eventually.WaitUntilAsync(() => Status("/hls/live/camera1/index.m3u8") == HttpStatusCode.NotFound, "the playlist is withdrawn at the end of retention");
            Assert.Equal(HttpStatusCode.OK, Status(segmentPath));
            evidence.Add("retention expired: playlist 404, withdrawn segments still 200 while draining");
            await Eventually.WaitUntilAsync(() => Status(segmentPath) == HttpStatusCode.NotFound, "the withdrawn segments drain");
            await Eventually.WaitUntilAsync(() => catalog.Current.Find("live", "camera1") is null, "the drained incarnation leaves the catalog");
            Assert.Equal(HttpStatusCode.NotFound, Status("/health/streams/live/camera1"));
            evidence.Add("drained: segment 404, catalog entry removed, /health/streams/live/camera1 404");
        }
        finally
        {
            await sampling.CancelAsync();
            await sampler;
        }

        Assert.Equal("absent", states[0]);
        Assert.StartsWith("Publishing/", states[1], StringComparison.Ordinal);
        Assert.Contains(states, static s => s.StartsWith("Publishing/Live/", StringComparison.Ordinal));
        Assert.Contains(states, static s => s.StartsWith("Ended/Ended/", StringComparison.Ordinal));
        Assert.Equal("absent", states[^1]);
        AssertForwardOnly(states);

        AssertEvents(consumer.Of(incarnation), incarnation, rtmpPort, httpPort, evidence);
        Assert.Equal(StreamEventReasons.RetentionExpired, consumer.Of(incarnation).Single(static e => e.Type == StreamEventType.PlaylistWithdrawn).Reason);

        provider.ForceFlush(10_000);
        var totals = Totals(exported);
        foreach (var name in Growing)
        {
            Assert.True(totals.GetValueOrDefault(name) > baseline.GetValueOrDefault(name), $"{name} did not grow: {baseline.GetValueOrDefault(name)} -> {totals.GetValueOrDefault(name)}");
            evidence.Add($"metric {name}: {baseline.GetValueOrDefault(name)} -> {totals[name]}");
        }

        string[] forbidden = [EndToEndHost.Key(1), "camera1", "live", "127.0.0.1", rtmpPort.ToString(System.Globalization.CultureInfo.InvariantCulture), httpPort.ToString(System.Globalization.CultureInfo.InvariantCulture), incarnation];
        foreach (var metric in exported)
        {
            Assert.True(TelemetryTestSupport.Vocabulary.TryGetValue(metric.Name, out var vocabulary), $"unexpected instrument {metric.Name}");
            foreach (var point in metric.MetricPoints)
            {
                foreach (var tag in point.Tags)
                {
                    var value = Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture)!;
                    Assert.True(vocabulary.TryGetValue(tag.Key, out var allowed) && allowed.Contains(value), $"{metric.Name} has tag {tag.Key}={value}");
                    Assert.DoesNotContain(value, forbidden);
                }
            }
        }

        await Eventually.WaitUntilAsync(() => collector.Requests.Any(r => Meters.All(m => Contains(r.Body, m))), "an OTLP export carries every application meter");
        Assert.All(collector.Requests, r => Assert.Equal("POST /v1/metrics HTTP/1.1", r.RequestLine));
        Assert.All(collector.Requests, r => Assert.All(new[] { EndToEndHost.Key(1), incarnation, "camera1" }, f => Assert.False(Contains(r.Body, f), $"OTLP payload contains {f}")));
        evidence.Add($"OTLP: {collector.Requests.Count} POST /v1/metrics requests, application/x-protobuf; a payload carries all five meters; no key, stream or incarnation in any payload");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await host.StopAsync(CancellationToken.None).WithinSafetyTimeout();
        evidence.Add($"host stopped in {stopwatch.Elapsed.TotalSeconds:F3} s; lifecycle {host.Services.GetRequiredService<IApplicationLifecycle>().State}");
        Assert.Equal(ApplicationState.Stopped, host.Services.GetRequiredService<IApplicationLifecycle>().State);
        Assert.DoesNotContain(_logs.GetSnapshot(), static r => r.Level >= LogLevel.Error || r.Id.Id is 1100 or 9100 or 9102 or 9301 or 9302);
        Assert.DoesNotContain(_logs.GetSnapshot(), static r => r.Message.Contains("e2e-secret-key-0001", StringComparison.Ordinal));
        evidence.Save();
    }

    /// <summary>Asserts the forward-only catalog state sequence of one incarnation.</summary>
    /// <param name="states">The observed states.</param>
    internal static void AssertForwardOnly(List<string> states)
    {
        string[] order = ["absent", "Publishing/Pending", "Publishing/Live", "Ended/Live", "Ended/Ended", "Ended/Withdrawn", "absent"];
        var position = 0;
        foreach (var state in states.Select(static s => s.Split("/media=")[0]))
        {
            var next = Array.IndexOf(order, state, position);
            Assert.True(next >= position, $"catalog state {state} after {order[position]}: {string.Join(" -> ", states)}");
            position = next;
        }
    }

    /// <summary>Asserts one incarnation's lifecycle events: order, schema, identity, timestamps and content.</summary>
    /// <param name="events">The incarnation's events.</param>
    /// <param name="incarnation">The incarnation.</param>
    /// <param name="rtmpPort">The RTMP port, which must not appear.</param>
    /// <param name="httpPort">The HTTP port, which must not appear.</param>
    /// <param name="evidence">The evidence log.</param>
    internal static void AssertEvents(List<StreamEvent> events, string incarnation, int rtmpPort, int httpPort, Evidence evidence)
    {
        evidence.Add($"events of {incarnation}: {string.Join(", ", events.Select(static e => e.Reason is null ? e.Type.ToString() : $"{e.Type}({e.Reason})"))}");
        Assert.Equal(StreamEventType.Published, events[0].Type);
        Assert.Equal(StreamEventType.Drained, events[^1].Type);
        foreach (var terminal in new[] { StreamEventType.Published, StreamEventType.Ended, StreamEventType.PlaylistWithdrawn, StreamEventType.Drained })
        {
            Assert.Single(events, e => e.Type == terminal);
        }

        var ended = events.FindIndex(static e => e.Type == StreamEventType.Ended);
        Assert.True(events.FindLastIndex(static e => e.Type == StreamEventType.MediaDescribed) < ended);
        Assert.True(ended < events.FindIndex(static e => e.Type == StreamEventType.PlaylistWithdrawn));
        Assert.Contains(events[ended].Reason, new[] { StreamEventReasons.Unpublished, StreamEventReasons.StreamDeleted, StreamEventReasons.ClientDisconnected });
        var described = events.Last(static e => e.Type == StreamEventType.MediaDescribed).Media!;
        Assert.NotNull(described.Video);
        Assert.NotNull(described.Audio);
        Assert.All(events, e =>
        {
            Assert.Equal(StreamEvent.CurrentSchemaVersion, e.SchemaVersion);
            Assert.Equal(("live", "camera1", incarnation), (e.Application, e.Stream, e.IncarnationId));
            Assert.Equal(TimeSpan.Zero, e.Timestamp.Offset);
            Assert.Equal(e.Type == StreamEventType.MediaDescribed, e.Media is not null);
            var json = JsonSerializer.Serialize(e, StreamEventJsonContext.Default.StreamEvent);
            foreach (var secret in new[] { "e2e-secret-key", "127.0.0.1", "::1", $":{rtmpPort}", $":{httpPort}" })
            {
                Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
            }
        });
        Assert.True(events.Zip(events.Skip(1)).All(static p => p.First.Timestamp <= p.Second.Timestamp));
    }

    /// <summary>Sums every point of each instrument's latest export.</summary>
    /// <param name="exported">The exports.</param>
    /// <returns>Totals by instrument name.</returns>
    internal static Dictionary<string, double> Totals(List<MetricSnapshot> exported)
    {
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var metric in exported)
        {
            totals[metric.Name] = metric.MetricPoints.Sum(p => metric.MetricType switch
            {
                MetricType.LongSum or MetricType.LongSumNonMonotonic => p.GetSumLong(),
                MetricType.DoubleSum or MetricType.DoubleSumNonMonotonic => p.GetSumDouble(),
                MetricType.LongGauge => p.GetGaugeLastValueLong(),
                MetricType.DoubleGauge => p.GetGaugeLastValueDouble(),
                MetricType.Histogram => p.GetHistogramCount(),
                _ => 0,
            });
        }

        return totals;
    }

    /// <summary>Returns the media segment URIs a playlist lists.</summary>
    /// <param name="playlist">The playlist text.</param>
    /// <returns>The URIs.</returns>
    internal static List<string> Segments(string playlist) =>
        [.. playlist.Split('\n').Select(static l => l.TrimEnd('\r')).Where(static l => l.Length > 0 && !l.StartsWith('#'))];

    /// <summary>Sums a playlist's <c>#EXTINF</c> durations.</summary>
    /// <param name="playlist">The playlist text.</param>
    /// <returns>Seconds.</returns>
    private static double Duration(string playlist) => playlist.Split('\n')
        .Where(static l => l.StartsWith("#EXTINF:", StringComparison.Ordinal))
        .Sum(static l => double.Parse(l["#EXTINF:".Length..].TrimEnd(',', '\r'), System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Returns whether a byte payload contains an ASCII string.</summary>
    /// <param name="body">The payload.</param>
    /// <param name="value">The string.</param>
    /// <returns><see langword="true"/> if it does.</returns>
    private static bool Contains(byte[] body, string value) => body.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) >= 0;

    /// <summary>Verifies the management surface for the live publication.</summary>
    /// <param name="url">Builds request URLs.</param>
    /// <param name="incarnation">The live incarnation.</param>
    /// <param name="evidence">The evidence log.</param>
    /// <returns>A task that completes when verified.</returns>
    private async Task AssertManagementAsync(Func<string, Uri> url, string incarnation, Evidence evidence)
    {
        foreach (var (path, status) in new[] { ("/health/live", "live"), ("/health/ready", "ready") })
        {
            using var response = await _client.GetAsync(url(path), Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
            Assert.Equal((status, "Running"), (body.RootElement.GetProperty("status").GetString(), body.RootElement.GetProperty("state").GetString()));
            AssertManagementHeaders(response);
        }

        using (var list = await _client.GetAsync(url("/health/streams"), Token))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            AssertManagementHeaders(list);
            var text = await list.Content.ReadAsStringAsync(Token);
            using var body = JsonDocument.Parse(text);
            var stream = Assert.Single(body.RootElement.GetProperty("streams").EnumerateArray());
            Assert.Equal(("live", "camera1", incarnation, true), (stream.GetProperty("application").GetString(), stream.GetProperty("stream").GetString(), stream.GetProperty("incarnationId").GetString(), stream.GetProperty("current").GetBoolean()));
            Assert.Equal(("publishing", "live", true), (stream.GetProperty("publishing").GetProperty("state").GetString(), stream.GetProperty("hls").GetProperty("state").GetString(), stream.GetProperty("hls").GetProperty("playlistAvailable").GetBoolean()));
            Assert.Equal("mp4a.40.2", stream.GetProperty("media").GetProperty("audio").GetProperty("codec").GetString());
            Assert.DoesNotContain("e2e-secret-key", text, StringComparison.Ordinal);
            Assert.DoesNotContain("generation", text, StringComparison.OrdinalIgnoreCase);
            evidence.Add($"GET /health/streams: {text}");
        }

        using (var detail = await _client.GetAsync(url("/health/streams/live/camera1"), Token))
        {
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            using var body = JsonDocument.Parse(await detail.Content.ReadAsStringAsync(Token));
            Assert.Equal(incarnation, body.RootElement.GetProperty("stream").GetProperty("incarnationId").GetString());
        }

        using (var head = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url("/health/streams/live/camera1")), Token))
        {
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.True(head.Content.Headers.ContentLength > 0);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync(Token));
            AssertManagementHeaders(head);
        }

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            using var rejected = await _client.SendAsync(new HttpRequestMessage(method, url("/health/streams")), Token);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, rejected.StatusCode);
            Assert.Equal("GET, HEAD", rejected.Content.Headers.Allow.Count > 0 ? string.Join(", ", rejected.Content.Headers.Allow) : string.Join(", ", rejected.Headers.GetValues("Allow")));
            AssertManagementHeaders(rejected);
        }

        foreach (var path in new[] { "/health/streams/live/unknown", "/health/streams/other/camera1", "/health/unknown" })
        {
            using var missing = await _client.GetAsync(url(path), Token);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            AssertManagementHeaders(missing);
        }

        evidence.Add("management: live/ready 200, streams list and detail 200, HEAD headers only, POST/PUT/DELETE 405 Allow: GET, HEAD, unknown 404, all no-store and nosniff");
    }

    /// <summary>Asserts the management response headers.</summary>
    /// <param name="response">The response.</param>
    private static void AssertManagementHeaders(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    /// <summary>Asserts an HLS resource is served with its media type, and that HEAD returns headers only.</summary>
    /// <param name="path">The path.</param>
    /// <param name="contentType">The expected media type.</param>
    /// <returns>A task that completes when verified.</returns>
    private async Task AssertResourceAsync(string path, string contentType)
    {
        var uri = new Uri($"http://127.0.0.1:{((IPEndPoint)GetOriginEndPoint()).Port}{path}");
        using var get = await _client.GetAsync(uri, Token);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(contentType, get.Content.Headers.ContentType!.MediaType);
        var length = (await get.Content.ReadAsByteArrayAsync(Token)).Length;
        Assert.True(length > 0);
        using var head = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, uri), Token);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync(Token));
    }

    /// <summary>The started origin's endpoint, set by the test.</summary>
    private EndPoint? _origin;

    /// <summary>Returns the origin endpoint.</summary>
    /// <returns>The endpoint.</returns>
    private EndPoint GetOriginEndPoint() => _origin ?? throw new InvalidOperationException("The origin has not started.");

    /// <summary>Returns the status of a GET.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The status.</returns>
    private HttpStatusCode Status(string path)
    {
        using var response = _client.GetAsync(new Uri($"http://127.0.0.1:{((IPEndPoint)GetOriginEndPoint()).Port}{path}"), Token).GetAwaiter().GetResult();
        return response.StatusCode;
    }

    /// <summary>Polls a playlist until it satisfies a condition.</summary>
    /// <param name="path">The playlist path.</param>
    /// <param name="condition">The condition.</param>
    /// <returns>The playlist text.</returns>
    private async Task<string> WaitForPlaylistAsync(string path, Func<string, bool> condition)
    {
        string? text = null;
        await Eventually.WaitUntilAsync(
            () =>
            {
                using var response = _client.GetAsync(new Uri($"http://127.0.0.1:{((IPEndPoint)GetOriginEndPoint()).Port}{path}"), Token).GetAwaiter().GetResult();
                text = response.StatusCode == HttpStatusCode.OK ? response.Content.ReadAsStringAsync(Token).GetAwaiter().GetResult() : null;
                return text is not null && condition(text);
            },
            $"{path} reaches the expected state");
        return text!;
    }
}
