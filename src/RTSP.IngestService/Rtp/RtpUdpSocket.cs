// The UDP transport of one RTP output: one socket for the output's lifetime, configured once (bind address, TTL or hop
// limit, multicast interface and loopback, send buffer) and used only by the output's sender task. Datagrams are sent
// with the SocketAddress overload of SendToAsync, which neither allocates nor re-serialises the destination per packet.
// The sender never joins a multicast group; receivers do. QoS marking (RtpSocketQos) is applied here, once per socket and
// identically for unicast and multicast, before the socket is bound.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RTSP.IngestService.Configuration;

namespace RTSP.IngestService.Rtp;

/// <summary>A datagram socket that sends RTP and RTCP packets.</summary>
internal interface IRtpDatagramSocket : IDisposable
{
    /// <summary>Gets the bound local endpoint, if known.</summary>
    EndPoint? LocalEndPoint { get; }

    /// <summary>Gets the ToS / Traffic Class the socket reported after QoS marking was applied, or <see langword="null"/> when marking is off.</summary>
    byte? TrafficClass { get; }

    /// <summary>Sends one datagram.</summary>
    /// <param name="datagram">The datagram.</param>
    /// <param name="destination">The serialised destination.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>The bytes sent.</returns>
    ValueTask<int> SendToAsync(ReadOnlyMemory<byte> datagram, SocketAddress destination, CancellationToken cancellationToken);
}

/// <summary>Creates the socket of an output session.</summary>
/// <param name="output">The validated output settings.</param>
/// <param name="qos">The output's effective QoS marking.</param>
/// <returns>The configured socket.</returns>
internal delegate IRtpDatagramSocket RtpSocketFactory(RtpOutputOptions output, RtpQosPolicy qos);

/// <summary>Classifies transmission failures.</summary>
internal static class RtpTransportErrors
{
    /// <summary>
    /// Determines whether a send failure is transient: the network or the receiver may recover, so the packet is
    /// dropped and sending continues. Every other failure (an invalid address, a closed socket, a datagram too large for
    /// the interface, permission denied) is terminal for the output.
    /// </summary>
    /// <param name="error">The socket error.</param>
    /// <returns><see langword="true"/> for a transient failure.</returns>
    public static bool IsTransient(SocketError error) => error is
        SocketError.NoBufferSpaceAvailable or SocketError.WouldBlock or SocketError.TryAgain or SocketError.Interrupted
        or SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.NetworkDown or SocketError.HostDown
        or SocketError.ConnectionRefused or SocketError.ConnectionReset or SocketError.TimedOut or SocketError.NetworkReset;
}

/// <summary>The production <see cref="IRtpDatagramSocket"/>: a UDP <see cref="Socket"/>.</summary>
internal sealed class RtpUdpSocket : IRtpDatagramSocket
{
    /// <summary><c>SIO_UDP_CONNRESET</c>: stops ICMP port-unreachable reports failing later socket operations on Windows.</summary>
    private const int SioUdpConnectionReset = unchecked((int)0x9800000C);

    /// <summary>The socket.</summary>
    private readonly Socket _socket;

    /// <summary>Initializes a new instance of the <see cref="RtpUdpSocket"/> class and configures the socket.</summary>
    /// <param name="output">The validated output settings.</param>
    /// <param name="qos">The QoS marking; when disabled the socket's ToS / Traffic Class is left to the operating system.</param>
    /// <exception cref="SocketException">The socket cannot be created, bound or configured.</exception>
    /// <exception cref="RtpQosException">The QoS marking cannot be applied or verified.</exception>
    /// <exception cref="InvalidOperationException">The source address is not assigned to any local interface.</exception>
    public RtpUdpSocket(RtpOutputOptions output, RtpQosPolicy qos = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var destination = IPAddress.Parse(output.Destination);
        var source = output.SourceAddress is { } text ? IPAddress.Parse(text) : null;
        var ipv6 = destination.AddressFamily == AddressFamily.InterNetworkV6;
        var multicast = output.Mode == RtpOutputMode.Multicast;
        _socket = new Socket(destination.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _socket.IOControl(SioUdpConnectionReset, [0, 0, 0, 0], null);
            }

            if (qos.Enabled)
            {
                TrafficClass = RtpSocketQos.Apply(_socket, qos);
            }

            if (output.SendBufferSize is { } buffer)
            {
                _socket.SendBufferSize = buffer;
            }

            var ttl = output.EffectiveTimeToLive;
            if (multicast)
            {
                var level = ipv6 ? SocketOptionLevel.IPv6 : SocketOptionLevel.IP;
                _socket.SetSocketOption(level, SocketOptionName.MulticastTimeToLive, ttl);
                _socket.SetSocketOption(level, SocketOptionName.MulticastLoopback, output.MulticastLoopback);
                if (source is not null)
                {
                    if (ipv6)
                    {
                        _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, InterfaceIndex(source));
                    }
                    else
                    {
                        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, source.GetAddressBytes());
                    }
                }
            }
            else
            {
                _socket.Ttl = (short)ttl;
            }

            _socket.Bind(new IPEndPoint(source ?? (ipv6 ? IPAddress.IPv6Any : IPAddress.Any), 0));
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public EndPoint? LocalEndPoint => _socket.LocalEndPoint;

    /// <inheritdoc />
    public byte? TrafficClass { get; }

    /// <summary>Reads the socket's current ToS / Traffic Class; for diagnostics and tests.</summary>
    /// <returns>The byte.</returns>
    internal byte ReadTrafficClass() => RtpSocketQos.Read(_socket);

    /// <inheritdoc />
    public ValueTask<int> SendToAsync(ReadOnlyMemory<byte> datagram, SocketAddress destination, CancellationToken cancellationToken) =>
        _socket.SendToAsync(datagram, SocketFlags.None, destination, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _socket.Dispose();

    /// <summary>Finds the index of the interface that owns an IPv6 address; a scoped address names it directly.</summary>
    /// <param name="address">The address.</param>
    /// <returns>The interface index.</returns>
    /// <exception cref="InvalidOperationException">No interface has the address.</exception>
    internal static int InterfaceIndex(IPAddress address)
    {
        if (address.ScopeId != 0)
        {
            return (int)address.ScopeId;
        }

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var properties = nic.GetIPProperties();
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.Equals(address) && properties.GetIPv6Properties() is { } v6)
                {
                    return v6.Index;
                }
            }
        }

        throw new InvalidOperationException("The configured IPv6 SourceAddress is not assigned to any local network interface.");
    }
}
