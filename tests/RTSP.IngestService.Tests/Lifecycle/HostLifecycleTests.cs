// Integration-style tests of the Generic Host lifecycle as composed by IngestServiceHost: lifecycle states and
// readiness at each phase, hosted-service start/stop ordering, cancellation propagation, shutdown timeout handling
// and background-service fault handling.

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using RTSP.IngestService.Lifecycle;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Lifecycle;

/// <summary>
/// Tests for <see cref="ApplicationLifecycleCoordinator"/> running inside a real host.
/// </summary>
public sealed class HostLifecycleTests
{
    /// <summary>Configuration that minimises the shutdown timeout so timeout tests complete quickly.</summary>
    private static readonly Dictionary<string, string?> ShortShutdown = new() { ["Lifetime:ShutdownTimeout"] = "00:00:01" };

    /// <summary>Captures every log record written by the host under test.</summary>
    private readonly FakeLogCollector _logs = new();

    /// <summary>Journal shared with the test hosted services.</summary>
    private readonly LifecycleJournal _journal = new();

    /// <summary>Verifies that a built but unstarted host reports NotStarted and is not ready.</summary>
    [Fact]
    public void BuiltHost_IsNotStartedAndNotReady()
    {
        using var host = TestHosts.CreateBuilder(_logs).Build();
        var lifecycle = host.Services.GetRequiredService<IApplicationLifecycle>();

        Assert.Equal(ApplicationState.NotStarted, lifecycle.State);
        Assert.False(lifecycle.IsReady);
    }

    /// <summary>
    /// Verifies that hosted services observe Starting and no readiness while starting, that the application is
    /// Running and ready once StartAsync returns, and that services observe Stopping and no readiness while stopping.
    /// </summary>
    [Fact]
    public async Task Readiness_ReflectsEachLifecyclePhase()
    {
        using var host = BuildHost(static services => services.AddHostedService<RecordingServiceA>());
        var lifecycle = host.Services.GetRequiredService<IApplicationLifecycle>();

        await host.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationState.Running, lifecycle.State);
        Assert.True(lifecycle.IsReady);

        await host.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        Assert.False(lifecycle.IsReady);

