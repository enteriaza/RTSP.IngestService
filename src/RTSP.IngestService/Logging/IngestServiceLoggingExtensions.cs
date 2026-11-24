// Replaces the default Microsoft.Extensions.Logging providers with a single Serilog provider so that every log event,
// including framework and hosting events, flows through one pipeline. The Serilog logger is created lazily from
// validated options when the logger factory is first resolved, which happens while the host is being built.

using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hosting;
using Serilog.Extensions.Logging;

namespace RTSP.IngestService.Logging;

/// <summary>
/// Logging registration for the service.
/// </summary>
public static class IngestServiceLoggingExtensions
{
    /// <summary>
    /// Clears all registered logging providers, including the Event Log provider added by <c>AddWindowsService</c>,
    /// and registers a <see cref="SerilogLoggerProvider"/> built by <see cref="SerilogPipeline.CreateLogger"/>.
    /// </summary>
    /// <param name="logging">The logging builder.</param>
    /// <param name="mode">The hosting mode, which selects the sinks.</param>
    /// <returns>The same <paramref name="logging"/> instance for chaining.</returns>
    /// <remarks>
    /// Call after the platform hosting registration so the providers it adds are removed. The provider owns the
    /// Serilog logger and disposes (flushes) it when the host's service provider is disposed.
    /// </remarks>
    public static ILoggingBuilder AddIngestServiceLogging(this ILoggingBuilder logging, HostingMode mode)
    {
        ArgumentNullException.ThrowIfNull(logging);

        logging.ClearProviders();
        logging.Services.AddSingleton<ILoggerProvider>(sp => new SerilogLoggerProvider(
            SerilogPipeline.CreateLogger(
                sp.GetRequiredService<IOptions<ApplicationOptions>>().Value,
                sp.GetRequiredService<IOptions<FileLoggingOptions>>().Value,
                sp.GetRequiredService<IHostEnvironment>(),
                mode),
            dispose: true));

        return logging;
    }
}
