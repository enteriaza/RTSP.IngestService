// The OpenTelemetry SDK reports its own failures, such as a collector that cannot be reached, only through its
// EventSources. This listener forwards their warnings and errors to the service log. Only the source, event name and
// event id are logged: payloads can contain the collector endpoint and exception text, so they are never logged.
// Expected notices are skipped: the hosting integration reports that no tracer or logger provider is registered
// (only metrics are configured), and the SDK reports every runtime or framework instrument it does not subscribe to.

using System.Diagnostics.Tracing;

namespace RTSP.IngestService.Telemetry;

/// <summary>Logs warnings and errors written by the OpenTelemetry SDK's EventSources.</summary>
/// <remarks>
/// An <see cref="EventListener"/> observes every matching EventSource in the process; it exists only while OTLP export
/// is enabled. Events arrive on the SDK's threads, typically its export thread, and are logged without blocking.
/// </remarks>
internal sealed partial class OpenTelemetryDiagnosticsListener : EventListener
{
    /// <summary>Prefix of the SDK's EventSource names (<c>OpenTelemetry-Sdk</c>, <c>OpenTelemetry-Exporter-OpenTelemetryProtocol</c>, ...).</summary>
    private const string SourcePrefix = "OpenTelemetry-";

    /// <summary>The SDK's EventSource name.</summary>
    private const string SdkSourceName = "OpenTelemetry-Sdk";

    /// <summary>
    /// The SDK's <c>MetricInstrumentIgnored</c> event id; its payload is the instrument name, the meter name, the reason
    /// and a suggested fix.
    /// </summary>
    private const int MetricInstrumentIgnoredEventId = 33;

    /// <summary>
    /// The logger. The base constructor reports existing sources before this is assigned, so events can arrive while
    /// it is still <see langword="null"/>; they are not logged.
    /// </summary>
    private readonly ILogger? _logger;

    /// <summary>Initializes a new instance of the <see cref="OpenTelemetryDiagnosticsListener"/> class.</summary>
    /// <param name="logger">The logger.</param>
    public OpenTelemetryDiagnosticsListener(ILogger logger) => _logger = logger;

    /// <inheritdoc />
    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        base.OnEventSourceCreated(eventSource);
        if (eventSource.Name.StartsWith(SourcePrefix, StringComparison.Ordinal))
        {
            EnableEvents(eventSource, EventLevel.Warning);
        }
    }

    /// <inheritdoc />
    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (_logger is { } logger
            && eventData.Level is EventLevel.Critical or EventLevel.Error or EventLevel.Warning
            && !IsForeignInstrumentIgnored(eventData)
            && eventData.EventName is not ("TracerProviderNotRegistered" or "LoggerProviderNotRegistered"))
        {
            LogDiagnostic(logger, eventData.EventSource.Name, eventData.EventName ?? "unnamed", eventData.EventId);
        }
    }

    /// <summary>
    /// Returns whether the event is the SDK's notice that it ignores an instrument of a meter other than the service's
    /// own. The SDK writes it at warning level for every runtime and framework instrument (<c>System.Runtime</c>,
    /// <c>System.Net.*</c>, ...), which is expected and not a failure; for one of the service's meters it is kept.
    /// </summary>
    /// <param name="eventData">The event.</param>
    /// <returns><see langword="true"/> to skip the event.</returns>
    private static bool IsForeignInstrumentIgnored(EventWrittenEventArgs eventData) =>
        eventData.EventId == MetricInstrumentIgnoredEventId
        && eventData.EventSource.Name == SdkSourceName
        && eventData.Payload is [_, string meter, ..]
        && !TelemetryServiceCollectionExtensions.MeterNames.Contains(meter, StringComparer.Ordinal);

    /// <summary>Logs one SDK diagnostic without its payload.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="source">The EventSource name.</param>
    /// <param name="eventName">The event name.</param>
    /// <param name="sourceEventId">The event's id within its source.</param>
    [LoggerMessage(EventId = 9301, Level = LogLevel.Warning, Message = "OpenTelemetry reported {EventName} (event {SourceEventId} of {Source}); metrics export continues")]
    private static partial void LogDiagnostic(ILogger logger, string source, string eventName, int sourceEventId);
}
