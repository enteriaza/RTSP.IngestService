// End-to-end tests of hashed publisher credentials in the real service host over TCP RTMP: per-credential
// applications propagate to the registry, HLS store, events and catalog; failed attempts leave no session, registry
// entry, HLS stream or Published event; revocation and rotation take effect after a reload without ending established
// sessions; and the failure rate limit rejects further attempts without affecting an established publisher.

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Testing;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Rtmp;
using RTSP.IngestService.Rtmp.Amf;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Streams;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Rtmp;

/// <summary>Host-level tests for <see cref="ConfiguredPublisherAuthenticator"/> and <see cref="PublisherCredentialStore"/>.</summary>
public sealed class PublisherCredentialHostTests
{
    /// <summary>Key of camera A.</summary>
    private const string KeyA = "host-key-aaaaaaaaaaaaaaaa";

    /// <summary>Key of camera B.</summary>
    private const string KeyB = "host-key-bbbbbbbbbbbbbbbb";

    /// <summary>Key of camera C.</summary>
    private const string KeyC = "host-key-cccccccccccccccc";

    /// <summary>Captured host logs.</summary>
    private readonly FakeLogCollector _logs = new();

    /// <summary>Received stream events.</summary>
    private readonly RecordingConsumer _events = new();

    /// <summary>
    /// Verifies that each credential publishes only to its own application, that the authenticated application reaches
    /// the registry, HLS store, events and catalog, and that failed attempts leave no trace and expose no secret.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Credentials_AssignApplications_AndFailuresLeaveNoTrace()
    {
        var provider = new ReloadableConfigurationProvider(
            WithRateLimit(TestCredential.Settings(new TestCredential("cam-a", "live", KeyA), new TestCredential("cam-b", "studio", KeyB)), 100));
        using var host = Build(provider);
        var registry = host.Services.GetRequiredService<PublishedStreamRegistry>();
        var inventory = (IHlsStreamInventory)host.Services.GetRequiredService<IHlsSegmentStore>();
        var catalog = host.Services.GetRequiredService<StreamCatalog>();
        var hashes = provider.GetChildKeys([], "Rtmp:Authentication:Publishers").Distinct().Select(i => Value(provider, $"Rtmp:Authentication:Publishers:{i}:KeyHash")).ToList();

        await host.StartAsync(TestContext.Current.CancellationToken).WithinSafetyTimeout();
        try
        {
            await RejectedAsync(host, KeyA, "studio");
            await RejectedAsync(host, KeyB, "live");
            await RejectedAsync(host, "host-key-wrongwrongwrongw", "live");
            await RejectedAsync(host, "short", "live");

            Assert.Equal(0, registry.Count);
            Assert.Empty(inventory.GetStreams());
            Assert.DoesNotContain(_events.Received, static e => e.Type == StreamEventType.Published);
            Assert.Empty(catalog.Current.Streams);

            await using var a = await RtmpTestClient.ConnectAsync(host.Services.GetRequiredService<RtmpListener>().LocalEndPoint!);
            await using var b = await RtmpTestClient.ConnectAsync(host.Services.GetRequiredService<RtmpListener>().LocalEndPoint!);
            Assert.Equal("NetStream.Publish.Start", (await a.PublishFlowAsync(KeyA, "live")).Code);
            Assert.Equal("NetStream.Publish.Start", (await b.PublishFlowAsync(KeyB, "studio")).Code);

            Assert.NotNull(registry.Find("live", "cam-a"));
            Assert.NotNull(registry.Find("studio", "cam-b"));
            Assert.Equal(2, registry.Count);
            await Eventually.WaitUntilAsync(
                () => _events.Received.Count(static e => e.Type == StreamEventType.Published) == 2, "both publications are announced");
            Assert.Contains(_events.Received, static e => e.Type == StreamEventType.Published && (e.Application, e.Stream) == ("live", "cam-a"));
            Assert.Contains(_events.Received, static e => e.Type == StreamEventType.Published && (e.Application, e.Stream) == ("studio", "cam-b"));
            await Eventually.WaitUntilAsync(
                () => catalog.Current.Find("live", "cam-a") is not null && catalog.Current.Find("studio", "cam-b") is not null, "both publications are catalogued");
            Assert.Contains(inventory.GetStreams(), static s => (s.Application, s.Stream) == ("live", "cam-a"));
            Assert.Contains(inventory.GetStreams(), static s => (s.Application, s.Stream) == ("studio", "cam-b"));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WithinSafetyTimeout();
        }

        Assert.Equal(4, _logs.GetSnapshot().Count(static r => r.Id.Id == 5205));
        AssertNoSecrets([KeyA, KeyB, .. hashes.Select(static h => h.Split('$')[2])]);
    }

