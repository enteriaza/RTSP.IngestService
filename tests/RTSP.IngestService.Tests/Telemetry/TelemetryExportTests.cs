// Tests of OTLP metrics export in the real service host. Disabled by default: no exporter, no connection even with an
// endpoint configured, and the in-process instruments still work. Enabled: an in-memory exporter added beside the OTLP
// exporter receives every instrument of every application meter, recorded through the real metrics classes, with its
// name, unit, description, type, values and fixed tag vocabulary unchanged; the OTLP exporter delivers the same meters
// over the wire to a loopback collector; and a real FFmpeg publish and HTTP playback export no identifying tag.

using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Http;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtmp;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Streams;
using RTSP.IngestService.Telemetry;
using RTSP.IngestService.Tests.Hls;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Telemetry;

/// <summary>Tests for <see cref="TelemetryServiceCollectionExtensions.AddTelemetryExport"/> in the service host.</summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class TelemetryExportTests
{
    /// <summary>The configured test stream key.</summary>
    private const string StreamKey = "telemetry-key-0001";

    /// <summary>The value each instrument holds after <see cref="Record"/>, by tag set (<c>key=value;...</c>, ordered by key).</summary>
    private static readonly Dictionary<string, Dictionary<string, double>> Expected = new()
    {
        ["rtmp.connections.accepted"] = new() { [""] = 2 },
        ["rtmp.connections.rejected"] = new() { ["reason=connection_limit"] = 1 },
        ["rtmp.connections.active"] = new() { [""] = 1 },
        ["rtmp.connection.duration"] = new() { ["close_reason=protocol_error"] = 1.5 },
        ["rtmp.handshakes"] = new() { ["result=success"] = 1 },
        ["rtmp.authentication.failures"] = new() { [""] = 1 },
        ["rtmp.authentication.attempts"] = new() { ["result=rate_limited"] = 1 },
        ["rtmp.authentication.reloads"] = new() { ["result=failure"] = 1 },
        ["rtmp.protocol_errors"] = new() { [""] = 1 },
        ["rtmp.sessions.active"] = new() { [""] = 1 },
        ["rtmp.publisher.disconnects"] = new() { ["reason=client_disconnected"] = 1 },
        ["rtmp.bytes_received"] = new() { [""] = 4096 },
        ["rtmp.messages_received"] = new() { ["type=video"] = 1 },
        ["media.samples"] = new() { ["track=video"] = 1, ["track=audio"] = 1 },
        ["media.keyframes"] = new() { [""] = 1 },
        ["media.sample_bytes"] = new() { ["track=video"] = 1000, ["track=audio"] = 200 },
        ["media.configurations"] = new() { ["track=video"] = 1 },
        ["media.errors"] = new() { ["kind=malformed_packet;track=audio"] = 1 },
        ["media.timestamp_discontinuities"] = new() { ["kind=jump;track=video"] = 1 },
        ["media.sessions.active"] = new() { [""] = 1 },
        ["hls.sessions.active"] = new() { [""] = 1 },
        ["hls.initialization_segments"] = new() { [""] = 1 },
        ["hls.segments"] = new() { ["boundary=keyframe"] = 1 },
        ["hls.segment_bytes"] = new() { [""] = 3500 },
        ["hls.segment.duration"] = new() { [""] = 2 },
        ["hls.keyframe_wait"] = new() { [""] = 0.25 },
        ["hls.storage.bytes"] = new() { [""] = 3500 },
        ["hls.storage.evictions"] = new() { ["reason=retired"] = 2 },
        ["hls.errors"] = new() { ["kind=storage_full"] = 1 },
        ["hls.discontinuities"] = new() { ["reason=timestamp"] = 1 },
        ["hls.samples.dropped"] = new() { ["reason=before_sync_sample;track=video"] = 3 },
        ["http.origin.requests"] = new() { ["resource=playlist;status_class=2xx"] = 1 },
        ["http.origin.requests.active"] = new() { [""] = 1 },
        ["http.origin.request.duration"] = new() { ["resource=playlist"] = 0.5 },
        ["http.origin.response_bytes"] = new() { ["resource=segment"] = 777 },
        ["http.origin.cancellations"] = new() { ["resource=segment"] = 1 },
        ["http.origin.missing"] = new() { ["reason=unknown_stream;resource=playlist"] = 1 },
        ["http.origin.failures"] = new() { ["resource=other"] = 1 },
        ["stream_events.published"] = new() { ["type=ended"] = 1 },
        ["stream_events.dropped"] = new() { ["type=drained"] = 1 },
        ["stream_events.rejected"] = new() { ["type=published"] = 1 },
        ["stream_events.consumer_failures"] = new() { ["type=media_described"] = 1 },
        ["stream_catalog.reconciliations"] = new() { ["result=success"] = 1 },
        ["stream_catalog.reconciliation.duration"] = new() { [""] = 0.125 },
        ["stream_catalog.reconciliation.requests"] = new() { ["reason=periodic"] = 1 },
        ["stream_catalog.reconciliation.coalesced"] = new() { [""] = 1 },
        ["stream_catalog.streams"] = new() { [""] = 3 },
        ["stream_catalog.reconciliation.age"] = new() { [""] = 2 },
    };

    /// <summary>Instruments, one per kind, whose names must appear in the OTLP request body.</summary>
    private static readonly string[] OnTheWire = ["rtmp.connections.accepted", "hls.storage.bytes", "hls.segment.duration", "stream_catalog.streams"];

    /// <summary>Instruments a real publish, playback and missing-stream request must have recorded.</summary>
    private static readonly string[] FromTraffic = ["rtmp.bytes_received", "media.samples", "hls.segments", "http.origin.requests", "http.origin.missing", "stream_events.published", "stream_catalog.reconciliations"];

    /// <summary>Captured host logs.</summary>
    private readonly FakeLogCollector _logs = new();

    /// <summary>Gets the test cancellation token.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Test A: with the shipped configuration export is disabled and no exporter exists, while the application's
    /// instruments still record in process: an RTMP connection is counted by a listener on the host's meter.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Default_ExportIsDisabledAndInstrumentsStillRecord()
    {
        using var host = TestHosts.CreateBuilder(_logs).Build();
        var otlp = host.Services.GetRequiredService<IOptions<TelemetryOptions>>().Value.Otlp;
        Assert.False(otlp.Enabled);
        Assert.True(string.IsNullOrEmpty(otlp.Endpoint));
        using var accepted = new MetricCollector<long>(host.Services.GetRequiredService<IMeterFactory>(), RtmpMetrics.MeterName, "rtmp.connections.accepted");

        await host.StartAsync(Token).WithinSafetyTimeout();
        try
        {
            Assert.False(Export(host).IsEnabled);
            using var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)host.Services.GetRequiredService<RtmpListener>().LocalEndPoint!, Token);
            await accepted.WaitForMeasurementsAsync(1, Token).WithinSafetyTimeout();
            Assert.Equal(1, accepted.LastMeasurement!.Value);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        Assert.DoesNotContain(_logs.GetSnapshot(), static r => r.Id.Id is 9300 or 9301 or 9302);
    }

    /// <summary>
    /// Test A: disabled with an endpoint configured makes no connection over the whole host lifetime, including stop
    /// and disposal, where an enabled exporter always sends its final export; the enabled control in
    /// <see cref="Enabled_ExportsEveryMeterOverOtlpHttp"/> shows that export.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Disabled_WithEndpoint_MakesNoConnection()
    {
        await using var collector = new FakeCollector(respond: true);
        var overrides = TelemetryTestSupport.Enabled(collector.Endpoint, interval: "00:00:01");
        overrides["Telemetry:Otlp:Enabled"] = "false";
        var host = TestHosts.CreateBuilder(_logs, overrides: overrides).Build();
        try
        {
            await host.StartAsync(Token).WithinSafetyTimeout();
            Assert.False(Export(host).IsEnabled);
            Record(host.Services);
            await host.StopAsync(CancellationToken.None);
        }
        finally
        {
            host.Dispose();
        }

        Assert.Equal(0, collector.Connections);
        Assert.Empty(collector.Requests);
    }

    /// <summary>
    /// Verifies the enabled OTLP/HTTP exporter delivers every application meter over the wire: the final export at
    /// stop is a protobuf POST to <c>/v1/metrics</c> whose scopes name all five meters, and enabling is logged
    /// without the endpoint.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Enabled_ExportsEveryMeterOverOtlpHttp()
    {
        await using var collector = new FakeCollector(respond: true);
        using var host = TestHosts.CreateBuilder(_logs, overrides: TelemetryTestSupport.Enabled(collector.Endpoint)).Build();
        await host.StartAsync(Token).WithinSafetyTimeout();
        Assert.True(Export(host).IsEnabled);
        Record(host.Services);
        await host.StopAsync(CancellationToken.None);

        var request = await collector.FirstRequest.WithinSafetyTimeout();
        Assert.Equal("POST /v1/metrics HTTP/1.1", request.RequestLine);
        Assert.Equal("application/x-protobuf", request.ContentType);
        var body = Encoding.UTF8.GetString(request.Body);
        Assert.All(TelemetryServiceCollectionExtensions.MeterNames, name => Assert.Contains(name, body, StringComparison.Ordinal));
        Assert.All(OnTheWire, name => Assert.Contains(name, body, StringComparison.Ordinal));
        var enabled = Assert.Single(_logs.GetSnapshot(), static r => r.Id.Id == 9300);
        Assert.DoesNotContain(collector.Endpoint, enabled.Message, StringComparison.Ordinal);
        Assert.Empty(_logs.GetSnapshot().Where(static r => r.Id.Id is 9301 or 9302).Select(static r => r.Message));
    }

    /// <summary>
    /// Test C and D: every instrument the application creates, on every application meter, is exported with its
    /// meter, name, unit, description and instrument type unchanged and with the values recorded through the real
    /// metrics classes; nothing else is exported; and every tag is one of the instrument's fixed keys with one of its
    /// fixed values.
    /// </summary>
    [Fact]
    public void Enabled_ExportsEveryInstrumentUnchanged()
    {
        var exported = new List<MetricSnapshot>();
        using var host = BuildWithInMemoryExporter(TelemetryTestSupport.Enabled(TelemetryTestSupport.RefusingEndpoint()), exported);
        var provider = host.Services.GetRequiredService<MeterProvider>();
        var factory = host.Services.GetRequiredService<IMeterFactory>();
        var instruments = new List<Instrument>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory))
                {
                    lock (instruments)
                    {
                        instruments.Add(instrument);
                    }
                }
            },
        };

        Record(host.Services);
        listener.Start();
        provider.ForceFlush(10_000);
        var latest = Latest(exported);

        Assert.Equal(48, instruments.Count);
        Assert.Equal(TelemetryTestSupport.Vocabulary.Keys.Order(StringComparer.Ordinal), instruments.Select(static i => i.Name).Order(StringComparer.Ordinal));
        Assert.Equal(TelemetryServiceCollectionExtensions.MeterNames.Order(StringComparer.Ordinal), instruments.Select(static i => i.Meter.Name).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(instruments.Select(static i => i.Name).Order(StringComparer.Ordinal), latest.Keys.Order(StringComparer.Ordinal));
        foreach (var instrument in instruments)
        {
            var metric = latest[instrument.Name];
            Assert.Equal((instrument.Meter.Name, instrument.Unit, instrument.Description, TypeOf(instrument)), (metric.MeterName, metric.Unit, metric.Description, metric.MetricType));
            Assert.Equal(Expected[instrument.Name], Points(metric));
            if (metric.MetricType == MetricType.Histogram)
            {
                Assert.Equal(1, Assert.Single(metric.MetricPoints).GetHistogramCount());
            }
        }

        AssertVocabulary(latest.Values);
    }

    /// <summary>
    /// Test D: a real FFmpeg publish with a stream key, HLS playback over HTTP and a request for an unknown stream
    /// produce exported measurements on every meter, and no exported tag carries the stream key, the publisher or
    /// application name, a session or incarnation identifier, a client address or a port; every tag is in the fixed
    /// vocabulary.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task RealTraffic_ExportsNoIdentifyingTags()
    {
        var tools = HlsFfmpegInteropTests.Tools();
        var exported = new List<MetricSnapshot>();
        await using var collector = new FakeCollector(respond: true);
        var overrides = TelemetryTestSupport.Enabled(collector.Endpoint);
        overrides["Rtmp:Authentication:Publishers:0:Name"] = "camera";
        overrides["Rtmp:Authentication:Publishers:0:Application"] = "live";
        overrides["Rtmp:Authentication:Publishers:0:KeyHash"] = PublisherKeyHash.Create(StreamKey);
        overrides["Hls:TargetSegmentDuration"] = "00:00:01";
        overrides["Hls:MaxSegmentDuration"] = "00:00:04";
        using var host = BuildWithInMemoryExporter(overrides, exported);
        using var client = new HttpClient();
        var forbidden = new List<string> { StreamKey, "camera", "live", "127.0.0.1", "::1" };

        await host.StartAsync(Token).WithinSafetyTimeout();
        try
        {
            var origin = host.Services.GetRequiredService<HlsHttpOrigin>();
            var rtmpPort = ((IPEndPoint)host.Services.GetRequiredService<RtmpListener>().LocalEndPoint!).Port;
            var httpPort = origin.LocalEndPoint!.Port;
            forbidden.AddRange([rtmpPort.ToString(CultureInfo.InvariantCulture), httpPort.ToString(CultureInfo.InvariantCulture)]);
            var exit = await HlsFfmpegInteropTests.RunAsync(
                tools.Ffmpeg,
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                "-t", "3", "-c:v", "libx264", "-profile:v", "high", "-bf", "2", "-g", "25", "-keyint_min", "25", "-sc_threshold", "0", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-ac", "2", "-b:a", "96k",
                "-f", "flv", $"rtmp://127.0.0.1:{rtmpPort}/live/{StreamKey}");
            Assert.True(exit.Code == 0, $"FFmpeg exited with {exit.Code}: {exit.Errors}");

            var playlist = new Uri($"http://127.0.0.1:{httpPort}/hls/live/camera/index.m3u8");
            await Eventually.WaitUntilAsync(
                () =>
                {
                    using var response = client.GetAsync(playlist, Token).GetAwaiter().GetResult();
                    return response.StatusCode == HttpStatusCode.OK
                        && response.Content.ReadAsStringAsync(Token).GetAwaiter().GetResult().Contains("#EXT-X-ENDLIST", StringComparison.Ordinal);
                },
                "the HTTP playlist ends");
            var probe = await HlsFfmpegInteropTests.ProbeAsync(tools.Probe, playlist.AbsoluteUri);
            Assert.Equal(2, probe.Streams.Count);
            using (var missing = await client.GetAsync(new Uri($"http://127.0.0.1:{httpPort}/hls/live/unknown/index.m3u8"), Token))
            {
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }

            var catalog = host.Services.GetRequiredService<StreamCatalog>().Current;
            forbidden.AddRange(catalog.Streams.Select(static s => s.IncarnationId));
            host.Services.GetRequiredService<MeterProvider>().ForceFlush(10_000);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        var latest = Latest(exported);
        Assert.Equal(TelemetryServiceCollectionExtensions.MeterNames.Order(StringComparer.Ordinal), latest.Values.Select(static m => m.MeterName).Distinct().Order(StringComparer.Ordinal));
        Assert.All(FromTraffic, name => Assert.True(Points(latest[name]).Values.Sum() > 0, name));
        AssertVocabulary(latest.Values);
        foreach (var (key, value) in latest.Values.SelectMany(static m => m.MetricPoints).SelectMany(static p => Tags(p)))
        {
            Assert.DoesNotContain(value, forbidden);
            Assert.All(forbidden, identifier => Assert.DoesNotContain(identifier, value, StringComparison.OrdinalIgnoreCase));
            Assert.False(Guid.TryParse(value, out _), $"{key}={value}");
            Assert.False(IPAddress.TryParse(value, out _) && value.Contains('.', StringComparison.Ordinal), $"{key}={value}");
        }
    }

    /// <summary>Builds a host whose meter provider also has an in-memory exporter beside the configured OTLP exporter.</summary>
    /// <param name="overrides">Configuration overrides.</param>
    /// <param name="exported">Receives exported snapshots.</param>
    /// <returns>The host, not started.</returns>
    private IHost BuildWithInMemoryExporter(Dictionary<string, string?> overrides, List<MetricSnapshot> exported)
    {
        var builder = TestHosts.CreateBuilder(_logs, overrides: overrides);
        builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddInMemoryExporter(exported));
        return builder.Build();
    }

    /// <summary>Returns the host's export service.</summary>
    /// <param name="host">The host.</param>
    /// <returns>The service.</returns>
    private static OtlpMetricsExportService Export(IHost host) => Assert.Single(host.Services.GetServices<IHostedService>().OfType<OtlpMetricsExportService>());

    /// <summary>Records known measurements on every instrument through the application's metrics classes.</summary>
    /// <param name="services">The host's services.</param>
    private static void Record(IServiceProvider services)
    {
        var rtmp = services.GetRequiredService<RtmpMetrics>();
        rtmp.ConnectionAccepted();
        rtmp.ConnectionAccepted();
        rtmp.ConnectionRejected();
        rtmp.ConnectionClosed(TimeSpan.FromSeconds(1.5), RtmpCloseReason.ProtocolError);
        rtmp.Handshake(succeeded: true);
        rtmp.AuthenticationFailed();
        rtmp.AuthenticationAttempt(AuthenticationResult.RateLimited);
        rtmp.CredentialReload(succeeded: false);
        rtmp.SessionStarted();
        rtmp.SessionStarted();
        rtmp.SessionEnded(PublishEndReason.ClientDisconnected);
        rtmp.BytesReceived(4096);
        rtmp.MessageReceived("video");

        var media = services.GetRequiredService<MediaMetrics>();
        media.SessionStarted();
        media.Sample(MediaTrack.Video, 1000, keyframe: true);
        media.Sample(MediaTrack.Audio, 200, keyframe: false);
        media.Configuration(MediaTrack.Video);
        media.Error(MediaTrack.Audio, MediaErrorKind.MalformedPacket);
        media.Discontinuity(MediaTrack.Video, TimestampDiscontinuity.Jump);

        var hls = services.GetRequiredService<HlsMetrics>();
        hls.SessionStarted();
        hls.Initialization(500);
        hls.Segment(3000, TimeSpan.FromSeconds(2), HlsSegmentBoundary.Keyframe, TimeSpan.FromSeconds(0.25));
        hls.StorageChanged(3500);
        hls.Evicted("retired", 2);
        hls.Error(HlsErrorKind.StorageFull);
        hls.Discontinuity("timestamp");
        hls.SamplesDropped(video: true, "before_sync_sample", 3);

        var http = services.GetRequiredService<HttpOriginMetrics>();
        http.RequestStarted();
        http.RequestStarted();
        http.RequestCompleted(HttpOriginResource.Playlist, 200, TimeSpan.FromSeconds(0.5));
        http.BytesWritten(HttpOriginResource.Segment, 777);
        http.Cancelled(HttpOriginResource.Segment);
        http.Missing(HttpOriginResource.Playlist, HttpOriginMissReason.UnknownStream);
        http.Failed(HttpOriginResource.Other);

        var events = services.GetRequiredService<StreamEventMetrics>();
        events.Published(StreamEventType.Ended);
        events.Dropped(StreamEventType.Drained);
        events.Rejected(StreamEventType.Published);
        events.ConsumerFailed(StreamEventType.MediaDescribed);

        var catalog = services.GetRequiredService<StreamCatalogMetrics>();
        catalog.Reconciled(success: true, TimeSpan.FromSeconds(0.125));
        catalog.Requested(StreamCatalogTrigger.Periodic);
        catalog.Coalesced();
        catalog.Attach(static () => (3, TimeSpan.FromSeconds(2)));
    }

    /// <summary>Returns the exported metric type an instrument must have.</summary>
    /// <param name="instrument">The instrument.</param>
    /// <returns>The metric type.</returns>
    private static MetricType TypeOf(Instrument instrument) => instrument switch
    {
        Counter<long> => MetricType.LongSum,
        UpDownCounter<long> => MetricType.LongSumNonMonotonic,
        Histogram<double> => MetricType.Histogram,
        ObservableGauge<int> => MetricType.LongGauge,
        ObservableGauge<double> => MetricType.DoubleGauge,
        _ => throw new InvalidOperationException($"Unexpected instrument type {instrument.GetType()} of {instrument.Name}."),
    };

    /// <summary>Returns the latest snapshot of each exported metric.</summary>
    /// <param name="exported">The exported snapshots, oldest first.</param>
    /// <returns>The latest snapshot by metric name.</returns>
    private static Dictionary<string, MetricSnapshot> Latest(List<MetricSnapshot> exported)
    {
        lock (exported)
        {
            return exported.GroupBy(static m => m.Name).ToDictionary(static g => g.Key, static g => g.Last(), StringComparer.Ordinal);
        }
    }

    /// <summary>Returns a metric's points as values (sum, gauge value or histogram sum) by tag set.</summary>
    /// <param name="metric">The metric.</param>
    /// <returns>The values.</returns>
    private static Dictionary<string, double> Points(MetricSnapshot metric) => metric.MetricPoints.ToDictionary(
        static p => string.Join(';', Tags(p).OrderBy(static t => t.Key, StringComparer.Ordinal).Select(static t => $"{t.Key}={t.Value}")),
        p => metric.MetricType switch
        {
            MetricType.LongSum or MetricType.LongSumNonMonotonic => p.GetSumLong(),
            MetricType.DoubleSum or MetricType.DoubleSumNonMonotonic => p.GetSumDouble(),
            MetricType.LongGauge => p.GetGaugeLastValueLong(),
            MetricType.DoubleGauge => p.GetGaugeLastValueDouble(),
            MetricType.Histogram => p.GetHistogramSum(),
            _ => throw new InvalidOperationException($"Unexpected metric type {metric.MetricType}."),
        });

    /// <summary>Returns a point's tags as strings.</summary>
    /// <param name="point">The point.</param>
    /// <returns>The tags.</returns>
    private static List<(string Key, string Value)> Tags(MetricPoint point)
    {
        var tags = new List<(string, string)>();
        foreach (var tag in point.Tags)
        {
            tags.Add((tag.Key, Assert.IsType<string>(tag.Value)));
        }

        return tags;
    }

    /// <summary>Asserts every tag of every point is one of its instrument's keys with one of that key's fixed values.</summary>
    /// <param name="metrics">The metrics.</param>
    private static void AssertVocabulary(IEnumerable<MetricSnapshot> metrics)
    {
        foreach (var metric in metrics)
        {
            var allowed = TelemetryTestSupport.Vocabulary[metric.Name];
            foreach (var point in metric.MetricPoints)
            {
                foreach (var (key, value) in Tags(point))
                {
                    Assert.True(allowed.TryGetValue(key, out var values), $"{metric.Name} has unexpected tag {key}");
                    Assert.Contains(value, values);
                }
            }
        }
    }
}
