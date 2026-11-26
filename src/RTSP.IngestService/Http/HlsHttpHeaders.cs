// Response headers common to every HLS response of the HTTP origin: security headers, and CORS when configured. Built
// once from the options, so a request costs a few assignments of constant strings and, with an origin allowlist, one
// frozen-set lookup of the request's Origin header. Management routes on the same listener do not use this policy.

using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using RTSP.IngestService.Configuration;

namespace RTSP.IngestService.Http;

/// <summary>Applies the security and CORS headers of HLS responses, and answers CORS preflight requests.</summary>
/// <remarks>
/// <para>Security headers, on every HLS response including errors, 304 and preflights:</para>
/// <list type="bullet">
/// <item><c>X-Content-Type-Options: nosniff</c>: browsers use the declared media type and never sniff another.</item>
/// <item><c>Referrer-Policy: no-referrer</c>: a page navigated to from a directly opened resource sends no referrer.</item>
/// <item><c>X-Frame-Options: DENY</c>: a directly opened resource, which browsers render as a media document, cannot be framed by another site. Script and <c>video</c> element loads are unaffected.</item>
/// <item><c>Cross-Origin-Resource-Policy</c>: configured, <c>cross-origin</c> by default so media can be embedded from any origin.</item>
/// </list>
/// <para>
/// There is deliberately no <c>Content-Security-Policy</c> (responses are not documents and a policy could only break
/// players), no <c>Strict-Transport-Security</c> (the origin is plain HTTP) and no cookie.
/// </para>
/// <para>
/// CORS (<see cref="HttpCorsOptions"/>), when enabled: an allowed request gets <c>Access-Control-Allow-Origin</c>
/// (the listed origin, or <c>*</c> with <see cref="HttpCorsOptions.AllowAnyOrigin"/>) and
/// <c>Access-Control-Expose-Headers</c>. A request from an unlisted origin gets no CORS header, so the browser
/// withholds the response from the page. With an allowlist every response also carries <c>Vary: Origin</c>, so a
/// shared cache never serves one origin's grant to another. A request origin is never echoed unless listed.
/// </para>
/// </remarks>
internal sealed class HlsHttpHeaders
{
    /// <summary>Response headers exposed to cross-origin scripts beyond the CORS-safelisted ones.</summary>
    public const string ExposedHeaders = "Content-Length, Content-Range, Accept-Ranges, ETag, Last-Modified";

    /// <summary>Methods granted to a preflight.</summary>
    public const string AllowedMethods = "GET, HEAD";

    /// <summary>The non-safelisted request header granted to a preflight.</summary>
    public const string AllowedHeaders = "Range";

    /// <summary>The <c>Vary</c> value of preflight responses.</summary>
    private const string PreflightVary = "Origin, Access-Control-Request-Method, Access-Control-Request-Headers";

    /// <summary>The configured CORP value.</summary>
    private readonly string _resourcePolicy;

    /// <summary>Whether CORS is enabled.</summary>
    private readonly bool _cors;

    /// <summary>Whether every origin is granted.</summary>
    private readonly bool _anyOrigin;

    /// <summary>Whether credentials are granted to listed origins.</summary>
    private readonly bool _credentials;

    /// <summary>The listed origins, compared ordinally against the serialised Origin header.</summary>
    private readonly FrozenSet<string> _origins;

    /// <summary>The <c>Access-Control-Max-Age</c> value.</summary>
    private readonly string _maxAge;

