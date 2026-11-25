// Registers optional OpenTelemetry metrics export over OTLP. The OpenTelemetry SDK subscribes to the service's existing
// meters by name and exports their instruments unchanged; no instrument, tag or meter is added. Whether anything is
// exported is decided from the validated options when the meter provider is built: when disabled, the provider has no
// meters and no reader, so no exporter exists and nothing connects.

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Http;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtmp;
using RTSP.IngestService.Streams;

namespace RTSP.IngestService.Telemetry;

/// <summary>Dependency-injection registration for telemetry export.</summary>
public static class TelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Upper bound of one export request, and of the reader's collect-and-export cycle, when the collector does not
    /// answer.
    /// </summary>
    internal static readonly TimeSpan ExportTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Every meter of the service, all of which are exported when export is enabled.</summary>
    internal static readonly string[] MeterNames =
    [
        RtmpMetrics.MeterName,
        MediaMetrics.MeterName,
        HlsMetrics.MeterName,
        HttpOriginMetrics.MeterName,
        StreamEventMetrics.MeterName,
    ];

    /// <summary>
    /// Registers the OpenTelemetry meter provider and, when <see cref="OtlpOptions.Enabled"/>, a periodic OTLP
    /// exporter of every meter in <see cref="MeterNames"/>, together with the hosted service that flushes it at
    /// shutdown and logs export failures.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <remarks>
    /// Register before the ingest components so the flush runs after they have stopped and includes their final
    /// measurements. Only metrics are configured: there is no tracing or log export.
    /// </remarks>
    public static IServiceCollection AddTelemetryExport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var hosted = services.Where(static d => d.ServiceType == typeof(IHostedService)).ToHashSet();
        services.AddOpenTelemetry().WithMetrics();

        // AddOpenTelemetry inserts its hosted service, which builds the providers at start, ahead of every other hosted
        // service. Move it to this point of the composition so the lifecycle coordinator stays first.
        foreach (var added in services.Where(d => d.ServiceType == typeof(IHostedService) && !hosted.Contains(d)).ToList())
        {
            services.Remove(added);
            services.Add(added);
        }

        services.ConfigureOpenTelemetryMeterProvider(static (provider, builder) =>
        {
            var otlp = provider.GetRequiredService<IOptions<TelemetryOptions>>().Value.Otlp;
            if (!otlp.Enabled)
            {
                return;
            }

            var service = provider.GetRequiredService<IOptions<ApplicationOptions>>().Value.Name;
            builder
                .ConfigureResource(resource => resource.AddService(service, autoGenerateServiceInstanceId: false))
                .AddMeter(MeterNames)
                .AddReader(CreateReader(otlp));
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, OtlpMetricsExportService>());

        return services;
    }

    /// <summary>Returns the URI metrics are sent to: the endpoint, with <c>/v1/metrics</c> appended for OTLP/HTTP.</summary>
    /// <param name="otlp">Validated, enabled settings.</param>
    /// <returns>The URI.</returns>
    internal static Uri MetricsEndpoint(OtlpOptions otlp)
    {
        var endpoint = new Uri(otlp.Endpoint!, UriKind.Absolute);
        if (otlp.Protocol != OtlpOptions.HttpProtobuf)
        {
            return endpoint;
        }

        var path = endpoint.AbsolutePath.TrimEnd('/');
        return path.EndsWith("/v1/metrics", StringComparison.Ordinal) ? endpoint : new UriBuilder(endpoint) { Path = path + "/v1/metrics" }.Uri;
    }

    /// <summary>Creates the SDK's periodic reader around an OTLP metric exporter.</summary>
    /// <param name="otlp">Validated, enabled settings.</param>
    /// <returns>The reader.</returns>
    private static PeriodicExportingMetricReader CreateReader(OtlpOptions otlp)
    {
        var timeout = (int)ExportTimeout.TotalMilliseconds;
        var exporter = new OtlpMetricExporter(new OtlpExporterOptions
        {
            Protocol = otlp.Protocol == OtlpOptions.HttpProtobuf ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc,
            Endpoint = MetricsEndpoint(otlp),
            TimeoutMilliseconds = timeout,
        });
        return new PeriodicExportingMetricReader(exporter, (int)otlp.ExportInterval.TotalMilliseconds, timeout);
    }
}
