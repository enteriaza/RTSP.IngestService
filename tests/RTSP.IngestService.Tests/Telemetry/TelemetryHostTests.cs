// Failure isolation of OTLP metrics export in the real service host. A collector that refuses connections does not
// affect startup, the listeners, readiness or the lifecycle; the failed export is logged without the endpoint. A
// collector that accepts connections but never answers cannot hold shutdown beyond the flush bound, and a cancelled
// host stop token ends the flush wait at once. Collectors are loopback sockets, so nothing depends on the network.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using OpenTelemetry.Metrics;
using RTSP.IngestService.Http;
using RTSP.IngestService.Lifecycle;
using RTSP.IngestService.Rtmp;
using RTSP.IngestService.Telemetry;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Telemetry;

/// <summary>Failure-isolation and shutdown tests for <see cref="OtlpMetricsExportService"/>.</summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class TelemetryHostTests : IDisposable
{
    /// <summary>Captured host logs.</summary>
    private readonly FakeLogCollector _logs = new();

    /// <summary>The client.</summary>
    private readonly HttpClient _client = new();

    /// <summary>Gets the test cancellation token.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    /// <summary>
    /// Test E: with a collector that refuses connections the host starts, both listeners bind, an RTMP connection is
    /// accepted and readiness is healthy; a failed export is logged (event 9301) without the endpoint and changes
    /// neither readiness nor the lifecycle, which ends Stopped after a stop well within the flush bound.
    /// </summary>
    /// <param name="protocol">The OTLP protocol.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData("grpc")]
    [InlineData("http/protobuf")]
    public async Task UnavailableCollector_DoesNotAffectHealthOrIngest(string protocol)
    {
        var endpoint = TelemetryTestSupport.RefusingEndpoint();
        using var host = TestHosts.CreateBuilder(_logs, overrides: TelemetryTestSupport.Enabled(endpoint, protocol, "00:00:01")).Build();
        var lifecycle = host.Services.GetRequiredService<IApplicationLifecycle>();

        await host.StartAsync(Token).WithinSafetyTimeout();
        var origin = host.Services.GetRequiredService<HlsHttpOrigin>();
        var rtmp = host.Services.GetRequiredService<RtmpListener>();
        Assert.True(origin.IsListening);
        Assert.True(rtmp.IsListening);
        Assert.Equal(ApplicationState.Running, lifecycle.State);
        Assert.Equal(HttpStatusCode.OK, await ReadyAsync(origin));

        Assert.False(host.Services.GetRequiredService<MeterProvider>().ForceFlush(10_000));
        Assert.Contains(_logs.GetSnapshot(), static r => r.Id.Id == 9301);
        using (var client = new TcpClient())
        {
            await client.ConnectAsync((IPEndPoint)rtmp.LocalEndPoint!, Token);
        }

        Assert.Equal(ApplicationState.Running, lifecycle.State);
        Assert.Equal(HttpStatusCode.OK, await ReadyAsync(origin));

        var stopwatch = Stopwatch.StartNew();
        await host.StopAsync(CancellationToken.None).WithinSafetyTimeout();
        Assert.True(stopwatch.Elapsed < OtlpMetricsExportService.FlushTimeout, $"Stop took {stopwatch.Elapsed}.");
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        Assert.Null(lifecycle.Failure);
        var port = new Uri(endpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.All(_logs.GetSnapshot().Where(static r => r.Id.Id is >= 9300 and <= 9302), r => Assert.DoesNotContain(port, r.Message, StringComparison.Ordinal));
        Assert.All(_logs.GetSnapshot().Where(static r => r.Id.Id is >= 9300 and <= 9302), r => Assert.Null(r.Exception));
    }

    /// <summary>
    /// Test F: with a collector that accepts the final export but never answers, stop returns once the flush bound
    /// elapses and disposal does not wait for the hung request; the process could terminate. The undelivered export
    /// is logged: as 9302 when the reader had not stopped by the bound, or, when the SDK cancelled the export at the
    /// shutdown deadline just before, as the SDK's export failure (9301).
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task UnresponsiveCollector_ShutdownIsBounded()
    {
        await using var collector = new FakeCollector(respond: false);
        var host = TestHosts.CreateBuilder(_logs, overrides: TelemetryTestSupport.Enabled(collector.Endpoint)).Build();
        var lifecycle = host.Services.GetRequiredService<IApplicationLifecycle>();
        await host.StartAsync(Token).WithinSafetyTimeout();

        var stopwatch = Stopwatch.StartNew();
        await host.StopAsync(CancellationToken.None).WithinSafetyTimeout();
        var stopped = stopwatch.Elapsed;
        stopwatch.Restart();
        host.Dispose();
        var disposed = stopwatch.Elapsed;

        await collector.FirstRequest.WithinSafetyTimeout();
        Assert.True(stopped < OtlpMetricsExportService.FlushTimeout + TimeSpan.FromSeconds(2), $"Stop took {stopped}.");
        Assert.True(disposed < TimeSpan.FromSeconds(2), $"Dispose took {disposed}.");
        Assert.Contains(_logs.GetSnapshot(), static r => r.Id.Id is 9301 or 9302);
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
    }

    /// <summary>
    /// Test F: the flush honours the host's stop token. With a one-second shutdown timeout and a collector that never
    /// answers, stop returns when the token is cancelled, well before the flush bound, and the lifecycle is Stopped.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task UnresponsiveCollector_FlushEndsWhenStopTokenIsCancelled()
    {
        await using var collector = new FakeCollector(respond: false);
        var overrides = TelemetryTestSupport.Enabled(collector.Endpoint);
        overrides["Lifetime:ShutdownTimeout"] = "00:00:01";
        var host = TestHosts.CreateBuilder(_logs, overrides: overrides).Build();
        try
        {
            var lifecycle = host.Services.GetRequiredService<IApplicationLifecycle>();
            await host.StartAsync(Token).WithinSafetyTimeout();

            var stopwatch = Stopwatch.StartNew();
            await host.StopAsync(CancellationToken.None).WithinSafetyTimeout();

            Assert.True(stopwatch.Elapsed < OtlpMetricsExportService.FlushTimeout - TimeSpan.FromSeconds(2), $"Stop took {stopwatch.Elapsed}.");
            await collector.FirstRequest.WithinSafetyTimeout();
            Assert.Single(_logs.GetSnapshot(), static r => r.Id.Id == 9302);
            Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        }
        finally
        {
            var stopwatch = Stopwatch.StartNew();
            host.Dispose();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Dispose took {stopwatch.Elapsed}.");
        }
    }

    /// <summary>Requests readiness.</summary>
    /// <param name="origin">The started origin.</param>
    /// <returns>The status code.</returns>
    private async Task<HttpStatusCode> ReadyAsync(HlsHttpOrigin origin)
    {
        using var response = await _client.GetAsync(new Uri($"http://127.0.0.1:{origin.LocalEndPoint!.Port}/health/ready"), Token);
        return response.StatusCode;
    }
}
