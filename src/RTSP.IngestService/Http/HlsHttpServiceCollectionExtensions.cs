// Registers the HTTP HLS origin as a hosted service. Hosted services start in registration order and stop in
// reverse, so registering the origin before RTMP ingest keeps playback available while publishers are disconnected
// during shutdown.

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;

namespace RTSP.IngestService.Http;

/// <summary>Dependency-injection registration for the HTTP HLS origin.</summary>
public static class HlsHttpServiceCollectionExtensions
{
    /// <summary>Registers <see cref="HlsHttpOrigin"/> as a hosted service, with its request pipeline and metrics.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <remarks>
    /// Requires <see cref="HttpOriginOptions"/> and <see cref="HlsOptions"/> (see
    /// <see cref="IngestServiceOptionsExtensions.AddIngestServiceOptions"/>) and an <see cref="IHlsSegmentStore"/>
    /// (see <see cref="HlsServiceCollectionExtensions.AddHlsPackaging"/>). Call before
    /// <c>AddRtmpIngest</c>.
    /// </remarks>
    public static IServiceCollection AddHlsHttpOrigin(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();
        services.TryAddSingleton<HttpOriginMetrics>();
        services.TryAddSingleton(static sp => new HlsHttpApplication(
            sp.GetRequiredService<IHlsSegmentStore>(),
            sp.GetRequiredService<IOptions<HlsOptions>>(),
            sp.GetRequiredService<HttpOriginMetrics>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<HlsHttpApplication>>()));
        services.TryAddSingleton(static sp => new HlsHttpOrigin(
            sp.GetRequiredService<IOptions<HttpOriginOptions>>(),
            sp.GetRequiredService<HlsHttpApplication>(),
            sp,
            sp.GetRequiredService<ILoggerFactory>()));
        services.AddHostedService(static sp => sp.GetRequiredService<HlsHttpOrigin>());

        return services;
    }
}
