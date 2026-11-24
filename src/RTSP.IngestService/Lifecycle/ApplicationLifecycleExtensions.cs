// Registers the application lifecycle state machine, its read-only view and the coordinator hosted service. The
// coordinator is inserted ahead of every other hosted service so that it observes the full start and stop sequence.

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace RTSP.IngestService.Lifecycle;

/// <summary>
/// Dependency-injection registration for the application lifecycle.
/// </summary>
public static class ApplicationLifecycleExtensions
{
    /// <summary>
    /// Registers <see cref="ApplicationLifecycle"/> and <see cref="IApplicationLifecycle"/> as the same singleton,
    /// <see cref="TimeProvider.System"/> if no <see cref="TimeProvider"/> is registered, and
    /// <see cref="ApplicationLifecycleCoordinator"/> as the first <see cref="IHostedService"/>.
    /// </summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <remarks>
    /// The host starts hosted services in registration order and stops them in reverse, so inserting the coordinator
    /// at the front holds regardless of when other hosted services are registered. Calling this method more than once
    /// has no further effect.
    /// </remarks>
    public static IServiceCollection AddApplicationLifecycle(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(static d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ApplicationLifecycleCoordinator)))
        {
            return services;
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ApplicationLifecycle>();
        services.AddSingleton<IApplicationLifecycle>(static sp => sp.GetRequiredService<ApplicationLifecycle>());

        services.Insert(0, ServiceDescriptor.Singleton<IHostedService, ApplicationLifecycleCoordinator>());

        return services;
    }
}
