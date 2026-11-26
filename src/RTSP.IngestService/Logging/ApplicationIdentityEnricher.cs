// Attaches the application and deployment identity to every log event. The properties are created once, and they
// replace any event or log-context property of the same name, so a message template can never make an event claim a
// different application or node.

using RTSP.IngestService.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace RTSP.IngestService.Logging;

/// <summary>Adds <c>ApplicationName</c> and the configured deployment identity properties to log events.</summary>
internal sealed class ApplicationIdentityEnricher : ILogEventEnricher
{
    /// <summary>The application name property.</summary>
    public const string ApplicationName = "ApplicationName";

    /// <summary>The instance identifier property.</summary>
    public const string ApplicationInstanceId = "ApplicationInstanceId";

    /// <summary>The logical node identifier property.</summary>
    public const string ApplicationNodeId = "ApplicationNodeId";

    /// <summary>The region property.</summary>
    public const string ApplicationRegion = "ApplicationRegion";

    /// <summary>The availability zone property.</summary>
    public const string ApplicationAvailabilityZone = "ApplicationAvailabilityZone";

    /// <summary>The properties: the name, then each configured deployment value.</summary>
    private readonly LogEventProperty[] _properties;

    /// <summary>Initializes a new instance of the <see cref="ApplicationIdentityEnricher"/> class.</summary>
    /// <param name="identity">The identity.</param>
    public ApplicationIdentityEnricher(ApplicationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var properties = new List<LogEventProperty> { new(ApplicationName, new ScalarValue(identity.Name)) };
        Add(ApplicationInstanceId, identity.InstanceId);
        Add(ApplicationNodeId, identity.NodeId);
        Add(ApplicationRegion, identity.Region);
        Add(ApplicationAvailabilityZone, identity.AvailabilityZone);
        _properties = [.. properties];

        void Add(string name, string? value)
        {
            if (value is not null)
            {
                properties.Add(new LogEventProperty(name, new ScalarValue(value)));
            }
        }
    }

    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        foreach (var property in _properties)
        {
            logEvent.AddOrUpdateProperty(property);
        }
    }
}
