// Registers the management endpoints. They are served by the HTTP origin's request pipeline, not by a hosted service
// of their own; listener-bound state is read through the service provider at request time, because the HTTP origin
// itself depends on the pipeline that contains these endpoints.

using Microsoft.Extensions.DependencyInjection.Extensions;
using RTSP.IngestService.Http;
using RTSP.IngestService.Lifecycle;
using RTSP.IngestService.Rtmp;

namespace RTSP.IngestService.Management;

/// <summary>Dependency-injection registration for the management endpoints.</summary>
public static class ManagementServiceCollectionExtensions
{
    /// <summary>Registers <see cref="ManagementEndpoints"/> for the HTTP origin to serve when management is enabled.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <remarks>
    /// Requires <see cref="IApplicationLifecycle"/>. Readiness reports the RTMP or HTTP listener as unbound when
    /// <see cref="RtmpListener"/> or <see cref="HlsHttpOrigin"/> is not registered.
    /// </remarks>
    public static IServiceCollection AddManagementEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(static sp => new ManagementEndpoints(
            sp.GetRequiredService<IApplicationLifecycle>(),
            () => sp.GetService<RtmpListener>()?.IsListening == true,
            () => sp.GetService<HlsHttpOrigin>()?.IsListening == true,
            sp.GetRequiredService<ILogger<ManagementEndpoints>>()));

        return services;
    }
}
