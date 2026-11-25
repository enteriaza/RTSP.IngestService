// Runs the stream catalog's reconciliations: once during startup, before later hosted services (the HTTP origin and
// the RTMP listener) start, and then one at a time whenever a request is pending: periodic timer ticks, events the
// catalog could not apply, detected event loss, and a short settle timer while a snapshot showed an HLS stream still
// live after its RTMP session ended, or live before its first playlist was published (neither the HLS close nor the
// first playlist has an event). Requests are coalesced into at most one pending
// request, so no queue or storm of reconciliations can build up.

using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;

namespace RTSP.IngestService.Streams;

/// <summary>Hosts the stream catalog's reconciliation loop.</summary>
public sealed class StreamCatalogService : BackgroundService
{
    /// <summary>How long after an unsettled reconciliation the next one is requested.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1);

    /// <summary>The catalog.</summary>
    private readonly StreamCatalog _catalog;

    /// <summary>The reconciliation interval.</summary>
    private readonly TimeSpan _interval;

    /// <summary>The time source.</summary>
    private readonly TimeProvider _time;

    /// <summary>The periodic timer, while running.</summary>
    private ITimer? _timer;

    /// <summary>The one-shot settle timer, while running.</summary>
    private ITimer? _settle;

    /// <summary>Initializes a new instance of the <see cref="StreamCatalogService"/> class.</summary>
    /// <param name="catalog">The catalog.</param>
    /// <param name="options">The catalog settings.</param>
    /// <param name="time">The time source.</param>
    internal StreamCatalogService(StreamCatalog catalog, IOptions<StreamCatalogOptions> options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _interval = options.Value.ReconciliationInterval;
    }

    /// <summary>
    /// Performs the startup reconciliation, then starts the timers and the loop. If the startup reconciliation fails,
    /// the catalog stays uninitialized and the loop retries.
    /// </summary>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <returns>A task completing when started.</returns>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _settle = _time.CreateTimer(static state => ((StreamCatalog)state!).RequestReconciliation(StreamCatalogTrigger.Inconsistency), _catalog, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (!_catalog.ReconcileNow(StreamCatalogTrigger.Startup))
        {
            _catalog.RequestReconciliation(StreamCatalogTrigger.Startup);
        }

        ArmSettle();
        _timer = _time.CreateTimer(static state => ((StreamCatalog)state!).RequestReconciliation(StreamCatalogTrigger.Periodic), _catalog, _interval, _interval);
        return base.StartAsync(cancellationToken);
    }

    /// <summary>Stops the timers and the loop.</summary>
    /// <param name="cancellationToken">Bounds the wait.</param>
    /// <returns>A task completing when stopped.</returns>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (ExecuteTask is { IsCompleted: false } loop)
        {
            // The cancelled loop only waits for a request or finishes an in-memory reconciliation, so it ends
            // promptly even if the host's shutdown budget is already spent.
            await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        if (_timer is { } timer)
        {
            await timer.DisposeAsync().ConfigureAwait(false);
        }

        if (_settle is { } settle)
        {
            await settle.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _timer?.Dispose();
        _settle?.Dispose();
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            while (true)
            {
                await _catalog.WaitForRequestAsync(stoppingToken).ConfigureAwait(false);
                _catalog.Reconcile();
                ArmSettle();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Arms the settle timer if the last reconciliation found unsettled incarnations.</summary>
    private void ArmSettle()
    {
        if (_catalog.HasUnsettled)
        {
            _settle?.Change(SettleDelay, Timeout.InfiniteTimeSpan);
        }
    }
}
