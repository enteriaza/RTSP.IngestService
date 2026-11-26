// IP-level QoS marking of RTP output sockets. The DS field (RFC 2474) and the ECN field (RFC 3168) share one byte, the
// IPv4 Type of Service or IPv6 Traffic Class: DSCP in bits 7-2, ECN in bits 1-0. The byte is set once on each session's
// own socket, before it is bound or sends anything, and read back. A mismatch, or an option the OS rejects, fails the
// socket rather than leaving marking silently inactive. IPv4 uses IP_TOS. IPv6 uses IPV6_TCLASS, whose option number
// differs by OS (Linux 67, Windows 39) and which .NET does not name, so it is set as a raw option.
// Nothing here runs per packet.

using System.Net.Sockets;
using System.Runtime.InteropServices;
using RTSP.IngestService.Configuration;

namespace RTSP.IngestService.Rtp;

/// <summary>The effective QoS marking of one output: the transport default with the output's overrides applied.</summary>
/// <param name="Enabled">Whether the socket's ToS / Traffic Class is set; when not, the OS default applies.</param>
/// <param name="Dscp">The Differentiated Services Code Point, 0 to 63.</param>
/// <param name="Ecn">The ECN codepoint, 0 to 3.</param>
internal readonly record struct RtpQosPolicy(bool Enabled, int Dscp, int Ecn)
{
    /// <summary>Gets the policy that leaves the socket's marking to the operating system.</summary>
    public static RtpQosPolicy Disabled => default;

    /// <summary>Gets the ToS / Traffic Class byte: <c>(Dscp &lt;&lt; 2) | Ecn</c>.</summary>
    public byte TrafficClass => RtpTrafficClass.Compose(Dscp, Ecn);

    /// <summary>Resolves an output's policy: each setting the output sets overrides the transport default.</summary>
    /// <param name="transport">The <c>Rtp:Transport:Qos</c> default.</param>
    /// <param name="output">The output's <c>Qos</c> section, if any.</param>
    /// <returns>The validated effective policy.</returns>
    public static RtpQosPolicy Resolve(RtpQosOptions transport, RtpQosOptions? output)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return new RtpQosPolicy(
            output?.Enabled ?? transport.Enabled ?? false,
            output?.Dscp ?? transport.Dscp ?? RtpDscp.CS0,
            output?.Ecn ?? transport.Ecn ?? RtpEcn.NotEct);
    }

    /// <summary>Describes the policy for logs, for example <c>DSCP 34 (AF41), ECN 0 (Not-ECT), traffic class 0x88</c>.</summary>
    /// <returns>The description, or <c>off (operating system default)</c>.</returns>
    public override string ToString() => Enabled
        ? $"DSCP {Dscp}{RtpTrafficClass.DscpName(Dscp)}, ECN {Ecn} ({RtpTrafficClass.EcnName(Ecn)}), traffic class 0x{TrafficClass:x2}"
        : "off (operating system default)";
}

/// <summary>Bit layout of the ToS / Traffic Class byte.</summary>
internal static class RtpTrafficClass
{
    /// <summary>The ECN bits (1-0).</summary>
    public const int EcnMask = 0b0000_0011;

    /// <summary>The DSCP bits (7-2).</summary>
    public const int DscpMask = 0b1111_1100;

    /// <summary>Builds the byte from a DSCP and an ECN codepoint.</summary>
    /// <param name="dscp">0 to 63.</param>
    /// <param name="ecn">0 to 3.</param>
    /// <returns><c>(dscp &lt;&lt; 2) | ecn</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    public static byte Compose(int dscp, int ecn) => WithEcn(WithDscp(0, dscp), ecn);

    /// <summary>Replaces the DSCP of a byte, keeping its ECN bits.</summary>
    /// <param name="trafficClass">The byte.</param>
    /// <param name="dscp">0 to 63.</param>
    /// <returns>The new byte.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dscp"/> is out of range.</exception>
    public static byte WithDscp(byte trafficClass, int dscp)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dscp, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dscp, 63);
        return (byte)((trafficClass & EcnMask) | (dscp << 2));
    }

    /// <summary>Replaces the ECN codepoint of a byte, keeping its DSCP.</summary>
    /// <param name="trafficClass">The byte.</param>
    /// <param name="ecn">0 to 3.</param>
    /// <returns>The new byte.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ecn"/> is out of range.</exception>
    public static byte WithEcn(byte trafficClass, int ecn)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ecn, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ecn, 3);
        return (byte)((trafficClass & DscpMask) | ecn);
    }

    /// <summary>Returns the DSCP of a byte.</summary>
    /// <param name="trafficClass">The byte.</param>
    /// <returns>0 to 63.</returns>
    public static int DscpOf(byte trafficClass) => trafficClass >> 2;

    /// <summary>Returns the ECN codepoint of a byte.</summary>
    /// <param name="trafficClass">The byte.</param>
    /// <returns>0 to 3.</returns>
    public static int EcnOf(byte trafficClass) => trafficClass & EcnMask;

    /// <summary>Names a well-known DSCP for logs.</summary>
    /// <param name="dscp">The DSCP.</param>
    /// <returns>For example <c> (AF41)</c>, or empty.</returns>
    public static string DscpName(int dscp) => dscp switch
    {
        RtpDscp.CS0 => " (CS0)",
        RtpDscp.AF11 => " (AF11)",
        RtpDscp.AF21 => " (AF21)",
        RtpDscp.AF31 => " (AF31)",
        RtpDscp.AF41 => " (AF41)",
        RtpDscp.EF => " (EF)",
        RtpDscp.CS6 => " (CS6)",
        _ => string.Empty,
    };

    /// <summary>Names an ECN codepoint.</summary>
    /// <param name="ecn">The codepoint.</param>
    /// <returns><c>Not-ECT</c>, <c>ECT(1)</c>, <c>ECT(0)</c> or <c>CE</c>.</returns>
    public static string EcnName(int ecn) => ecn switch
    {
        RtpEcn.NotEct => "Not-ECT",
        RtpEcn.Ect1 => "ECT(1)",
        RtpEcn.Ect0 => "ECT(0)",
        _ => "CE",
    };
}

