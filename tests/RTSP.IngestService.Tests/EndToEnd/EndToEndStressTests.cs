// End-to-end stress and lifecycle verification in the production host, with bounded workloads: concurrent FFmpeg
// publishers with concurrent HLS readers, a publisher disconnect and reconnect of the same identity (a new incarnation
// with a higher generation that the old one's late events cannot touch), periodic reconciliation while publishing,
// and a full shutdown while publishers and readers are active with an unavailable or unresponsive OTLP collector;
// event-channel overflow while delivery is held, with media unaffected and the catalog recovering by reconciliation,
// including incarnations opened, withdrawn and drained between reconciliations; and the HTTP operational surface for
// a failed lifecycle and for non-loopback clients presenting forwarding headers.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Http;
using RTSP.IngestService.Lifecycle;
using RTSP.IngestService.Rtmp;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Streams;
using RTSP.IngestService.Tests.Hls;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Tests.Telemetry;

namespace RTSP.IngestService.Tests.EndToEnd;

/// <summary>End-to-end concurrency, shutdown, event-pressure and HTTP-surface tests.</summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class EndToEndStressTests : IDisposable
{
    /// <summary>The concurrently published streams.</summary>
    private static readonly string[] Cameras = ["camera1", "camera2", "camera3"];

    /// <summary>Captured host logs.</summary>
    private readonly FakeLogCollector _logs = new();

    /// <summary>The client.</summary>
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Gets the test cancellation token.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    /// <summary>
    /// Verifies three concurrent publishers with concurrent readers, a disconnect and reconnect of one identity, and a
    /// bounded shutdown while everything is active, with the OTLP collector refusing connections or never answering.
    /// </summary>
    /// <param name="collectorMode"><c>refused</c> or <c>unresponsive</c>.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData("refused")]
    [InlineData("unresponsive")]
    public async Task ConcurrentPublishers_ReconnectAndShutdownWhileActive(string collectorMode)
    {
        var tools = HlsFfmpegInteropTests.Tools();
        var evidence = new Evidence($"concurrency-shutdown-{collectorMode}");
        await using var unresponsive = new FakeCollector(respond: false);
        var overrides = EndToEndHost.Settings();
        foreach (var (key, value) in TelemetryTestSupport.Enabled(collectorMode == "refused" ? TelemetryTestSupport.RefusingEndpoint() : unresponsive.Endpoint, interval: "00:00:01"))
        {
            overrides[key] = value;
        }

        overrides["StreamCatalog:ReconciliationInterval"] = "00:00:01";
        var gate = new StopGate();
        var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        using var host = EndToEndHost.Build(_logs, overrides, configure: s => s.AddSingleton<IHostedService>(gate));
        await host.StartAsync(Token).WithinSafetyTimeout();
        var origin = host.Services.GetRequiredService<HlsHttpOrigin>();
        var rtmp = host.Services.GetRequiredService<RtmpListener>();
        var catalog = host.Services.GetRequiredService<StreamCatalog>();
        var consumer = host.Services.GetRequiredService<RecordingEventConsumer>();
        var rtmpPort = EndToEndHost.RtmpPort(host);
        var httpPort = origin.LocalEndPoint!.Port;
        Uri Url(string path) => new($"http://127.0.0.1:{httpPort}{path}");

        var publishers = new List<FfmpegPublisher>
        {
            new(tools.Ffmpeg, rtmpPort, EndToEndHost.Key(1), 60, "testsrc", 440),
            new(tools.Ffmpeg, rtmpPort, EndToEndHost.Key(2), 60, "smptebars", 660),
            new(tools.Ffmpeg, rtmpPort, EndToEndHost.Key(3), 60, "rgbtestsrc", 880),
        };
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var statuses = new ConcurrentDictionary<int, int>();
        var transportFailures = 0;
        var readers = Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            while (!reading.IsCancellationRequested)
            {
                foreach (var camera in Cameras)
                {
                    try
                    {
                        using var playlist = await client.GetAsync(Url($"/hls/live/{camera}/index.m3u8"), reading.Token);
                        statuses.AddOrUpdate((int)playlist.StatusCode, 1, static (_, n) => n + 1);
                        if (playlist.StatusCode == HttpStatusCode.OK && EndToEndPipelineTests.Segments(await playlist.Content.ReadAsStringAsync(reading.Token)) is [.., var last])
                        {
                            using var segment = await client.GetAsync(Url($"/hls/live/{camera}/{last}"), reading.Token);
                            statuses.AddOrUpdate((int)segment.StatusCode, 1, static (_, n) => n + 1);
                            _ = await segment.Content.ReadAsByteArrayAsync(reading.Token);
                        }
                    }
                    catch (OperationCanceledException) when (reading.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (HttpRequestException)
                    {
                        Interlocked.Increment(ref transportFailures);
                        await Task.Delay(20, reading.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    }
                }
            }
        }, Token)).ToArray();

        try
        {
            await Eventually.WaitUntilAsync(
                () => Enumerable.Range(1, 3).All(n => catalog.Current.Find("live", $"camera{n}") is { Publishing.State: StreamPublishingState.Publishing, Hls.State: StreamHlsState.Live, Media.Video: not null, Media.Audio: not null }),
                "three concurrent publications are live and described");
            var first = Enumerable.Range(1, 3).Select(n => catalog.Current.Find("live", $"camera{n}")!).ToArray();
            Assert.Equal(3, first.Select(static e => e.IncarnationId).Distinct().Count());
            Assert.Equal(3, first.Select(static e => e.Generation).Distinct().Count());
            evidence.Add($"three publications live: {string.Join("; ", first.Select(static e => $"{e.Stream} incarnation={e.IncarnationId} generation={e.Generation} video={e.Media!.Video!.Codec}"))}");
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(Url("/health/ready"))).Status);

            var reconciled = catalog.Current.ReconciledAt;
            await Eventually.WaitUntilAsync(() => catalog.Current.ReconciledAt > reconciled, "a periodic reconciliation runs while publishing");
            Assert.True(Enumerable.Range(1, 3).All(n => catalog.Current.Find("live", $"camera{n}")?.Publishing.State == StreamPublishingState.Publishing));
            evidence.Add($"periodic reconciliation during publishing: reconciledAt {reconciled:O} -> {catalog.Current.ReconciledAt:O}, all three still publishing");

            var old = first[1];
            await Eventually.WaitUntilAsync(() => host.Services.GetRequiredService<IHlsSegmentStore>().GetPlaylist("live", "camera2") is { Segments.Count: >= 2 }, "camera2 has published segments");
            publishers[1].Kill();
            await Eventually.WaitUntilAsync(() => catalog.Current.Find("live", "camera2") is { Publishing.State: StreamPublishingState.Ended } e && e.IncarnationId == old.IncarnationId, "the disconnected publication ends");
            await Eventually.WaitUntilAsync(() => consumer.Of(old.IncarnationId).Any(static e => e.Type == StreamEventType.Ended), "the disconnect is reported");
            var endReason = consumer.Of(old.IncarnationId).Single(static e => e.Type == StreamEventType.Ended).Reason;
            Assert.Contains(endReason, new[] { StreamEventReasons.ClientDisconnected, StreamEventReasons.ConnectionError });
            evidence.Add($"camera2 publisher killed: incarnation {old.IncarnationId} ended ({endReason}); catalog {catalog.Current.Find("live", "camera2")!.Publishing.State}/{catalog.Current.Find("live", "camera2")!.Hls.State}");

            // The ended publication's HLS stream stays open until its transcoder has drained (up to
            // Transcoding:DrainTimeout), and HLS refuses the identity until then; reconnect once it is released.
            await Eventually.WaitUntilAsync(() => catalog.Current.Find("live", "camera2") is { Hls.State: not StreamHlsState.Live } e && e.IncarnationId == old.IncarnationId, "the disconnected publication's HLS stream has ended");
            evidence.Add($"camera2 HLS stream released: {catalog.Current.Find("live", "camera2")!.Hls.State}");

            publishers.Add(new(tools.Ffmpeg, rtmpPort, EndToEndHost.Key(2), 60, "smptebars", 660));
            await Eventually.WaitUntilAsync(() => catalog.Current.Find("live", "camera2") is { Publishing.State: StreamPublishingState.Publishing, Hls.State: StreamHlsState.Live } e && e.IncarnationId != old.IncarnationId, "the reconnected publication is live");
            var replacement = catalog.Current.Find("live", "camera2")!;
            Assert.True(replacement.Generation > old.Generation);
            Assert.True(replacement.Current);
            await Eventually.WaitUntilAsync(() => consumer.Of(old.IncarnationId).Any(static e => e.Type == StreamEventType.PlaylistWithdrawn), "the old playlist is withdrawn");
            var withdrawal = consumer.Of(old.IncarnationId).Single(static e => e.Type == StreamEventType.PlaylistWithdrawn).Reason;
            Assert.Contains(withdrawal, new[] { StreamEventReasons.Replaced, StreamEventReasons.Aborted });
            await Eventually.WaitUntilAsync(() => catalog.Current.ReconciledAt > DateTimeOffset.UtcNow.AddMilliseconds(-200), "a reconciliation follows the replacement");
            var after = catalog.Current.Find("live", "camera2")!;
            Assert.Equal((replacement.IncarnationId, StreamPublishingState.Publishing, StreamHlsState.Live), (after.IncarnationId, after.Publishing.State, after.Hls.State));
            if (catalog.Current.Streams.SingleOrDefault(e => e.IncarnationId == old.IncarnationId) is { } stale)
            {
                Assert.False(stale.Current);
                Assert.Equal(StreamPublishingState.Ended, stale.Publishing.State);
            }

            using (var detail = JsonDocument.Parse((await GetAsync(Url("/health/streams/live/camera2"))).Body))
            {
                Assert.Equal(replacement.IncarnationId, detail.RootElement.GetProperty("stream").GetProperty("incarnationId").GetString());
            }

            evidence.Add($"camera2 reconnected: incarnation {replacement.IncarnationId} generation {replacement.Generation} > {old.Generation}; old playlist withdrawn ({withdrawal}); old incarnation {(catalog.Current.Streams.Any(e => e.IncarnationId == old.IncarnationId) ? "still listed as non-current ended" : "removed")}");

            var store = host.Services.GetRequiredService<IHlsSegmentStore>();
            await Eventually.WaitUntilAsync(() => store.GetPlaylist("live", "camera2") is not null, "the replacement publishes its first playlist");
            var playlists = new HashSet<string>(StringComparer.Ordinal);
            foreach (var camera in Cameras)
            {
                var (status, body) = await GetAsync(Url($"/hls/live/{camera}/index.m3u8"));
                Assert.Equal(HttpStatusCode.OK, status);
                var bytes = await _client.GetByteArrayAsync(Url($"/hls/live/{camera}/{EndToEndPipelineTests.Segments(body)[^1]}"), Token);
                Assert.True(playlists.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))), $"{camera} serves another stream's media");
            }

            evidence.Add("isolation: each stream's latest media segment differs from the others'");

            await Eventually.WaitUntilAsync(() => statuses.GetValueOrDefault(200) >= 60, "concurrent readers retrieve playlists and segments");

            var stopwatch = Stopwatch.StartNew();
            var stop = host.StopAsync(CancellationToken.None);
            await gate.Entered.WithinSafetyTimeout();
            Assert.Equal(ApplicationState.Stopping, host.Services.GetRequiredService<IApplicationLifecycle>().State);
            var ready = await GetAsync(Url("/health/ready"));
            var live = await GetAsync(Url("/health/live"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.Status);
            Assert.Equal(HttpStatusCode.OK, live.Status);
            Assert.Contains("\"Stopping\"", ready.Body, StringComparison.Ordinal);
            evidence.Add($"shutdown begun: lifecycle Stopping, ready 503 {ready.Body}, live 200 {live.Body}");
            gate.Release();
            await stop.WaitAsync(TimeSpan.FromSeconds(25), Token);
            stopwatch.Stop();
            evidence.Add($"host stopped in {stopwatch.Elapsed.TotalSeconds:F3} s with collector {collectorMode}");
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"shutdown took {stopwatch.Elapsed}");
        }
        finally
        {
            gate.Release();
            await reading.CancelAsync();
            await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
            foreach (var publisher in publishers)
            {
                await publisher.DisposeAsync();
            }
        }

        Assert.Equal(ApplicationState.Stopped, host.Services.GetRequiredService<IApplicationLifecycle>().State);
        Assert.False(rtmp.IsListening);
        Assert.False(origin.IsListening);
        using (var probe = new TcpClient())
        {
            await Assert.ThrowsAnyAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, rtmpPort, Token).AsTask());
        }

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _client.GetAsync(Url("/health/live"), Token));
        Assert.Empty(host.Services.GetRequiredService<PublishedStreamRegistry>().Sessions);
        Assert.DoesNotContain(((IHlsStreamInventory)host.Services.GetRequiredService<IHlsSegmentStore>()).GetStreams(), static s => s.State == HlsStreamState.Live);

        var events = consumer.Events;
        foreach (var group in events.GroupBy(static e => e.IncarnationId))
        {
            foreach (var type in new[] { StreamEventType.Published, StreamEventType.Ended, StreamEventType.PlaylistWithdrawn, StreamEventType.Drained })
            {
                Assert.True(group.Count(e => e.Type == type) <= 1, $"{group.Key} has duplicate {type} events");
            }

            Assert.Single(group.Select(static e => (e.Application, e.Stream)).Distinct());
            evidence.Add($"events of {group.First().Stream} {group.Key}: {string.Join(", ", group.Select(static e => e.Reason is null ? e.Type.ToString() : $"{e.Type}({e.Reason})"))}");
        }

        var shutdownEnded = events.Where(static e => e is { Type: StreamEventType.Ended, Reason: StreamEventReasons.ServerShutdown }).Select(static e => e.Stream).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["camera1", "camera2", "camera3"], shutdownEnded);
        Assert.DoesNotContain(statuses.Keys, static s => s >= 500);
        evidence.Add($"reader responses: {string.Join(", ", statuses.OrderBy(static p => p.Key).Select(static p => $"{p.Key}x{p.Value}"))}; transport failures after listeners closed: {transportFailures}");

        var records = _logs.GetSnapshot();
        Assert.DoesNotContain(records, static r => r.Level >= LogLevel.Error || r.Id.Id is 1100 or 9100 or 9102);
        Assert.Contains(records, collectorMode == "refused" ? static r => r.Id.Id == 9301 : static r => r.Id.Id is 9301 or 9302);
        Assert.DoesNotContain(records, static r => r.Message.Contains("e2e-secret-key", StringComparison.Ordinal));
        evidence.Add($"telemetry failure logs: {string.Join(", ", records.Where(static r => r.Id.Id is 9301 or 9302).Select(static r => r.Id.Id).Distinct())}; no error logs, no abandoned services");
        host.Dispose();
        evidence.Add($"managed memory before host {memoryBefore / 1024} KiB, after dispose {GC.GetTotalMemory(forceFullCollection: true) / 1024} KiB");
        evidence.Save();
    }

    /// <summary>
    /// Verifies that overflowing the event channel while delivery is held never blocks media, that incarnations opened,
    /// withdrawn and drained between reconciliations never appear, and that the catalog recovers by reconciliation.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task EventOverflow_RecoversByReconciliation_WithoutBlockingMedia()
    {
        var tools = HlsFfmpegInteropTests.Tools();
        var evidence = new Evidence("event-pressure");
        var overrides = EndToEndHost.Settings();
        overrides["StreamEvents:QueueCapacity"] = "16";
        overrides["StreamCatalog:ReconciliationInterval"] = "01:00:00";
        using var host = EndToEndHost.Build(_logs, overrides);
        await host.StartAsync(Token).WithinSafetyTimeout();
        var catalog = host.Services.GetRequiredService<StreamCatalog>();
        var consumer = host.Services.GetRequiredService<RecordingEventConsumer>();
        var channel = host.Services.GetRequiredService<StreamEventChannel>();
        var store = host.Services.GetRequiredService<IHlsSegmentStore>();
        var registry = host.Services.GetRequiredService<PublishedStreamRegistry>();
        var endPoint = host.Services.GetRequiredService<RtmpListener>().LocalEndPoint!;
        try
        {
            var publisher = new FfmpegPublisher(tools.Ffmpeg, EndToEndHost.RtmpPort(host), EndToEndHost.Key(1), 14);
            await using (publisher)
            {
                await Eventually.WaitUntilAsync(() => catalog.Current.Find("live", "camera1") is { Hls.State: StreamHlsState.Live, Media.Video: not null }, "camera1 is live in the catalog");
                var known = await RtmpTestClient.ConnectAsync(endPoint);
                Assert.Equal("NetStream.Publish.Start", (await known.PublishFlowAsync(EndToEndHost.Key(3))).Code);
                await Eventually.WaitUntilAsync(() => catalog.Current.Find("live", "camera3") is not null, "camera3 is catalogued");
                var knownIncarnation = catalog.Current.Find("live", "camera3")!.IncarnationId;

                consumer.Hold();
                await known.DisposeAsync();
                await consumer.Held.Task.WithinSafetyTimeout();

                // camera3 sends no media, so it is live without a playlist and keeps the settle timer reconciling
                // until its end is observed; only then do no reconciliations run while delivery is held.
                await Eventually.WaitUntilAsync(() => !catalog.HasUnsettled && catalog.Current.Find("live", "camera3") is null, "the catalog settles without camera3's events");
                var lastSequence = LastSequence(store);
                var transient = new List<string>();
                for (var cycle = 0; cycle < 8; cycle++)
                {
                    var client = await RtmpTestClient.ConnectAsync(endPoint);
                    await using (client)
                    {
                        Assert.Equal("NetStream.Publish.Start", (await client.PublishFlowAsync(EndToEndHost.Key(2))).Code);
                        transient.Add(registry.Sessions.Single(static s => s.StreamIdentity == "camera2").IncarnationId);
                    }

                    await Eventually.WaitUntilAsync(() => registry.Sessions.All(static s => s.StreamIdentity == "camera1") && !store.ContainsStream("live", "camera2"), "the transient publication is unregistered, withdrawn and drained");
                }

                Assert.True(channel.DroppedCount > 0);
                Assert.DoesNotContain(catalog.Current.Streams, static e => e.Stream == "camera2");
                await Eventually.WaitUntilAsync(() => LastSequence(store) >= lastSequence + 2, "media keeps packaging while event delivery is held");
                evidence.Add($"delivery held: {channel.DroppedCount} events dropped from a 16-event channel; 8 camera2 incarnations opened, withdrawn and drained without a reconciliation; camera1 media sequence {lastSequence} -> {LastSequence(store)}");

                consumer.Release();
                await Eventually.WaitUntilAsync(
                    () => _logs.GetSnapshot().Any(static r => r.Id.Id == 9202)
                        && catalog.Current.Streams.Count == 1 && catalog.Current.Find("live", "camera1") is { Publishing.State: StreamPublishingState.Publishing, Hls.State: StreamHlsState.Live, Hls.PlaylistAvailable: true },
                    "the event loss is detected and the catalog recovers to exactly the live publication");
                Assert.Contains(_logs.GetSnapshot(), static r => r.Id.Id == 9202);
                Assert.DoesNotContain(catalog.Current.Streams, e => transient.Contains(e.IncarnationId) || e.IncarnationId == knownIncarnation);
                var version = catalog.Current.Version;
                await Eventually.WaitUntilAsync(() => catalog.Current.Version > version + 1, "later events update the catalog");
                Assert.DoesNotContain(catalog.Current.Streams, e => transient.Contains(e.IncarnationId) || e.IncarnationId == knownIncarnation);
                Assert.Single(catalog.Current.Streams);
                evidence.Add($"released: 'events lost' logged, catalog reconciled to {catalog.Current.Streams.Count} entry (camera1 publishing/live); {consumer.Events.Count(e => transient.Contains(e.IncarnationId))} late events of transient incarnations delivered without resurrecting them; camera3 ({knownIncarnation}) removed");

                var exit = await publisher.WaitAsync();
                Assert.True(exit.Code == 0, $"FFmpeg exited with {exit.Code}: {exit.Errors}");
            }

            await Eventually.WaitUntilAsync(() => catalog.Current.Find("live", "camera1") is { Publishing.State: StreamPublishingState.Ended }, "camera1 ends in the catalog");
        }
        finally
        {
            consumer.Release();
            await host.StopAsync(CancellationToken.None).WithinSafetyTimeout();
        }

        Assert.Equal(ApplicationState.Stopped, host.Services.GetRequiredService<IApplicationLifecycle>().State);
        Assert.DoesNotContain(_logs.GetSnapshot(), static r => r.Level >= LogLevel.Error || r.Id.Id is 1100 or 9100 or 9102);
        evidence.Save();
    }

    /// <summary>
    /// Verifies the health endpoints on the real listener in the Failed state, and that a non-loopback client is
    /// refused every management path whatever forwarding headers it sends, while HLS paths stay public.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task HttpSurface_FailedLifecycleAndNonLoopbackClients()
    {
        var evidence = new Evidence("http-surface");
        var address = NetworkInterface.GetAllNetworkInterfaces()
            .Where(static n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(static n => n.GetIPProperties().UnicastAddresses)
            .Select(static a => a.Address)
            .FirstOrDefault(static a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal));
        var overrides = EndToEndHost.Settings();
        if (address is not null)
        {
            overrides["Http:ListenAddress"] = "0.0.0.0";
        }

        using var host = EndToEndHost.Build(_logs, overrides);
        await host.StartAsync(Token).WithinSafetyTimeout();
        try
        {
            var port = host.Services.GetRequiredService<HlsHttpOrigin>().LocalEndPoint!.Port;
            Uri Loopback(string path) => new($"http://127.0.0.1:{port}{path}");
            if (address is not null)
            {
                foreach (var path in new[] { "/health/live", "/health/ready", "/health/streams", "/health/streams/live/camera1", "/health/unknown" })
                {
                    foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head, HttpMethod.Post })
                    {
                        using var request = new HttpRequestMessage(method, new Uri($"http://{address}:{port}{path}"));
                        request.Headers.Add("X-Forwarded-For", "127.0.0.1");
                        request.Headers.Add("Forwarded", "for=127.0.0.1");
                        request.Headers.Add("X-Real-IP", "127.0.0.1");
                        request.Headers.Host = "localhost";
                        using var response = await _client.SendAsync(request, Token);
                        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Token));
                        Assert.True(response.Headers.CacheControl!.NoStore);
                    }
                }

                using var hls = await _client.GetAsync(new Uri($"http://{address}:{port}/hls/live/camera1/index.m3u8"), Token);
                Assert.Equal(HttpStatusCode.NotFound, hls.StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await GetAsync(Loopback("/health/ready"))).Status);
                evidence.Add($"non-loopback client {address}: every management path and method 403 with an empty no-store body despite X-Forwarded-For, Forwarded, X-Real-IP and Host: localhost; HLS path 404; loopback ready 200");
            }
            else
            {
                evidence.Add("non-loopback client: environmentally blocked (no non-loopback IPv4 address)");
            }

            Assert.True(host.Services.GetRequiredService<ApplicationLifecycle>().TryFail(new InvalidOperationException("simulated failure")));
            var live = await GetAsync(Loopback("/health/live"));
            var ready = await GetAsync(Loopback("/health/ready"));
            Assert.Equal((HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable), (live.Status, ready.Status));
            Assert.Contains("\"not_live\"", live.Body, StringComparison.Ordinal);
            Assert.Contains("\"Failed\"", ready.Body, StringComparison.Ordinal);
            using (var head = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Loopback("/health/live")), Token))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, head.StatusCode);
                Assert.Empty(await head.Content.ReadAsByteArrayAsync(Token));
            }

            Assert.DoesNotContain("simulated failure", live.Body + ready.Body, StringComparison.Ordinal);
            evidence.Add($"Failed lifecycle: live 503 {live.Body}, ready 503 {ready.Body}, HEAD live 503 without body, failure message not exposed");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WithinSafetyTimeout();
        }

        Assert.Equal(ApplicationState.Failed, host.Services.GetRequiredService<IApplicationLifecycle>().State);
        evidence.Save();
        if (address is null)
        {
            Assert.Skip("No non-loopback IPv4 address is available; the non-loopback checks were not executed.");
        }
    }

    /// <summary>Returns the sequence number of camera1's last listed segment, or -1.</summary>
    /// <param name="store">The store.</param>
    /// <returns>The sequence number.</returns>
    private static long LastSequence(IHlsSegmentStore store) => store.GetPlaylist("live", "camera1") is { Segments: [.., var last] } ? last.Sequence : -1;

    /// <summary>GETs a URL and returns its status and body.</summary>
    /// <param name="uri">The URL.</param>
    /// <returns>The status and body.</returns>
    private async Task<(HttpStatusCode Status, string Body)> GetAsync(Uri uri)
    {
        using var response = await _client.GetAsync(uri, Token);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Token));
    }

    /// <summary>Holds host shutdown at the start of the stop order (it is registered last) until released.</summary>
    private sealed class StopGate : IHostedService
    {
        /// <summary>Completed when shutdown reaches the gate.</summary>
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completed by the test.</summary>
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets a task completed when shutdown reaches the gate.</summary>
        public Task Entered => _entered.Task;

        /// <summary>Lets shutdown continue.</summary>
        public void Release() => _released.TrySetResult();

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            return _released.Task;
        }
    }
}
