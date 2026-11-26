// Integration tests of the RTMP listener inside the real service host: readiness only after the bind, publishing
// with configured credentials, graceful host shutdown with an active publisher, and startup failures for port
// conflicts and invalid RTMP configuration with their exit codes.

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using RTSP.IngestService.Hosting;
using RTSP.IngestService.Lifecycle;
using RTSP.IngestService.Rtmp;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Rtmp;

/// <summary>
/// Integration tests for <see cref="RtmpServiceCollectionExtensions.AddRtmpIngest"/> in the service host.
/// </summary>
public sealed class RtmpHostIntegrationTests
{
    /// <summary>The configured test stream key.</summary>
    private const string StreamKey = "integration-key-0001";

    /// <summary>Captured host logs.</summary>
    private readonly FakeLogCollector _logs = new();

    /// <summary>
    /// Verifies that the host is ready only after the listener is bound, that a client can publish with configured
    /// credentials, and that stopping the host closes the publisher and ends its session as a server shutdown.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Host_BindsBeforeReady_AcceptsPublisher_AndStopsGracefully()
    {
        using var host = TestHosts.CreateBuilder(_logs, overrides: Credentials()).Build();
        var lifecycle = host.Services.GetRequiredService<IApplicationLifecycle>();
        var listener = host.Services.GetRequiredService<RtmpListener>();
        var registry = host.Services.GetRequiredService<PublishedStreamRegistry>();
        Assert.Null(listener.LocalEndPoint);

        await host.StartAsync(TestContext.Current.CancellationToken).WithinSafetyTimeout();

        Assert.True(lifecycle.IsReady);
        var endPoint = Assert.IsType<IPEndPoint>(listener.LocalEndPoint);
        Assert.NotEqual(0, endPoint.Port);
        var records = _logs.GetSnapshot().ToList();
        var listening = records.FindIndex(static r => r.Id.Id == 5000);
        var running = records.FindIndex(static r => r.Id.Id == 1000 && r.Message.EndsWith("to Running", StringComparison.Ordinal));
        Assert.InRange(listening, 0, running - 1);

        await using var client = await RtmpTestClient.ConnectAsync(endPoint);
        Assert.Equal("NetStream.Publish.Start", (await client.PublishFlowAsync(StreamKey)).Code);
        Assert.NotNull(registry.Find("live", "camera"));

        await host.StopAsync(TestContext.Current.CancellationToken).WithinSafetyTimeout();

        await client.WaitForCloseAsync().WithinSafetyTimeout();
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        Assert.Equal(0, registry.Count);
        Assert.Contains(_logs.GetSnapshot(), static r => r.Id.Id == 5207 && r.Message.Contains(nameof(PublishEndReason.ServerShutdown), StringComparison.Ordinal));
        Assert.DoesNotContain(_logs.GetSnapshot(), static r => r.Message.Contains(StreamKey, StringComparison.Ordinal));
    }

    /// <summary>Verifies that a port conflict fails startup with exit code 1 and the bind failure logged.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task PortConflict_FailsStartup()
    {
        using var occupier = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        occupier.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        occupier.Listen();
        var port = ((IPEndPoint)occupier.LocalEndPoint!).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var exitCode = await RunAsync(new Dictionary<string, string?> { ["Rtmp:Port"] = port });

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Contains(_logs.GetSnapshot(), static r => r.Id.Id == 5001);
    }

    /// <summary>Verifies that invalid RTMP configuration fails startup with the configuration-error exit code.</summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="value">The invalid value.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData("Rtmp:Port", "70000")]
    [InlineData("Rtmp:ListenAddress", "not-an-address")]
    [InlineData("Rtmp:MaxBufferedBytes", "65536")]
    [InlineData("Rtmp:HandshakeTimeout", "00:00:00")]
    [InlineData("Rtmp:UnknownSetting", "1")]
    [InlineData("Rtmp:Authentication:Publishers:0:Name", "only-a-name")]
    [InlineData("Media:MaxSampleSize", "1023")]
    [InlineData("Media:MaxNalUnitsPerSample", "0")]
    [InlineData("Media:DiscontinuityThreshold", "00:00:00")]
    [InlineData("Media:UnknownSetting", "1")]
    [InlineData("Hls:TargetSegmentDuration", "00:00:00.5")]
    [InlineData("Hls:MaxSegmentDuration", "00:00:03")]
    [InlineData("Hls:PlaylistWindowSize", "2")]
    [InlineData("Hls:MaxSessionStorageMegabytes", "16")]
    [InlineData("Hls:MaxTotalStorageMegabytes", "64")]
    [InlineData("Hls:MaxSessions", "0")]
    [InlineData("Hls:MaxSegmentSize", "1024")]
    [InlineData("Hls:EndedStreamRetention", "00:11:00")]
    [InlineData("Hls:UnknownSetting", "1")]
    [InlineData("Http:Port", "70000")]
    [InlineData("Http:ListenAddress", "localhost")]
    [InlineData("Http:MaxConnections", "0")]
    [InlineData("Http:RequestHeadersTimeout", "00:00:00")]
    [InlineData("Http:KeepAliveTimeout", "02:00:00")]
    [InlineData("Http:UnknownSetting", "1")]
    [InlineData("Http:Cors:Enabled", "true")]
    [InlineData("Http:Cors:AllowedOrigins:0", "*")]
    [InlineData("Http:Cors:UnknownSetting", "1")]
    [InlineData("Http:CrossOriginResourcePolicy", "none")]
    public async Task InvalidConfiguration_FailsWithConfigurationError(string key, string value)
    {
        var exitCode = await RunAsync(new Dictionary<string, string?> { [key] = value });

        Assert.Equal(ExitCodes.ConfigurationError, exitCode);
        Assert.DoesNotContain(_logs.GetSnapshot(), static r => r.Id.Id == 5000);
    }

    /// <summary>Configuration with one publisher named <c>camera</c> using <see cref="StreamKey"/>.</summary>
    /// <returns>The overrides.</returns>
    private static Dictionary<string, string?> Credentials() => new()
    {
        ["Rtmp:Authentication:Publishers:0:Name"] = "camera",
        ["Rtmp:Authentication:Publishers:0:Application"] = "live",
        ["Rtmp:Authentication:Publishers:0:KeyHash"] = PublisherKeyHash.Create(StreamKey),
    };

    /// <summary>Runs the service to completion with <paramref name="overrides"/>.</summary>
    /// <param name="overrides">Configuration overrides.</param>
    /// <returns>The exit code.</returns>
    private Task<int> RunAsync(IReadOnlyDictionary<string, string?> overrides) =>
        ServiceRunner.RunAsync(TestHosts.Arguments, HostingMode.Console, TestHosts.Configure(_logs, overrides), TestContext.Current.CancellationToken).WithinSafetyTimeout();
}
