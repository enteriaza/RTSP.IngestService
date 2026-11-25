// Tests of the stream catalog's reconciliation service with fake time: the startup reconciliation completes before
// StartAsync returns, a failed one is retried, the periodic timer requests reconciliations, a request arriving during
// a reconciliation causes exactly one more, and stopping ends the loop promptly. Gates synchronise; nothing sleeps.

using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Streams;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Streams;

/// <summary>Tests for <see cref="StreamCatalogService"/>.</summary>
public sealed class StreamCatalogServiceTests : IAsyncDisposable
{
    /// <summary>The reconciliation interval used.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>The fixture.</summary>
    private readonly CatalogFixture _f = new();

    /// <summary>The service.</summary>
    private readonly StreamCatalogService _service;

    /// <summary>Initializes a new instance of the <see cref="StreamCatalogServiceTests"/> class.</summary>
    public StreamCatalogServiceTests() =>
        _service = new StreamCatalogService(_f.Catalog, Options.Create(new StreamCatalogOptions { ReconciliationInterval = Interval }), _f.Time);

    /// <summary>Gets the test cancellation token.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
        _f.Dispose();
    }

    /// <summary>Verifies StartAsync returns only after the startup reconciliation published an initialized catalog.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Start_ReconcilesBeforeReturning()
    {
        _f.Source.Publish("0123456789abcdef0123456789abcdef", 1).Store("0123456789abcdef0123456789abcdef", 1, HlsStreamState.Live);

        await _service.StartAsync(Token);

        Assert.True(_f.Catalog.Current.IsInitialized);
        Assert.Single(_f.Catalog.Current.Streams);
        Assert.Equal(["startup"], _f.RequestReasons());
    }

    /// <summary>Verifies a failed startup reconciliation leaves the catalog uninitialized and is retried by the loop.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Start_FailedReconciliation_IsRetried()
    {
        _f.Source.Fail = true;

        await _service.StartAsync(Token);
        Assert.False(_f.Catalog.Current.IsInitialized);

        _f.Source.Fail = false;
        _f.Catalog.RequestReconciliation(StreamCatalogTrigger.Event);
        await Eventually.WaitUntilAsync(() => _f.Catalog.Current.IsInitialized, "the retry initializes the catalog");
        Assert.Equal(["startup", "startup", "event"], _f.RequestReasons());
    }

    /// <summary>Verifies each elapsed interval requests and runs a reconciliation, and none runs before it elapses.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Timer_RequestsPeriodicReconciliation()
    {
        await _service.StartAsync(Token);
        Assert.Equal(1, _f.Source.Acquisitions);

        _f.Time.Advance(Interval - TimeSpan.FromSeconds(1));
        Assert.DoesNotContain("periodic", _f.RequestReasons());

        _f.Time.Advance(TimeSpan.FromSeconds(1));
        await Eventually.WaitUntilAsync(() => _f.Source.Acquisitions == 2, "the first periodic reconciliation runs");
        _f.Time.Advance(Interval);
        await Eventually.WaitUntilAsync(() => _f.Source.Acquisitions == 3, "the second periodic reconciliation runs");
        Assert.Equal(["startup", "periodic", "periodic"], _f.RequestReasons());
    }

    /// <summary>
    /// Verifies requests arriving while a reconciliation is acquiring its snapshot are coalesced into exactly one
    /// further reconciliation, which then sees the change the requests announced.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task RequestsDuringReconciliation_CauseOneMore()
    {
        await _service.StartAsync(Token);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _f.Source.DuringAcquire = () =>
        {
            entered.Set();
            release.Wait(Eventually.SafetyTimeout);
        };

        _f.Catalog.RequestReconciliation(StreamCatalogTrigger.Event);
        Assert.True(entered.Wait(Eventually.SafetyTimeout, Token));
        _f.Source.Publish("0123456789abcdef0123456789abcdef", 1);
        for (var i = 0; i < 10; i++)
        {
            _f.Catalog.RequestReconciliation(StreamCatalogTrigger.Event);
        }

        release.Set();
        await Eventually.WaitUntilAsync(() => _f.Catalog.Current.Streams.Count == 1, "the follow-up reconciliation sees the new stream");
        Assert.Equal(3, _f.Source.Acquisitions);
        Assert.Equal(9, _f.Coalesced.GetMeasurementSnapshot().Sum(static m => m.Value));
    }

    /// <summary>
    /// Verifies the settle timer: while a reconciliation leaves an HLS stream live after its RTMP session ended, one
    /// more is requested after <see cref="StreamCatalogService.SettleDelay"/>, and none once it has settled.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task SettleTimer_ReconcilesUntilSettled()
    {
        const string Incarnation = "0123456789abcdef0123456789abcdef";
        _f.Source.Store(Incarnation, 1, HlsStreamState.Live);
        await _service.StartAsync(Token);
        Assert.True(_f.Catalog.HasUnsettled);

        _f.Time.Advance(StreamCatalogService.SettleDelay - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, _f.Source.Acquisitions);
        _f.Time.Advance(TimeSpan.FromMilliseconds(1));
        await Eventually.WaitUntilAsync(() => _f.Reconciliations.GetMeasurementSnapshot().Count == 2, "the settle reconciliation completes");
        Assert.True(_f.Catalog.HasUnsettled);

        _f.Source.Hls.Clear();
        _f.Source.Store(Incarnation, 1, HlsStreamState.Ended);
        await Eventually.WaitUntilAsync(
            () =>
            {
                // The loop re-arms the timer just after reconciling; advancing until it fires avoids racing that.
                _f.Time.Advance(StreamCatalogService.SettleDelay);
                return !_f.Catalog.HasUnsettled;
            },
            "the stream settles");
        Assert.Equal(StreamHlsState.Ended, _f.Catalog.Current.Find("live", "cam")!.Hls.State);

        var acquisitions = _f.Source.Acquisitions;
        _f.Time.Advance(StreamCatalogService.SettleDelay * 5);
        Assert.Equal(acquisitions, _f.Source.Acquisitions);
        Assert.Equal(["startup", "inconsistency", "inconsistency"], _f.RequestReasons());
    }

    /// <summary>
    /// Verifies the first published playlist, which has no event, is discovered by the settle timer: while the stream
    /// is live without a playlist, a reconciliation is requested every <see cref="StreamCatalogService.SettleDelay"/>
    /// (well within the periodic interval), and none once the playlist is reported.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task SettleTimer_DiscoversFirstPlaylist()
    {
        const string Incarnation = "0123456789abcdef0123456789abcdef";
        _f.Source.Publish(Incarnation, 1).Store(Incarnation, 1, HlsStreamState.Live, playlistAvailable: false);
        await _service.StartAsync(Token);
        Assert.True(_f.Catalog.HasUnsettled);
        Assert.False(_f.Catalog.Current.Find("live", "cam")!.Hls.PlaylistAvailable);

        _f.Time.Advance(StreamCatalogService.SettleDelay);
        await Eventually.WaitUntilAsync(() => _f.Reconciliations.GetMeasurementSnapshot().Count == 2, "the settle reconciliation completes");
        Assert.False(_f.Catalog.Current.Find("live", "cam")!.Hls.PlaylistAvailable);

        _f.Source.Hls.Clear();
        _f.Source.Store(Incarnation, 1, HlsStreamState.Live, playlistAvailable: true);
        await Eventually.WaitUntilAsync(
            () =>
            {
                // The loop re-arms the timer just after reconciling; advancing until it fires avoids racing that.
                _f.Time.Advance(StreamCatalogService.SettleDelay);
                return _f.Catalog.Current.Find("live", "cam")!.Hls.PlaylistAvailable;
            },
            "the first playlist is discovered");
        Assert.False(_f.Catalog.HasUnsettled);
        Assert.True(_f.RequestReasons().Count(static r => r == "inconsistency") >= 2);

        var acquisitions = _f.Source.Acquisitions;
        _f.Time.Advance(StreamCatalogService.SettleDelay * 5);
        Assert.Equal(acquisitions, _f.Source.Acquisitions);
    }

    /// <summary>Verifies stopping ends the loop and the timer: no reconciliation runs afterwards.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Stop_EndsLoopAndTimer()
    {
        await _service.StartAsync(Token);

        await _service.StopAsync(Token).WithinSafetyTimeout();
        Assert.True(_service.ExecuteTask!.IsCompleted);
        _f.Time.Advance(Interval * 3);

        Assert.Equal(1, _f.Source.Acquisitions);
        Assert.Equal(["startup"], _f.RequestReasons());
    }
}
