// Tests for hosting-mode selection and the service registrations made for each mode. A real service manager is not
// available to the test process, so these tests verify the application's registrations and the framework's
// context-aware gating, not SCM or systemd behaviour itself.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Hosting;
using RTSP.IngestService.Lifecycle;
using RTSP.IngestService.Tests.Infrastructure;
using Serilog.Extensions.Logging;

namespace RTSP.IngestService.Tests.Hosting;

/// <summary>
/// Tests for <see cref="HostingModeDetector"/>, <see cref="PlatformHostingExtensions"/> and the composition
/// root's logging registration.
/// </summary>
public sealed class HostingRegistrationTests
{
    /// <summary>Type name of the framework's internal console lifetime.</summary>
    private const string ConsoleLifetimeTypeName = "Microsoft.Extensions.Hosting.Internal.ConsoleLifetime";

    /// <summary>Captures log records from hosts built by these tests.</summary>
    private readonly FakeLogCollector _logs = new();

    /// <summary>Verifies the hosting-mode precedence: Windows Service, then systemd, then console.</summary>
    /// <param name="isWindowsService">Simulated Windows Service detection result.</param>
    /// <param name="isSystemdService">Simulated systemd detection result.</param>
    /// <param name="expected">The expected mode.</param>
    [Theory]
    [InlineData(false, false, HostingMode.Console)]
    [InlineData(true, false, HostingMode.WindowsService)]
    [InlineData(false, true, HostingMode.Systemd)]
    [InlineData(true, true, HostingMode.WindowsService)]
    public void Select_AppliesPrecedence(bool isWindowsService, bool isSystemdService, HostingMode expected) =>
        Assert.Equal(expected, HostingModeDetector.Select(isWindowsService, isSystemdService));

    /// <summary>
    /// Verifies that the test process, which is started by a test runner rather than a service manager, is detected
    /// as a console application.
    /// </summary>
    [Fact]
    public void Detect_InTestProcess_IsConsole() => Assert.Equal(HostingMode.Console, HostingModeDetector.Detect());

    /// <summary>Verifies that console mode uses the framework console lifetime and registers no watchdog.</summary>
    [Fact]
    public void ConsoleMode_UsesConsoleLifetime()
    {
        using var host = TestHosts.CreateBuilder(_logs, HostingMode.Console).Build();

        Assert.Equal(ConsoleLifetimeTypeName, host.Services.GetRequiredService<IHostLifetime>().GetType().FullName);
        Assert.DoesNotContain(host.Services.GetServices<IHostedService>(), static s => s is SystemdWatchdogService);
    }

    /// <summary>
    /// Verifies the systemd registrations: the watchdog service is registered after the lifecycle coordinator, a
    /// notifier is always resolvable (disabled without <c>NOTIFY_SOCKET</c>), and the framework installs
    /// <see cref="SystemdLifetime"/> only when the process really is a systemd service.
    /// </summary>
    [Fact]
    public void SystemdMode_RegistersWatchdogAndFrameworkGatedLifetime()
    {
        using var host = TestHosts.CreateBuilder(_logs, HostingMode.Systemd).Build();

        var hostedServices = host.Services.GetServices<IHostedService>().ToArray();
        Assert.IsType<ApplicationLifecycleCoordinator>(hostedServices[0]);
        Assert.IsType<SystemdWatchdogService>(hostedServices[1]);

        var notifier = host.Services.GetRequiredService<ISystemdNotifier>();
        Assert.Equal(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NOTIFY_SOCKET")), notifier.IsEnabled);

        var lifetime = host.Services.GetRequiredService<IHostLifetime>();
        if (SystemdHelpers.IsSystemdService())
        {
            Assert.IsType<SystemdLifetime>(lifetime);
        }
        else
        {
            Assert.Equal(ConsoleLifetimeTypeName, lifetime.GetType().FullName);
        }
    }

    /// <summary>
    /// Verifies that a host composed for systemd starts and stops cleanly outside systemd, with the watchdog
    /// disabled, and reaches Stopped.
    /// </summary>
    [Fact]
    public async Task SystemdMode_OutsideSystemd_StartsAndStops()
    {
        using var host = TestHosts.CreateBuilder(_logs, HostingMode.Systemd).Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await Eventually.WaitUntilAsync(() => _logs.GetSnapshot().Any(static r => r.Id.Id == 2000), "the watchdog reports that it is disabled");
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ApplicationState.Stopped, host.Services.GetRequiredService<IApplicationLifecycle>().State);
        Assert.Single(_logs.GetSnapshot(), static r => r.Id.Id == 2000);
    }

