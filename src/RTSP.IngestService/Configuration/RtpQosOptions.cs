// IP-level QoS marking settings for RTP/UDP output: the DSCP (RFC 2474) and ECN (RFC 3168) fields of the IPv4 ToS /
// IPv6 Traffic Class byte. Rtp:Transport:Qos is the default for every output; an output's own Qos section overrides it
// field by field. Unset fields fall back to the default, and with nothing configured marking is off, so the operating
// system's default applies.

using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace RTSP.IngestService.Configuration;

/// <summary>Transport settings shared by every RTP output (<c>Rtp:Transport</c>).</summary>
public sealed class RtpTransportOptions
{
    /// <summary>Gets or sets the default QoS marking of every output's socket; each output may override it.</summary>
    [ValidateObjectMembers]
    public RtpQosOptions Qos { get; set; } = new();
}

/// <summary>
/// The IPv4 ToS / IPv6 Traffic Class marking of an RTP output's socket: <c>(Dscp &lt;&lt; 2) | Ecn</c>.
/// </summary>
/// <remarks>
/// Each setting is optional. At <c>Rtp:Transport:Qos</c> an unset value means its default (disabled, DSCP 0, ECN 0);
/// on an output, an unset value inherits the transport default. Marking classifies packets for networks configured to
/// honour it; it reserves no bandwidth and guarantees no latency.
/// </remarks>
public sealed class RtpQosOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the socket's ToS / Traffic Class is set. When disabled the socket option
    /// is not touched and the operating system's default marking applies. Defaults to <see langword="false"/>.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Gets or sets the Differentiated Services Code Point, 0 to 63 (see <see cref="RtpDscp"/> for common values).
    /// Defaults to 0 (<see cref="RtpDscp.CS0"/>).
    /// </summary>
    [Range(0, 63)]
    public int? Dscp { get; set; }

    /// <summary>
    /// Gets or sets the ECN codepoint, 0 to 3 (see <see cref="RtpEcn"/>). Defaults to 0 (<see cref="RtpEcn.NotEct"/>).
    /// The service never sets ECN on its own; configuring ECT does not by itself provide congestion notification or
    /// congestion control.
    /// </summary>
    [Range(0, 3)]
    public int? Ecn { get; set; }
}

/// <summary>Common Differentiated Services Code Points. Any value from 0 to 63 may be configured.</summary>
public static class RtpDscp
{
    /// <summary>Class selector 0: best effort (RFC 2474).</summary>
    public const int CS0 = 0;

    /// <summary>Assured forwarding class 1, low drop precedence (RFC 2597).</summary>
    public const int AF11 = 10;

    /// <summary>Assured forwarding class 2, low drop precedence (RFC 2597).</summary>
    public const int AF21 = 18;

    /// <summary>Assured forwarding class 3, low drop precedence: multimedia streaming (RFC 4594).</summary>
    public const int AF31 = 26;

    /// <summary>Assured forwarding class 4, low drop precedence: multimedia conferencing and video (RFC 4594).</summary>
    public const int AF41 = 34;

    /// <summary>Expedited forwarding (RFC 3246).</summary>
    public const int EF = 46;

    /// <summary>Class selector 6: network control (RFC 4594).</summary>
    public const int CS6 = 48;
}

/// <summary>ECN codepoints of the two low-order bits (RFC 3168).</summary>
public static class RtpEcn
{
    /// <summary>Not ECN-capable transport.</summary>
    public const int NotEct = 0;

    /// <summary>ECN-capable transport, ECT(1).</summary>
    public const int Ect1 = 1;

    /// <summary>ECN-capable transport, ECT(0).</summary>
    public const int Ect0 = 2;

    /// <summary>Congestion experienced: set by congested network devices; a sender should not set it (RFC 3168, section 5).</summary>
    public const int CongestionExperienced = 3;
}
