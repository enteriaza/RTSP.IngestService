// Integration tests of the management endpoints in the real service host: they share the single HTTP origin
// listener, readiness is 503 while startup is held before and after the RTMP listener binds and while shutdown is held
// before the listeners close, liveness and readiness after stop, management configuration errors exit 78, and HLS
// playback of a real FFmpeg publish is unaffected when management is disabled. Gates are hosted services released by
// the test, so there are no delays.

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Testing;
using RTSP.IngestService.Hosting;
using RTSP.IngestService.Http;
using RTSP.IngestService.Lifecycle;
using RTSP.IngestService.Management;
using RTSP.IngestService.Rtmp;
using RTSP.IngestService.Tests.Hls;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Management;

/// <summary>Integration tests for <see cref="ManagementServiceCollectionExtensions.AddManagementEndpoints"/> in the service host.</summary>
public sealed class ManagementHostTests : IDisposable
{
    /// <summary>The configured test stream key.</summary>
    private const string StreamKey = "management-key-0001";

    /// <summary>Captured host logs.</summary>
    private readonly FakeLogCollector _logs = new();

    /// <summary>The client.</summary>
    private readonly HttpClient _client = new();

    /// <summary>Gets the test cancellation token.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    /// <summary>
    /// Verifies HLS and management share the one HTTP origin: a single origin hosted service, no other HTTP server,
    /// one listening event, and both route families answering on the configured HTTP port. After stop, both listeners
    /// report unbound and readiness and liveness are 503.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Host_ServesHealthOnTheSingleHttpListener()
    {
        using var host = TestHosts.CreateBuilder(_logs, overrides: Settings()).Build();
        var origin = host.Services.GetRequiredService<HlsHttpOrigin>();
        var rtmp = host.Services.GetRequiredService<RtmpListener>();
        Assert.False(origin.IsListening);
        Assert.False(rtmp.IsListening);

        await host.StartAsync(Token).WithinSafetyTimeout();
        try
        {
            var hosted = host.Services.GetServices<IHostedService>().ToList();
            Assert.Single(hosted.OfType<HlsHttpOrigin>());
            Assert.DoesNotContain(hosted, static s => s.GetType().Namespace?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true);
            Assert.Null(host.Services.GetService<IServer>());
            Assert.Single(_logs.GetSnapshot(), static r => r.Id.Id == 8000);
            Assert.True(origin.IsListening);
            Assert.True(rtmp.IsListening);

            var (status, body) = await GetHealthAsync(origin, "/health/ready");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(("ready", "Running", true, true), body);
            Assert.Equal(HttpStatusCode.OK, (await GetHealthAsync(origin, "/health/live")).Status);
            using var hls = await _client.GetAsync(new Uri($"http://127.0.0.1:{origin.LocalEndPoint!.Port}/hls/live/camera/index.m3u8"), Token);
            Assert.Equal(HttpStatusCode.NotFound, hls.StatusCode);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        Assert.False(origin.IsListening);
        Assert.False(rtmp.IsListening);
        var endpoints = host.Services.GetRequiredService<ManagementEndpoints>();
        Assert.Equal(ApplicationState.Stopped, host.Services.GetRequiredService<IApplicationLifecycle>().State);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, endpoints.Ready().Status);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, endpoints.Live().Status);
    }

    /// <summary>
    /// Verifies readiness is 503 during startup: while a service registered before the RTMP listener is starting (HTTP
    /// bound, RTMP not), and while one registered after it is starting (both bound, lifecycle still Starting); liveness
    /// is 200 throughout and readiness becomes 200 once startup completes.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Startup_ReadinessIsUnavailableUntilRunning()
    {
        var beforeRtmp = new Gate();
        var afterRtmp = new Gate();
        var builder = TestHosts.CreateBuilder(_logs, overrides: Settings());
        var rtmpIndex = LastHostedServiceIndex(builder.Services);
        builder.Services.Insert(rtmpIndex, ServiceDescriptor.Singleton<IHostedService>(new StartGateService(beforeRtmp)));
        builder.Services.AddSingleton<IHostedService>(new StartGateService(afterRtmp));
        using var host = builder.Build();
        var origin = host.Services.GetRequiredService<HlsHttpOrigin>();

        var start = host.StartAsync(Token);
        try
        {
            await beforeRtmp.Entered.WithinSafetyTimeout();
            Assert.Equal((HttpStatusCode.ServiceUnavailable, ("not_ready", "Starting", false, true)), await GetHealthAsync(origin, "/health/ready"));
            Assert.Equal(HttpStatusCode.OK, (await GetHealthAsync(origin, "/health/live")).Status);
            beforeRtmp.Release();

            await afterRtmp.Entered.WithinSafetyTimeout();
            Assert.Equal((HttpStatusCode.ServiceUnavailable, ("not_ready", "Starting", true, true)), await GetHealthAsync(origin, "/health/ready"));
            Assert.Equal(HttpStatusCode.OK, (await GetHealthAsync(origin, "/health/live")).Status);
            afterRtmp.Release();

            await start.WithinSafetyTimeout();
            Assert.Equal((HttpStatusCode.OK, ("ready", "Running", true, true)), await GetHealthAsync(origin, "/health/ready"));
        }
        finally
        {
            beforeRtmp.Release();
            afterRtmp.Release();
            await start.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            await host.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Verifies readiness is withdrawn as soon as shutdown begins, while both listeners are still bound and the
    /// management endpoints are still served, and that liveness stays 200 until the application has stopped.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Shutdown_ReadinessIsWithdrawnBeforeListenersClose()
    {
        var gate = new Gate();
        var builder = TestHosts.CreateBuilder(_logs, overrides: Settings());
        builder.Services.AddSingleton<IHostedService>(new StopGateService(gate));
        using var host = builder.Build();
        var origin = host.Services.GetRequiredService<HlsHttpOrigin>();
        var rtmp = host.Services.GetRequiredService<RtmpListener>();
        await host.StartAsync(Token).WithinSafetyTimeout();
        Assert.Equal(HttpStatusCode.OK, (await GetHealthAsync(origin, "/health/ready")).Status);

        var stop = host.StopAsync(CancellationToken.None);
        try
        {
            await gate.Entered.WithinSafetyTimeout();
            Assert.True(rtmp.IsListening);
            Assert.True(origin.IsListening);
            Assert.Equal((HttpStatusCode.ServiceUnavailable, ("not_ready", "Stopping", true, true)), await GetHealthAsync(origin, "/health/ready"));
            Assert.Equal((HttpStatusCode.OK, ("live", "Stopping", null, null)), await GetHealthAsync(origin, "/health/live"));
        }
        finally
        {
            gate.Release();
            await stop.WithinSafetyTimeout();
        }

        Assert.False(rtmp.IsListening);
        Assert.False(origin.IsListening);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, host.Services.GetRequiredService<ManagementEndpoints>().Live().Status);
    }

    /// <summary>
    /// Verifies management has no listener settings of its own (a management port is an unknown key) and that an
    /// unconvertible <c>Management:Enabled</c> is a configuration error; both exit 78 before anything binds.
    /// </summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="value">The value.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData("Management:Port", "8081")]
    [InlineData("Management:ListenAddress", "127.0.0.1")]
    [InlineData("Management:MaxConnections", "16")]
    [InlineData("Management:Enabled", "sometimes")]
    public async Task InvalidManagementConfiguration_ExitsWithConfigurationError(string key, string value)
    {
        var overrides = Settings();
        overrides[key] = value;

        var exitCode = await ServiceRunner.RunAsync(TestHosts.Arguments, HostingMode.Console, TestHosts.Configure(_logs, overrides), Token).WithinSafetyTimeout();

        Assert.Equal(ExitCodes.ConfigurationError, exitCode);
        Assert.DoesNotContain(_logs.GetSnapshot(), static r => r.Id.Id == 8000);
    }

    /// <summary>
    /// Verifies that with management disabled the health paths are 404 while a real FFmpeg publish over RTMP still
    /// plays over HTTP, and the application lifecycle still reaches Running.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Disabled_HostStillServesHlsPlayback()
    {
        var tools = HlsFfmpegInteropTests.Tools();
        var overrides = Settings();
        overrides["Management:Enabled"] = "false";
        using var host = TestHosts.CreateBuilder(_logs, overrides: overrides).Build();
        await host.StartAsync(Token).WithinSafetyTimeout();
        try
        {
            Assert.True(host.Services.GetRequiredService<IApplicationLifecycle>().IsReady);
            var origin = host.Services.GetRequiredService<HlsHttpOrigin>();
            foreach (var path in new[] { "/health/live", "/health/ready" })
            {
                using var health = await _client.GetAsync(new Uri($"http://127.0.0.1:{origin.LocalEndPoint!.Port}{path}"), Token);
                Assert.Equal(HttpStatusCode.NotFound, health.StatusCode);
            }

            var port = ((IPEndPoint)host.Services.GetRequiredService<RtmpListener>().LocalEndPoint!).Port;
            var exit = await HlsFfmpegInteropTests.RunAsync(
                tools.Ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-re", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "3", "-c:a", "aac", "-ac", "2", "-f", "flv", $"rtmp://127.0.0.1:{port}/live/{StreamKey}"]);
            Assert.True(exit.Code == 0, $"FFmpeg exited with {exit.Code}: {exit.Errors}");

            var playlistUri = new Uri($"http://127.0.0.1:{origin.LocalEndPoint!.Port}/hls/live/camera/index.m3u8");
            await Eventually.WaitUntilAsync(
                () =>
                {
                    using var response = _client.GetAsync(playlistUri, Token).GetAwaiter().GetResult();
                    return response.StatusCode == HttpStatusCode.OK
                        && response.Content.ReadAsStringAsync(Token).GetAwaiter().GetResult().Contains("#EXT-X-ENDLIST", StringComparison.Ordinal);
                },
                "the HTTP playlist ends");
            var probe = await HlsFfmpegInteropTests.ProbeAsync(tools.Probe, playlistUri.AbsoluteUri);
            var audio = Assert.Single(probe.Streams);
            Assert.Equal(("audio", "aac"), (audio.CodecType, audio.CodecName));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Host settings: one publisher <c>camera</c> and 1 s segments.</summary>
    /// <returns>The overrides.</returns>
    private static Dictionary<string, string?> Settings() => new()
    {
        ["Rtmp:Authentication:Publishers:0:Name"] = "camera",
        ["Rtmp:Authentication:Publishers:0:StreamKey"] = StreamKey,
        ["Hls:TargetSegmentDuration"] = "00:00:01",
        ["Hls:MaxSegmentDuration"] = "00:00:04",
    };

    /// <summary>Returns the index of the last hosted-service registration, which is the RTMP listener after composition.</summary>
    /// <param name="services">The composed services.</param>
    /// <returns>The index.</returns>
    private static int LastHostedServiceIndex(IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(IHostedService))
            {
                return i;
            }
        }

        throw new InvalidOperationException("No hosted service is registered.");
    }

    /// <summary>Requests a health endpoint and parses its body.</summary>
    /// <param name="origin">The started origin.</param>
    /// <param name="path">The endpoint.</param>
    /// <returns>The status and the body's status, state and listener flags.</returns>
    private async Task<(HttpStatusCode Status, (string?, string?, bool?, bool?) Body)> GetHealthAsync(HlsHttpOrigin origin, string path)
    {
        using var response = await _client.GetAsync(new Uri($"http://127.0.0.1:{origin.LocalEndPoint!.Port}{path}"), Token);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        var root = body.RootElement;
        static bool? Flag(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.GetBoolean() : null;
        return (response.StatusCode, (root.GetProperty("status").GetString(), root.GetProperty("state").GetString(), Flag(root, "rtmpListener"), Flag(root, "httpListener")));
    }

    /// <summary>A point a hosted service waits at until the test releases it.</summary>
    private sealed class Gate
    {
        /// <summary>Completed when the service reaches the gate.</summary>
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completed by the test.</summary>
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets a task that completes when the service reaches the gate.</summary>
        public Task Entered => _entered.Task;

        /// <summary>Lets the service continue.</summary>
        public void Release() => _released.TrySetResult();

        /// <summary>Signals arrival and waits for release.</summary>
        /// <returns>A task that completes on release.</returns>
        public Task WaitAsync()
        {
            _entered.TrySetResult();
            return _released.Task;
        }
    }

    /// <summary>Holds host startup at its position in the start order until released.</summary>
    /// <param name="gate">The gate.</param>
    private sealed class StartGateService(Gate gate) : IHostedService
    {
        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken) => gate.WaitAsync();

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Holds host shutdown at its position in the stop order until released.</summary>
    /// <param name="gate">The gate.</param>
    private sealed class StopGateService(Gate gate) : IHostedService
    {
        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) => gate.WaitAsync();
    }
}
