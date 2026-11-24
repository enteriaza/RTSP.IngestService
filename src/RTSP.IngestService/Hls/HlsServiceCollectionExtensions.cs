// Registers HLS packaging as the media session handler, with the bounded in-memory segment store. Both are
// registered with TryAdd, so a host that registers its own IHlsSegmentStore (for example disk-backed) or
// IMediaSessionHandler first replaces them without changing the packaging layer.

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;
using RTSP.IngestService.Streams;

namespace RTSP.IngestService.Hls;

/// <summary>Dependency-injection registration for HLS packaging.</summary>
public static class HlsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="HlsPackagingHandler"/> as the <see cref="IMediaSessionHandler"/>, with its metrics and
    /// <see cref="InMemoryHlsSegmentStore"/> as the default <see cref="IHlsSegmentStore"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <remarks>
    /// Requires <see cref="HlsOptions"/> to be registered (see
    /// <see cref="IngestServiceOptionsExtensions.AddIngestServiceOptions"/>). Call before
    /// <see cref="MediaServiceCollectionExtensions.AddMediaProcessing"/>, whose discarding handler is only a fallback.
    /// </remarks>
    public static IServiceCollection AddHlsPackaging(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();
        services.TryAddSingleton<HlsMetrics>();
        services.TryAddSingleton<IHlsSegmentStore>(static sp => new InMemoryHlsSegmentStore(
            sp.GetRequiredService<IOptions<HlsOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<HlsMetrics>(),
            sp.GetService<IStreamEventPublisher>()));
        services.TryAddSingleton<IMediaSessionHandler>(static sp => new HlsPackagingHandler(
            sp.GetRequiredService<IOptions<HlsOptions>>(),
            sp.GetRequiredService<IHlsSegmentStore>(),
            sp.GetRequiredService<HlsMetrics>(),
            sp.GetRequiredService<ILogger<HlsPackagingHandler>>()));

        return services;
    }
}