    /// <summary>Initializes a new instance of the <see cref="HlsHttpHeaders"/> class.</summary>
    /// <param name="options">The validated HTTP origin options.</param>
    public HlsHttpHeaders(HttpOriginOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var cors = options.Cors;
        _resourcePolicy = options.CrossOriginResourcePolicy;
        _cors = cors.Enabled;
        _anyOrigin = cors.AllowAnyOrigin;
        _credentials = cors.AllowCredentials && !cors.AllowAnyOrigin;
        _origins = cors.AllowedOrigins.ToFrozenSet(StringComparer.Ordinal);
        _maxAge = cors.MaxAgeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Gets the policy with default options: security headers, <c>cross-origin</c> resources and no CORS.</summary>
    public static HlsHttpHeaders Default { get; } = new(new HttpOriginOptions());

    /// <summary>Gets a short description of the CORS policy for the startup log.</summary>
    public string CorsDescription => !_cors ? "disabled"
        : _anyOrigin ? "any origin"
        : $"{_origins.Count} allowed origins ({string.Join(", ", _origins.Order(StringComparer.Ordinal))}){(_credentials ? " with credentials" : string.Empty)}";

    /// <summary>Gets the configured <c>Cross-Origin-Resource-Policy</c>.</summary>
    public string ResourcePolicy => _resourcePolicy;

    /// <summary>Sets the security headers and, for an allowed origin, the CORS headers of an HLS response.</summary>
    /// <param name="context">The request.</param>
    /// <remarks>Call before the status is decided, and again after the response is cleared.</remarks>
    public void Apply(HttpContext context)
    {
        var request = context.Request.Headers;
        var response = context.Response.Headers;
        ApplySecurity(response);
        if (!_cors)
        {
            return;
        }

        if (_anyOrigin)
        {
            response.AccessControlAllowOrigin = "*";
            response.AccessControlExposeHeaders = ExposedHeaders;
            return;
        }

        response.Vary = HeaderNames.Origin;
        if (TryGetListedOrigin(request.Origin, out var origin))
        {
            response.AccessControlAllowOrigin = origin;
            response.AccessControlExposeHeaders = ExposedHeaders;
            if (_credentials)
            {
                response.AccessControlAllowCredentials = "true";
            }
        }
    }

    /// <summary>Returns whether a request is a CORS preflight this policy answers.</summary>
    /// <param name="request">The request.</param>
    /// <returns>
    /// <see langword="true"/> for an <c>OPTIONS</c> request with <c>Origin</c> and <c>Access-Control-Request-Method</c>
    /// while CORS is enabled; with CORS disabled, <c>OPTIONS</c> keeps its 405 answer.
    /// </returns>
    public bool IsPreflight(HttpRequest request) =>
        _cors
        && HttpMethods.IsOptions(request.Method)
        && request.Headers.Origin.Count > 0
        && request.Headers.AccessControlRequestMethod.Count > 0;

    /// <summary>
    /// Answers a preflight with 204 and no body, without touching the segment store. It is granted only for an allowed
    /// origin requesting <c>GET</c> or <c>HEAD</c> with no non-safelisted header other than <c>Range</c>; otherwise the
    /// 204 carries no CORS header and the browser blocks the actual request.
    /// </summary>
    /// <param name="context">The request.</param>
    public void AnswerPreflight(HttpContext context)
    {
        var request = context.Request.Headers;
        var response = context.Response.Headers;
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        ApplySecurity(response);
        response.Vary = PreflightVary;

        string? origin = null;
        if ((!_anyOrigin && !TryGetListedOrigin(request.Origin, out origin))
            || request.AccessControlRequestMethod is not { Count: 1 } method
            || (method[0] is not "GET" and not "HEAD")
            || !OnlyRange(request.AccessControlRequestHeaders))
        {
            return;
        }

        response.AccessControlAllowOrigin = _anyOrigin ? "*" : origin;
        response.AccessControlAllowMethods = AllowedMethods;
        response.AccessControlAllowHeaders = AllowedHeaders;
        response.AccessControlMaxAge = _maxAge;
        if (_credentials)
        {
            response.AccessControlAllowCredentials = "true";
        }
    }

    /// <summary>Returns whether a requested-headers list names nothing but <c>Range</c>.</summary>
    /// <param name="values">The <c>Access-Control-Request-Headers</c> values.</param>
    /// <returns><see langword="true"/> if every listed header is <c>range</c> (any case), or none is listed.</returns>
    private static bool OnlyRange(StringValues values)
    {
        foreach (var value in values)
        {
            foreach (var name in (value ?? string.Empty).AsSpan().Split(','))
            {
                var header = (value ?? string.Empty).AsSpan()[name].Trim();
                if (header.Length > 0 && !header.Equals(AllowedHeaders, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Sets the security headers.</summary>
    /// <param name="response">The response headers.</param>
    private void ApplySecurity(IHeaderDictionary response)
    {
        response.XContentTypeOptions = "nosniff";
        response["Referrer-Policy"] = "no-referrer";
        response.XFrameOptions = "DENY";
        response["Cross-Origin-Resource-Policy"] = _resourcePolicy;
    }

    /// <summary>Looks up a request's single Origin value in the allowlist.</summary>
    /// <param name="values">The Origin header values.</param>
    /// <param name="origin">The configured origin instance that matched.</param>
    /// <returns><see langword="true"/> if exactly one Origin value is present and it is listed.</returns>
    private bool TryGetListedOrigin(StringValues values, out string? origin)
    {
        origin = null;
        return values.Count == 1 && values[0] is { } value && _origins.TryGetValue(value, out origin);
    }
}
