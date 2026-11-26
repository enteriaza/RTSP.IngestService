// Registers RTP/UDP output as a decorator of the IMediaSessionHandler registered before it (HLS packaging in the
// service host). The decision is made when the handler is first resolved, from the validated options: with no
// configured outputs the inner handler itself is the IMediaSessionHandler, so the composition behaves exactly as without
// RTP output.

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;

namespace RTSP.IngestService.Rtp;

/// <summary>Dependency-injection registration for RTP output.</summary>
public static class RtpOutputServiceCollectionExtensions
{
    /// <summary>
    /// Wraps the current <see cref="IMediaSessionHandler"/> registration in <see cref="RtpOutputHandler"/> when
    /// <see cref="RtpOptions.Outputs"/> is not empty, with its metrics and the UDP socket factory.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <remarks>
    /// Requires <see cref="RtpOptions"/> (see <see cref="IngestServiceOptionsExtensions.AddIngestServiceOptions"/>).
    /// Call after the consumer it decorates (<see cref="Hls.HlsServiceCollectionExtensions.AddHlsPackaging"/>) and
    /// before <see cref="MediaServiceCollectionExtensions.AddMediaProcessing"/>; without a prior registration it
    /// decorates <see cref="DiscardingMediaSessionHandler"/>. A handler registered afterwards replaces the decorator.
    /// </remarks>
    public static IServiceCollection AddRtpOutput(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var inner = services.LastOrDefault(static d => d.ServiceType == typeof(IMediaSessionHandler) && !d.IsKeyedService)
            ?? ServiceDescriptor.Singleton<IMediaSessionHandler, DiscardingMediaSessionHandler>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();
        services.TryAddSingleton<RtpMetrics>();
        services.TryAddSingleton<RtpSocketFactory>(static _ => static (output, qos) => new RtpUdpSocket(output, qos));
        services.AddSingleton(sp =>
        {
            var handler = Create(sp, inner);
            var owned = inner.ImplementationInstance is null;
            var options = sp.GetRequiredService<IOptions<RtpOptions>>();
            var rtp = options.Value.Outputs.Count == 0
                ? null
                : new RtpOutputHandler(
                    handler,
                    owned,
                    options,
                    sp.GetRequiredService<RtpSocketFactory>(),
                    sp.GetRequiredService<RtpMetrics>(),
                    sp.GetRequiredService<ILogger<RtpOutputHandler>>(),
                    sp.GetRequiredService<TimeProvider>());
            return new RtpOutputRegistration(handler, owned, rtp);
        });
        services.RemoveAll<IMediaSessionHandler>();
        services.AddSingleton<IMediaSessionHandler>(static sp => sp.GetRequiredService<RtpOutputRegistration>().Handler);

        return services;
    }

    /// <summary>Creates the decorated handler from its original registration.</summary>
    /// <param name="services">The provider.</param>
    /// <param name="descriptor">The original registration.</param>
    /// <returns>The handler.</returns>
    private static IMediaSessionHandler Create(IServiceProvider services, ServiceDescriptor descriptor) =>
        (IMediaSessionHandler)(descriptor.ImplementationInstance
            ?? descriptor.ImplementationFactory?.Invoke(services)
            ?? ActivatorUtilities.CreateInstance(services, descriptor.ImplementationType!));
}

/// <summary>
/// The media session handler chosen by <see cref="RtpOutputServiceCollectionExtensions.AddRtpOutput"/>: the RTP
/// decorator when outputs are configured, otherwise the decorated handler itself. Owns both for disposal.
/// </summary>
internal sealed class RtpOutputRegistration : IAsyncDisposable
{
    /// <summary>The decorated handler.</summary>
    private readonly IMediaSessionHandler _inner;

    /// <summary>Whether <see cref="_inner"/> was created by the registration and must be disposed with it.</summary>
    private readonly bool _ownsInner;

    /// <summary>Initializes a new instance of the <see cref="RtpOutputRegistration"/> class.</summary>
    /// <param name="inner">The decorated handler.</param>
    /// <param name="ownsInner">Whether <paramref name="inner"/> must be disposed here.</param>
    /// <param name="rtp">The RTP decorator, or <see langword="null"/> when no output is configured.</param>
    public RtpOutputRegistration(IMediaSessionHandler inner, bool ownsInner, RtpOutputHandler? rtp)
    {
        _inner = inner;
        _ownsInner = ownsInner;
        Rtp = rtp;
    }

    /// <summary>Gets the RTP decorator, or <see langword="null"/> when no output is configured.</summary>
    public RtpOutputHandler? Rtp { get; }

    /// <summary>Gets the handler registered as <see cref="IMediaSessionHandler"/>.</summary>
    public IMediaSessionHandler Handler => (IMediaSessionHandler?)Rtp ?? _inner;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Rtp is not null)
        {
            await Rtp.DisposeAsync().ConfigureAwait(false);
        }
        else if (_ownsInner)
        {
            switch (_inner)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }
}
