// Tests for HeartbeatService: periodic heartbeat logging on a fake clock, application of reloaded intervals, retention
// of the last valid interval when a reload fails validation, and cancellation.

using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Lifecycle;
using RTSP.IngestService.Services;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Services;

/// <summary>
/// Tests for <see cref="HeartbeatService"/>.
/// </summary>
public sealed class HeartbeatServiceTests
{
    /// <summary>Clock driving the heartbeat timer.</summary>
    private readonly FakeTimeProvider _time = new();

    /// <summary>Logger capturing heartbeat entries.</summary>
    private readonly FakeLogger<HeartbeatService> _logger = new();

    /// <summary>Lifecycle reported in heartbeats.</summary>
    private readonly StubApplicationLifecycle _lifecycle = new() { State = ApplicationState.Running };

    /// <summary>Options monitor controlled by the test.</summary>
    private readonly TestOptionsMonitor<HeartbeatOptions> _options = new(new HeartbeatOptions { Interval = TimeSpan.FromSeconds(10) });

    /// <summary>
    /// Verifies that no heartbeat is logged before the interval elapses and that each elapsed interval logs one
    /// heartbeat carrying the lifecycle state and uptime.
    /// </summary>
    [Fact]
    public async Task LogsHeartbeatEachInterval()
    {
        using var service = await StartAsync();

        Assert.Empty(Heartbeats());
        _time.Advance(TimeSpan.FromSeconds(10));
        await WaitForHeartbeatsAsync(1);
        _time.Advance(TimeSpan.FromSeconds(10));
        await WaitForHeartbeatsAsync(2);

        var latest = Heartbeats()[^1];
        Assert.Equal("Running", latest.GetStructuredStateValue("State"));
        Assert.Equal(TimeSpan.FromSeconds(20).ToString(), latest.GetStructuredStateValue("Uptime"));

        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(service.ExecuteTask!.IsCanceled);
    }

    /// <summary>
    /// Verifies that a reloaded interval is applied after the next heartbeat and governs subsequent heartbeats.
    /// </summary>
    [Fact]
    public async Task AppliesReloadedInterval()
    {
        using var service = await StartAsync();
        _options.Value = new HeartbeatOptions { Interval = TimeSpan.FromSeconds(30) };

        _time.Advance(TimeSpan.FromSeconds(10));
        await WaitForHeartbeatsAsync(1);
        await Eventually.WaitUntilAsync(() => _logger.Collector.GetSnapshot().Any(static r => r.Id.Id == 3002), "the interval change is logged");

        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Single(Heartbeats());
        _time.Advance(TimeSpan.FromSeconds(20));
        await WaitForHeartbeatsAsync(2);

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies that when the reloaded configuration fails validation, the previous interval is kept and a warning
    /// is logged.
    /// </summary>
    [Fact]
    public async Task InvalidReload_KeepsPreviousInterval()
    {
        using var service = await StartAsync();
        _options.ValidationFailure = new OptionsValidationException(Options.DefaultName, typeof(HeartbeatOptions), ["Interval out of range"]);

        _time.Advance(TimeSpan.FromSeconds(10));
        await WaitForHeartbeatsAsync(1);
        await Eventually.WaitUntilAsync(() => _logger.Collector.GetSnapshot().Any(static r => r.Id.Id == 3003), "the invalid reload warning is logged");
        _time.Advance(TimeSpan.FromSeconds(10));
        await WaitForHeartbeatsAsync(2);

        var warning = _logger.Collector.GetSnapshot().First(static r => r.Id.Id == 3003);
        Assert.Equal(TimeSpan.FromSeconds(10).ToString(), warning.GetStructuredStateValue("Interval"));
        Assert.DoesNotContain(_logger.Collector.GetSnapshot(), static r => r.Id.Id == 3002);

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Starts the service and waits until its timer exists, so that clock advances are observed.</summary>
    /// <returns>The running service.</returns>
    private async Task<HeartbeatService> StartAsync()
    {
        var service = new HeartbeatService(_options, _lifecycle, _time, _logger);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await Eventually.WaitUntilAsync(() => _logger.Collector.GetSnapshot().Any(static r => r.Id.Id == 3000), "the heartbeat timer has started");
        return service;
    }

    /// <summary>Returns the heartbeat records logged so far.</summary>
    /// <returns>The heartbeat records.</returns>
    private FakeLogRecord[] Heartbeats() => [.. _logger.Collector.GetSnapshot().Where(static r => r.Id.Id == 3001)];

    /// <summary>Waits until at least <paramref name="count"/> heartbeats have been logged.</summary>
    /// <param name="count">The expected number of heartbeats.</param>
    /// <returns>A task that completes when the heartbeats have been logged.</returns>
    private Task WaitForHeartbeatsAsync(int count) =>
        Eventually.WaitUntilAsync(() => Heartbeats().Length >= count, $"{count} heartbeat(s) are logged");
}