    /// <summary>
    /// Verifies the Windows Service registrations outside SCM: the service name comes from configuration and the
    /// framework leaves the console lifetime in place because the process was not started by SCM.
    /// </summary>
    [Fact]
    public void WindowsServiceMode_OutsideScm_KeepsConsoleLifetimeAndSetsServiceName()
    {
        using var host = TestHosts.CreateBuilder(
            _logs,
            HostingMode.WindowsService,
            new Dictionary<string, string?> { ["Application:Name"] = "IngestEdge" }).Build();

        Assert.Equal("IngestEdge", host.Services.GetRequiredService<IOptions<WindowsServiceLifetimeOptions>>().Value.ServiceName);
        Assert.Equal(ConsoleLifetimeTypeName, host.Services.GetRequiredService<IHostLifetime>().GetType().FullName);
    }

    /// <summary>
    /// Verifies that the framework's <see cref="WindowsServiceLifetime"/> registration, as made by
    /// <c>AddWindowsService</c> under SCM, is replaced by <see cref="WindowsServiceControlLifetime"/> and that the
    /// replacement resolves with its dependencies. Windows only.
    /// </summary>
    [Fact]
    public void ReplaceWindowsServiceLifetime_ReplacesFrameworkLifetime()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("WindowsServiceLifetime can only be constructed on Windows.");
            return;
        }

        var builder = TestHosts.CreateBuilder(_logs, HostingMode.Console);
        builder.Services.AddSingleton<IHostLifetime, WindowsServiceLifetime>();

        Assert.True(PlatformHostingExtensions.ReplaceWindowsServiceLifetime(builder.Services));
        Assert.DoesNotContain(builder.Services, static d => d.ImplementationType == typeof(WindowsServiceLifetime));

        using var host = builder.Build();
        Assert.IsType<WindowsServiceControlLifetime>(host.Services.GetRequiredService<IHostLifetime>());
    }

    /// <summary>Verifies that replacement is a no-op when the framework did not register its lifetime.</summary>
    [Fact]
    public void ReplaceWindowsServiceLifetime_WithoutFrameworkLifetime_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The replacement API is Windows-specific.");
            return;
        }

        var services = new ServiceCollection();

        Assert.False(PlatformHostingExtensions.ReplaceWindowsServiceLifetime(services));
        Assert.Empty(services);
    }

    /// <summary>
    /// Verifies the SCM stop wait hint: the shutdown timeout plus the disposal margin, clamped to the maximum
    /// representable value.
    /// </summary>
    [Fact]
    public void StopWaitHint_IsShutdownTimeoutPlusMargin()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("WindowsServiceControlLifetime is Windows-specific.");
            return;
        }

        Assert.Equal(35_000, WindowsServiceControlLifetime.GetStopWaitHintMilliseconds(TimeSpan.FromSeconds(30)));
        Assert.Equal(int.MaxValue, WindowsServiceControlLifetime.GetStopWaitHintMilliseconds(TimeSpan.FromDays(30)));
    }

    /// <summary>
    /// Verifies that, in every hosting mode, the composition root leaves exactly one logging provider: Serilog. In
    /// particular, the Event Log provider that <c>AddWindowsService</c> adds under SCM is removed.
    /// </summary>
    /// <param name="mode">The hosting mode.</param>
    [Theory]
    [InlineData(HostingMode.Console)]
    [InlineData(HostingMode.WindowsService)]
    [InlineData(HostingMode.Systemd)]
    public void Logging_UsesSingleSerilogProvider(HostingMode mode)
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), "rtsp-ingest-service-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var builder = IngestServiceHost.CreateBuilder(
                [.. TestHosts.Arguments, $"--FileLogging:Path={Path.Combine(logDirectory, "test-.log")}"],
                mode);

            using (var host = builder.Build())
            {
                Assert.IsType<SerilogLoggerProvider>(Assert.Single(host.Services.GetServices<ILoggerProvider>()));
            }
        }
        finally
        {
            if (Directory.Exists(logDirectory))
            {
                Directory.Delete(logDirectory, recursive: true);
            }
        }
    }

    /// <summary>Verifies that an undefined hosting mode is rejected.</summary>
    [Fact]
    public void AddPlatformHosting_UndefinedMode_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddPlatformHosting((HostingMode)99));
}
