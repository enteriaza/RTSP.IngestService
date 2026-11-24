// End-to-end tests of ServiceRunner: every startup and shutdown outcome is run through the real runner and asserted by
// exit code, final lifecycle state and logged events.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Hosting;
using RTSP.IngestService.Lifecycle;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Hosting;

/// <summary>
/// Tests for <see cref="ServiceRunner"/>.
/// </summary>
public sealed class ServiceRunnerTests
{
    /// <summary>Captures log records from runs that use fake logging.</summary>
    private readonly FakeLogCollector _logs = new();

    /// <summary>Journal shared with the test hosted services.</summary>
    private readonly LifecycleJournal _journal = new();

    /// <summary>
    /// Verifies that a run stopped through the cancellation token after reaching Running exits with code 0, ends in
    /// Stopped and logs the termination event.
    /// </summary>
    [Fact]
    public async Task GracefulShutdown_ReturnsSuccess()
    {
        using var shutdown = new CancellationTokenSource();
        var run = RunAsync(services => services.AddHostedService<RecordingServiceA>(), cancellationToken: shutdown.Token);

        await Eventually.WaitUntilAsync(() => _journal.Lifecycle?.IsReady == true, "the application is ready");
        await shutdown.CancelAsync();
        var exitCode = await run.WithinSafetyTimeout();

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(ApplicationState.Stopped, _journal.Lifecycle!.State);
        var terminated = Assert.Single(_logs.GetSnapshot(), static r => r.Id.Id == 4005);
        Assert.Equal("Stopped", terminated.GetStructuredStateValue("State"));
        Assert.Equal("0", terminated.GetStructuredStateValue("ExitCode"));
    }

    /// <summary>
    /// Verifies that a hosted service failing in StartAsync produces exit code 1 and a Failed lifecycle, that a
    /// service which started before it is still stopped, and that a critical startup failure is logged.
    /// </summary>
    [Fact]
    public async Task StartupFailure_ReturnsFailureAndStopsStartedServices()
    {
        var exitCode = await RunAsync(static services =>
        {
            services.AddHostedService<RecordingServiceA>();
            services.AddHostedService<FailingStartService>();
        }).WithinSafetyTimeout();

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Equal(ApplicationState.Failed, _journal.Lifecycle!.State);
        Assert.Equal(FailingStartService.FailureMessage, _journal.Lifecycle.Failure!.Message);
        Assert.Contains("A:stop:Failed:False", _journal.Events);
        var critical = Assert.Single(_logs.GetSnapshot(), static r => r.Id.Id == 4003);
        Assert.Equal(LogLevel.Critical, critical.Level);
        Assert.IsType<InvalidOperationException>(critical.Exception);
    }