        Assert.Equal(["A:start:Starting:False", "A:stop:Stopping:False"], _journal.Events);
    }

    /// <summary>
    /// Verifies that a normal start and stop logs exactly the transitions Starting, Running, Stopping, Stopped.
    /// </summary>
    [Fact]
    public async Task StartAndStop_LogsCompleteTransitionSequence()
    {
        using var host = BuildHost();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        var states = _logs.GetSnapshot()
            .Where(static r => r.Category == typeof(ApplicationLifecycle).FullName && r.Level == LogLevel.Information)
            .Select(static r => r.GetStructuredStateValue("State"));
        Assert.Equal(["Starting", "Running", "Stopping", "Stopped"], states);
    }

    /// <summary>
    /// Verifies that hosted services start in registration order and stop in reverse order, and that the lifecycle
    /// coordinator is the first hosted service regardless of registration order.
    /// </summary>
    [Fact]
    public async Task HostedServices_StartInOrderAndStopInReverse()
    {
        using var host = BuildHost(static services =>
        {
            services.AddHostedService<RecordingServiceA>();
            services.AddHostedService<RecordingServiceB>();
            services.AddHostedService<RecordingServiceC>();
        });

        Assert.IsType<ApplicationLifecycleCoordinator>(host.Services.GetServices<IHostedService>().First());

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        var order = _journal.Events.Select(static e => string.Join(':', e.Split(':')[..2]));
        Assert.Equal(["A:start", "B:start", "C:start", "C:stop", "B:stop", "A:stop"], order);
    }

    /// <summary>
    /// Verifies that stopping the host cancels a background service's stopping token and waits for it to finish.
    /// </summary>
    [Fact]
    public async Task Stop_PropagatesCancellationToBackgroundServices()
    {
        using var host = BuildHost(static services => services.AddHostedService<CancellationObservingService>());

        await host.StartAsync(TestContext.Current.CancellationToken);
        await Eventually.WaitUntilAsync(() => _journal.Events.Contains("Observer:executing"), "the background service is executing");
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Observer:executing", "Observer:cancelled"], _journal.Events);
        Assert.Equal(ApplicationState.Stopped, host.Services.GetRequiredService<IApplicationLifecycle>().State);
    }

    /// <summary>
    /// Verifies that when a hosted service does not stop, the shutdown timeout cancels its stop token, the host
    /// finishes stopping after roughly the configured timeout, and the lifecycle still reaches Stopped.
    /// </summary>
    [Fact]
    public async Task ShutdownTimeout_CancelsStopTokenAndCompletesShutdown()
    {
        using var host = BuildHost(static services => services.AddHostedService<BlockingStopService>(), ShortShutdown);

        await host.StartAsync(TestContext.Current.CancellationToken);
        var stopwatch = Stopwatch.StartNew();
        await host.StopAsync(CancellationToken.None).WithinSafetyTimeout();
        stopwatch.Stop();

        Assert.Contains("BlockingStop:stop-token-cancelled", _journal.Events);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(900), Eventually.SafetyTimeout);
        Assert.Equal(ApplicationState.Stopped, host.Services.GetRequiredService<IApplicationLifecycle>().State);
    }

    /// <summary>
    /// Verifies that a background service which ignores cancellation is abandoned at the shutdown timeout, a warning
    /// naming it is logged, and the lifecycle reaches Stopped rather than Failed.
    /// </summary>
    [Fact]
    public async Task ShutdownTimeout_AbandonedBackgroundServiceIsLogged()
    {
        using var host = BuildHost(
            static services =>
            {
                services.AddSingleton<UncooperativeBackgroundService>();
                services.AddHostedService(static sp => sp.GetRequiredService<UncooperativeBackgroundService>());
            },
            ShortShutdown);
        var service = host.Services.GetRequiredService<UncooperativeBackgroundService>();

        try
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            await service.ExecutionStarted.WithinSafetyTimeout();
            await host.StopAsync(CancellationToken.None).WithinSafetyTimeout();

            // Once the timeout has expired the host stops the remaining background services without waiting for them,
            // so under load a production service may legitimately be reported as abandoned as well.
            var warning = Assert.Single(
                _logs.GetSnapshot(),
                static r => r.Id.Id == 1100 && r.GetStructuredStateValue("ServiceType") == typeof(UncooperativeBackgroundService).FullName);
            Assert.Equal(LogLevel.Warning, warning.Level);
            Assert.Equal(ApplicationState.Stopped, host.Services.GetRequiredService<IApplicationLifecycle>().State);
        }
        finally
        {
            service.Release();
        }
    }

    /// <summary>
    /// Verifies that a background service fault stops the host, withdraws readiness, and leaves the lifecycle Failed
    /// with the fault recorded.
    /// </summary>
    [Fact]
    public async Task BackgroundServiceFault_StopsHostAndFailsLifecycle()
    {
        using var host = BuildHost(static services => services.AddHostedService<FaultingBackgroundService>());
        var lifecycle = host.Services.GetRequiredService<IApplicationLifecycle>();

        await host.StartAsync(TestContext.Current.CancellationToken);

        // Microsoft.Extensions.Hosting 11 rethrows a faulted background service from StopAsync, and therefore from
        // WaitForShutdownAsync; ServiceRunner logs that as a shutdown failure.
        var stopFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.WaitForShutdownAsync(TestContext.Current.CancellationToken).WithinSafetyTimeout());
        Assert.Equal(FaultingBackgroundService.FailureMessage, stopFailure.Message);

        Assert.Equal(ApplicationState.Failed, lifecycle.State);
        Assert.False(lifecycle.IsReady);
        var failure = Assert.IsType<InvalidOperationException>(lifecycle.Failure);
        Assert.Equal(FaultingBackgroundService.FailureMessage, failure.Message);
    }

    /// <summary>
    /// Verifies that registering the lifecycle twice does not add a second coordinator.
    /// </summary>
    [Fact]
    public void AddApplicationLifecycle_IsIdempotent()
    {
        var services = new ServiceCollection();

        services.AddApplicationLifecycle();
        services.AddApplicationLifecycle();

        Assert.Single(services, static d => d.ImplementationType == typeof(ApplicationLifecycleCoordinator));
        Assert.Single(services, static d => d.ServiceType == typeof(ApplicationLifecycle));
    }

    /// <summary>Builds a host with the journal registered and optional extra services and configuration.</summary>
    /// <param name="configureServices">Adds test services.</param>
    /// <param name="overrides">Configuration overrides.</param>
    /// <returns>The built host.</returns>
    private IHost BuildHost(Action<IServiceCollection>? configureServices = null, IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var builder = TestHosts.CreateBuilder(_logs, overrides: overrides);
        builder.Services.AddSingleton(_journal);
        configureServices?.Invoke(builder.Services);
        return builder.Build();
    }
}
