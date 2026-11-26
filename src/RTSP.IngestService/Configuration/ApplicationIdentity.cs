// The identity this process reports in logs and telemetry: the application name, which is the entry assembly's name
// and so identical on every node, plus the optional deployment identity (instance, node, region, availability zone)
// from the validated Application section. Built once per host. The name has one source, the project's AssemblyName;
// ServiceAssemblyName reads it from this assembly's metadata for code that names the service outside a host. The
// Windows Service name is a separate, fixed constant (WindowsServiceName) and is not derived from any of these.

using System.Reflection;
using OpenTelemetry.Resources;

namespace RTSP.IngestService.Configuration;

/// <summary>Application and deployment identity of the running service.</summary>
public sealed class ApplicationIdentity
{
    /// <summary>
    /// The Windows Service name, fixed so that it matches the installed service on every deployment. It is a separate
    /// identity from <see cref="Name"/>: it is deliberately not derived from the assembly name, the deployment identity
    /// or the host name, so renaming the assembly or moving a node does not change it.
    /// </summary>
    public const string WindowsServiceName = "RTSP.IngestService";

    /// <summary>OpenTelemetry resource attribute of the application name (semantic convention).</summary>
    public const string ServiceNameAttribute = "service.name";

    /// <summary>OpenTelemetry resource attribute of the instance (semantic convention).</summary>
    public const string ServiceInstanceIdAttribute = "service.instance.id";

    /// <summary>OpenTelemetry resource attribute of the region (semantic convention).</summary>
    public const string CloudRegionAttribute = "cloud.region";

    /// <summary>OpenTelemetry resource attribute of the availability zone (semantic convention).</summary>
    public const string CloudAvailabilityZoneAttribute = "cloud.availability_zone";

    /// <summary>OpenTelemetry resource attribute of the logical node. There is no semantic convention for it, so it is custom.</summary>
    public const string NodeIdAttribute = "application.node.id";

    /// <summary>Initializes a new instance of the <see cref="ApplicationIdentity"/> class.</summary>
    /// <param name="name">The application name.</param>
    /// <param name="deployment">The validated deployment identity.</param>
    private ApplicationIdentity(string name, ApplicationOptions deployment)
    {
        Name = name;
        InstanceId = deployment.InstanceId;
        NodeId = deployment.NodeId;
        Region = deployment.Region;
        AvailabilityZone = deployment.AvailabilityZone;
    }

    /// <summary>
    /// Gets the name of the service assembly, from its metadata (the project's <c>AssemblyName</c>). It is the
    /// application name when the service runs through its own executable, and the executable's file name.
    /// </summary>
    public static string ServiceAssemblyName { get; } = GetApplicationName(typeof(ApplicationIdentity).Assembly);

    /// <summary>
    /// Gets the application name: the entry assembly's simple name, <see cref="ServiceAssemblyName"/> for the service.
    /// </summary>
    public string Name { get; }

    /// <summary>Gets the configured instance identifier, or <see langword="null"/>.</summary>
    public string? InstanceId { get; }

    /// <summary>Gets the configured logical node identifier, or <see langword="null"/>.</summary>
    public string? NodeId { get; }

    /// <summary>Gets the configured region, or <see langword="null"/>.</summary>
    public string? Region { get; }

    /// <summary>Gets the configured availability zone, or <see langword="null"/>.</summary>
    public string? AvailabilityZone { get; }

    /// <summary>Gets a one-line description for the startup log, naming only the configured values.</summary>
    public string Description
    {
        get
        {
            var parts = new List<string>(5) { Name };
            Add("instance", InstanceId);
            Add("node", NodeId);
            Add("region", Region);
            Add("availability zone", AvailabilityZone);
            return parts.Count == 1 ? $"{Name}; no deployment identity configured" : string.Join("; ", parts);

            void Add(string label, string? value)
            {
                if (value is not null)
                {
                    parts.Add($"{label} {value}");
                }
            }
        }
    }

    /// <summary>Creates the identity from an entry assembly and the validated deployment identity.</summary>
    /// <param name="entryAssembly">The process's entry assembly, normally <see cref="Assembly.GetEntryAssembly"/>.</param>
    /// <param name="deployment">The validated <c>Application</c> options.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="InvalidOperationException">There is no entry assembly, or it has no name.</exception>
    public static ApplicationIdentity Create(Assembly? entryAssembly, ApplicationOptions deployment)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        return new ApplicationIdentity(GetApplicationName(entryAssembly), deployment);
    }

    /// <summary>Returns the application name of an entry assembly.</summary>
    /// <param name="entryAssembly">The entry assembly.</param>
    /// <returns>Its simple name.</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="entryAssembly"/> is <see langword="null"/> (the process was started from unmanaged code without a
    /// managed entry point) or has no name. No substitute identity is invented.
    /// </exception>
    public static string GetApplicationName(Assembly? entryAssembly)
    {
        if (entryAssembly is null)
        {
            throw new InvalidOperationException(
                "The application name cannot be determined: the process has no entry assembly. The service must be started through its own executable.");
        }

        var name = entryAssembly.GetName().Name;
        return string.IsNullOrEmpty(name)
            ? throw new InvalidOperationException("The application name cannot be determined: the entry assembly has no name.")
            : name;
    }

    /// <summary>
    /// Replaces a resource builder's contents with this identity and the SDK description: <c>service.name</c>,
    /// <c>telemetry.sdk.*</c>, and each configured deployment attribute. Unconfigured values are omitted, never sent
    /// empty. The <c>OTEL_SERVICE_NAME</c> and <c>OTEL_RESOURCE_ATTRIBUTES</c> environment variables are not read, so
    /// the identity cannot be overridden or contradicted from the environment.
    /// </summary>
    /// <param name="resource">The resource builder.</param>
    public void ConfigureResource(ResourceBuilder resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var attributes = new List<KeyValuePair<string, object>>(3);
        if (Region is not null)
        {
            attributes.Add(new(CloudRegionAttribute, Region));
        }

        if (AvailabilityZone is not null)
        {
            attributes.Add(new(CloudAvailabilityZoneAttribute, AvailabilityZone));
        }

        if (NodeId is not null)
        {
            attributes.Add(new(NodeIdAttribute, NodeId));
        }

        resource
            .Clear()
            .AddService(Name, autoGenerateServiceInstanceId: false, serviceInstanceId: InstanceId)
            .AddTelemetrySdk()
            .AddAttributes(attributes);
    }
}
