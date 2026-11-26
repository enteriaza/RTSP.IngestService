// Tests for options binding and validation: shipped configuration, code defaults, mandatory values, range and pattern
// validation, strict binding of unknown keys and unconvertible values, startup validation and the mapping of
// LifetimeOptions onto HostOptions.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Configuration;

/// <summary>
/// Tests for <see cref="IngestServiceOptionsExtensions"/> and the options types it registers.
/// </summary>
public sealed class OptionsTests
{
    /// <summary>An empty configuration: every value has a code default or is optional.</summary>
    private static readonly Dictionary<string, string?> MinimalConfiguration = [];

    /// <summary>
    /// Verifies that the appsettings.json shipped with the application binds and validates, with the documented
    /// production values.
    /// </summary>
    [Fact]
    public void ShippedAppSettings_AreValid()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
        using var provider = BuildProvider(configuration);

        var deployment = provider.GetRequiredService<IOptions<ApplicationOptions>>().Value;
        Assert.Null(deployment.InstanceId);
        Assert.Null(deployment.NodeId);
        Assert.Null(deployment.Region);
        Assert.Null(deployment.AvailabilityZone);

        var lifetime = provider.GetRequiredService<IOptions<LifetimeOptions>>().Value;
        Assert.Equal(TimeSpan.FromMinutes(1), lifetime.StartupTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), lifetime.ShutdownTimeout);

        var fileLogging = provider.GetRequiredService<IOptions<FileLoggingOptions>>().Value;
        Assert.Equal(FileLoggingMode.Auto, fileLogging.Mode);
        Assert.Equal("logs/rtsp-ingest-service-.log", fileLogging.Path);
        Assert.Equal(31, fileLogging.RetainedFileCountLimit);
        Assert.Equal(100, fileLogging.FileSizeLimitMegabytes);
        Assert.True(provider.GetRequiredService<IOptions<ManagementOptions>>().Value.Enabled);
    }

    /// <summary>Verifies the code defaults applied when only the mandatory application name is configured.</summary>
    [Fact]
    public void Defaults_AreAppliedWhenSectionsAreAbsent()
    {
        using var provider = BuildProvider(MinimalConfiguration);

        var lifetime = provider.GetRequiredService<IOptions<LifetimeOptions>>().Value;
        Assert.Equal(TimeSpan.FromMinutes(1), lifetime.StartupTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), lifetime.ShutdownTimeout);

        var fileLogging = provider.GetRequiredService<IOptions<FileLoggingOptions>>().Value;
        Assert.Equal(FileLoggingMode.Auto, fileLogging.Mode);
        Assert.Equal("logs/rtsp-ingest-service-.log", fileLogging.Path);
        Assert.Equal(31, fileLogging.RetainedFileCountLimit);
        Assert.Equal(100, fileLogging.FileSizeLimitMegabytes);
        Assert.True(provider.GetRequiredService<IOptions<ManagementOptions>>().Value.Enabled);
    }

    /// <summary>Verifies that a fully specified valid configuration binds every value.</summary>
    [Fact]
    public void ValidConfiguration_BindsAllValues()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Application:NodeId"] = "edge-01.ingest",
            ["Lifetime:StartupTimeout"] = "00:02:00",
            ["Lifetime:ShutdownTimeout"] = "00:00:45",
            ["FileLogging:Mode"] = "enabled",
            ["FileLogging:Path"] = "/var/log/streaming/service-.log",
            ["FileLogging:RetainedFileCountLimit"] = "7",
            ["FileLogging:FileSizeLimitMegabytes"] = "10",
        });

        Assert.Equal("edge-01.ingest", provider.GetRequiredService<IOptions<ApplicationOptions>>().Value.NodeId);
        Assert.Equal(TimeSpan.FromMinutes(2), provider.GetRequiredService<IOptions<LifetimeOptions>>().Value.StartupTimeout);
        Assert.Equal(TimeSpan.FromSeconds(45), provider.GetRequiredService<IOptions<LifetimeOptions>>().Value.ShutdownTimeout);
        var fileLogging = provider.GetRequiredService<IOptions<FileLoggingOptions>>().Value;
        Assert.Equal(FileLoggingMode.Enabled, fileLogging.Mode);
        Assert.Equal("/var/log/streaming/service-.log", fileLogging.Path);
        Assert.Equal(7, fileLogging.RetainedFileCountLimit);
        Assert.Equal(10, fileLogging.FileSizeLimitMegabytes);
    }

    /// <summary>
    /// Verifies that out-of-range values fail validation for each options type, including values just outside the
    /// inclusive bounds.
    /// </summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="value">An out-of-range value.</param>
    [Theory]
    [InlineData("Lifetime:StartupTimeout", "00:00:00")]
    [InlineData("Lifetime:StartupTimeout", "01:00:01")]
    [InlineData("Lifetime:ShutdownTimeout", "00:00:00.999")]
    [InlineData("Lifetime:ShutdownTimeout", "00:10:01")]
    [InlineData("FileLogging:RetainedFileCountLimit", "0")]
    [InlineData("FileLogging:RetainedFileCountLimit", "366")]
    [InlineData("FileLogging:FileSizeLimitMegabytes", "0")]
    [InlineData("FileLogging:FileSizeLimitMegabytes", "10241")]
    [InlineData("FileLogging:Path", " ")]
    public void OutOfRangeValue_FailsValidation(string key, string value)
    {
        using var provider = BuildProvider(new Dictionary<string, string?>(MinimalConfiguration) { [key] = value });

        Assert.Throws<OptionsValidationException>(() => ResolveSection(provider, key));
    }

    /// <summary>Verifies that inclusive bounds are accepted.</summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="value">A boundary value.</param>
    [Theory]
    [InlineData("Lifetime:ShutdownTimeout", "00:00:01")]
    [InlineData("Lifetime:ShutdownTimeout", "00:10:00")]
    [InlineData("Lifetime:StartupTimeout", "01:00:00")]
    [InlineData("Lifetime:StartupTimeout", "00:00:01")]
    [InlineData("FileLogging:RetainedFileCountLimit", "365")]
    public void BoundaryValue_IsAccepted(string key, string value)
    {
        using var provider = BuildProvider(new Dictionary<string, string?>(MinimalConfiguration) { [key] = value });

        Assert.NotNull(ResolveSection(provider, key));
    }

    /// <summary>
    /// Verifies that an unknown key in a bound section is rejected as an <see cref="OptionsValidationException"/>
    /// whose message names the key.
    /// </summary>
    [Fact]
    public void UnknownKey_FailsAsValidationError()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>(MinimalConfiguration) { ["Lifetime:ShutdownTimout"] = "00:00:10" });

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<LifetimeOptions>>().Value);
        Assert.Contains("ShutdownTimout", ex.Message, StringComparison.Ordinal);
        Assert.Equal(typeof(LifetimeOptions), ex.OptionsType);
    }

    /// <summary>Verifies that a value that cannot be converted is rejected as an <see cref="OptionsValidationException"/>.</summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="value">An unconvertible value.</param>
    [Theory]
    [InlineData("FileLogging:Mode", "Sometimes")]
    [InlineData("Lifetime:StartupTimeout", "soon")]
    [InlineData("FileLogging:RetainedFileCountLimit", "many")]
    public void UnconvertibleValue_FailsAsValidationError(string key, string value)
    {
        using var provider = BuildProvider(new Dictionary<string, string?>(MinimalConfiguration) { [key] = value });

        var ex = Assert.Throws<OptionsValidationException>(() => ResolveSection(provider, key));
        Assert.Contains(key.Split(':')[0], ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that startup validation rejects invalid configuration when the host starts, before any hosted
    /// service is started.
    /// </summary>
    [Fact]
    public async Task StartupValidation_FailsBeforeHostedServicesStart()
    {
        var journal = new LifecycleJournal();
        var builder = TestHosts.CreateBuilder(new FakeLogCollector(), overrides: new Dictionary<string, string?> { ["Rtmp:Port"] = "70000" });
        builder.Services.AddSingleton(journal);
        builder.Services.AddHostedService<RecordingServiceA>();
        using var host = builder.Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Empty(journal.Events);
    }

    /// <summary>
    /// Verifies that HostOptions takes its shutdown timeout from LifetimeOptions, leaves the startup timeout to
    /// ServiceRunner, and enforces sequential start/stop with the StopHost background-exception behaviour.
    /// </summary>
    [Fact]
    public void HostOptions_AreMappedFromLifetimeOptions()
    {
        using var host = TestHosts.CreateBuilder(new FakeLogCollector(), overrides: new Dictionary<string, string?>
        {
            ["Lifetime:StartupTimeout"] = "00:00:20",
            ["Lifetime:ShutdownTimeout"] = "00:00:40",
        }).Build();

        var options = host.Services.GetRequiredService<IOptions<HostOptions>>().Value;
        Assert.Equal(Timeout.InfiniteTimeSpan, options.StartupTimeout);
        Assert.Equal(TimeSpan.FromSeconds(40), options.ShutdownTimeout);
        Assert.False(options.ServicesStartConcurrently);
        Assert.False(options.ServicesStopConcurrently);
        Assert.Equal(BackgroundServiceExceptionBehavior.StopHost, options.BackgroundServiceExceptionBehavior);
    }

    /// <summary>Verifies that invalid lifetime options make host construction fail with a validation error.</summary>
    [Fact]
    public void InvalidLifetimeOptions_FailHostBuild()
    {
        var builder = TestHosts.CreateBuilder(new FakeLogCollector(), overrides: new Dictionary<string, string?> { ["Lifetime:ShutdownTimeout"] = "00:00:00" });

        Assert.Throws<OptionsValidationException>(builder.Build);
    }

    /// <summary>Builds a provider containing only the options registrations over in-memory configuration.</summary>
    /// <param name="values">Configuration values.</param>
    /// <returns>The service provider.</returns>
    private static ServiceProvider BuildProvider(IReadOnlyDictionary<string, string?> values) =>
        BuildProvider(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    /// <summary>Builds a provider containing only the options registrations.</summary>
    /// <param name="configuration">The configuration to bind.</param>
    /// <returns>The service provider.</returns>
    private static ServiceProvider BuildProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddIngestServiceOptions(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>Resolves the options type bound to the section named by the first segment of <paramref name="key"/>.</summary>
    /// <param name="provider">The service provider.</param>
    /// <param name="key">A configuration key such as <c>Lifetime:ShutdownTimeout</c>.</param>
    /// <returns>The resolved options instance.</returns>
    private static object ResolveSection(IServiceProvider provider, string key) => key.Split(':')[0] switch
    {
        ApplicationOptions.SectionName => provider.GetRequiredService<IOptions<ApplicationOptions>>().Value,
        LifetimeOptions.SectionName => provider.GetRequiredService<IOptions<LifetimeOptions>>().Value,
        FileLoggingOptions.SectionName => provider.GetRequiredService<IOptions<FileLoggingOptions>>().Value,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown section."),
    };
}
