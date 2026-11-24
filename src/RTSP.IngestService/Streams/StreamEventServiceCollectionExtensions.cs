// Registers the stream lifecycle event channel and its dispatcher. Producers resolve IStreamEventPublisher
// optionally, so a composition without this registration publishes nothing and is otherwise unchanged.

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;

namespace RTSP.IngestService.Streams;

/// <summary>Dependency-injection registration for stream lifecycle events.</summary>
public static class StreamEventServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="StreamEventChannel"/> as the <see cref="IStreamEventPublisher"/> and
    /// <see cref="StreamEventDispatcher"/> as a hosted service delivering to the <see cref="IStreamEventConsumer"/>, if
    /// one is registered.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <remarks>
    /// Requires <see cref="StreamEventOptions"/> (see
    /// <see cref="IngestServiceOptionsExtensions.AddIngestServiceOptions"/>). Call before the HTTP origin and the RTMP
    /// listener, so the dispatcher starts before any publication and stops after both.
    /// </remarks>
    public static IServiceCollection AddStreamEvents(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();
        services.TryAddSingleton<StreamEventMetrics>();
        services.TryAddSingleton(static sp => new StreamEventChannel(
            sp.GetRequiredService<IOptions<StreamEventOptions>>(),
            sp.GetRequiredService<StreamEventMetrics>()));
        services.TryAddSingleton<IStreamEventPublisher>(static sp => sp.GetRequiredService<StreamEventChannel>());
        services.TryAddSingleton(static sp => new StreamEventDispatcher(
            sp.GetRequiredService<StreamEventChannel>(),
            sp.GetService<IStreamEventConsumer>(),
            sp.GetRequiredService<StreamEventMetrics>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<StreamEventDispatcher>>()));
        services.AddHostedService(static sp => sp.GetRequiredService<StreamEventDispatcher>());

        return services;
    }
}
