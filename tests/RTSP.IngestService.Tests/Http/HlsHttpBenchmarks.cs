// Explicit (not run by default) throughput measurement of the HTTP HLS origin over loopback: playlist and 64 KB segment
// GETs from concurrent keep-alive clients, with CORS off and with an origin allowlist and an Origin header on every
// request, for the performance notes of the HLS security headers. Run with --filter-class "*HlsHttpBenchmarks"
// --explicit only; RTSP_HTTP_BENCHMARK_OUTPUT appends the results to a file.

using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Http;
using RTSP.IngestService.Tests.Hls;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace RTSP.IngestService.Tests.Http;

/// <summary>Throughput measurements of the HTTP HLS origin.</summary>
public sealed class HlsHttpBenchmarks
{
    /// <summary>Concurrent clients.</summary>
    private const int Clients = 4;

    /// <summary>Requests per client per resource.</summary>
    private const int Requests = 5000;

    /// <summary>Measures requests per second and allocated bytes per request.</summary>
    /// <param name="cors">Whether an origin allowlist is configured and every request sends a listed Origin.</param>
    /// <returns>A task that completes when the measurement is finished.</returns>
    [Theory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Origin_RequestThroughput(bool cors)
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meters = services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var hls = new HlsOptions();
        var store = new InMemoryHlsSegmentStore(MsOptions.Create(hls), time, new HlsMetrics(meters));
        var session = Guid.NewGuid();
        Assert.True(store.TryOpenStream(session, "live", "cam", 4, out _));
        store.AddInitialization(session, new HlsSegment(HlsSegmentKind.Initialization, "init-0.mp4", new byte[800]));
        Assert.Equal(HlsAppendResult.Published, store.AppendSegment(session, new HlsSegment(HlsSegmentKind.Media, "segment-0.m4s", new byte[64 * 1024]), HlsPlaylistTests.Entry(0, 4.0)));

        var http = new HttpOriginOptions { ListenAddress = "127.0.0.1", Port = 0, Cors = cors ? new() { Enabled = true, AllowedOrigins = ["https://player.example.com"] } : new() };
        var application = new HlsHttpApplication(store, MsOptions.Create(hls), new HttpOriginMetrics(meters), time, NullLogger<HlsHttpApplication>.Instance, headers: new HlsHttpHeaders(http));
        var origin = new HlsHttpOrigin(MsOptions.Create(http), application, services, NullLoggerFactory.Instance);
        await origin.StartAsync(Token);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{origin.LocalEndPoint!.Port}/") };
            if (cors)
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://player.example.com");
            }

            var playlist = "hls/live/cam/index.m3u8";
            var segment = $"hls/live/cam/{store.Generation}/segment-0.m4s";
            await RunAsync(client, playlist, 200);
            await RunAsync(client, segment, 200);

            foreach (var (name, path) in new[] { ("playlist", playlist), ("segment 64 KB", segment) })
            {
                var allocated = GC.GetTotalAllocatedBytes(precise: true);
                var start = Stopwatch.GetTimestamp();
                await RunAsync(client, path, Requests);
                var seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
                allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
                Report(string.Create(
                    CultureInfo.InvariantCulture,
                    $"http origin, CORS {(cors ? "allowlist + Origin" : "off")}, {name}: {Clients * Requests} requests from {Clients} clients in {seconds:F3} s; {Clients * Requests / seconds:F0} requests/s; {allocated / (Clients * Requests)} bytes allocated per request (client and server)"));
            }
        }
        finally
        {
            await origin.StopAsync(CancellationToken.None);
            await origin.DisposeAsync();
        }
    }

    /// <summary>Gets the test cancellation token.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Sends GETs from <see cref="Clients"/> concurrent loops and checks each is 200.</summary>
    /// <param name="client">The client.</param>
    /// <param name="path">The path.</param>
    /// <param name="count">Requests per loop.</param>
    /// <returns>A task that completes when all requests are done.</returns>
    private static Task RunAsync(HttpClient client, string path, int count) => Task.WhenAll(Enumerable.Range(0, Clients).Select(_ => Task.Run(
        async () =>
        {
            for (var i = 0; i < count; i++)
            {
                using var response = await client.GetAsync(path, HttpCompletionOption.ResponseContentRead, Token);
                Assert.True(response.IsSuccessStatusCode);
            }
        },
        Token)));

    /// <summary>Writes a result to the test output and, if <c>RTSP_HTTP_BENCHMARK_OUTPUT</c> names a file, appends it there.</summary>
    /// <param name="line">The result.</param>
    private static void Report(string line)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        if (Environment.GetEnvironmentVariable("RTSP_HTTP_BENCHMARK_OUTPUT") is { Length: > 0 } path)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }
}
