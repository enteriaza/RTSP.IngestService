// Management routes of the shared HTTP origin: liveness, readiness and the read-only stream inventory. They run inside the HLS origin's Kestrel
// request pipeline (there is no second server or listener) and are restricted to loopback clients by the transport's
// remote address; forwarding headers are never consulted, so a client cannot claim to be local.

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Lifecycle;
using RTSP.IngestService.Rtp;
using RTSP.IngestService.Streams;

namespace RTSP.IngestService.Management;

/// <summary>Serves <c>/health/live</c>, <c>/health/ready</c> and, with a catalog, <c>/health/streams</c> to loopback clients.</summary>
/// <remarks>
/// <para>
/// Every path under <see cref="PathPrefix"/> is a management path. A request whose connection is not from a loopback
/// address (IPv4 <c>127.0.0.0/8</c>, IPv6 <c>::1</c> or IPv4-mapped loopback), or that has no remote address, is
/// answered 403 with an empty body before the path or method is examined, so rejected clients learn nothing about the
/// endpoints. <c>X-Forwarded-For</c>, <c>Forwarded</c> and similar headers are ignored.
/// </para>
/// <para>
/// Liveness is 200 unless the lifecycle is <see cref="ApplicationState.Failed"/> or <see cref="ApplicationState.Stopped"/>.
/// Readiness is 200 only while the lifecycle is <see cref="ApplicationState.Running"/> and both the RTMP and HTTP
/// listeners report that they are bound; otherwise 503. Both return a small JSON body with
/// <c>Cache-Control: no-store</c>; <c>HEAD</c> returns the same status and headers without the body; other methods
/// are 405.
/// </para>
/// <para>
/// <c>/health/streams</c> lists the stream catalog and <c>/health/streams/{application}/{stream}</c> returns the
/// current incarnation of one stream (404 if unknown); both are 503 until the catalog's first reconciliation, and
/// each response is serialized from one immutable catalog snapshot.
/// </para>
/// <para>
/// Requests never touch the media pipeline, and a failure is answered 500 (or aborts the response) without affecting
/// the listener.
/// </para>
/// </remarks>
internal sealed partial class ManagementEndpoints
{
    /// <summary>Path prefix of every management resource.</summary>
    public const string PathPrefix = "/health/";

    /// <summary>Liveness path.</summary>
    public const string LivePath = "/health/live";

    /// <summary>Readiness path.</summary>
    public const string ReadyPath = "/health/ready";

    /// <summary>Stream inventory path; <c>{StreamsPath}/{application}/{stream}</c> is one stream's current incarnation.</summary>
    public const string StreamsPath = "/health/streams";

    /// <summary>RTP output inventory path; <c>{RtpPath}/{output}.sdp</c> is an output's session description.</summary>
    public const string RtpPath = "/health/rtp";

    /// <summary>Cache policy of every management response.</summary>
    public const string CacheControl = "no-store";

    /// <summary>Media type of health bodies.</summary>
    public const string JsonContentType = "application/json; charset=utf-8";

    /// <summary>The <c>Allow</c> value of 405 responses.</summary>
    public const string AllowedMethods = "GET, HEAD";

    /// <summary>The application lifecycle.</summary>
    private readonly IApplicationLifecycle _lifecycle;

    /// <summary>Reports whether the RTMP listener is bound.</summary>
    private readonly Func<bool> _rtmpListening;

    /// <summary>Reports whether the shared HTTP listener is bound.</summary>
    private readonly Func<bool> _httpListening;

    /// <summary>Logger.</summary>
    private readonly ILogger _logger;

    /// <summary>The stream catalog, if the stream inventory is served.</summary>
    private readonly StreamCatalog? _catalog;

    /// <summary>The RTP output stage, if RTP outputs are configured.</summary>
    private readonly RtpOutputHandler? _rtp;

    /// <summary>Initializes a new instance of the <see cref="ManagementEndpoints"/> class.</summary>
    /// <param name="lifecycle">The application lifecycle.</param>
    /// <param name="rtmpListening">Reports whether the RTMP listener is bound; called on every readiness request.</param>
    /// <param name="httpListening">Reports whether the HTTP listener is bound; called on every readiness request.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="catalog">The stream catalog; <see langword="null"/> to not serve the stream inventory (its paths are then 404).</param>
    /// <param name="rtp">The RTP output stage; <see langword="null"/> to not serve the RTP paths (they are then 404).</param>
    public ManagementEndpoints(IApplicationLifecycle lifecycle, Func<bool> rtmpListening, Func<bool> httpListening, ILogger<ManagementEndpoints> logger, StreamCatalog? catalog = null, RtpOutputHandler? rtp = null)
    {
        _catalog = catalog;
        _rtp = rtp;
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(rtmpListening);
        ArgumentNullException.ThrowIfNull(httpListening);
        ArgumentNullException.ThrowIfNull(logger);

        _lifecycle = lifecycle;
        _rtmpListening = rtmpListening;
        _httpListening = httpListening;
        _logger = logger;
    }