/// <summary>The QoS marking of an RTP output socket could not be applied; the socket is not used.</summary>
internal sealed class RtpQosException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="RtpQosException"/> class.</summary>
    /// <param name="message">The actionable description.</param>
    /// <param name="innerException">The socket failure, if any.</param>
    public RtpQosException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Applies and reads the ToS / Traffic Class of a UDP socket.</summary>
internal static class RtpSocketQos
{
    /// <summary><c>IPPROTO_IPV6</c>.</summary>
    internal const int Ipv6Level = 41;

    /// <summary><c>IPV6_TCLASS</c> on Linux.</summary>
    internal const int LinuxIpv6TrafficClass = 67;

    /// <summary><c>IPV6_TCLASS</c> on Windows.</summary>
    internal const int WindowsIpv6TrafficClass = 39;

    /// <summary>Sets the socket's ToS / Traffic Class to the policy's byte and verifies it by reading it back.</summary>
    /// <param name="socket">An IPv4 or IPv6 UDP socket that has not sent anything.</param>
    /// <param name="policy">An enabled policy.</param>
    /// <returns>The byte the socket reports after setting it, equal to <see cref="RtpQosPolicy.TrafficClass"/>.</returns>
    /// <exception cref="RtpQosException">The OS rejected the option, does not support it, or reports a different value.</exception>
    public static byte Apply(Socket socket, RtpQosPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var ipv6 = socket.AddressFamily == AddressFamily.InterNetworkV6;
        var family = ipv6 ? "IPv6 Traffic Class (IPV6_TCLASS)" : "IPv4 Type of Service (IP_TOS)";
        var requested = policy.TrafficClass;
        try
        {
            if (ipv6)
            {
                Span<byte> value = stackalloc byte[sizeof(int)];
                MemoryMarshal.Write(value, (int)requested);
                socket.SetRawSocketOption(Ipv6Level, Ipv6TrafficClassOption(), value);
            }
            else
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, requested);
            }
        }
        catch (SocketException ex)
        {
            throw new RtpQosException(Rejected(family, policy, ex), ex);
        }

        var effective = Read(socket);
        if (effective != requested)
        {
            throw new RtpQosException(
                $"The operating system accepted the {family} value 0x{requested:x2} ({policy}) but reports 0x{effective:x2}, so the marking would not be what is configured. Check for an OS policy that overrides application marking, or disable QoS for this output.");
        }

        return effective;
    }

    /// <summary>Reads the socket's current ToS / Traffic Class.</summary>
    /// <param name="socket">An IPv4 or IPv6 UDP socket.</param>
    /// <returns>The byte.</returns>
    /// <exception cref="RtpQosException">The option cannot be read on this OS.</exception>
    public static byte Read(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        try
        {
            if (socket.AddressFamily == AddressFamily.InterNetworkV6)
            {
                Span<byte> value = stackalloc byte[sizeof(int)];
                var length = socket.GetRawSocketOption(Ipv6Level, Ipv6TrafficClassOption(), value);
                return length >= sizeof(int) ? (byte)MemoryMarshal.Read<int>(value) : value[0];
            }

            return (byte)(int)socket.GetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService)!;
        }
        catch (SocketException ex)
        {
            throw new RtpQosException($"The {(socket.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6 Traffic Class" : "IPv4 Type of Service")} of the socket cannot be read ({ex.SocketErrorCode}), so QoS marking cannot be verified.", ex);
        }
    }

    /// <summary>Returns the platform's <c>IPV6_TCLASS</c> option number.</summary>
    /// <returns>The option name.</returns>
    /// <exception cref="RtpQosException">The platform is neither Windows nor Linux.</exception>
    private static int Ipv6TrafficClassOption() =>
        OperatingSystem.IsWindows() ? WindowsIpv6TrafficClass
        : OperatingSystem.IsLinux() ? LinuxIpv6TrafficClass
        : throw new RtpQosException("IPv6 Traffic Class marking is supported on Windows and Linux only; disable QoS for IPv6 outputs on this operating system.");

    /// <summary>Describes an option the OS rejected, with the known Windows ECN restriction.</summary>
    /// <param name="family">The option.</param>
    /// <param name="policy">The policy.</param>
    /// <param name="exception">The failure.</param>
    /// <returns>The message.</returns>
    private static string Rejected(string family, RtpQosPolicy policy, SocketException exception) =>
        OperatingSystem.IsWindows() && policy.Ecn != RtpEcn.NotEct && exception.SocketErrorCode == SocketError.InvalidArgument
            ? $"Windows rejected the {family} value 0x{policy.TrafficClass:x2} ({policy}): Windows does not accept ECN bits through this socket option. Set Ecn to 0 for this output on Windows; DSCP marking alone is supported."
            : $"The operating system rejected the {family} value 0x{policy.TrafficClass:x2} ({policy}) with {exception.SocketErrorCode}. Change the output's Qos settings or disable QoS for it.";
}
