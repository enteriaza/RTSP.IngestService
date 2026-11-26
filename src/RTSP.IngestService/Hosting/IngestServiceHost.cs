// Composition root. This is the single place where the host builder is created and every service registration is
// applied, in an order that matters: lifecycle before other hosted services, platform hosting before logging (so
// logging can remove providers that the platform integration adds), telemetry export before the ingest components (so
// its final flush runs after they have stopped), and the RTMP listener last so that it starts after every other
// service and is the first to stop.

using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Http;
using RTSP.IngestService.Lifecycle;
using RTSP.IngestService.Logging;
using RTSP.IngestService.Management;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtmp;
using RTSP.IngestService.Rtp;
using RTSP.IngestService.Streams;
using RTSP.IngestService.Telemetry;
using RTSP.IngestService.Transcoding;

namespace RTSP.IngestService.Hosting;

/// <summary>
/// Creates and configures the service's <see cref="HostApplicationBuilder"/>.
/// </summary>
public static class IngestServiceHost
{
    /// <summary>
    /// Creates a host builder with the full service composition for <paramref name="mode"/>.
    /// </summary>
    /// <param name="args">Command-line arguments; they are added as the highest-priority configuration source.</param>
    /// <param name="mode">The hosting mode selected for this process.</param>
    /// <returns>A builder ready to be built; callers may add further configuration or services first.</returns>
    /// <remarks>
    /// The content root is the application directory (<see cref="AppContext.BaseDirectory"/>) rather than the working
    /// directory, because the Service Control Manager starts services in <c>%SystemRoot%\System32</c>. Configuration
    /// is loaded by the default host builder from <c>appsettings.json</c>, <c>appsettings.{Environment}.json</c>,
    /// <c>DOTNET_</c>-prefixed and unprefixed environment variables, and the command line. The environment defaults
    /// to Production and is selected with <c>DOTNET_ENVIRONMENT</c> or <c>--environment</c>.
    /// </remarks>
    public static HostApplicationBuilder CreateBuilder(string[] args, HostingMode mode)
    {
        ArgumentNullException.ThrowIfNull(args);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Services.AddIngestServiceOptions(builder.Configuration);
        builder.Services.AddIngestServiceHostOptions();
        builder.Services.AddApplicationLifecycle();
        builder.Services.AddPlatformHosting(mode);
        builder.Logging.AddIngestServiceLogging(mode);
        builder.Services.AddTelemetryExport();
        builder.Services.AddStreamEvents();
        builder.Services.AddStreamCatalog();
        builder.Services.AddHlsPackaging();
        builder.Services.AddRtpOutput();
        builder.Services.AddTranscoding();
        builder.Services.AddMediaProcessing();
        builder.Services.AddManagementEndpoints();
        builder.Services.AddHlsHttpOrigin();
        builder.Services.AddRtmpIngest();

        return builder;
    }
}
