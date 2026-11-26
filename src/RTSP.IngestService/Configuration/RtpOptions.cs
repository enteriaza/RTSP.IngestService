// RTP/UDP output settings bound from the "Rtp" configuration section: one entry per destination, each naming the
// published stream it carries. Every value is validated at startup and invalid values are rejected rather than clamped;
// addresses are literals, never host names, so starting an output never depends on DNS.

using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace RTSP.IngestService.Configuration;

/// <summary>How an RTP output addresses its receivers.</summary>
public enum RtpOutputMode
{
    /// <summary>One receiver at a unicast address.</summary>
    Unicast,

    /// <summary>Any number of receivers that joined a multicast group.</summary>
    Multicast,
}

/// <summary>
/// Settings for RTP/UDP output (<see cref="Rtp.RtpOutputHandler"/>).
/// </summary>
/// <remarks>
/// The options are read once at startup; changing them requires a restart. With no <see cref="Outputs"/> the RTP output
/// stage is not added to the service at all.
/// </remarks>
public sealed class RtpOptions : IValidatableObject
{
    /// <summary>Configuration section bound to this type.</summary>
    public const string SectionName = "Rtp";

    /// <summary>The largest number of configured outputs.</summary>
    public const int MaxOutputs = 64;

    /// <summary>Gets or sets the outputs. Names must be unique; at most <see cref="MaxOutputs"/> entries.</summary>
    [ValidateEnumeratedItems]
    public IList<RtpOutputOptions> Outputs { get; set; } = [];

    /// <summary>Gets or sets the transport settings shared by every output, such as the default QoS marking.</summary>
    [ValidateObjectMembers]
    public RtpTransportOptions Transport { get; set; } = new();

    /// <summary>Rejects too many outputs and duplicate names.</summary>
    /// <param name="validationContext">The validation context.</param>
    /// <returns>One failure per problem.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Outputs.Count > MaxOutputs)
        {
            yield return new ValidationResult($"At most {MaxOutputs} RTP outputs may be configured; found {Outputs.Count}.", [nameof(Outputs)]);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var output in Outputs)
        {
            if (output.Name is { Length: > 0 } name && !names.Add(name))
            {
                yield return new ValidationResult($"RTP output name '{name}' is configured more than once.", [nameof(Outputs)]);
            }
        }
    }
}

/// <summary>One RTP/UDP destination for the video and/or audio of one published stream.</summary>
/// <remarks>
/// Each track is its own RTP session (RFC 3550, section 5.2) with its own SSRC, sent to its own even port; RTCP for the
/// track uses the next (odd) port. Several outputs may name the same stream; each is independent.
/// </remarks>
public sealed class RtpOutputOptions : IValidatableObject
{
    /// <summary>Default IP MTU: standard Ethernet.</summary>
    public const int DefaultMtu = 1500;

    /// <summary>Default unicast TTL / hop limit.</summary>
    public const int DefaultUnicastTimeToLive = 64;

    /// <summary>Default multicast TTL / hop limit: the local network only.</summary>
    public const int DefaultMulticastTimeToLive = 1;

    /// <summary>
    /// Gets or sets the output's unique identifier, used in logs, management responses and the SDP URL. A letter or
    /// digit followed by up to 63 of <c>A-Z a-z 0-9 . _ -</c>.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the RTMP application of the source stream, for example <c>live</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    public string Application { get; set; } = string.Empty;

    /// <summary>Gets or sets the stream identity (the publisher credential's <c>Name</c>) of the source stream.</summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    public string Stream { get; set; } = string.Empty;

    /// <summary>Gets or sets whether <see cref="Destination"/> is a unicast address or a multicast group. Defaults to unicast.</summary>
    public RtpOutputMode Mode { get; set; } = RtpOutputMode.Unicast;

