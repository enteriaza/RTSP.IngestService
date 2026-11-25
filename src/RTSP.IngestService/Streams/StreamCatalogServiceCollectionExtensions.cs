// Registers the stream catalog, its reconciliation service and the catalog as the stream event consumer. The sources
// are resolved optionally when the catalog is first resolved, so compositions without an RTMP, media or HLS stage
// still get a catalog (reporting only what the registered sources hold).

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtmp.Publishing;

namespace RTSP.IngestService.Streams;

/// <summary>Dependency-injection registration for the stream catalog.</summary>
public static class StreamCatalogServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="StreamCatalog"/>, <see cref="StreamCatalogService"/> as a hosted service, and the catalog
    /// as the <see cref="IStreamEventConsumer"/> unless another consumer is already registered.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <remarks>
    /// Requires <see cref="StreamCatalogOptions"/> (see
    /// <see cref="IngestServiceOptionsExtensions.AddIngestServiceOptions"/>). Call after
    /// <see cref="StreamEventServiceCollectionExtensions.AddStreamEvents"/> and before the HTTP origin and the RTMP
    /// listener, so the startup reconciliation completes before either starts.
    /// </remarks>
    public static IServiceCollection AddStreamCatalog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();
        services.TryAddSingleton<StreamCatalogMetrics>();
        services.TryAddSingleton<IStreamInventorySource>(static sp => new RuntimeStreamInventorySource(
            sp.GetService<PublishedStreamRegistry>(),
            sp.GetService<IPublishSessionHandler>() as MediaPublishSessionHandler,
            sp.GetService<IHlsSegmentStore>() as IHlsStreamInventory));
        services.TryAddSingleton(static sp =>
        {
            var channel = sp.GetService<StreamEventChannel>();
            return new StreamCatalog(
                sp.GetRequiredService<IStreamInventorySource>(),
                sp.GetRequiredService<StreamCatalogMetrics>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<StreamCatalog>>(),
                channel is null ? null : () => channel.DroppedCount);
        });
        services.TryAddSingleton<IStreamEventConsumer>(static sp => sp.GetRequiredService<StreamCatalog>());
        services.TryAddSingleton(static sp => new StreamCatalogService(
            sp.GetRequiredService<StreamCatalog>(),
            sp.GetRequiredService<IOptions<StreamCatalogOptions>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService(static sp => sp.GetRequiredService<StreamCatalogService>());

        return services;
    }
}