    /// <summary>Determines whether <paramref name="path"/> is a management path.</summary>
    /// <param name="path">The request path.</param>
    /// <returns><see langword="true"/> for <c>/health</c> and every path under <see cref="PathPrefix"/>.</returns>
    public static bool IsManagementPath(string? path) =>
        path is not null && (path.StartsWith(PathPrefix, StringComparison.Ordinal) || path.Equals("/health", StringComparison.Ordinal));

    /// <summary>Determines whether a connection's remote address is a loopback address.</summary>
    /// <param name="address">The transport-level remote address, or <see langword="null"/> when unknown.</param>
    /// <returns><see langword="true"/> only for a known IPv4, IPv6 or IPv4-mapped loopback address.</returns>
    public static bool IsLoopback(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    /// <summary>Answers a management request.</summary>
    /// <param name="context">The request, whose path satisfies <see cref="IsManagementPath"/>.</param>
    /// <returns>A task that completes when the response is written.</returns>
    public async Task ServeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var response = context.Response;
        try
        {
            response.Headers.CacheControl = CacheControl;
            response.Headers.XContentTypeOptions = "nosniff";
            if (!IsLoopback(context.Connection.RemoteIpAddress))
            {
                response.StatusCode = StatusCodes.Status403Forbidden;
                response.ContentLength = 0;
                return;
            }

            var path = context.Request.Path.Value;
            (string Application, string Stream)? detail = null;
            string? sdpOutput = null;
            var rtp = _rtp is not null && IsRtpPath(path, out sdpOutput);
            if (path is not (LivePath or ReadyPath) && !rtp && !(_catalog is not null && IsStreamsPath(path, out detail)))
            {
                response.StatusCode = StatusCodes.Status404NotFound;
                response.ContentLength = 0;
                return;
            }

            var method = context.Request.Method;
            var head = HttpMethods.IsHead(method);
            if (!head && !HttpMethods.IsGet(method))
            {
                response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                response.Headers.Allow = AllowedMethods;
                response.ContentLength = 0;
                return;
            }

            int status;
            byte[] bytes;
            var contentType = JsonContentType;
            if (rtp)
            {
                if (Rtp(sdpOutput) is not { } answer)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    response.ContentLength = 0;
                    return;
                }

                (status, bytes, contentType) = answer;
            }
            else if (path is LivePath or ReadyPath)
            {
                (status, var body) = path == LivePath ? Live() : Ready();
                bytes = JsonSerializer.SerializeToUtf8Bytes(body, ManagementJsonContext.Default.HealthReport);
            }
            else if (Streams(detail) is { } streams)
            {
                (status, bytes) = streams;
            }
            else
            {
                response.StatusCode = StatusCodes.Status404NotFound;
                response.ContentLength = 0;
                return;
            }

            response.StatusCode = status;
            response.ContentType = contentType;
            response.ContentLength = bytes.Length;
            if (!head)
            {
                await response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            context.Abort();
        }
        catch (Exception ex)
        {
            LogRequestFailed(ex);
            if (response.HasStarted)
            {
                context.Abort();
            }
            else
            {
                response.Clear();
                response.StatusCode = StatusCodes.Status500InternalServerError;
                response.Headers.CacheControl = CacheControl;
            }
        }
    }

    /// <summary>Evaluates liveness.</summary>
    /// <returns>The status code and body.</returns>
    internal (int Status, HealthReport Body) Live()
    {
        var state = _lifecycle.State;
        var live = state is not (ApplicationState.Failed or ApplicationState.Stopped);
        return (live ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable, new HealthReport(live ? "live" : "not_live", state.ToString(), null, null));
    }

    /// <summary>Evaluates readiness from the lifecycle state and the explicit listener-bound state.</summary>
    /// <returns>The status code and body.</returns>
    internal (int Status, HealthReport Body) Ready()
    {
        var state = _lifecycle.State;
        var rtmp = _rtmpListening();
        var http = _httpListening();
        var ready = state == ApplicationState.Running && rtmp && http;
        return (ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable, new HealthReport(ready ? "ready" : "not_ready", state.ToString(), rtmp, http));
    }

    /// <summary>Serializes the stream inventory or one stream from a single catalog snapshot.</summary>
    /// <param name="detail">The requested stream, or <see langword="null"/> for the whole inventory.</param>
    /// <returns>The status code and body, or <see langword="null"/> if the stream is unknown.</returns>
    internal (int Status, byte[] Body)? Streams((string Application, string Stream)? detail)
    {
        var snapshot = _catalog!.Current;
        if (!snapshot.IsInitialized)
        {
            return (StatusCodes.Status503ServiceUnavailable, JsonSerializer.SerializeToUtf8Bytes(new StreamInventoryStatus("initializing"), ManagementJsonContext.Default.StreamInventoryStatus));
        }

        if (detail is not { } stream)
        {
            return (StatusCodes.Status200OK, JsonSerializer.SerializeToUtf8Bytes(
                new StreamInventoryReport(snapshot.Version, snapshot.ReconciledAt, snapshot.Streams), ManagementJsonContext.Default.StreamInventoryReport));
        }

        return snapshot.Find(stream.Application, stream.Stream) is { } entry
            ? (StatusCodes.Status200OK, JsonSerializer.SerializeToUtf8Bytes(
                new StreamDetailReport(snapshot.Version, snapshot.ReconciledAt, entry), ManagementJsonContext.Default.StreamDetailReport))
            : null;
    }

