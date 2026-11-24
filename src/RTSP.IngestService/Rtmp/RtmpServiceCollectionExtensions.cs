// Registers the RTMP ingest server. Authentication and the publish session handler are registered with TryAdd, so a
// host that registers its own IPublisherAuthenticator or IPublishSessionHandler first replaces the defaults without
// touching the protocol implementation.

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Rtmp.Publishing;

namespace RTSP.IngestService.Rtmp;

/// <summary>
/// Dependency-injection registration for RTMP ingest.
/// </summary>
public static class RtmpServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="RtmpListener"/> as a singleton and hosted service, with its metrics, the
    /// <see cref="PublishedStreamRegistry"/>, <see cref="ConfiguredPublisherAuthenticator"/> as the default
    /// <see cref="IPublisherAuthenticator"/> and <see cref="DiscardingPublishSessionHandler"/> as the default
    /// <see cref="IPublishSessionHandler"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <remarks>
    /// Requires <see cref="Configuration.RtmpOptions"/> to be registered (see
    /// <see cref="Configuration.IngestServiceOptionsExtensions.AddIngestServiceOptions"/>). Register after the
    /// other hosted services so that the listener starts last and stops first.
    /// </remarks>
    public static IServiceCollection AddRtmpIngest(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();
        services.TryAddSingleton<RtmpMetrics>();
        services.TryAddSingleton<PublishedStreamRegistry>();
        services.TryAddSingleton<IPublisherAuthenticator, ConfiguredPublisherAuthenticator>();
        services.TryAddSingleton<IPublishSessionHandler, DiscardingPublishSessionHandler>();
        services.TryAddSingleton(static sp => new RtmpListener(
            sp.GetRequiredService<IOptions<RtmpOptions>>(),
            sp.GetRequiredService<IPublisherAuthenticator>(),
            sp.GetRequiredService<IPublishSessionHandler>(),
            sp.GetRequiredService<PublishedStreamRegistry>(),
            sp.GetRequiredService<RtmpMetrics>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>()));
        services.AddHostedService(static sp => sp.GetRequiredService<RtmpListener>());

        return services;
    }
}