    /// <summary>
    /// Verifies revocation and rotation by reload: an established session keeps publishing after its credential is
    /// disabled, a new attempt with the revoked key is rejected, and a key added by the reload is accepted.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Reload_RevokesAndRotates_WithoutEndingEstablishedSessions()
    {
        var provider = new ReloadableConfigurationProvider(WithRateLimit(TestCredential.Settings(new TestCredential("cam-a", "live", KeyA)), 100));
        using var host = Build(provider);
        var registry = host.Services.GetRequiredService<PublishedStreamRegistry>();

        await host.StartAsync(TestContext.Current.CancellationToken).WithinSafetyTimeout();
        try
        {
            await using var established = await RtmpTestClient.ConnectAsync(host.Services.GetRequiredService<RtmpListener>().LocalEndPoint!);
            Assert.Equal("NetStream.Publish.Start", (await established.PublishFlowAsync(KeyA)).Code);
            var session = registry.Find("live", "cam-a");
            Assert.NotNull(session);

            provider.Replace(WithRateLimit(TestCredential.Settings(new TestCredential("cam-a", "live", KeyA, Enabled: false), new TestCredential("cam-c", "live", KeyC)), 100));
            Assert.Single(_logs.GetSnapshot(), static r => r.Id.Id == 5301);

            await RejectedAsync(host, KeyA, "live");
            await using var rotated = await RtmpTestClient.ConnectAsync(host.Services.GetRequiredService<RtmpListener>().LocalEndPoint!);
            Assert.Equal("NetStream.Publish.Start", (await rotated.PublishFlowAsync(KeyC)).Code);

            Assert.Same(session, registry.Find("live", "cam-a"));
            Assert.Null(session.EndReason);
            await established.SendCommandAsync(0, "createStream", AmfValue.Null);
            Assert.Equal("_result", (await established.ReadCommandAsync("_result", "_error"))[0].StringValue);
            Assert.Same(session, registry.Find("live", "cam-a"));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WithinSafetyTimeout();
        }

        Assert.DoesNotContain(_logs.GetSnapshot(), static r => r.Id.Id == 5302);
    }

    /// <summary>
    /// Verifies the failure rate limit over RTMP: after the allowance is spent, even a correct key from the same
    /// address is rejected without a session, while the established publisher from that address is unaffected.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task RateLimit_RejectsAfterFailures_WithoutAffectingEstablishedSession()
    {
        var provider = new ReloadableConfigurationProvider(
            WithRateLimit(TestCredential.Settings(new TestCredential("cam-a", "live", KeyA), new TestCredential("cam-b", "live", KeyB)), 2));
        using var host = Build(provider);
        var registry = host.Services.GetRequiredService<PublishedStreamRegistry>();
        using var attempts = new MetricCollector<long>(host.Services.GetRequiredService<IMeterFactory>(), RtmpMetrics.MeterName, "rtmp.authentication.attempts");

        await host.StartAsync(TestContext.Current.CancellationToken).WithinSafetyTimeout();
        try
        {
            await using var established = await RtmpTestClient.ConnectAsync(host.Services.GetRequiredService<RtmpListener>().LocalEndPoint!);
            Assert.Equal("NetStream.Publish.Start", (await established.PublishFlowAsync(KeyA)).Code);

            await RejectedAsync(host, "host-key-wrong00000000001", "live");
            await RejectedAsync(host, "host-key-wrong00000000002", "live");
            await RejectedAsync(host, KeyB, "live");

            Assert.Equal(["success", "invalid_credential", "invalid_credential", "rate_limited"], attempts.GetMeasurementSnapshot().Select(static m => (string)m.Tags["result"]!));
            Assert.Null(registry.Find("live", "cam-b"));
            var session = registry.Find("live", "cam-a");
            Assert.NotNull(session);
            Assert.Null(session.EndReason);
            await established.SendCommandAsync(0, "createStream", AmfValue.Null);
            Assert.Equal("_result", (await established.ReadCommandAsync("_result", "_error"))[0].StringValue);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WithinSafetyTimeout();
        }
    }

