// Host-level registrations: maps LifetimeOptions onto HostOptions and wires the platform service-manager integration
// for the selected hosting mode using the framework's AddWindowsService and AddSystemd extensions.

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;

namespace RTSP.IngestService.Hosting;

/// <summary>
/// Dependency-injection registration for host options and platform service-manager integration.
/// </summary>
public static class PlatformHostingExtensions
{
    /// <summary>
    /// Configures <see cref="HostOptions"/> from <see cref="LifetimeOptions"/>: the shutdown timeout, sequential
    /// start and stop (registration order and its reverse), and
    /// <see cref="BackgroundServiceExceptionBehavior.StopHost"/> so that a faulted background service stops the host.
    /// </summary>
    /// <remarks>
    /// <see cref="HostOptions.StartupTimeout"/> is left infinite because <see cref="ServiceRunner"/> enforces
    /// <see cref="LifetimeOptions.StartupTimeout"/> itself, which lets it distinguish a timeout from a shutdown request.
    /// </remarks>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <remarks>
    /// <see cref="HostOptions"/> is resolved when the host is built, so invalid <see cref="LifetimeOptions"/> fail
    /// <c>HostApplicationBuilder.Build</c> with an <see cref="OptionsValidationException"/>.
    /// </remarks>
    public static IServiceCollection AddIngestServiceHostOptions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<HostOptions>()
            .Configure<IOptions<LifetimeOptions>>(static (host, lifetime) =>
            {
                host.ShutdownTimeout = lifetime.Value.ShutdownTimeout;
                host.ServicesStartConcurrently = false;
                host.ServicesStopConcurrently = false;
                host.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
            });

        return services;
    }

    /// <summary>
    /// Registers the service-manager integration for <paramref name="mode"/>.
    /// </summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <param name="mode">The hosting mode selected for this process.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="HostingMode.Console"/>: no changes; the host's default console lifetime handles Ctrl+C, SIGINT,
    /// SIGQUIT and SIGTERM.
    /// </para>
    /// <para>
    /// <see cref="HostingMode.WindowsService"/>: calls <c>AddWindowsService</c>, sets the service name to
    /// <see cref="ApplicationIdentity.WindowsServiceName"/> and replaces the framework lifetime with
    /// <see cref="WindowsServiceControlLifetime"/>. The framework only registers its lifetime when the process was
    /// actually started by SCM; otherwise the console lifetime remains.
    /// </para>
    /// <para>
    /// <see cref="HostingMode.Systemd"/>: calls <c>AddSystemd</c> (Type=notify readiness and stopping notifications,
    /// SIGTERM handling) and registers <see cref="SystemdWatchdogService"/>. The framework only registers
    /// <see cref="SystemdLifetime"/> when the process was actually started by systemd; a disabled
    /// <see cref="SystemdNotifier"/> is registered as a fallback so the watchdog service can always be constructed.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddPlatformHosting(this IServiceCollection services, HostingMode mode)
    {
        ArgumentNullException.ThrowIfNull(services);

        switch (mode)
        {
            case HostingMode.WindowsService:
                services.AddWindowsService();
                services.Configure<WindowsServiceLifetimeOptions>(static windows => windows.ServiceName = ApplicationIdentity.WindowsServiceName);
                if (OperatingSystem.IsWindows())
                {
                    ReplaceWindowsServiceLifetime(services);
                }

                break;

            case HostingMode.Systemd:
                services.AddSystemd();
                services.TryAddSingleton<ISystemdNotifier, SystemdNotifier>();
                services.TryAddSingleton(static _ => SystemdWatchdogSettings.FromEnvironment());
                services.AddHostedService<SystemdWatchdogService>();
                break;

            case HostingMode.Console:
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported hosting mode.");
        }

        return services;
    }

    /// <summary>
    /// Replaces a framework <see cref="WindowsServiceLifetime"/> registration with
    /// <see cref="WindowsServiceControlLifetime"/>. Does nothing when no such registration exists.
    /// </summary>
    /// <param name="services">The service collection to modify.</param>
    /// <returns><see langword="true"/> if a registration was replaced.</returns>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static bool ReplaceWindowsServiceLifetime(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        for (var i = 0; i < services.Count; i++)
        {
            if (services[i].ServiceType == typeof(IHostLifetime) && services[i].ImplementationType == typeof(WindowsServiceLifetime))
            {
                services[i] = ServiceDescriptor.Singleton<IHostLifetime, WindowsServiceControlLifetime>();
                return true;
            }
        }

        return false;
    }
}
