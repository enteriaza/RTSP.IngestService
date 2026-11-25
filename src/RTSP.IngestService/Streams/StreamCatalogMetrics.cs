// Metrics for the stream catalog, on the stream lifecycle meter. Tags come from fixed vocabularies (the request reason
// and the reconciliation result); applications, stream identities and incarnation identifiers are never tags.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RTSP.IngestService.Streams;

/// <summary>Why a catalog reconciliation was requested.</summary>
internal enum StreamCatalogTrigger
{
    /// <summary>Service startup.</summary>
    Startup,

    /// <summary>The periodic timer.</summary>
    Periodic,

    /// <summary>An event the catalog could not apply incrementally (an unknown incarnation or a new publication).</summary>
    Event,

    /// <summary>Lifecycle events were dropped by the full channel.</summary>
    Loss,

    /// <summary>A snapshot showed a not-yet-settled combination of sources.</summary>
    Inconsistency,
}

/// <summary>Records stream catalog metrics on the <see cref="StreamEventMetrics.MeterName"/> meter.</summary>
/// <remarks>
/// <para>Instruments and tags:</para>
/// <list type="bullet">
/// <item><c>stream_catalog.streams</c>: entries in the published catalog (gauge).</item>
/// <item><c>stream_catalog.reconciliations</c>: completed reconciliation attempts; tag <c>result</c> (<c>success</c> or <c>failure</c>).</item>
/// <item><c>stream_catalog.reconciliation.duration</c>: reconciliation duration in seconds (histogram).</item>
/// <item><c>stream_catalog.reconciliation.requests</c>: reconciliation requests; tag <c>reason</c> (<c>startup</c>, <c>periodic</c>, <c>event</c>, <c>loss</c>, <c>inconsistency</c>).</item>
/// <item><c>stream_catalog.reconciliation.coalesced</c>: requests merged into an already pending one.</item>
/// <item><c>stream_catalog.reconciliation.age</c>: seconds since the last successful reconciliation (gauge; absent before the first).</item>
/// </list>
/// </remarks>
internal sealed class StreamCatalogMetrics
{
    /// <summary>Reconciliation attempts.</summary>
    private readonly Counter<long> _reconciliations;

    /// <summary>Reconciliation durations.</summary>
    private readonly Histogram<double> _duration;

    /// <summary>Reconciliation requests.</summary>
    private readonly Counter<long> _requests;

    /// <summary>Coalesced requests.</summary>
    private readonly Counter<long> _coalesced;

    /// <summary>Returns the observed state, once attached.</summary>
    private Func<(int Streams, TimeSpan? Age)>? _observe;

    /// <summary>Initializes a new instance of the <see cref="StreamCatalogMetrics"/> class.</summary>
    /// <param name="meterFactory">Factory owning the meter's lifetime.</param>
    public StreamCatalogMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(StreamEventMetrics.MeterName);
        _reconciliations = meter.CreateCounter<long>("stream_catalog.reconciliations", "{reconciliation}", "Stream catalog reconciliation attempts; tag result.");
        _duration = meter.CreateHistogram<double>("stream_catalog.reconciliation.duration", "s", "Stream catalog reconciliation duration.");
        _requests = meter.CreateCounter<long>("stream_catalog.reconciliation.requests", "{request}", "Stream catalog reconciliation requests; tag reason.");
        _coalesced = meter.CreateCounter<long>("stream_catalog.reconciliation.coalesced", "{request}", "Stream catalog reconciliation requests merged into a pending one.");
        meter.CreateObservableGauge("stream_catalog.streams", ObserveStreams, "{stream}", "Stream incarnations in the stream catalog.");
        meter.CreateObservableGauge("stream_catalog.reconciliation.age", ObserveAge, "s", "Seconds since the last successful stream catalog reconciliation.");
    }

    /// <summary>Attaches the catalog state the gauges observe.</summary>
    /// <param name="observe">Returns the entry count and the age of the last successful reconciliation.</param>
    public void Attach(Func<(int Streams, TimeSpan? Age)> observe) => Volatile.Write(ref _observe, observe);

    /// <summary>Records a reconciliation attempt.</summary>
    /// <param name="success">Whether it succeeded.</param>
    /// <param name="duration">How long it took.</param>
    public void Reconciled(bool success, TimeSpan duration)
    {
        _reconciliations.Add(1, new KeyValuePair<string, object?>("result", success ? "success" : "failure"));
        _duration.Record(duration.TotalSeconds);
    }

    /// <summary>Records a reconciliation request.</summary>
    /// <param name="trigger">Why it was requested.</param>
    public void Requested(StreamCatalogTrigger trigger) => _requests.Add(1, new KeyValuePair<string, object?>("reason", ReasonTag(trigger)));

    /// <summary>Records a request merged into a pending one.</summary>
    public void Coalesced() => _coalesced.Add(1);

    /// <summary>Returns the tag value of a trigger.</summary>
    /// <param name="trigger">The trigger.</param>
    /// <returns>The tag value.</returns>
    internal static string ReasonTag(StreamCatalogTrigger trigger) => trigger switch
    {
        StreamCatalogTrigger.Startup => "startup",
        StreamCatalogTrigger.Periodic => "periodic",
        StreamCatalogTrigger.Event => "event",
        StreamCatalogTrigger.Loss => "loss",
        StreamCatalogTrigger.Inconsistency => "inconsistency",
        _ => throw new UnreachableException(),
    };

    /// <summary>Observes the entry count.</summary>
    /// <returns>The measurements.</returns>
    private IEnumerable<Measurement<int>> ObserveStreams()
    {
        if (Volatile.Read(ref _observe) is { } observe)
        {
            yield return new Measurement<int>(observe().Streams);
        }
    }

    /// <summary>Observes the age of the last successful reconciliation.</summary>
    /// <returns>The measurements.</returns>
    private IEnumerable<Measurement<double>> ObserveAge()
    {
        if (Volatile.Read(ref _observe) is { } observe && observe().Age is { } age)
        {
            yield return new Measurement<double>(age.TotalSeconds);
        }
    }
}