    /// <summary>Connects, attempts to publish, and verifies the attempt is unauthorised and the connection closed.</summary>
    /// <param name="host">The started host.</param>
    /// <param name="key">The presented key.</param>
    /// <param name="application">The application to connect to.</param>
    /// <returns>A task that completes when the server has closed the connection.</returns>
    private static async Task RejectedAsync(IHost host, string key, string application)
    {
        await using var client = await RtmpTestClient.ConnectAsync(host.Services.GetRequiredService<RtmpListener>().LocalEndPoint!);
        Assert.Equal("NetStream.Publish.Unauthorized", (await client.PublishFlowAsync(key, application)).Code);
        await client.WaitForCloseAsync().WithinSafetyTimeout();
    }

    /// <summary>Adds the rate limit to credential settings.</summary>
    /// <param name="settings">The credential settings.</param>
    /// <param name="failureLimit">The failure limit.</param>
    /// <returns>The same dictionary.</returns>
    private static Dictionary<string, string?> WithRateLimit(Dictionary<string, string?> settings, int failureLimit)
    {
        settings["Rtmp:Authentication:RateLimit:FailureLimit"] = failureLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return settings;
    }

    /// <summary>Reads a value from the provider.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="key">The key.</param>
    /// <returns>The value.</returns>
    private static string Value(ReloadableConfigurationProvider provider, string key) =>
        provider.TryGet(key, out var value) ? value! : throw new KeyNotFoundException(key);

    /// <summary>Builds a host whose credentials come from <paramref name="provider"/>.</summary>
    /// <param name="provider">The reloadable credential configuration.</param>
    /// <returns>The host.</returns>
    private IHost Build(ReloadableConfigurationProvider provider)
    {
        var builder = TestHosts.CreateBuilder(_logs);
        ((IConfigurationBuilder)builder.Configuration).Add(new ReloadableConfigurationSource(provider));
        builder.Services.AddSingleton<IStreamEventConsumer>(sp =>
        {
            _events.Inner = sp.GetRequiredService<StreamCatalog>();
            return _events;
        });
        return builder.Build();
    }

    /// <summary>Asserts that no log record contains any of <paramref name="secrets"/>.</summary>
    /// <param name="secrets">Keys and hash digests.</param>
    private void AssertNoSecrets(IEnumerable<string> secrets)
    {
        var records = _logs.GetSnapshot();
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(records, r => r.Message.Contains(secret, StringComparison.Ordinal));
        }
    }

    /// <summary>Records every stream event and forwards it to the catalog, which is the host's only consumer.</summary>
    private sealed class RecordingConsumer : IStreamEventConsumer
    {
        /// <summary>Gets the received events.</summary>
        public ConcurrentQueue<StreamEvent> Received { get; } = new();

        /// <summary>Gets or sets the consumer events are forwarded to.</summary>
        public StreamCatalog? Inner { get; set; }

        /// <inheritdoc />
        public ValueTask ConsumeAsync(StreamEvent streamEvent, CancellationToken cancellationToken)
        {
            Received.Enqueue(streamEvent);
            return Inner?.ConsumeAsync(streamEvent, cancellationToken) ?? ValueTask.CompletedTask;
        }
    }
}
