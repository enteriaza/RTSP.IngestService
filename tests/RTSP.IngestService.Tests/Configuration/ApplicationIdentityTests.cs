// Tests for the application and deployment identity: the name comes from the entry assembly and cannot be configured
// (Application:Name and OTEL_* variables are rejected or ignored), the deployment values bind, are optional and are
// validated, and the identity reaches the OpenTelemetry resource (in process and over the wire, whether or not export
// is enabled) and the startup log without changing the application name or the Windows Service name.

using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hosting;
using RTSP.IngestService.Rtmp;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Tests.Telemetry;

namespace RTSP.IngestService.Tests.Configuration;

/// <summary>Tests for <see cref="ApplicationIdentity"/> and <see cref="ApplicationOptions"/>.</summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class ApplicationIdentityTests
{
    /// <summary>A complete example deployment identity.</summary>
    private static readonly Dictionary<string, string?> Node01 = new()
    {
        ["Application:InstanceId"] = "i-0123456789abcdef0",
        ["Application:NodeId"] = "node01.af-south-1",
        ["Application:Region"] = "af-south-1",
        ["Application:AvailabilityZone"] = "af-south-1a",
    };

    /// <summary>Gets the test cancellation token.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Verifies the service assembly is named <c>RTSP.IngestService</c>, that <see cref="ApplicationIdentity.ServiceAssemblyName"/>
    /// is read from its metadata, and that an identity is the name of the assembly it is created from. This is the
    /// tests' one literal of the application name: <c>service.name</c>, the <c>ApplicationName</c> log property and the
    /// executable's file name all follow the project's <c>AssemblyName</c>, so an accidental rename fails here.
    /// </summary>
    [Fact]
    public void ServiceAssembly_IsNamedRtspIngestService()
    {
        var service = typeof(ApplicationIdentity).Assembly;

        Assert.Equal("RTSP.IngestService", ApplicationIdentity.ServiceAssemblyName);
        Assert.Equal(service.GetName().Name, ApplicationIdentity.ServiceAssemblyName);
        Assert.Equal(ApplicationIdentity.ServiceAssemblyName, ApplicationIdentity.Create(service, new ApplicationOptions()).Name);
        Assert.Equal(typeof(ApplicationIdentityTests).Assembly.GetName().Name, ApplicationIdentity.Create(typeof(ApplicationIdentityTests).Assembly, new ApplicationOptions()).Name);
    }

    /// <summary>
    /// Verifies the Windows Service name is a compile-time constant, so nothing at run time (assembly, configuration,
    /// deployment identity, host name) can change it, and that it is independent of the runtime identity: an identity
    /// from another assembly with a full deployment identity has a different name while the service name is unchanged.
    /// </summary>
    [Fact]
    public void WindowsServiceName_IsAConstantIndependentOfTheRuntimeIdentity()
    {
        var field = typeof(ApplicationIdentity).GetField(nameof(ApplicationIdentity.WindowsServiceName))!;
        Assert.True(field.IsLiteral);

        var identity = ApplicationIdentity.Create(typeof(ApplicationIdentityTests).Assembly, new ApplicationOptions
        {
            InstanceId = "i-0123456789abcdef0",
            NodeId = "node01.af-south-1",
            Region = "af-south-1",
            AvailabilityZone = "af-south-1a",
        });
        Assert.NotEqual(ApplicationIdentity.WindowsServiceName, identity.Name);
        Assert.DoesNotContain(identity.NodeId!, ApplicationIdentity.WindowsServiceName, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.MachineName, ApplicationIdentity.WindowsServiceName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies the host's identity is the process's entry assembly (the test runner here; the service executable in
    /// production), not a library such as the service assembly the composition lives in.
    /// </summary>
    [Fact]
    public void Host_NameIsTheEntryAssemblyName()
    {
        using var host = TestHosts.CreateBuilder(new FakeLogCollector()).Build();

        var entry = Assembly.GetEntryAssembly()!.GetName().Name;
        Assert.Equal(entry, host.Services.GetRequiredService<ApplicationIdentity>().Name);
        Assert.NotEqual(ApplicationIdentity.ServiceAssemblyName, entry);
    }

    /// <summary>Verifies a missing entry assembly fails with an explicit error instead of an invented name.</summary>
    [Fact]
    public void MissingEntryAssembly_FailsExplicitly()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ApplicationIdentity.Create(null, new ApplicationOptions()));

        Assert.Contains("no entry assembly", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies the shipped configuration has no Application section and needs none.</summary>
    [Fact]
    public void ShippedConfiguration_HasNoApplicationSection()
    {
        var shipped = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: false).Build();
        Assert.False(shipped.GetSection(ApplicationOptions.SectionName).Exists());

        using var provider = new ServiceCollection().AddIngestServiceOptions(shipped).BuildServiceProvider();
        var identity = provider.GetRequiredService<ApplicationIdentity>();
        Assert.Null(identity.InstanceId);
        Assert.Null(identity.NodeId);
        Assert.Null(identity.Region);
        Assert.Null(identity.AvailabilityZone);
        Assert.Equal($"{identity.Name}; no deployment identity configured", identity.Description);
    }

    /// <summary>
    /// Verifies the retired Application:Name is rejected as a configuration error that says why and what to do,
    /// whether it comes from configuration or an environment variable, so it can never override the name.
    /// </summary>
    [Fact]
    public void ApplicationName_IsRejected_FromConfigurationAndEnvironment()
    {
        var prefix = $"RTSPIDTEST{Guid.NewGuid():N}_";
        Environment.SetEnvironmentVariable(prefix + "Application__Name", "Spoofed");
        try
        {
            foreach (var configuration in new IConfiguration[]
            {
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Application:Name"] = "Spoofed" }).Build(),
                new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build(),
            })
            {
                using var provider = new ServiceCollection().AddIngestServiceOptions(configuration).BuildServiceProvider();
                var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<ApplicationIdentity>());
                Assert.Equal(typeof(ApplicationOptions), ex.OptionsType);
                Assert.Contains("Application:Name is no longer supported", ex.Message, StringComparison.Ordinal);
                Assert.Contains("Application__Name", ex.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "Application__Name", null);
        }
    }

    /// <summary>Verifies every deployment value binds, and that an unset value stays unset.</summary>
    [Fact]
    public void DeploymentIdentity_Binds_AndUnsetValuesStayUnset()
    {
        var identity = Identity(Node01);
        Assert.Equal("i-0123456789abcdef0", identity.InstanceId);
        Assert.Equal("node01.af-south-1", identity.NodeId);
        Assert.Equal("af-south-1", identity.Region);
        Assert.Equal("af-south-1a", identity.AvailabilityZone);
        Assert.Equal($"{identity.Name}; instance i-0123456789abcdef0; node node01.af-south-1; region af-south-1; availability zone af-south-1a", identity.Description);

        var partial = Identity(new Dictionary<string, string?> { ["Application:NodeId"] = "edge-7", ["Application:Region"] = "on-prem-jhb" });
        Assert.Equal("edge-7", partial.NodeId);
        Assert.Equal("on-prem-jhb", partial.Region);
        Assert.Null(partial.InstanceId);
        Assert.Null(partial.AvailabilityZone);
        Assert.Equal($"{partial.Name}; node edge-7; region on-prem-jhb", partial.Description);
    }

    /// <summary>Verifies generic, non-AWS identifiers are accepted, including the length limits.</summary>
    /// <param name="key">The setting.</param>
    /// <param name="value">A valid value.</param>
    [Theory]
    [InlineData("InstanceId", "3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("InstanceId", "ingest-7d9f8b6c5-x2k4q")]
    [InlineData("InstanceId", "vm:jhb:042")]
    [InlineData("NodeId", "node_01")]
    [InlineData("NodeId", "N")]
    [InlineData("Region", "westeurope")]
    [InlineData("AvailabilityZone", "1")]
    public void DeploymentIdentity_AcceptsGenericValues(string key, string value) =>
        Assert.NotNull(Identity(new Dictionary<string, string?> { [$"Application:{key}"] = value }));

    /// <summary>Verifies invalid values fail validation with a message naming the setting and the problem.</summary>
    /// <param name="key">The setting.</param>
    /// <param name="value">An invalid value.</param>
    /// <param name="expected">Text the message must contain.</param>
    [Theory]
    [InlineData("InstanceId", "", "is empty")]
    [InlineData("NodeId", "   ", "is empty")]
    [InlineData("Region", " af-south-1", "must start and end")]
    [InlineData("AvailabilityZone", "af-south-1a-", "must start and end")]
    [InlineData("NodeId", "node 01", "contains ' '")]
    [InlineData("NodeId", "node/01", "contains '/'")]
    [InlineData("InstanceId", "i-01\u00e923", "contains '\u00e9'")]
    [InlineData("InstanceId", "i-01;23", "contains ';'")]
    public void DeploymentIdentity_InvalidValues_FailValidation(string key, string value, string expected)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Identity(new Dictionary<string, string?> { [$"Application:{key}"] = value }));

        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies the length limits: 128 for instance and node, 64 for region and zone, inclusive.</summary>
    /// <param name="key">The setting.</param>
    /// <param name="limit">Its maximum length.</param>
    [Theory]
    [InlineData("InstanceId", ApplicationOptions.MaxIdentifierLength)]
    [InlineData("NodeId", ApplicationOptions.MaxIdentifierLength)]
    [InlineData("Region", ApplicationOptions.MaxLocationLength)]
    [InlineData("AvailabilityZone", ApplicationOptions.MaxLocationLength)]
    public void DeploymentIdentity_LengthLimits(string key, int limit)
    {
        Assert.Equal(limit, Identity(new Dictionary<string, string?> { [$"Application:{key}"] = new string('a', limit) }) switch
        {
            { InstanceId: { } v } when key == "InstanceId" => v.Length,
            { NodeId: { } v } when key == "NodeId" => v.Length,
            { Region: { } v } when key == "Region" => v.Length,
            { AvailabilityZone: { } v } => v.Length,
            _ => 0,
        });

        var ex = Assert.Throws<OptionsValidationException>(() => Identity(new Dictionary<string, string?> { [$"Application:{key}"] = new string('a', limit + 1) }));
        Assert.Contains($"maximum length of {limit}", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies an unknown key in the section is still rejected by strict binding.</summary>
    [Fact]
    public void UnknownApplicationKey_FailsValidation()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Identity(new Dictionary<string, string?> { ["Application:Zone"] = "a" }));

        Assert.Contains("Zone", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that different node and instance identities give the same application name, Windows Service name and
    /// <c>service.name</c>, and different instance and node attributes.
    /// </summary>
    [Fact]
    public void DifferentDeployments_ShareTheApplicationIdentity()
    {
        var node02 = new Dictionary<string, string?>
        {
            ["Application:InstanceId"] = "i-0fedcba9876543210",
            ["Application:NodeId"] = "node02.af-south-1",
            ["Application:Region"] = "af-south-1",
            ["Application:AvailabilityZone"] = "af-south-1b",
        };
        using var first = TestHosts.CreateBuilder(new FakeLogCollector(), HostingMode.WindowsService, Node01).Build();
        using var second = TestHosts.CreateBuilder(new FakeLogCollector(), HostingMode.WindowsService, node02).Build();

        var a = Resource(first);
        var b = Resource(second);
        Assert.Equal(first.Services.GetRequiredService<ApplicationIdentity>().Name, second.Services.GetRequiredService<ApplicationIdentity>().Name);
        Assert.Equal(a[ApplicationIdentity.ServiceNameAttribute], b[ApplicationIdentity.ServiceNameAttribute]);
        Assert.NotEqual(a[ApplicationIdentity.ServiceInstanceIdAttribute], b[ApplicationIdentity.ServiceInstanceIdAttribute]);
        Assert.NotEqual(a[ApplicationIdentity.NodeIdAttribute], b[ApplicationIdentity.NodeIdAttribute]);
        Assert.Equal("af-south-1b", b[ApplicationIdentity.CloudAvailabilityZoneAttribute]);
        Assert.All(new[] { first, second }, static host => Assert.Equal(
            ApplicationIdentity.WindowsServiceName,
            host.Services.GetRequiredService<IOptions<WindowsServiceLifetimeOptions>>().Value.ServiceName));
    }

    /// <summary>
    /// Verifies the meter provider's resource carries exactly the identity attributes, with export disabled and
    /// enabled: <c>service.name</c> and the SDK attributes always, each deployment attribute only when configured,
    /// and nothing from <c>OTEL_SERVICE_NAME</c> or <c>OTEL_RESOURCE_ATTRIBUTES</c>.
    /// </summary>
    /// <param name="enabled">Whether OTLP export is enabled.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resource_HasExactlyTheIdentity_WhetherOrNotExportIsEnabled(bool enabled)
    {
        var spoofing = new Dictionary<string, string?>
        {
            ["OTEL_SERVICE_NAME"] = "Spoofed",
            ["OTEL_RESOURCE_ATTRIBUTES"] = "service.name=Spoofed,service.instance.id=spoofed,cloud.region=spoofed,deployment.environment=spoofed",
        };
        if (enabled)
        {
            foreach (var (key, value) in TelemetryTestSupport.Enabled(TelemetryTestSupport.RefusingEndpoint()))
            {
                spoofing[key] = value;
            }
        }

        using (var bare = TestHosts.CreateBuilder(new FakeLogCollector(), overrides: spoofing).Build())
        {
            var resource = Resource(bare);
            Assert.Equal(bare.Services.GetRequiredService<ApplicationIdentity>().Name, resource[ApplicationIdentity.ServiceNameAttribute]);
            Assert.Equal(["service.name", "telemetry.sdk.language", "telemetry.sdk.name", "telemetry.sdk.version"], resource.Keys.Order(StringComparer.Ordinal));
        }

        foreach (var (key, value) in Node01)
        {
            spoofing[key] = value;
        }

        using var host = TestHosts.CreateBuilder(new FakeLogCollector(), overrides: spoofing).Build();
        var configured = Resource(host);
        Assert.Equal(host.Services.GetRequiredService<ApplicationIdentity>().Name, configured[ApplicationIdentity.ServiceNameAttribute]);
        Assert.Equal("i-0123456789abcdef0", configured[ApplicationIdentity.ServiceInstanceIdAttribute]);
        Assert.Equal("node01.af-south-1", configured[ApplicationIdentity.NodeIdAttribute]);
        Assert.Equal("af-south-1", configured[ApplicationIdentity.CloudRegionAttribute]);
        Assert.Equal("af-south-1a", configured[ApplicationIdentity.CloudAvailabilityZoneAttribute]);
        Assert.Equal(8, configured.Count);
    }

    /// <summary>
    /// Verifies the identity reaches the collector as resource attributes: each appears once in an export request
    /// carrying many measurements (so it is not repeated per data point), and no metric is named after it.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Export_SendsTheIdentityAsResourceAttributes()
    {
        await using var collector = new FakeCollector(respond: true);
        var overrides = TelemetryTestSupport.Enabled(collector.Endpoint);
        foreach (var (key, value) in Node01)
        {
            overrides[key] = value;
        }

        using var host = TestHosts.CreateBuilder(new FakeLogCollector(), overrides: overrides).Build();
        await host.StartAsync(Token).WithinSafetyTimeout();
        var rtmp = host.Services.GetRequiredService<RtmpMetrics>();
        rtmp.ConnectionAccepted();
        rtmp.ConnectionRejected();
        rtmp.Handshake(succeeded: true);
        await host.StopAsync(CancellationToken.None);

        var body = Encoding.UTF8.GetString((await collector.FirstRequest.WithinSafetyTimeout()).Body);
        foreach (var text in new[] { "service.instance.id", "i-0123456789abcdef0", "application.node.id", "node01.af-south-1", "cloud.region", "cloud.availability_zone", "af-south-1a" })
        {
            Assert.Equal(1, Occurrences(body, text));
        }

        Assert.Equal(1, Occurrences(body, "service.name"));
        Assert.Contains(host.Services.GetRequiredService<ApplicationIdentity>().Name, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies the identity is logged once at startup (event 4008), before the host starts, naming only the configured
    /// values. A pre-cancelled run stops right after the host is built, so no waiting is needed.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Startup_LogsTheIdentity()
    {
        var logs = new FakeLogCollector();
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        Assert.Equal(ExitCodes.Success, await ServiceRunner.RunAsync(TestHosts.Arguments, HostingMode.Console, TestHosts.Configure(logs, Node01), stop.Token).WithinSafetyTimeout());

        var identity = Assert.Single(logs.GetSnapshot(), static r => r.Id.Id == 4008);
        Assert.Equal(
            $"Application identity: {Assembly.GetEntryAssembly()!.GetName().Name}; instance i-0123456789abcdef0; node node01.af-south-1; region af-south-1; availability zone af-south-1a",
            identity.Message);
    }

    /// <summary>Binds the options and builds the identity from the service assembly.</summary>
    /// <param name="values">The configuration.</param>
    /// <returns>The identity.</returns>
    private static ApplicationIdentity Identity(IReadOnlyDictionary<string, string?> values)
    {
        using var provider = new ServiceCollection().AddIngestServiceOptions(new ConfigurationBuilder().AddInMemoryCollection(values).Build()).BuildServiceProvider();
        return ApplicationIdentity.Create(typeof(ApplicationIdentity).Assembly, provider.GetRequiredService<IOptions<ApplicationOptions>>().Value);
    }

    /// <summary>Returns the resource attributes of a host's meter provider.</summary>
    /// <param name="host">The host.</param>
    /// <returns>The attributes as strings.</returns>
    private static Dictionary<string, string> Resource(IHost host) =>
        host.Services.GetRequiredService<MeterProvider>().GetResource().Attributes.ToDictionary(static a => a.Key, static a => Convert.ToString(a.Value, System.Globalization.CultureInfo.InvariantCulture)!);

    /// <summary>Counts the occurrences of a string.</summary>
    /// <param name="text">The text.</param>
    /// <param name="value">The string.</param>
    /// <returns>The count.</returns>
    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
