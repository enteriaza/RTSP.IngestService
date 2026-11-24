// HTTP HLS origin listener settings bound from the "Http" configuration section. Every value is validated at startup,
// before the listener binds, and invalid values are rejected rather than clamped. Plain HTTP only; TLS belongs to a
// reverse proxy or a later phase.

using System.ComponentModel.DataAnnotations;

namespace RTSP.IngestService.Configuration;

/// <summary>
/// Settings for the HTTP origin (<see cref="Http.HlsHttpOrigin"/>) that serves HLS playlists and segments.
/// </summary>
/// <remarks>
/// The options are read once when the listener starts; changing them requires a restart.
/// </remarks>
public sealed class HttpOriginOptions
{
    /// <summary>Configuration section bound to this type.</summary>
    public const string SectionName = "Http";

    /// <summary>
    /// Gets or sets the literal IP address the listener binds to. Defaults to <c>127.0.0.1</c> (loopback only), so
    /// serving viewers on other hosts is an explicit choice: set <c>0.0.0.0</c> (all IPv4 interfaces), <c>::</c> (all
    /// IPv6 and, where supported, IPv4) or a specific interface address.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [IPAddress]
    public string ListenAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// Gets or sets the TCP port. Defaults to 8080; valid range is 0 to 65535, where 0 lets the operating system
    /// choose a free port (used by tests).
    /// </summary>
    [Range(0, 65535)]
    public int Port { get; set; } = 8080;

    /// <summary>
    /// Gets or sets the maximum number of concurrent HTTP connections. Further connections are refused by Kestrel
    /// until one closes, bounding per-connection memory. Defaults to 1000; valid range is 1 to 100000.
    /// </summary>
    [Range(1, 100000)]
    public int MaxConnections { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the maximum time to receive a request's headers; slower clients are disconnected. Defaults to
    /// 30 seconds; valid range is one second to five minutes.
    /// </summary>
    [TimeSpanRange("00:00:01", "00:05:00")]
    public TimeSpan RequestHeadersTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets how long an idle keep-alive connection stays open between requests. Players refresh live
    /// playlists every few seconds, so this should exceed the target segment duration. Defaults to two minutes;
    /// valid range is one second to one hour.
    /// </summary>
    [TimeSpanRange("00:00:01", "01:00:00")]
    public TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