    /// <summary>
    /// Verifies that invalid configuration detected by startup validation produces exit code 78 before any hosted
    /// service starts.
    /// </summary>
    /// <param name="key">The configuration key to override.</param>
    /// <param name="value">An invalid value for <paramref name="key"/>.</param>
    [Theory]
    [InlineData("Application:Name", "")]
    [InlineData("Lifetime:StartupTimeout", "00:00:00")]
    [InlineData("FileLogging:Mode", "Sometimes")]
    [InlineData("Lifetime:Unexpected", "1")]
    public async Task InvalidConfiguration_ReturnsConfigurationError(string key, string value)
    {
        var exitCode = await RunAsync(
            static services => services.AddHostedService<RecordingServiceA>(),
            new Dictionary<string, string?> { [key] = value }).WithinSafetyTimeout();

        Assert.Equal(ExitCodes.ConfigurationError, exitCode);
        Assert.DoesNotContain(_journal.Events, static e => e.StartsWith("A:start", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies that invalid lifetime configuration, which is resolved while the host is built, is reported through
    /// the bootstrap path with exit code 78 when the production Serilog pipeline is in use.
    /// </summary>
    [Fact]
    public async Task InvalidConfigurationDuringBuild_ReturnsConfigurationError()
    {
        var exitCode = await ServiceRunner.RunAsync(
            [.. TestHosts.Arguments, "--Lifetime:ShutdownTimeout=00:00:00"],
            HostingMode.Console,
            configure: null,
            TestContext.Current.CancellationToken).WithinSafetyTimeout();

        Assert.Equal(ExitCodes.ConfigurationError, exitCode);
    }

    /// <summary>
    /// Verifies that a background service fault after startup stops the host with exit code 1 and a Failed lifecycle.
    /// </summary>
    [Fact]
    public async Task BackgroundServiceFault_ReturnsFailure()
    {
        var exitCode = await RunAsync(static services =>
        {
            services.AddHostedService<RecordingServiceA>();
            services.AddHostedService<FaultingBackgroundService>();
        }).WithinSafetyTimeout();

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Equal(ApplicationState.Failed, _journal.Lifecycle!.State);
        Assert.Equal(FaultingBackgroundService.FailureMessage, _journal.Lifecycle.Failure!.Message);
    }

    /// <summary>
    /// Verifies that a shutdown request received while a hosted service is still starting cancels startup, is
    /// treated as a graceful stop (exit code 0, Stopped), and is logged as a cancelled startup.
    /// </summary>
    [Fact]
    public async Task ShutdownRequestDuringStartup_ReturnsSuccess()
    {
        var exitCode = await RunAsync(static services =>
        {
            services.AddSingleton(new BlockingStartSettings(RequestStopDuringStart: true));
            services.AddHostedService<BlockingStartService>();
        }).WithinSafetyTimeout();

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(ApplicationState.Stopped, _journal.Lifecycle!.State);
        Assert.Single(_logs.GetSnapshot(), static r => r.Id.Id == 4002);
    }

    /// <summary>
    /// Verifies that cancelling the runner's token while a hosted service is still starting is treated as a graceful
    /// stop.
    /// </summary>
    [Fact]
    public async Task CancellationDuringStartup_ReturnsSuccess()
    {
        using var shutdown = new CancellationTokenSource();
        var run = RunAsync(
            static services =>
            {
                services.AddSingleton(new BlockingStartSettings(RequestStopDuringStart: false));
                services.AddHostedService<BlockingStartService>();
            },
            cancellationToken: shutdown.Token);

        await Eventually.WaitUntilAsync(() => _journal.Events.Any(static e => e.StartsWith("BlockingStart:start", StringComparison.Ordinal)), "startup is blocked");
        await shutdown.CancelAsync();

        Assert.Equal(ExitCodes.Success, await run.WithinSafetyTimeout());
        Assert.Equal(ApplicationState.Stopped, _journal.Lifecycle!.State);
    }

    /// <summary>
    /// Verifies that startup exceeding the configured startup timeout fails with exit code 1 and records a
    /// <see cref="TimeoutException"/>, even though the host also calls StopApplication when the already-started
    /// heartbeat background service is cancelled by the timeout.
    /// </summary>
    [Fact]
    public async Task StartupTimeout_ReturnsFailure()
    {
        var time = new FakeTimeProvider();
        var run = RunAsync(
            services =>
            {
                services.AddSingleton<TimeProvider>(time);
                services.AddSingleton(new BlockingStartSettings(RequestStopDuringStart: false));
                services.AddHostedService<BlockingStartService>();
            },
            new Dictionary<string, string?> { ["Lifetime:StartupTimeout"] = "00:00:05" });

        await Eventually.WaitUntilAsync(() => _journal.Events.Any(static e => e.StartsWith("BlockingStart:start", StringComparison.Ordinal)), "startup is blocked");
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.False(run.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        var exitCode = await run.WithinSafetyTimeout();

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Equal(ApplicationState.Failed, _journal.Lifecycle!.State);
        Assert.IsType<TimeoutException>(_journal.Lifecycle.Failure);
        Assert.Single(_logs.GetSnapshot(), static r => r.Id.Id == 4003);
        Assert.DoesNotContain(_logs.GetSnapshot(), static r => r.Id.Id == 4002);
    }

    /// <summary>Runs the service through <see cref="ServiceRunner"/> with fake logging and the journal registered.</summary>
    /// <param name="configureServices">Adds test services.</param>
    /// <param name="overrides">Configuration overrides.</param>
    /// <param name="cancellationToken">Requests shutdown when cancelled; defaults to the test cancellation token.</param>
    /// <returns>The exit code.</returns>
    private Task<int> RunAsync(
        Action<IServiceCollection> configureServices,
        IReadOnlyDictionary<string, string?>? overrides = null,
        CancellationToken? cancellationToken = null)
    {
        var configure = TestHosts.Configure(_logs, overrides);
        return ServiceRunner.RunAsync(
            TestHosts.Arguments,
            HostingMode.Console,
            builder =>
            {
                configure(builder);
                builder.Services.AddSingleton(_journal);
                configureServices(builder.Services);
            },
            cancellationToken ?? TestContext.Current.CancellationToken);
    }
}
