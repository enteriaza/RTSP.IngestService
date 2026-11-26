// Cross-origin resource sharing settings of the HTTP HLS origin, bound from "Http:Cors". CORS is off by default: no
// Access-Control-* header is sent and browsers apply the same-origin policy. When enabled, access is granted only to
// the origins listed, or to every origin with AllowAnyOrigin; request origins are never reflected unless listed.
// Origins are validated at startup in their exact serialised form, so a configured value either matches what browsers
// send or startup fails with the form to use. CORS controls which web pages may read responses; it authenticates
// nobody.

using System.ComponentModel.DataAnnotations;

namespace RTSP.IngestService.Configuration;

/// <summary>CORS settings for HLS responses (<c>Http:Cors</c>).</summary>
/// <remarks>
/// <para>
/// The origin serves only <c>GET</c> and <c>HEAD</c>, so the only methods granted are those, and the only request
/// header granted beyond the CORS-safelisted ones is <c>Range</c>.
/// </para>
/// <para>
/// HLS needs no cookies or HTTP authentication, so <see cref="AllowCredentials"/> is off by default. It is
/// available for deployments where a front end sends credentials to an intermediary in front of the origin.
/// </para>
/// </remarks>
public sealed class HttpCorsOptions : IValidatableObject
{
    /// <summary>The most origins <see cref="AllowedOrigins"/> may list.</summary>
    public const int MaxAllowedOrigins = 100;

    /// <summary>
    /// Gets or sets a value indicating whether CORS headers are sent. Defaults to <see langword="false"/>, which
    /// leaves responses without any <c>Access-Control-*</c> header and answers <c>OPTIONS</c> with 405 as before.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the origins granted access, each in serialised form: <c>scheme://host[:port]</c> with a lower-case
    /// <c>http</c> or <c>https</c> scheme, a lower-case (punycode) host, no default port, and no path, trailing slash,
    /// query or fragment, for example <c>https://player.example.com</c> or <c>http://localhost:3000</c>. Matching is exact.
    /// </summary>
    public IList<string> AllowedOrigins { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether every origin is granted access (<c>Access-Control-Allow-Origin: *</c>).
    /// Defaults to <see langword="false"/>. Cannot be combined with <see cref="AllowedOrigins"/> or
    /// <see cref="AllowCredentials"/>.
    /// </summary>
    public bool AllowAnyOrigin { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <c>Access-Control-Allow-Credentials: true</c> is sent to listed origins,
    /// letting their pages read responses to requests made with cookies or HTTP authentication. Defaults to
    /// <see langword="false"/>; the origin itself uses neither. Requires <see cref="AllowedOrigins"/>.
    /// </summary>
    public bool AllowCredentials { get; set; }

    /// <summary>
    /// Gets or sets how long, in seconds, browsers may cache a preflight result (<c>Access-Control-Max-Age</c>).
    /// Defaults to 600; valid range is 0 to 86400. Browsers apply their own upper limit (two hours in Chromium).
    /// </summary>
    [Range(0, 86400)]
    public int MaxAgeSeconds { get; set; } = 600;

    /// <summary>Parses a configured origin.</summary>
    /// <param name="value">The configured value.</param>
    /// <param name="error">Why the value is not acceptable, including the form to use where one exists.</param>
    /// <returns><see langword="true"/> if <paramref name="value"/> is a serialised http or https origin.</returns>
    public static bool TryValidateOrigin(string? value, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "is empty";
            return false;
        }

        if (value == "*")
        {
            error = "is a wildcard; set AllowAnyOrigin to true instead of listing '*'";
            return false;
        }

        if (value.Contains('*', StringComparison.Ordinal))
        {
            error = "contains a wildcard; wildcard hosts are not supported, so list each origin";
            return false;
        }

        if (value == "null")
        {
            error = "is the opaque origin of sandboxed documents and local files, which any page can produce, so it cannot be granted";
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.HostNameType is UriHostNameType.Unknown or UriHostNameType.Basic)
        {
            error = "is not an absolute URL such as 'https://player.example.com'";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "must use the http or https scheme";
            return false;
        }

        var host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.IdnHost.Trim('[', ']')}]" : uri.IdnHost;
        var serialized = uri.IsDefaultPort ? $"{uri.Scheme}://{host}" : $"{uri.Scheme}://{host}:{uri.Port}";
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/" || !string.Equals(value, serialized, StringComparison.Ordinal))
        {
            error = $"is not in the form browsers send in the Origin header (scheme://host[:port], lower case, no default port, path, trailing slash, query or fragment); use '{serialized}'";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Validates the combination of settings and every listed origin.</summary>
    /// <param name="validationContext">The validation context.</param>
    /// <returns>The failures.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AllowAnyOrigin && AllowCredentials)
        {
            yield return new ValidationResult(
                "AllowAnyOrigin cannot be combined with AllowCredentials: browsers reject 'Access-Control-Allow-Origin: *' on credentialed requests, and granting credentials to every origin would let any site read responses with the viewer's credentials. List the front-end origins in AllowedOrigins instead.",
                [nameof(AllowAnyOrigin), nameof(AllowCredentials)]);
        }

        if (AllowAnyOrigin && AllowedOrigins.Count > 0)
        {
            yield return new ValidationResult(
                "AllowAnyOrigin grants every origin, so AllowedOrigins would have no effect; remove AllowedOrigins or set AllowAnyOrigin to false.",
                [nameof(AllowAnyOrigin), nameof(AllowedOrigins)]);
        }

        if (Enabled && !AllowAnyOrigin && AllowedOrigins.Count == 0)
        {
            yield return new ValidationResult(
                "CORS is enabled but grants no origin: list the front-end origins in AllowedOrigins (for example 'https://player.example.com'), set AllowAnyOrigin to true, or set Enabled to false.",
                [nameof(AllowedOrigins)]);
        }

        if (AllowedOrigins.Count > MaxAllowedOrigins)
        {
            yield return new ValidationResult($"At most {MaxAllowedOrigins} origins may be listed in AllowedOrigins; found {AllowedOrigins.Count}.", [nameof(AllowedOrigins)]);
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < AllowedOrigins.Count; i++)
        {
            var origin = AllowedOrigins[i];
            if (!TryValidateOrigin(origin, out var error))
            {
                yield return new ValidationResult($"AllowedOrigins:{i} '{origin}' {error}.", [nameof(AllowedOrigins)]);
            }
            else if (!seen.Add(origin))
            {
                yield return new ValidationResult($"AllowedOrigins:{i} '{origin}' is listed more than once.", [nameof(AllowedOrigins)]);
            }
        }
    }
}
