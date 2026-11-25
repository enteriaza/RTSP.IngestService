// Integration tests of the stream catalog in the real service host: its composition (reconciliation hosted after the
// event dispatcher and before the HTTP origin and RTMP listener, the catalog as the event consumer, still a single
// HTTP origin), initialization before startup completes, a real RTMP publication appearing in and leaving
// /health/streams, a real FFmpeg publication's media description and ended HLS state, and strict validation of the
// reconciliation interval (exit 78).

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hosting;
using RTSP.IngestService.Http;
using RTSP.IngestService.Rtmp;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Streams;
using RTSP.IngestService.Tests.Hls;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Streams;

/// <summary>Integration tests for <see cref="StreamCatalogServiceCollectionExtensions.AddStreamCatalog"/> in the service host.</summary>
public sealed class StreamCatalogHostTests : IDisposable
{
    /// <summary>The configured test stream key.</summary>
    private const string StreamKey = "stream-catalog-key-0001";

    /// <summary>Captured host logs.</summary>
    private readonly FakeLogCollector _logs = new();

    /// <summary>The client.</summary>
    private readonly HttpClient _client = new();

    /// <summary>Gets the test cancellation token.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    /// <summary>
    /// Verifies the composition and a real publication's lifetime in the inventory: initialized when startup
    /// completes, publishing while connected, and gone once its (segmentless) HLS stream drained.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Host_ReportsRealPublication()
    {
        using var host = TestHosts.CreateBuilder(_logs, overrides: Settings()).Build();
        var hosted = host.Services.GetServices<IHostedService>().ToList();
        var catalogIndex = hosted.FindIndex(static s => s is StreamCatalogService);
        Assert.True(hosted.FindIndex(static s => s is StreamEventDispatcher) < catalogIndex);
        Assert.True(catalogIndex < hosted.FindIndex(static s => s is HlsHttpOrigin));
        Assert.True(catalogIndex < hosted.FindIndex(static s => s is RtmpListener));
        Assert.Single(hosted.OfType<HlsHttpOrigin>());
        Assert.Single(hosted.OfType<StreamCatalogService>());
        var catalog = host.Services.GetRequiredService<StreamCatalog>();
        Assert.Same(catalog, host.Services.GetRequiredService<IStreamEventConsumer>());

        await host.StartAsync(Token).WithinSafetyTimeout();
        try
        {
            Assert.True(catalog.Current.IsInitialized);
            var origin = host.Services.GetRequiredService<HlsHttpOrigin>();
            Assert.Equal(0, (await GetAsync(origin, "/health/streams")).RootElement.GetProperty("streams").GetArrayLength());

            var client = await RtmpTestClient.ConnectAsync(host.Services.GetRequiredService<RtmpListener>().LocalEndPoint!);
            await using (client)
            {
                Assert.Equal("NetStream.Publish.Start", (await client.PublishFlowAsync(StreamKey)).Code);
                await Eventually.WaitUntilAsync(() => catalog.Current.Find("live", "camera") is not null, "the publication is catalogued");

                using var detail = await GetAsync(origin, "/health/streams/live/camera");
                var stream = detail.RootElement.GetProperty("stream");
                Assert.Equal("publishing", stream.GetProperty("publishing").GetProperty("state").GetString());
                Assert.Equal(32, stream.GetProperty("incarnationId").GetString()!.Length);
                Assert.DoesNotContain(StreamKey, detail.RootElement.GetRawText(), StringComparison.Ordinal);
            }

            await Eventually.WaitUntilAsync(() => catalog.Current.Streams.Count == 0, "the ended publication leaves the catalog");
            Assert.DoesNotContain(_logs.GetSnapshot(), r => r.Message.Contains(StreamKey, StringComparison.Ordinal));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WithinSafetyTimeout();
        }
    }