    /// <summary>Serializes the RTP output inventory, or renders one output's session description.</summary>
    /// <param name="sdpOutput">The output whose SDP is requested, or <see langword="null"/> for the inventory.</param>
    /// <returns>The status code, body and content type, or <see langword="null"/> if the output is unknown.</returns>
    internal (int Status, byte[] Body, string ContentType)? Rtp(string? sdpOutput)
    {
        var rtp = _rtp!;
        if (sdpOutput is null)
        {
            var outputs = rtp.GetOutputs().Select(static s => new RtpOutputReport(
                s.Output.Name,
                s.Output.Application,
                s.Output.Stream,
                s.Output.Mode == RtpOutputMode.Multicast ? "multicast" : "unicast",
                s.Output.Destination,
                s.Output.VideoPort,
                s.Output.AudioPort,
                s.Qos.Enabled ? new RtpQosReport(true, s.Qos.Dscp, s.Qos.Ecn, s.Qos.TrafficClass) : new RtpQosReport(false, null, null, null),
                s.Session is { } session ? Report(session) : null)).ToList();
            return (StatusCodes.Status200OK, JsonSerializer.SerializeToUtf8Bytes(new RtpOutputsReport(outputs), ManagementJsonContext.Default.RtpOutputsReport), JsonContentType);
        }

        if (rtp.GetOutput(sdpOutput) is not { } status)
        {
            return null;
        }

        if (status.Session is not { } current || status.StreamInfo is not { } info)
        {
            return (StatusCodes.Status503ServiceUnavailable, JsonSerializer.SerializeToUtf8Bytes(new RtpStatus("not_publishing"), ManagementJsonContext.Default.RtpStatus), JsonContentType);
        }

        return RtpSdp.Render(current, info) is { } sdp
            ? (StatusCodes.Status200OK, System.Text.Encoding.UTF8.GetBytes(sdp), RtpSdp.ContentType)
            : (StatusCodes.Status503ServiceUnavailable, JsonSerializer.SerializeToUtf8Bytes(new RtpStatus("not_described"), ManagementJsonContext.Default.RtpStatus), JsonContentType);
    }

    /// <summary>Builds the report of an output session.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The report.</returns>
    private static RtpSessionReport Report(RtpOutputSession session)
    {
        var stats = session.Statistics.Snapshot();
        return new RtpSessionReport(
            session.State.ToString().ToLowerInvariant(),
            session.StartedAt,
            RtpOutputSession.Ssrc(session.Video?.Stream),
            RtpOutputSession.Ssrc(session.AudioStream),
            stats.PacketsSent,
            stats.BytesSent,
            stats.PacketsDropped,
            stats.SendErrors,
            stats.RtcpPacketsSent,
            Math.Round(stats.Bitrate),
            Math.Round(stats.PacketRate, 1),
            session.TrafficClass);
    }

    /// <summary>Recognizes <see cref="RtpPath"/> and <c>/health/rtp/{output}.sdp</c>.</summary>
    /// <param name="path">The request path.</param>
    /// <param name="sdpOutput">The output whose SDP is requested, or <see langword="null"/> for the inventory.</param>
    /// <returns><see langword="true"/> if the path is an RTP path.</returns>
    private static bool IsRtpPath(string? path, out string? sdpOutput)
    {
        sdpOutput = null;
        if (path == RtpPath)
        {
            return true;
        }

        if (path is null || !path.StartsWith(RtpPath + "/", StringComparison.Ordinal) || !path.EndsWith(".sdp", StringComparison.Ordinal))
        {
            return false;
        }

        var name = path[(RtpPath.Length + 1)..^4];
        if (name.Length == 0 || name.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        sdpOutput = name;
        return true;
    }

    /// <summary>Recognizes <see cref="StreamsPath"/> and <c>/health/streams/{application}/{stream}</c>.</summary>
    /// <param name="path">The request path.</param>
    /// <param name="detail">The requested stream, or <see langword="null"/> for the inventory.</param>
    /// <returns><see langword="true"/> if the path is a stream inventory path.</returns>
    private static bool IsStreamsPath(string? path, out (string Application, string Stream)? detail)
    {
        detail = null;
        if (path == StreamsPath)
        {
            return true;
        }

        if (path is null || !path.StartsWith(StreamsPath + "/", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = path[(StreamsPath.Length + 1)..].Split('/');
        if (segments is not [{ Length: > 0 } application, { Length: > 0 } stream])
        {
            return false;
        }

        detail = (application, stream);
        return true;
    }

    /// <summary>Logs an unexpected management request failure.</summary>
    /// <param name="exception">The failure.</param>
    [LoggerMessage(EventId = 9000, Level = LogLevel.Error, Message = "Management request failed")]
    private partial void LogRequestFailed(Exception exception);
}