    /// <summary>
    /// Gets or sets the destination: a literal IPv4 or IPv6 unicast address, or with <see cref="RtpOutputMode.Multicast"/>
    /// an IPv4 group in <c>224.0.0.0/4</c> or an IPv6 group in <c>ff00::/8</c>.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [IPAddress]
    public string Destination { get; set; } = string.Empty;

    /// <summary>Gets or sets the even UDP port of the H.264 RTP session; <see langword="null"/> to not send video.</summary>
    [Range(2, 65534)]
    public int? VideoPort { get; set; }

    /// <summary>Gets or sets the even UDP port of the AAC RTP session; <see langword="null"/> to not send audio.</summary>
    [Range(2, 65534)]
    public int? AudioPort { get; set; }

    /// <summary>Gets or sets the dynamic RTP payload type of video. Defaults to 96; valid range is 96 to 127.</summary>
    [Range(96, 127)]
    public int VideoPayloadType { get; set; } = 96;

    /// <summary>Gets or sets the dynamic RTP payload type of audio. Defaults to 97; valid range is 96 to 127.</summary>
    [Range(96, 127)]
    public int AudioPayloadType { get; set; } = 97;

    /// <summary>
    /// Gets or sets the audio RTP clock rate in hertz; <see langword="null"/> (the default) uses the AAC sampling rate,
    /// as RFC 3640 recommends. Valid range is 8000 to 192000. Video always uses 90 kHz (RFC 6184, section 8.2.1).
    /// </summary>
    [Range(8000, 192000)]
    public int? AudioClockRate { get; set; }

    /// <summary>
    /// Gets or sets the IP MTU of the path. RTP packets are sized so that each IP datagram (IP and UDP headers included)
    /// fits, so no IP fragmentation occurs. Defaults to 1500 (Ethernet); valid range is 576 to 9216.
    /// </summary>
    [Range(576, 9216)]
    public int Mtu { get; set; } = DefaultMtu;

    /// <summary>
    /// Gets or sets the IP TTL (IPv4) or hop limit (IPv6); <see langword="null"/> uses 64 for unicast and 1 for
    /// multicast. Valid range is 1 to 255.
    /// </summary>
    [Range(1, 255)]
    public int? TimeToLive { get; set; }

