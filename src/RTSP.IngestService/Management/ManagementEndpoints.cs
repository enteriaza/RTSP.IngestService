// Management routes of the shared HTTP origin: liveness and readiness. They run inside the HLS origin's Kestrel
// request pipeline (there is no second server or listener) and are restricted to loopback clients by the transport's
// remote address; forwarding headers are never consulted, so a client cannot claim to be local.

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RTSP.IngestService.Lifecycle;

namespace RTSP.IngestService.Management;

/// <summary>Serves <c>/health/live</c> and <c>/health/ready</c> to loopback clients.</summary>
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

    /// <summary>Initializes a new instance of the <see cref="ManagementEndpoints"/> class.</summary>
    /// <param name="lifecycle">The application lifecycle.</param>
    /// <param name="rtmpListening">Reports whether the RTMP listener is bound; called on every readiness request.</param>
    /// <param name="httpListening">Reports whether the HTTP listener is bound; called on every readiness request.</param>
    /// <param name="logger">Logger.</param>
    public ManagementEndpoints(IApplicationLifecycle lifecycle, Func<bool> rtmpListening, Func<bool> httpListening, ILogger<ManagementEndpoints> logger)
    {
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
            if (path is not (LivePath or ReadyPath))
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

            var (status, body) = path == LivePath ? Live() : Ready();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body, ManagementJsonContext.Default.HealthReport);
            response.StatusCode = status;
            response.ContentType = JsonContentType;
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

    /// <summary>Logs an unexpected management request failure.</summary>
    /// <param name="exception">The failure.</param>
    [LoggerMessage(EventId = 9000, Level = LogLevel.Error, Message = "Management request failed")]
    private partial void LogRequestFailed(Exception exception);
}
