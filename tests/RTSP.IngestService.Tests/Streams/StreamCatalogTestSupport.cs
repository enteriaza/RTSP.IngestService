// Test support for the stream catalog: a scriptable inventory source whose contents, failures and acquisition-time
// interleavings are set by the test, and a fixture owning a catalog over it with fake time, metrics and logs.

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Streams;

namespace RTSP.IngestService.Tests.Streams;

/// <summary>An inventory source whose snapshot is set by the test.</summary>
internal sealed class FakeInventorySource : IStreamInventorySource
{
    /// <summary>Gets the HLS instances returned.</summary>
    public List<HlsStreamStatus> Hls { get; } = [];

    /// <summary>Gets the registered sessions returned.</summary>
    public List<PublishingStatus> Publishing { get; } = [];

    /// <summary>Gets or sets a value indicating whether acquisition throws.</summary>
    public bool Fail { get; set; }

    /// <summary>Gets or sets an action run after the copy is taken and before it is returned, simulating concurrent changes.</summary>
    public Action? DuringAcquire { get; set; }

    /// <summary>Gets the number of acquisitions.</summary>
    public int Acquisitions => Volatile.Read(ref _acquisitions);

    /// <summary>Backs <see cref="Acquisitions"/>.</summary>
    private int _acquisitions;

    /// <inheritdoc />
    public StreamInventorySnapshot Acquire()
    {
        Interlocked.Increment(ref _acquisitions);
        if (Fail)
        {
            throw new InvalidOperationException("Simulated source failure");
        }

        var snapshot = new StreamInventorySnapshot([.. Hls], [.. Publishing]);
        var during = DuringAcquire;
        DuringAcquire = null;
        during?.Invoke();
        return snapshot;
    }

    /// <summary>Adds a publishing session.</summary>
    /// <param name="incarnation">The incarnation identifier.</param>
    /// <param name="generation">The generation.</param>
    /// <param name="stream">The stream identity.</param>
    /// <param name="media">The media description.</param>
    /// <returns>This source.</returns>
    public FakeInventorySource Publish(string incarnation, long generation, string stream = "cam", StreamMediaDescription? media = null)
    {
        Publishing.Add(new PublishingStatus("live", stream, incarnation, generation, true, DateTimeOffset.UnixEpoch, null, null, media));
        return this;
    }

    /// <summary>Adds an HLS instance.</summary>
    /// <param name="incarnation">The incarnation identifier.</param>
    /// <param name="generation">The generation.</param>
    /// <param name="state">The state.</param>
    /// <param name="stream">The stream identity.</param>
    /// <param name="playlistAvailable">Whether the store serves its playlist; by default, when live or ended (a playlist has been published).</param>
    /// <returns>This source.</returns>
    public FakeInventorySource Store(string incarnation, long generation, HlsStreamState state, string stream = "cam", bool? playlistAvailable = null)
    {
        Hls.Add(new HlsStreamStatus("live", stream, incarnation, generation, state, playlistAvailable ?? state is HlsStreamState.Live or HlsStreamState.Ended));
        return this;
    }

    /// <summary>Removes everything about an incarnation.</summary>
    /// <param name="incarnation">The incarnation identifier.</param>
    public void Forget(string incarnation)
    {
        Hls.RemoveAll(h => h.IncarnationId == incarnation);
        Publishing.RemoveAll(p => p.IncarnationId == incarnation);
    }
}

/// <summary>Owns a catalog over a <see cref="FakeInventorySource"/>.</summary>
internal sealed class CatalogFixture : IDisposable
{
    /// <summary>Owns the meter factory.</summary>
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();

    /// <summary>Initializes a new instance of the <see cref="CatalogFixture"/> class.</summary>
    public CatalogFixture()
    {
        Meters = _services.GetRequiredService<IMeterFactory>();
        Requests = new MetricCollector<long>(Meters, StreamEventMetrics.MeterName, "stream_catalog.reconciliation.requests");
        Reconciliations = new MetricCollector<long>(Meters, StreamEventMetrics.MeterName, "stream_catalog.reconciliations");
        Coalesced = new MetricCollector<long>(Meters, StreamEventMetrics.MeterName, "stream_catalog.reconciliation.coalesced");
        Catalog = new StreamCatalog(Source, new StreamCatalogMetrics(Meters), Time, new FakeLogger<StreamCatalog>(Logs), () => Dropped);
    }

    /// <summary>Gets the source.</summary>
    public FakeInventorySource Source { get; } = new();

    /// <summary>Gets the fake time, starting at 2026-01-01.</summary>
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>Gets the captured logs.</summary>
    public FakeLogCollector Logs { get; } = new();

    /// <summary>Gets the meter factory.</summary>
    public IMeterFactory Meters { get; }

    /// <summary>Gets the reconciliation request measurements.</summary>
    public MetricCollector<long> Requests { get; }

    /// <summary>Gets the reconciliation attempt measurements.</summary>
    public MetricCollector<long> Reconciliations { get; }

    /// <summary>Gets the coalesced request measurements.</summary>
    public MetricCollector<long> Coalesced { get; }

    /// <summary>Gets the catalog.</summary>
    public StreamCatalog Catalog { get; }

    /// <summary>Gets or sets the simulated channel dropped count.</summary>
    public long Dropped { get; set; }

    /// <summary>Returns the reasons of the recorded reconciliation requests.</summary>
    /// <returns>The reason tags, in order.</returns>
    public IEnumerable<string?> RequestReasons() => Requests.GetMeasurementSnapshot().Select(static m => m.Tags["reason"] as string);

    /// <summary>Creates an event.</summary>
    /// <param name="type">The type.</param>
    /// <param name="incarnation">The incarnation.</param>
    /// <param name="reason">The reason.</param>
    /// <param name="media">The media.</param>
    /// <param name="stream">The stream identity.</param>
    /// <returns>The event.</returns>
    public StreamEvent Event(StreamEventType type, string incarnation, string? reason = null, StreamMediaDescription? media = null, string stream = "cam") =>
        StreamEventFactory.Create(type, "live", stream, incarnation, Time.GetUtcNow(), reason, media);

    /// <summary>Delivers an event as the dispatcher would.</summary>
    /// <param name="streamEvent">The event.</param>
    public void Deliver(StreamEvent streamEvent) => Catalog.ConsumeAsync(streamEvent, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <summary>Disposes the catalog, collectors and meters.</summary>
    public void Dispose()
    {
        Catalog.Dispose();
        Requests.Dispose();
        Reconciliations.Dispose();
        Coalesced.Dispose();
        _services.Dispose();
    }
}

/// <summary>Media descriptions for tests.</summary>
internal static class TestMedia
{
    /// <summary>Returns a video description.</summary>
    /// <param name="version">The configuration version.</param>
    /// <returns>The description.</returns>
    public static StreamVideoDescription Video(int version = 1) => new() { Codec = "avc1.64001F", ConfigurationVersion = version, Width = 1280, Height = 720 };

    /// <summary>Returns an audio description.</summary>
    /// <param name="version">The configuration version.</param>
    /// <returns>The description.</returns>
    public static StreamAudioDescription Audio(int version = 1) => new() { Codec = "mp4a.40.2", ConfigurationVersion = version, SampleRate = 48000, ChannelConfiguration = 2, Channels = 2 };
}