    /// <summary>
    /// Gets or sets the literal local address the socket binds to; <see langword="null"/> lets the operating system
    /// choose. For multicast it also selects the outgoing interface (the interface that owns the address).
    /// </summary>
    [IPAddress]
    public string? SourceAddress { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether multicast packets are also delivered to receivers on this host.
    /// Defaults to <see langword="false"/>. Multicast only.
    /// </summary>
    public bool MulticastLoopback { get; set; }

    /// <summary>
    /// Gets or sets the socket send buffer in bytes; <see langword="null"/> keeps the operating system default. Valid
    /// range is 65536 to 67108864.
    /// </summary>
    [Range(65536, 67108864)]
    public int? SendBufferSize { get; set; }

    /// <summary>
    /// Gets or sets how many RTP packets may wait for transmission. A whole access unit that does not fit is dropped.
    /// Defaults to 2048; valid range is 64 to 65536.
    /// </summary>
    [Range(64, 65536)]
    public int QueueCapacity { get; set; } = 2048;

    /// <summary>
    /// Gets or sets a value indicating whether RTCP sender reports, source descriptions and BYE packets are sent on
    /// each track's port + 1. Defaults to <see langword="true"/>; receivers need them to synchronise audio and video.
    /// </summary>
    public bool RtcpEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets this output's QoS marking; each setting that is set overrides <c>Rtp:Transport:Qos</c>.
    /// <see langword="null"/> (the default) uses the transport default unchanged.
    /// </summary>
    [ValidateObjectMembers]
    public RtpQosOptions? Qos { get; set; }

    /// <summary>Gets the configured TTL, or the mode's default.</summary>
    internal int EffectiveTimeToLive => TimeToLive ?? (Mode == RtpOutputMode.Multicast ? DefaultMulticastTimeToLive : DefaultUnicastTimeToLive);

    /// <summary>Rejects inconsistent destinations, families and ports.</summary>
    /// <param name="validationContext">The validation context.</param>
    /// <returns>One failure per problem, naming the output.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (VideoPort is null && AudioPort is null)
        {
            yield return new ValidationResult($"RTP output '{Name}' sends nothing: set VideoPort, AudioPort or both.", [nameof(VideoPort), nameof(AudioPort)]);
        }

        foreach (var (port, name) in new[] { (VideoPort, nameof(VideoPort)), (AudioPort, nameof(AudioPort)) })
        {
            if (port is { } value && value % 2 != 0)
            {
                yield return new ValidationResult($"RTP output '{Name}' {name} {value} is odd; RTP uses an even port and RTCP the next odd port (RFC 3550, section 11).", [name]);
            }
        }

        if (VideoPort is { } video && AudioPort is { } audio && video == audio)
        {
            yield return new ValidationResult($"RTP output '{Name}' uses port {video} for both video and audio; each track needs its own port pair.", [nameof(AudioPort)]);
        }

        if (!TryParse(Destination, out var destination))
        {
            yield break;
        }

        if (Mode == RtpOutputMode.Multicast && !IsMulticast(destination))
        {
            yield return new ValidationResult(
                $"RTP output '{Name}' is multicast but Destination is not a multicast group (IPv4 224.0.0.0/4 or IPv6 ff00::/8).", [nameof(Destination)]);
        }

        if (Mode == RtpOutputMode.Unicast && (IsMulticast(destination) || destination.Equals(IPAddress.Any) || destination.Equals(IPAddress.IPv6Any) || destination.Equals(IPAddress.Broadcast)))
        {
            yield return new ValidationResult(
                $"RTP output '{Name}' is unicast but Destination is a multicast, broadcast or unspecified address; set Mode to Multicast or use a unicast address.", [nameof(Destination)]);
        }

        if (Mode == RtpOutputMode.Unicast && MulticastLoopback)
        {
            yield return new ValidationResult($"RTP output '{Name}' sets MulticastLoopback, which applies only to multicast outputs.", [nameof(MulticastLoopback)]);
        }

        if (SourceAddress is not null && TryParse(SourceAddress, out var source))
        {
            if (source.AddressFamily != destination.AddressFamily)
            {
                yield return new ValidationResult(
                    $"RTP output '{Name}' binds to an {Family(source)} SourceAddress but sends to an {Family(destination)} Destination; both must be the same address family.", [nameof(SourceAddress)]);
            }
            else if (IsMulticast(source))
            {
                yield return new ValidationResult($"RTP output '{Name}' SourceAddress must be a local unicast address, not a multicast group.", [nameof(SourceAddress)]);
            }
        }
    }

    /// <summary>Determines whether an address is an IPv4 (224.0.0.0/4) or IPv6 (ff00::/8) multicast address.</summary>
    /// <param name="address">The address.</param>
    /// <returns><see langword="true"/> for a multicast address.</returns>
    internal static bool IsMulticast(IPAddress address) => address.AddressFamily switch
    {
        AddressFamily.InterNetwork => (address.GetAddressBytes()[0] & 0xF0) == 0xE0,
        AddressFamily.InterNetworkV6 => address.IsIPv6Multicast,
        _ => false,
    };

    /// <summary>Parses a literal address validated by <see cref="IPAddressAttribute"/>.</summary>
    /// <param name="text">The text.</param>
    /// <param name="address">The address.</param>
    /// <returns><see langword="true"/> if the text is a literal address.</returns>
    private static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IPAddress? address)
    {
        address = null;
        return text is not null && new IPAddressAttribute().IsValid(text) && IPAddress.TryParse(text, out address);
    }

    /// <summary>Names an address family for messages.</summary>
    /// <param name="address">The address.</param>
    /// <returns><c>IPv4</c> or <c>IPv6</c>.</returns>
    private static string Family(IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";
}
