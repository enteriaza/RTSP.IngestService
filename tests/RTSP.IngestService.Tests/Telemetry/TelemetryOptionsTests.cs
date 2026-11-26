// Tests for the Telemetry:Otlp configuration: shipped and code defaults, endpoint, protocol and interval validation,
// strict binding of unknown keys, the exit code 78 of invalid configuration through the service runner, and the metrics
// URI derived from the endpoint. Validation messages never contain the configured endpoint.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hosting;
using RTSP.IngestService.Telemetry;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Telemetry;

/// <summary>Tests for <see cref="TelemetryOptions"/> and <see cref="OtlpOptions"/>.</summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class TelemetryOptionsTests
{
    /// <summary>Gets the test cancellation token.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Verifies the shipped appsettings.json and the code defaults: disabled, no endpoint, gRPC, 15 seconds.</summary>
    [Fact]
    public void Defaults_ExportIsDisabled()
    {
        var shipped = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: false).Build();
        var absent = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        foreach (var configuration in new IConfiguration[] { shipped, absent })
        {
            var otlp = Resolve(configuration);
            Assert.False(otlp.Enabled);
            Assert.True(string.IsNullOrEmpty(otlp.Endpoint));
            Assert.Equal(("grpc", TimeSpan.FromSeconds(15)), (otlp.Protocol, otlp.ExportInterval));
        }
    }

    /// <summary>Verifies that invalid settings fail validation with a message naming the setting but not the value.</summary>
    /// <param name="enabled">The Enabled value.</param>
    /// <param name="endpoint">The Endpoint value, or <see langword="null"/> for unset.</param>
    /// <param name="protocol">The Protocol value.</param>
    /// <param name="interval">The ExportInterval value.</param>
    /// <param name="setting">The setting the failure names.</param>
    [Theory]
    [InlineData("true", null, "grpc", "00:00:15", "Endpoint")]
    [InlineData("true", " ", "grpc", "00:00:15", "Endpoint")]
    [InlineData("true", "http://", "grpc", "00:00:15", "Endpoint")]
    [InlineData("true", "not a uri", "grpc", "00:00:15", "Endpoint")]
    [InlineData("true", "/v1/metrics", "http/protobuf", "00:00:15", "Endpoint")]
    [InlineData("true", "collector:4317", "grpc", "00:00:15", "Endpoint")]
    [InlineData("true", "ftp://collector:4317", "grpc", "00:00:15", "Endpoint")]
    [InlineData("true", "file:///tmp/metrics", "grpc", "00:00:15", "Endpoint")]
    [InlineData("true", "http://user:secret@collector:4318", "http/protobuf", "00:00:15", "Endpoint")]
    [InlineData("true", "http://collector:4318?token=secret", "http/protobuf", "00:00:15", "Endpoint")]
    [InlineData("false", "tcp://collector:4317", "grpc", "00:00:15", "Endpoint")]
    [InlineData("true", "http://collector:4317", "http", "00:00:15", "Protocol")]
    [InlineData("true", "http://collector:4317", "GRPC", "00:00:15", "Protocol")]
    [InlineData("true", "http://collector:4317", "http/json", "00:00:15", "Protocol")]
    [InlineData("true", "http://collector:4317", "", "00:00:15", "Protocol")]
    [InlineData("true", "http://collector:4317", "grpc", "00:00:00.999", "ExportInterval")]
    [InlineData("true", "http://collector:4317", "grpc", "00:05:00.001", "ExportInterval")]
    [InlineData("false", null, "grpc", "00:00:00", "ExportInterval")]
    public void InvalidSettings_FailValidation(string enabled, string? endpoint, string protocol, string interval, string setting)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(Values(enabled, endpoint, protocol, interval)));

        Assert.Equal(typeof(TelemetryOptions), ex.OptionsType);
        Assert.Contains(setting, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("collector", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies valid gRPC and HTTP/protobuf settings, including the interval bounds, bind unchanged.</summary>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="protocol">The protocol.</param>
    /// <param name="interval">The export interval.</param>
    [Theory]
    [InlineData("http://otel-collector:4317", "grpc", "00:00:15")]
    [InlineData("https://otel-collector.example:4317", "grpc", "00:00:01")]
    [InlineData("http://otel-collector:4318", "http/protobuf", "00:05:00")]
    [InlineData("https://otel-collector.example/otlp/", "http/protobuf", "00:01:00")]
    public void ValidSettings_Bind(string endpoint, string protocol, string interval)
    {
        var otlp = Resolve(Values("true", endpoint, protocol, interval));

        Assert.True(otlp.Enabled);
        Assert.Equal((endpoint, protocol, TimeSpan.Parse(interval, System.Globalization.CultureInfo.InvariantCulture)), (otlp.Endpoint, otlp.Protocol, otlp.ExportInterval));
    }

    /// <summary>Verifies unknown keys in the section or the subsection, and unconvertible values, are configuration errors.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    [Theory]
    [InlineData("Telemetry:Otlp:Headers", "api-key=secret")]
    [InlineData("Telemetry:Otlp:Timeout", "00:00:10")]
    [InlineData("Telemetry:Prometheus:Enabled", "true")]
    [InlineData("Telemetry:Otlp:Enabled", "sometimes")]
    [InlineData("Telemetry:Otlp:ExportInterval", "soon")]
    public void UnknownOrUnconvertible_FailsValidation(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();

        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(configuration));
        Assert.Contains("Telemetry", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies invalid telemetry configuration exits with code 78 through the service runner.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData("Telemetry:Otlp:Endpoint", null)]
    [InlineData("Telemetry:Otlp:Endpoint", "collector:4317")]
    [InlineData("Telemetry:Otlp:Protocol", "http/json")]
    [InlineData("Telemetry:Otlp:ExportInterval", "00:10:00")]
    [InlineData("Telemetry:Otlp:Headers", "api-key=secret")]
    public async Task InvalidConfiguration_ExitsWithConfigurationError(string key, string? value)
    {
        var logs = new FakeLogCollector();
        var overrides = TelemetryTestSupport.Enabled("http://127.0.0.1:4318");
        overrides[key] = value;

        var exitCode = await ServiceRunner.RunAsync(TestHosts.Arguments, HostingMode.Console, TestHosts.Configure(logs, overrides), Token).WithinSafetyTimeout();

        Assert.Equal(ExitCodes.ConfigurationError, exitCode);
        Assert.DoesNotContain(logs.GetSnapshot(), static r => r.Id.Id is 8000 or 9300);
    }

    /// <summary>Verifies the URI metrics are sent to: the endpoint for gRPC, the endpoint plus <c>/v1/metrics</c> for HTTP.</summary>
    /// <param name="endpoint">The configured endpoint.</param>
    /// <param name="protocol">The protocol.</param>
    /// <param name="expected">The metrics URI.</param>
    [Theory]
    [InlineData("http://collector:4317", "grpc", "http://collector:4317/")]
    [InlineData("http://collector:4318", "http/protobuf", "http://collector:4318/v1/metrics")]
    [InlineData("http://collector:4318/", "http/protobuf", "http://collector:4318/v1/metrics")]
    [InlineData("https://gateway.example/otlp", "http/protobuf", "https://gateway.example/otlp/v1/metrics")]
    [InlineData("http://collector:4318/v1/metrics", "http/protobuf", "http://collector:4318/v1/metrics")]
    public void MetricsEndpoint_AppendsSignalPathForHttp(string endpoint, string protocol, string expected)
    {
        var uri = TelemetryServiceCollectionExtensions.MetricsEndpoint(new OtlpOptions { Enabled = true, Endpoint = endpoint, Protocol = protocol });

        Assert.Equal(expected, uri.AbsoluteUri);
    }

    /// <summary>Returns configuration with the four settings.</summary>
    /// <param name="enabled">Enabled.</param>
    /// <param name="endpoint">Endpoint, or <see langword="null"/> for unset.</param>
    /// <param name="protocol">Protocol.</param>
    /// <param name="interval">ExportInterval.</param>
    /// <returns>The configuration.</returns>
    private static IConfiguration Values(string enabled, string? endpoint, string protocol, string interval)
    {
        var values = new Dictionary<string, string?>
        {
            ["Telemetry:Otlp:Enabled"] = enabled,
            ["Telemetry:Otlp:Protocol"] = protocol,
            ["Telemetry:Otlp:ExportInterval"] = interval,
        };
        if (endpoint is not null)
        {
            values["Telemetry:Otlp:Endpoint"] = endpoint;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>Binds and validates the telemetry options through the production registration.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The OTLP options.</returns>
    private static OtlpOptions Resolve(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddIngestServiceOptions(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<TelemetryOptions>>().Value.Otlp;
    }
}
