// Factory for real service hosts used by integration-style tests. Hosts are built through the production composition
// root and then adjusted for testing: the environment is fixed to "Testing", configuration overrides are layered on
// top of the shipped appsettings.json, and logging is redirected to an in-memory FakeLogCollector.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using RTSP.IngestService.Hosting;

namespace RTSP.IngestService.Tests.Infrastructure;

/// <summary>
/// Creates hosts and builder callbacks for tests.
/// </summary>
internal static class TestHosts
{
    /// <summary>Command-line arguments that select the Testing environment, so no Development settings are loaded.</summary>
    public static readonly string[] Arguments = ["--environment", "Testing"];

    /// <summary>
    /// Creates a builder through <see cref="IngestServiceHost.CreateBuilder"/> and applies <see cref="Configure"/>.
    /// </summary>
    /// <param name="collector">Receives every log record written by the host.</param>
    /// <param name="mode">The hosting mode to compose.</param>
    /// <param name="overrides">Configuration values layered over appsettings.json.</param>
    /// <returns>The configured builder.</returns>
    public static HostApplicationBuilder CreateBuilder(FakeLogCollector collector, HostingMode mode = HostingMode.Console, IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var builder = IngestServiceHost.CreateBuilder(Arguments, mode);
        Configure(collector, overrides)(builder);
        return builder;
    }

    /// <summary>
    /// Returns a builder callback that layers <paramref name="overrides"/> over the configuration, enables all log
    /// levels and replaces the Serilog provider with a fake provider writing to <paramref name="collector"/>.
    /// </summary>
    /// <param name="collector">Receives every log record written by the host.</param>
    /// <param name="overrides">Configuration values layered over appsettings.json.</param>
    /// <returns>A callback suitable for <see cref="ServiceRunner.RunAsync(string[], HostingMode, Action{HostApplicationBuilder}?, CancellationToken)"/>.</returns>
    public static Action<HostApplicationBuilder> Configure(FakeLogCollector collector, IReadOnlyDictionary<string, string?>? overrides = null) => builder =>
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Logging:LogLevel:Default"] = "Trace",
            ["Rtmp:ListenAddress"] = "127.0.0.1",
            ["Rtmp:Port"] = "0",
            ["Http:Port"] = "0",
        };

        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        builder.Configuration.AddInMemoryCollection(values);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FakeLoggerProvider(collector));
    };
}
