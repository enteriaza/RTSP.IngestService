// Tests for the systemd watchdog integration: parsing of WATCHDOG_USEC/WATCHDOG_PID and the keep-alive behaviour of
// SystemdWatchdogService, driven by a fake clock and a fake notifier in place of the systemd socket.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Hosting;
using RTSP.IngestService.Lifecycle;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Hosting;

/// <summary>
/// Tests for <see cref="SystemdWatchdogSettings"/> and <see cref="SystemdWatchdogService"/>.
/// </summary>
public sealed class SystemdWatchdogTests
{
    /// <summary>Process ID used in parsing tests.</summary>
    private const int CurrentPid = 4242;

    /// <summary>Fake systemd notifier.</summary>
    private readonly FakeSystemdNotifier _notifier = new();

    /// <summary>Lifecycle controlled by the test.</summary>
    private readonly StubApplicationLifecycle _lifecycle = new();

    /// <summary>Clock driving the keep-alive timer.</summary>
    private readonly FakeTimeProvider _time = new();

    /// <summary>Logger capturing watchdog diagnostics.</summary>
    private readonly FakeLogger<SystemdWatchdogService> _logger = new();

    /// <summary>Verifies that a positive timeout without a PID restriction enables the watchdog with half-timeout keep-alives.</summary>
    [Fact]
    public void Parse_ValidTimeout_EnablesWatchdog()
    {
        var settings = SystemdWatchdogSettings.Parse("30000000", null, CurrentPid);

        Assert.Equal(TimeSpan.FromSeconds(30), settings.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(15), settings.PingInterval);
    }

    /// <summary>Verifies that a matching WATCHDOG_PID enables the watchdog.</summary>
    [Fact]
    public void Parse_MatchingPid_EnablesWatchdog() =>
        Assert.Equal(TimeSpan.FromSeconds(10), SystemdWatchdogSettings.Parse("10000000", "4242", CurrentPid).Timeout);

    /// <summary>Verifies the inputs for which the watchdog is disabled.</summary>
    /// <param name="usec">The WATCHDOG_USEC value.</param>
    /// <param name="pid">The WATCHDOG_PID value.</param>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("0", null)]
    [InlineData("-5", null)]
    [InlineData("abc", null)]
    [InlineData(" 1000", null)]
    [InlineData("1000000", "1")]
    [InlineData("1000000", "not-a-pid")]
    public void Parse_InvalidOrForeign_DisablesWatchdog(string? usec, string? pid)
    {
        var settings = SystemdWatchdogSettings.Parse(usec, pid, CurrentPid);

        Assert.Null(settings.Timeout);
        Assert.Null(settings.PingInterval);
    }

    /// <summary>Verifies that an enormous timeout is clamped and the keep-alive interval is capped.</summary>
    [Fact]
    public void Parse_HugeTimeout_IsClampedAndIntervalCapped()
    {
        var settings = SystemdWatchdogSettings.Parse(ulong.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), null, CurrentPid);

        Assert.Equal(TimeSpan.MaxValue, settings.Timeout);
        Assert.Equal(SystemdWatchdogSettings.MaxPingInterval, settings.PingInterval);
    }

    /// <summary>Verifies that the service completes immediately without notifying when notifications are unavailable.</summary>
    [Fact]
    public async Task Service_NotifierDisabled_SendsNothing()
    {
        _notifier.IsEnabled = false;
        using var service = CreateService(new SystemdWatchdogSettings(TimeSpan.FromSeconds(10)));

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!.WithinSafetyTimeout();

        Assert.Equal(0, _notifier.Count);
        Assert.Equal(2000, _logger.LatestRecord.Id.Id);
    }

    /// <summary>Verifies that the service completes immediately when the unit does not enable the watchdog.</summary>
    [Fact]
    public async Task Service_WatchdogDisabled_SendsNothing()
    {
        using var service = CreateService(SystemdWatchdogSettings.Disabled);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!.WithinSafetyTimeout();

        Assert.Equal(0, _notifier.Count);
    }

    /// <summary>
    /// Verifies that a keep-alive is sent immediately and then once per interval, with the WATCHDOG=1 payload, and
    /// that stopping the service ends keep-alives.
    /// </summary>
    [Fact]
    public async Task Service_Enabled_SendsKeepAlivesEachInterval()
    {
        using var service = CreateService(new SystemdWatchdogSettings(TimeSpan.FromSeconds(10)));

        await service.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal("WATCHDOG=1", (await _notifier.NextAsync()).ToString());

        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("WATCHDOG=1", (await _notifier.NextAsync()).ToString());

        _time.Advance(TimeSpan.FromSeconds(5));
        await _notifier.NextAsync();

        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(service.ExecuteTask!.IsCanceled);
        var countAfterStop = _notifier.Count;
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(countAfterStop, _notifier.Count);
    }

    /// <summary>
    /// Verifies that keep-alives continue while stopping but are suppressed once the application has failed, with a
    /// single warning.
    /// </summary>
    [Fact]
    public async Task Service_SuppressesKeepAlivesWhenFailed()
    {
        using var service = CreateService(new SystemdWatchdogSettings(TimeSpan.FromSeconds(2)));
        await service.StartAsync(TestContext.Current.CancellationToken);
        await _notifier.NextAsync();

        _lifecycle.State = ApplicationState.Stopping;
        _time.Advance(TimeSpan.FromSeconds(1));
        await _notifier.NextAsync();

        _lifecycle.State = ApplicationState.Failed;
        _time.Advance(TimeSpan.FromSeconds(1));
        await Eventually.WaitUntilAsync(() => _logger.Collector.GetSnapshot().Any(static r => r.Id.Id == 2002), "the suppression warning is logged");
        _time.Advance(TimeSpan.FromSeconds(1));
        _time.Advance(TimeSpan.FromSeconds(1));

        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, _notifier.Count);
        var warning = Assert.Single(_logger.Collector.GetSnapshot(), static r => r.Id.Id == 2002);
        Assert.Equal(LogLevel.Warning, warning.Level);
    }

    /// <summary>Verifies that a socket failure is logged and keep-alives resume at the next interval.</summary>
    [Fact]
    public async Task Service_SocketFailure_IsLoggedAndRetried()
    {
        _notifier.FailNextNotification = true;
        using var service = CreateService(new SystemdWatchdogSettings(TimeSpan.FromSeconds(2)));

        await service.StartAsync(TestContext.Current.CancellationToken);
        await Eventually.WaitUntilAsync(() => _logger.Collector.GetSnapshot().Any(static r => r.Id.Id == 2003), "the send failure is logged");
        _time.Advance(TimeSpan.FromSeconds(1));
        await _notifier.NextAsync();

        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(service.ExecuteTask!.IsFaulted);
    }

    /// <summary>Creates the service under test.</summary>
    /// <param name="settings">The watchdog settings.</param>
    /// <returns>The service.</returns>
    private SystemdWatchdogService CreateService(SystemdWatchdogSettings settings) =>
        new(_notifier, settings, _lifecycle, _time, _logger);
}
