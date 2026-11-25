// Test support for publisher authentication: a configuration provider whose whole content can be replaced and
// reloaded synchronously, credential settings builders, and a fixture that composes the production options and RTMP
// registrations so that the authenticator, credential store and rate limiter are tested exactly as the host wires them.

using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Rtmp;
using RTSP.IngestService.Rtmp.Publishing;

namespace RTSP.IngestService.Tests.Rtmp;

/// <summary>A configuration source for <see cref="ReloadableConfigurationProvider"/>.</summary>
/// <param name="provider">The provider instance to return.</param>
internal sealed class ReloadableConfigurationSource(ReloadableConfigurationProvider provider) : IConfigurationSource
{
    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder) => provider;
}

/// <summary>
/// A configuration provider whose content is replaced as a whole; <see cref="Replace"/> raises the reload token
/// synchronously, so change listeners have run when it returns.
/// </summary>
internal sealed class ReloadableConfigurationProvider : ConfigurationProvider
{
    /// <summary>Initializes a new instance of the <see cref="ReloadableConfigurationProvider"/> class.</summary>
    /// <param name="values">The initial values.</param>
    public ReloadableConfigurationProvider(IReadOnlyDictionary<string, string?> values) =>
        Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);

    /// <summary>Replaces every value and signals a reload.</summary>
    /// <param name="values">The new values.</param>
    public void Replace(IReadOnlyDictionary<string, string?> values)
    {
        Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        OnReload();
    }
}

/// <summary>One publisher credential for test configuration.</summary>
/// <param name="Name">The name and stream identity.</param>
/// <param name="Application">The application.</param>
/// <param name="Key">The plaintext key; only its hash is placed in configuration.</param>
/// <param name="Enabled">Whether the credential is enabled.</param>
internal sealed record TestCredential(string Name, string Application, string Key, bool Enabled = true)
{
    /// <summary>Creates configuration values for <paramref name="credentials"/> under <c>Rtmp:Authentication:Publishers</c>.</summary>
    /// <param name="credentials">The credentials, hashed with fresh salts.</param>
    /// <returns>The values.</returns>
    public static Dictionary<string, string?> Settings(params TestCredential[] credentials)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < credentials.Length; i++)
        {
            var c = credentials[i];
            values[$"Rtmp:Authentication:Publishers:{i}:Name"] = c.Name;
            values[$"Rtmp:Authentication:Publishers:{i}:Application"] = c.Application;
            values[$"Rtmp:Authentication:Publishers:{i}:KeyHash"] = PublisherKeyHash.Create(c.Key);
            values[$"Rtmp:Authentication:Publishers:{i}:Enabled"] = c.Enabled ? "true" : "false";
        }

        return values;
    }
}

/// <summary>
/// Composes <see cref="IngestServiceOptionsExtensions.AddIngestServiceOptions"/> and
/// <see cref="RtmpServiceCollectionExtensions.AddRtmpIngest"/> over a reloadable configuration.
/// </summary>
internal sealed class AuthenticationFixture : IDisposable
{
    /// <summary>The service provider.</summary>
    private readonly ServiceProvider _services;

    /// <summary>Settings layered under every configuration (for example the rate limit).</summary>
    private readonly Dictionary<string, string?> _baseSettings;

    /// <summary>Initializes a new instance of the <see cref="AuthenticationFixture"/> class.</summary>
    /// <param name="settings">The initial credential and RTMP settings.</param>
    /// <param name="baseSettings">Settings kept across <see cref="Reload"/> calls.</param>
    public AuthenticationFixture(IReadOnlyDictionary<string, string?> settings, IReadOnlyDictionary<string, string?>? baseSettings = null)
    {
        _baseSettings = new Dictionary<string, string?>(baseSettings ?? new Dictionary<string, string?>(), StringComparer.OrdinalIgnoreCase);
        Provider = new ReloadableConfigurationProvider(Merge(settings));
        var configuration = new ConfigurationBuilder().Add(new ReloadableConfigurationSource(Provider)).Build();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Time);
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(new FakeLoggerProvider(Logs)));
        services.AddIngestServiceOptions(configuration);
        services.AddRtmpIngest();
        _services = services.BuildServiceProvider();

        var meters = _services.GetRequiredService<IMeterFactory>();
        Attempts = new MetricCollector<long>(meters, RtmpMetrics.MeterName, "rtmp.authentication.attempts");
        Reloads = new MetricCollector<long>(meters, RtmpMetrics.MeterName, "rtmp.authentication.reloads");
        Authenticator = _services.GetRequiredService<IPublisherAuthenticator>();
        Store = _services.GetRequiredService<PublisherCredentialStore>();
        RateLimiter = _services.GetRequiredService<PublisherAuthenticationRateLimiter>();
    }

    /// <summary>Gets the clock.</summary>
    public FakeTimeProvider Time { get; } = new();

    /// <summary>Gets the captured logs.</summary>
    public FakeLogCollector Logs { get; } = new();

    /// <summary>Gets the configuration provider.</summary>
    public ReloadableConfigurationProvider Provider { get; }

    /// <summary>Gets the authenticator registered for the host.</summary>
    public IPublisherAuthenticator Authenticator { get; }

    /// <summary>Gets the credential store.</summary>
    public PublisherCredentialStore Store { get; }

    /// <summary>Gets the rate limiter.</summary>
    public PublisherAuthenticationRateLimiter RateLimiter { get; }

    /// <summary>Gets the authentication attempt measurements.</summary>
    public MetricCollector<long> Attempts { get; }

    /// <summary>Gets the reload measurements.</summary>
    public MetricCollector<long> Reloads { get; }

    /// <summary>Creates a publish request from a TCP peer.</summary>
    /// <param name="key">The presented stream key.</param>
    /// <param name="application">The application the publisher connected to.</param>
    /// <param name="address">The peer address.</param>
    /// <returns>The request.</returns>
    public static PublishAuthenticationRequest Request(string key, string application = "live", string address = "192.0.2.10") =>
        new(1, new IPEndPoint(IPAddress.Parse(address), 50000), application, key, null, $"rtmp://host/{application}", "live");

    /// <summary>Replaces the configuration and reloads synchronously.</summary>
    /// <param name="settings">The new settings, layered over the base settings.</param>
    public void Reload(IReadOnlyDictionary<string, string?> settings) => Provider.Replace(Merge(settings));

    /// <summary>Authenticates a request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The result.</returns>
    public async Task<PublishAuthenticationResult> AuthenticateAsync(PublishAuthenticationRequest request) =>
        await Authenticator.AuthenticateAsync(request, TestContext.Current.CancellationToken);

    /// <summary>Returns the <c>result</c> tags of recorded attempts, in order.</summary>
    /// <returns>The tags.</returns>
    public string[] AttemptResults() => [.. Attempts.GetMeasurementSnapshot().Select(static m => (string)m.Tags["result"]!)];

    /// <inheritdoc />
    public void Dispose()
    {
        Attempts.Dispose();
        Reloads.Dispose();
        _services.Dispose();
    }

    /// <summary>Layers <paramref name="settings"/> over the base settings.</summary>
    /// <param name="settings">The settings.</param>
    /// <returns>The merged values.</returns>
    private Dictionary<string, string?> Merge(IReadOnlyDictionary<string, string?> settings)
    {
        var values = new Dictionary<string, string?>(_baseSettings, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        return values;
    }
}
