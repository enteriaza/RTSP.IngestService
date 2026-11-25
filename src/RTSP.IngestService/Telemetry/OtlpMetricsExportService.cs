// Ties the OpenTelemetry SDK's own lifecycle to host shutdown when OTLP export is enabled: it forwards the SDK's
// failure diagnostics to the log while the host runs, and at stop calls the SDK's MeterProvider.Shutdown, which runs a
// final collect-and-export, bounded by FlushTimeout and abandoned when the host's stop token is cancelled. Export
// itself runs on the SDK's reader thread; nothing here is on the media path. When export is disabled it does nothing.

using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using RTSP.IngestService.Configuration;

namespace RTSP.IngestService.Telemetry;

/// <summary>Flushes OTLP metrics export at shutdown and logs export failures.</summary>
internal sealed partial class OtlpMetricsExportService : IHostedService, IDisposable
{
    /// <summary>Longest the final flush may take at shutdown, unless the host's stop token is cancelled first.</summary>
    public static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The exporter settings.</summary>
    private readonly OtlpOptions _options;

    /// <summary>The logger.</summary>
    private readonly ILogger<OtlpMetricsExportService> _logger;

    /// <summary>Forwards SDK diagnostics while export is enabled; <see langword="null"/> when disabled.</summary>
    private readonly OpenTelemetryDiagnosticsListener? _diagnostics;

    /// <summary>The SDK meter provider; <see langword="null"/> when export is disabled.</summary>
    private readonly MeterProvider? _provider;

    /// <summary>Initializes a new instance of the <see cref="OtlpMetricsExportService"/> class.</summary>
    /// <param name="options">The telemetry settings.</param>
    /// <param name="services">The service provider, from which the meter provider is resolved when export is enabled.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public OtlpMetricsExportService(IOptions<TelemetryOptions> options, IServiceProvider services, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _options = options.Value.Otlp;
        _logger = loggerFactory.CreateLogger<OtlpMetricsExportService>();
        if (_options.Enabled)
        {
            _diagnostics = new OpenTelemetryDiagnosticsListener(loggerFactory.CreateLogger<OpenTelemetryDiagnosticsListener>());
            _provider = services.GetRequiredService<MeterProvider>();
        }
    }

    /// <summary>Gets a value indicating whether OTLP metrics export is enabled.</summary>
    public bool IsEnabled => _provider is not null;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_provider is not null)
        {
            LogEnabled(_options.Protocol, _options.ExportInterval, TelemetryServiceCollectionExtensions.MeterNames.Length);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_provider is not { } provider)
        {
            return;
        }

        var shutdown = Task.Run(() => provider.Shutdown((int)FlushTimeout.TotalMilliseconds), CancellationToken.None);
        bool flushed;
        try
        {
            flushed = await shutdown.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            flushed = false;
        }

        if (!flushed)
        {
            LogFlushIncomplete(FlushTimeout);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _diagnostics?.Dispose();

    /// <summary>Logs that export is enabled. The endpoint is not logged.</summary>
    /// <param name="protocol">The protocol.</param>
    /// <param name="exportInterval">The export interval.</param>
    /// <param name="meterCount">The number of exported meters.</param>
    [LoggerMessage(EventId = 9300, Level = LogLevel.Information, Message = "OTLP metrics export enabled over {Protocol} every {ExportInterval} for {MeterCount} meters")]
    private partial void LogEnabled(string protocol, TimeSpan exportInterval, int meterCount);

    /// <summary>
    /// Logs that the SDK reported its shutdown incomplete or the wait was abandoned. An export that failed within the
    /// bound is reported by the SDK's own diagnostics (9301).
    /// </summary>
    /// <param name="flushTimeout">The flush bound.</param>
    [LoggerMessage(EventId = 9302, Level = LogLevel.Warning, Message = "OTLP metrics export did not complete its final export within {FlushTimeout} or before the shutdown deadline; unexported measurements are discarded")]
    private partial void LogFlushIncomplete(TimeSpan flushTimeout);
}