    /// <summary>
    /// Verifies a real FFmpeg publication: after it ends, the inventory reports RTMP ended, the HLS playlist ended
    /// and still available, and the AAC description established while it was live.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Host_ReportsFfmpegPublicationMediaAndEndedHls()
    {
        var tools = HlsFfmpegInteropTests.Tools();
        using var host = TestHosts.CreateBuilder(_logs, overrides: Settings()).Build();
        await host.StartAsync(Token).WithinSafetyTimeout();
        try
        {
            var origin = host.Services.GetRequiredService<HlsHttpOrigin>();
            var catalog = host.Services.GetRequiredService<StreamCatalog>();
            var port = ((IPEndPoint)host.Services.GetRequiredService<RtmpListener>().LocalEndPoint!).Port;
            var exit = await HlsFfmpegInteropTests.RunAsync(
                tools.Ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-re", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "3", "-c:a", "aac", "-ac", "2", "-f", "flv", $"rtmp://127.0.0.1:{port}/live/{StreamKey}"]);
            Assert.True(exit.Code == 0, $"FFmpeg exited with {exit.Code}: {exit.Errors}");

            await Eventually.WaitUntilAsync(
                () => catalog.Current.Find("live", "camera") is { Publishing.State: StreamPublishingState.Ended, Hls.State: StreamHlsState.Ended },
                "the ended publication is reconciled");
            using var detail = await GetAsync(origin, "/health/streams/live/camera");
            var stream = detail.RootElement.GetProperty("stream");
            Assert.Equal("ended", stream.GetProperty("publishing").GetProperty("state").GetString());
            Assert.True(stream.GetProperty("hls").GetProperty("playlistAvailable").GetBoolean());
            var audio = stream.GetProperty("media").GetProperty("audio");
            Assert.StartsWith("mp4a.40.", audio.GetProperty("codec").GetString(), StringComparison.Ordinal);
            Assert.Equal(48000, audio.GetProperty("sampleRate").GetInt32());
            Assert.False(stream.GetProperty("media").TryGetProperty("video", out _));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WithinSafetyTimeout();
        }
    }

    /// <summary>Verifies the shipped default and that an out-of-range or malformed interval exits 78 before anything binds.</summary>
    /// <param name="value">The configured interval.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData("00:00:00")]
    [InlineData("00:00:00.500")]
    [InlineData("01:00:01")]
    [InlineData("-00:00:30")]
    [InlineData("soon")]
    public async Task InvalidReconciliationInterval_ExitsWithConfigurationError(string value)
    {
        Assert.Equal(TimeSpan.FromSeconds(30), new StreamCatalogOptions().ReconciliationInterval);
        var overrides = Settings();
        overrides["StreamCatalog:ReconciliationInterval"] = value;

        var exitCode = await ServiceRunner.RunAsync(TestHosts.Arguments, HostingMode.Console, TestHosts.Configure(_logs, overrides), Token).WithinSafetyTimeout();

        Assert.Equal(ExitCodes.ConfigurationError, exitCode);
        Assert.DoesNotContain(_logs.GetSnapshot(), static r => r.Id.Id == 8000);
    }

    /// <summary>Verifies the interval's valid range boundaries.</summary>
    /// <param name="seconds">The interval in seconds.</param>
    /// <param name="valid">Whether it is valid.</param>
    [Theory]
    [InlineData(1, true)]
    [InlineData(3600, true)]
    [InlineData(0, false)]
    [InlineData(3601, false)]
    public void Options_IntervalRange(int seconds, bool valid)
    {
        var result = new StreamCatalogOptionsValidator().Validate(null, new StreamCatalogOptions { ReconciliationInterval = TimeSpan.FromSeconds(seconds) });
        Assert.Equal(valid, result.Succeeded);
    }

    /// <summary>Host settings: one publisher <c>camera</c> and 1 s segments.</summary>
    /// <returns>The overrides.</returns>
    private static Dictionary<string, string?> Settings() => new()
    {
        ["Rtmp:Authentication:Publishers:0:Name"] = "camera",
        ["Rtmp:Authentication:Publishers:0:Application"] = "live",
        ["Rtmp:Authentication:Publishers:0:KeyHash"] = PublisherKeyHash.Create(StreamKey),
        ["Hls:TargetSegmentDuration"] = "00:00:01",
        ["Hls:MaxSegmentDuration"] = "00:00:04",
    };

    /// <summary>GETs a management path that must answer 200 and parses its body.</summary>
    /// <param name="origin">The started origin.</param>
    /// <param name="path">The path.</param>
    /// <returns>The body.</returns>
    private async Task<JsonDocument> GetAsync(HlsHttpOrigin origin, string path)
    {
        using var response = await _client.GetAsync(new Uri($"http://127.0.0.1:{origin.LocalEndPoint!.Port}{path}"), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
    }
}
