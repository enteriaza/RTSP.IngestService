// Test support for QoS marking: on Linux, reads the IPv4 ToS / IPv6 Traffic Class each UDP datagram arrived with, from
// the IP_TOS / IPV6_TCLASS ancillary data that IP_RECVTOS / IPV6_RECVTCLASS enable. .NET's ReceiveMessageFrom surfaces
// only packet information, so recvmsg is called directly. Windows can report the received byte only through WSARecvMsg
// control messages, so Windows tests inspect the sending socket's option value instead.

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Rtp;

namespace RTSP.IngestService.Tests.Rtp;

/// <summary>A UDP datagram and the ToS / Traffic Class byte it was received with.</summary>
/// <param name="Datagram">The payload.</param>
/// <param name="TrafficClass">The received byte.</param>
internal sealed record MarkedDatagram(byte[] Datagram, byte TrafficClass);

/// <summary>Receives UDP datagrams with their ToS / Traffic Class on Linux.</summary>
[SupportedOSPlatform("linux")]
internal static class LinuxTrafficClassReceiver
{
    /// <summary><c>IPPROTO_IP</c>.</summary>
    private const int IpLevel = 0;

    /// <summary><c>IP_TOS</c>.</summary>
    private const int IpTos = 1;

    /// <summary><c>IP_RECVTOS</c>.</summary>
    private const int IpReceiveTos = 13;

    /// <summary><c>IPPROTO_IPV6</c>.</summary>
    private const int Ipv6Level = 41;

    /// <summary><c>IPV6_RECVTCLASS</c>.</summary>
    private const int Ipv6ReceiveTrafficClass = 66;

    /// <summary><c>IPV6_TCLASS</c>.</summary>
    private const int Ipv6TrafficClass = 67;

    /// <summary><c>MSG_DONTWAIT</c>.</summary>
    private const int DontWait = 0x40;

    /// <summary>Asks the kernel to report the ToS / Traffic Class of every datagram received on a socket.</summary>
    /// <param name="socket">A bound UDP socket, before any datagram of interest arrives.</param>
    public static void Enable(Socket socket)
    {
        Span<byte> on = stackalloc byte[sizeof(int)];
        MemoryMarshal.Write(on, 1);
        if (socket.AddressFamily == AddressFamily.InterNetworkV6)
        {
            socket.SetRawSocketOption(Ipv6Level, Ipv6ReceiveTrafficClass, on);
        }
        else
        {
            socket.SetRawSocketOption(IpLevel, IpReceiveTos, on);
        }
    }

    /// <summary>Receives one datagram and the byte it arrived with.</summary>
    /// <param name="socket">A socket passed to <see cref="Enable"/>.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>The datagram.</returns>
    /// <exception cref="TimeoutException">Nothing arrived in time.</exception>
    /// <exception cref="InvalidOperationException">The datagram carried no ToS / Traffic Class.</exception>
    public static MarkedDatagram Receive(Socket socket, TimeSpan timeout)
    {
        if (!socket.Poll(timeout, SelectMode.SelectRead))
        {
            throw new TimeoutException("No datagram arrived.");
        }

        const int DataLength = 65536;
        const int ControlLength = 256;
        var data = Marshal.AllocHGlobal(DataLength);
        var control = Marshal.AllocHGlobal(ControlLength);
        var vector = Marshal.AllocHGlobal(Marshal.SizeOf<IoVector>());
        try
        {
            Marshal.StructureToPtr(new IoVector { Base = data, Length = DataLength }, vector, false);
            var header = new MessageHeader { Vector = vector, VectorLength = 1, Control = control, ControlLength = ControlLength };
            var received = ReceiveMessage((int)socket.SafeHandle.DangerousGetHandle(), ref header, DontWait);
            if (received < 0)
            {
                throw new SocketException(Marshal.GetLastPInvokeError());
            }

            var datagram = new byte[received];
            Marshal.Copy(data, datagram, 0, (int)received);
            return new MarkedDatagram(datagram, TrafficClass(control, (int)header.ControlLength) ?? throw new InvalidOperationException("The datagram carried no IP_TOS / IPV6_TCLASS control message."));
        }
        finally
        {
            Marshal.FreeHGlobal(vector);
            Marshal.FreeHGlobal(control);
            Marshal.FreeHGlobal(data);
        }
    }

