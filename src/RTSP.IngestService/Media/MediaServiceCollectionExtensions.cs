// Registers media processing as the RTMP publish session handler. The downstream IMediaSessionHandler is registered
// with TryAdd, so a host that registers its own consumer first replaces the discarding default; a host that
// registers its own IPublishSessionHandler first bypasses media processing altogether.

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Streams;

namespace RTSP.IngestService.Media;

/// <summary>
/// Dependency-injection registration for media processing.
/// </summary>
public static class MediaServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="MediaPublishSessionHandler"/> as the default <see cref="IPublishSessionHandler"/>, with
    /// its metrics and <see cref="DiscardingMediaSessionHandler"/> as the default <see cref="IMediaSessionHandler"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <remarks>
    /// Requires <see cref="MediaOptions"/> to be registered (see
    /// <see cref="IngestServiceOptionsExtensions.AddIngestServiceOptions"/>). Call before
    /// <see cref="Rtmp.RtmpServiceCollectionExtensions.AddRtmpIngest"/>, whose discarding handler is only a fallback.
    /// </remarks>
    public static IServiceCollection AddMediaProcessing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMetrics();
        services.TryAddSingleton<MediaMetrics>();
        services.TryAddSingleton<IMediaSessionHandler, DiscardingMediaSessionHandler>();
        services.TryAddSingleton(static sp => new MediaPublishSessionHandler(
            sp.GetRequiredService<IOptions<MediaOptions>>(),
            sp.GetRequiredService<IMediaSessionHandler>(),
            sp.GetRequiredService<MediaMetrics>(),
            sp.GetRequiredService<ILogger<MediaPublishSessionHandler>>(),
            sp.GetService<IStreamEventPublisher>(),
            sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IPublishSessionHandler>(static sp => sp.GetRequiredService<MediaPublishSessionHandler>());

        return services;
    }
}
