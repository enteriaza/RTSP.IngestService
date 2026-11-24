// Metrics for the HTTP HLS origin, on their own meter. Tags are limited to fixed vocabularies (resource, status
// class, reason); application names, stream identities, segment names and client addresses are never tags.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RTSP.IngestService.Http;

/// <summary>The kind of resource a request addressed.</summary>
internal enum HttpOriginResource
{
    /// <summary>A path that names no origin resource.</summary>
    Other,

    /// <summary>A media playlist.</summary>
    Playlist,

    /// <summary>An initialization segment.</summary>
    Initialization,

    /// <summary>A media segment.</summary>
    Segment,
}

/// <summary>Why a request found no resource.</summary>
internal enum HttpOriginMissReason
{
    /// <summary>The path names no origin resource.</summary>
    InvalidPath,

    /// <summary>Nothing is stored for the stream.</summary>
    UnknownStream,

    /// <summary>The stream is known but its playlist was withdrawn.</summary>
    PlaylistWithdrawn,

    /// <summary>The stream is known but the segment is not stored (expired or never produced).</summary>
    SegmentUnavailable,
}

/// <summary>Records HTTP origin metrics on the <see cref="MeterName"/> meter.</summary>
/// <remarks>
/// <para>Instruments and tags:</para>
/// <list type="bullet">
/// <item><c>http.origin.requests</c>: completed requests; tags <c>resource</c>, <c>status_class</c>.</item>
/// <item><c>http.origin.requests.active</c>: requests in progress.</item>
/// <item><c>http.origin.request.duration</c>: request durations in seconds; tag <c>resource</c>.</item>
/// <item><c>http.origin.response_bytes</c>: body bytes written; tag <c>resource</c>.</item>
/// <item><c>http.origin.cancellations</c>: requests ended by client disconnect or shutdown; tag <c>resource</c>.</item>
/// <item><c>http.origin.missing</c>: requests answered 404; tags <c>resource</c>, <c>reason</c> (<c>invalid_path</c>, <c>unknown_stream</c>, <c>playlist_withdrawn</c>, <c>segment_unavailable</c>).</item>
/// <item><c>http.origin.failures</c>: unexpected request failures; tag <c>resource</c>.</item>
/// </list>
/// <para>Thread-safe.</para>
/// </remarks>
internal sealed class HttpOriginMetrics
{
    /// <summary>Name of the meter.</summary>
    public const string MeterName = "RTSP.IngestService.Http";

    /// <summary>Completed requests.</summary>
    private readonly Counter<long> _requests;

    /// <summary>Requests in progress.</summary>
    private readonly UpDownCounter<long> _active;

    /// <summary>Request durations.</summary>
    private readonly Histogram<double> _duration;

    /// <summary>Body bytes written.</summary>
    private readonly Counter<long> _bytes;

    /// <summary>Cancelled requests.</summary>
    private readonly Counter<long> _cancellations;

    /// <summary>Requests answered 404.</summary>
    private readonly Counter<long> _missing;

    /// <summary>Unexpected failures.</summary>
    private readonly Counter<long> _failures;

    /// <summary>Initializes a new instance of the <see cref="HttpOriginMetrics"/> class.</summary>
    /// <param name="meterFactory">Factory owning the meter's lifetime.</param>
    public HttpOriginMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _requests = meter.CreateCounter<long>("http.origin.requests", "{request}", "Completed requests; tags resource and status_class.");
        _active = meter.CreateUpDownCounter<long>("http.origin.requests.active", "{request}", "Requests in progress.");
        _duration = meter.CreateHistogram<double>("http.origin.request.duration", "s", "Request durations; tag resource.");
        _bytes = meter.CreateCounter<long>("http.origin.response_bytes", "By", "Response body bytes written; tag resource.");
        _cancellations = meter.CreateCounter<long>("http.origin.cancellations", "{request}", "Requests ended by client disconnect or shutdown; tag resource.");
        _missing = meter.CreateCounter<long>("http.origin.missing", "{request}", "Requests for missing resources; tags resource and reason.");
        _failures = meter.CreateCounter<long>("http.origin.failures", "{request}", "Unexpected request failures; tag resource.");
    }

    /// <summary>Records a request that started.</summary>
    public void RequestStarted() => _active.Add(1);

    /// <summary>Records a request that completed.</summary>
    /// <param name="resource">The resource kind.</param>
    /// <param name="statusCode">The response status code.</param>
    /// <param name="elapsed">The request duration.</param>
    public void RequestCompleted(HttpOriginResource resource, int statusCode, TimeSpan elapsed)
    {
        _active.Add(-1);
        var resourceTag = new KeyValuePair<string, object?>("resource", ResourceTag(resource));
        _requests.Add(1, resourceTag, new KeyValuePair<string, object?>("status_class", StatusClassTag(statusCode)));
        _duration.Record(elapsed.TotalSeconds, resourceTag);
    }

    /// <summary>Records body bytes written.</summary>
    /// <param name="resource">The resource kind.</param>
    /// <param name="bytes">The byte count.</param>
    public void BytesWritten(HttpOriginResource resource, long bytes) =>
        _bytes.Add(bytes, new KeyValuePair<string, object?>("resource", ResourceTag(resource)));

    /// <summary>Records a cancelled request.</summary>
    /// <param name="resource">The resource kind.</param>
    public void Cancelled(HttpOriginResource resource) =>
        _cancellations.Add(1, new KeyValuePair<string, object?>("resource", ResourceTag(resource)));

    /// <summary>Records a request for a missing resource.</summary>
    /// <param name="resource">The resource kind.</param>
    /// <param name="reason">Why it is missing.</param>
    public void Missing(HttpOriginResource resource, HttpOriginMissReason reason) =>
        _missing.Add(
            1,
            new KeyValuePair<string, object?>("resource", ResourceTag(resource)),
            new KeyValuePair<string, object?>("reason", ReasonTag(reason)));

    /// <summary>Records an unexpected failure.</summary>
    /// <param name="resource">The resource kind.</param>
    public void Failed(HttpOriginResource resource) =>
        _failures.Add(1, new KeyValuePair<string, object?>("resource", ResourceTag(resource)));

    /// <summary>Returns the tag value of a resource kind.</summary>
    /// <param name="resource">The resource kind.</param>
    /// <returns>The tag value.</returns>
    internal static string ResourceTag(HttpOriginResource resource) => resource switch
    {
        HttpOriginResource.Playlist => "playlist",
        HttpOriginResource.Initialization => "initialization",
        HttpOriginResource.Segment => "segment",
        HttpOriginResource.Other => "other",
        _ => throw new UnreachableException(),
    };

    /// <summary>Returns the tag value of a miss reason.</summary>
    /// <param name="reason">The reason.</param>
    /// <returns>The tag value.</returns>
    internal static string ReasonTag(HttpOriginMissReason reason) => reason switch
    {
        HttpOriginMissReason.InvalidPath => "invalid_path",
        HttpOriginMissReason.UnknownStream => "unknown_stream",
        HttpOriginMissReason.PlaylistWithdrawn => "playlist_withdrawn",
        HttpOriginMissReason.SegmentUnavailable => "segment_unavailable",
        _ => throw new UnreachableException(),
    };

    /// <summary>Returns the status class tag of a status code.</summary>
    /// <param name="statusCode">The status code.</param>
    /// <returns><c>1xx</c> to <c>5xx</c>.</returns>
    internal static string StatusClassTag(int statusCode) => (statusCode / 100) switch
    {
        1 => "1xx",
        2 => "2xx",
        3 => "3xx",
        4 => "4xx",
        _ => "5xx",
    };
}
