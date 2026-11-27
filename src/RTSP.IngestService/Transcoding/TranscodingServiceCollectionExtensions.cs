// Registers the transcoding stage as the RTMP publish session handler, in front of media processing. Registrations
// use TryAdd, so a host that registers its own IPublishSessionHandler first replaces it. The lifecycle service is a
// hosted service registered here, ahead of the RTMP listener, so it reports the transcoding configuration before
// publishers can connect and stops any leftover pipeline after the listener has ended its sessions.

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtmp.Publishing;

namespace RTSP.IngestService.Transcoding;

/// <summary>Dependency-injection registration for transcoding.</summary>
public static class TranscodingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the transcoding stage as the default <see cref="IPublishSessionHandler"/>, delivering canonical
    /// media to <see cref="MediaPublishSessionHandler"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <remarks>
    /// Requires <see cref="TranscodingOptions"/> and <see cref="MediaOptions"/> (see
    /// <see cref="IngestServiceOptionsExtensions.AddIngestServiceOptions"/>) and
    /// <see cref="MediaServiceCollectionExtensions.AddMediaProcessing"/>. Call before
    /// <see cref="MediaServiceCollectionExtensions.AddMediaProcessing"/> and
    /// <see cref="Rtmp.RtmpServiceCollectionExtensions.AddRtmpIngest"/>.
    /// </remarks>
    public static IServiceCollection AddTranscoding(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMetrics();
        services.TryAddSingleton<TranscodingMetrics>();
        services.TryAddSingleton(static sp => new TranscodingPublishSessionHandler(
            sp.GetRequiredService<IOptions<TranscodingOptions>>(),
            sp.GetRequiredService<IOptions<MediaOptions>>(),
            sp.GetRequiredService<IOptions<HlsOptions>>(),
            sp.GetRequiredService<MediaPublishSessionHandler>(),
            sp.GetRequiredService<TranscodingMetrics>(),
            sp.GetRequiredService<ILogger<TranscodingPublishSessionHandler>>(),
            sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IPublishSessionHandler>(static sp => sp.GetRequiredService<TranscodingPublishSessionHandler>());
        services.AddHostedService<TranscodingLifecycleService>();

        return services;
    }
}