    /// <summary>Receives datagrams on a background thread.</summary>
    /// <param name="socket">A socket passed to <see cref="Enable"/>.</param>
    /// <returns>The datagram.</returns>
    public static Task<MarkedDatagram> ReceiveAsync(Socket socket) => Task.Run(() => Receive(socket, TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken);

    /// <summary>Finds the IP_TOS (one byte) or IPV6_TCLASS (an int) control message.</summary>
    /// <param name="control">The control buffer.</param>
    /// <param name="length">Its used length.</param>
    /// <returns>The byte, or <see langword="null"/>.</returns>
    private static byte? TrafficClass(nint control, int length)
    {
        var offset = 0;
        while (offset + 16 <= length)
        {
            var messageLength = (int)Marshal.ReadInt64(control, offset);
            var level = Marshal.ReadInt32(control, offset + 8);
            var type = Marshal.ReadInt32(control, offset + 12);
            if ((level, type) is (IpLevel, IpTos))
            {
                return Marshal.ReadByte(control, offset + 16);
            }

            if ((level, type) is (Ipv6Level, Ipv6TrafficClass))
            {
                return (byte)Marshal.ReadInt32(control, offset + 16);
            }

            if (messageLength < 16)
            {
                break;
            }

            offset += (messageLength + 7) & ~7;
        }

        return null;
    }

    /// <summary><c>recvmsg(2)</c>.</summary>
    /// <param name="socket">The file descriptor.</param>
    /// <param name="message">The message header.</param>
    /// <param name="flags">Flags.</param>
    /// <returns>The bytes received, or -1.</returns>
    [DllImport("libc", EntryPoint = "recvmsg", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint ReceiveMessage(int socket, ref MessageHeader message, int flags);

    /// <summary><c>struct iovec</c> (64-bit).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IoVector
    {
        /// <summary>The buffer.</summary>
        public nint Base;

        /// <summary>Its length.</summary>
        public nuint Length;
    }

    /// <summary><c>struct msghdr</c> (64-bit).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MessageHeader
    {
        /// <summary>The source address buffer (unused).</summary>
        public nint Name;

        /// <summary>Its length.</summary>
        public uint NameLength;

        /// <summary>The scatter vector.</summary>
        public nint Vector;

        /// <summary>The vector length.</summary>
        public nuint VectorLength;

        /// <summary>The ancillary data buffer.</summary>
        public nint Control;

        /// <summary>Its length; set to the used length on return.</summary>
        public nuint ControlLength;

        /// <summary>Flags on return.</summary>
        public int Flags;
    }
}

/// <summary>Records the sockets an output factory creates, so tests can inspect their options.</summary>
internal sealed class RecordingSocketFactory
{
    /// <summary>Gets the sockets created, with the output and policy each was created for.</summary>
    public List<(string Output, RtpQosPolicy Qos, RtpUdpSocket Socket)> Created { get; } = [];

    /// <summary>Gets the factory.</summary>
    public RtpSocketFactory Factory => Create;

    /// <summary>Creates and records a real socket.</summary>
    /// <param name="output">The output.</param>
    /// <param name="qos">The policy.</param>
    /// <returns>The socket.</returns>
    private RtpUdpSocket Create(RtpOutputOptions output, RtpQosPolicy qos)
    {
        var socket = new RtpUdpSocket(output, qos);
        lock (Created)
        {
            Created.Add((output.Name, qos, socket));
        }

        return socket;
    }
}

/// <summary>Helpers for QoS tests.</summary>
internal static class RtpQosTestData
{
    /// <summary>Returns a QoS section.</summary>
    /// <param name="dscp">The DSCP.</param>
    /// <param name="ecn">The ECN codepoint.</param>
    /// <param name="enabled">Whether marking is enabled.</param>
    /// <returns>The options.</returns>
    public static RtpQosOptions Qos(int? dscp, int? ecn = null, bool? enabled = true) => new() { Enabled = enabled, Dscp = dscp, Ecn = ecn };

    /// <summary>Returns the byte a socket of a family has before any marking: what the operating system uses by default.</summary>
    /// <param name="family">The family.</param>
    /// <returns>The byte.</returns>
    public static byte OperatingSystemDefault(AddressFamily family)
    {
        using var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        return RtpSocketQos.Read(socket);
    }

    /// <summary>Creates a receiver; on Linux it records the ToS / Traffic Class of each datagram.</summary>
    /// <param name="address">The local address.</param>
    /// <returns>The receiver.</returns>
    public static UdpReceiver Receiver(IPAddress address)
    {
        var receiver = UdpReceiver.Bind(address);
        if (OperatingSystem.IsLinux())
        {
            LinuxTrafficClassReceiver.Enable(receiver.Rtp);
            LinuxTrafficClassReceiver.Enable(receiver.Rtcp);
        }

        return receiver;
    }
}
