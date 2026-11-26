// Tests of RTP QoS marking (DSCP and ECN). They cover:
// - configuration: defaults, ranges, strict binding, field-wise per-output overrides;
// - the bit layout of the ToS / Traffic Class byte, exhaustively;
// - real sockets for IPv4 and IPv6, unicast and multicast, enabled and disabled, checked by reading the socket option back;
// - sessions sending real RTP and RTCP over loopback, with the byte each datagram arrived with checked on Linux and the
//   sending socket's option checked on Windows;
// - Windows' rejection of ECN bits as an actionable, isolated failure;
// - that sessions never inherit another session's policy and that packetisation is unchanged by marking.

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Rtp;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Rtp;

/// <summary>Tests for RTP QoS marking.</summary>
public sealed class RtpQosTests
{
    /// <summary>A valid unicast output.</summary>
    private static readonly Dictionary<string, string?> Valid = new()
    {
        ["Rtp:Outputs:0:Name"] = "studio-a",
        ["Rtp:Outputs:0:Application"] = "live",
        ["Rtp:Outputs:0:Stream"] = "camera1",
        ["Rtp:Outputs:0:Destination"] = "192.0.2.10",
        ["Rtp:Outputs:0:VideoPort"] = "5004",
    };

    /// <summary>Gets the socket cases: destination and mode for each family.</summary>
    public static TheoryData<string, RtpOutputMode> Destinations => new()
    {
        { "127.0.0.1", RtpOutputMode.Unicast },
        { "239.255.77.20", RtpOutputMode.Multicast },
        { "::1", RtpOutputMode.Unicast },
        { "ff15::77:20", RtpOutputMode.Multicast },
    };

    /// <summary>Gets the socket cases crossed with DSCP/ECN policies, including the extremes.</summary>
    public static TheoryData<string, RtpOutputMode, int, int> MarkedDestinations
    {
        get
        {
            var data = new TheoryData<string, RtpOutputMode, int, int>();
            foreach (var (destination, mode) in new[] { ("127.0.0.1", RtpOutputMode.Unicast), ("239.255.77.20", RtpOutputMode.Multicast), ("::1", RtpOutputMode.Unicast), ("ff15::77:20", RtpOutputMode.Multicast) })
            {
                foreach (var (dscp, ecn) in new[] { (0, 0), (RtpDscp.AF41, 0), (RtpDscp.EF, 0), (63, 0), (RtpDscp.EF, RtpEcn.Ect1), (0, RtpEcn.Ect0), (63, RtpEcn.CongestionExperienced) })
                {
                    data.Add(destination, mode, dscp, ecn);
                }
            }

            return data;
        }
    }

    /// <summary>Verifies that with nothing configured marking is off for every output.</summary>
    [Fact]
    public void Defaults_LeaveMarkingOff()
    {
        var options = Bind(Valid);

        Assert.Equal((null, null, null), (options.Transport.Qos.Enabled, options.Transport.Qos.Dscp, options.Transport.Qos.Ecn));
        Assert.Null(Assert.Single(options.Outputs).Qos);
        var policy = RtpQosPolicy.Resolve(options.Transport.Qos, options.Outputs[0].Qos);
        Assert.Equal(RtpQosPolicy.Disabled, policy);
        Assert.Equal("off (operating system default)", policy.ToString());
    }

    /// <summary>Verifies the extreme valid values bind at both the transport and the output level.</summary>
    /// <param name="scope">The section prefix.</param>
    /// <param name="key">The setting.</param>
    /// <param name="value">The value.</param>
    [Theory]
    [InlineData("Rtp:Transport:Qos:", "Dscp", 0)]
    [InlineData("Rtp:Transport:Qos:", "Dscp", 63)]
    [InlineData("Rtp:Transport:Qos:", "Ecn", 0)]
    [InlineData("Rtp:Transport:Qos:", "Ecn", 3)]
    [InlineData("Rtp:Outputs:0:Qos:", "Dscp", 0)]
    [InlineData("Rtp:Outputs:0:Qos:", "Dscp", 63)]
    [InlineData("Rtp:Outputs:0:Qos:", "Ecn", 0)]
    [InlineData("Rtp:Outputs:0:Qos:", "Ecn", 3)]
    public void BoundaryValues_AreAccepted(string scope, string key, int value)
    {
        var options = Bind(new Dictionary<string, string?>(Valid) { [scope + "Enabled"] = "true", [scope + key] = value.ToString(System.Globalization.CultureInfo.InvariantCulture) });

        var policy = RtpQosPolicy.Resolve(options.Transport.Qos, options.Outputs[0].Qos);
        Assert.True(policy.Enabled);
        Assert.Equal(value, key == "Dscp" ? policy.Dscp : policy.Ecn);
    }

    /// <summary>Verifies out-of-range values are rejected with a message naming the setting and its range.</summary>
    /// <param name="scope">The section prefix.</param>
    /// <param name="key">The setting.</param>
    /// <param name="value">The value.</param>
    /// <param name="range">The range named in the message.</param>
    [Theory]
    [InlineData("Rtp:Transport:Qos:", "Dscp", "-1", "between 0 and 63")]
    [InlineData("Rtp:Transport:Qos:", "Dscp", "64", "between 0 and 63")]
    [InlineData("Rtp:Transport:Qos:", "Ecn", "-1", "between 0 and 3")]
    [InlineData("Rtp:Transport:Qos:", "Ecn", "4", "between 0 and 3")]
    [InlineData("Rtp:Outputs:0:Qos:", "Dscp", "-1", "between 0 and 63")]
    [InlineData("Rtp:Outputs:0:Qos:", "Dscp", "64", "between 0 and 63")]
    [InlineData("Rtp:Outputs:0:Qos:", "Ecn", "-1", "between 0 and 3")]
    [InlineData("Rtp:Outputs:0:Qos:", "Ecn", "4", "between 0 and 3")]
    public void OutOfRangeValues_FailValidation(string scope, string key, string value, string range)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Bind(new Dictionary<string, string?>(Valid) { [scope + key] = value }));

        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
        Assert.Contains(range, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies unknown keys and non-numeric values in a QoS section are configuration errors.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    [Theory]
    [InlineData("Rtp:Transport:Qos:Priority", "1")]
    [InlineData("Rtp:Transport:Tos", "136")]
    [InlineData("Rtp:Outputs:0:Qos:TrafficClass", "136")]
    [InlineData("Rtp:Transport:Qos:Dscp", "AF41")]
    [InlineData("Rtp:Outputs:0:Qos:Enabled", "sometimes")]
    public void InvalidKeysAndValues_FailBinding(string key, string value) =>
        Assert.Throws<OptionsValidationException>(() => Bind(new Dictionary<string, string?>(Valid) { [key] = value }));

    /// <summary>Verifies an output's settings override the transport default one field at a time.</summary>
    [Fact]
    public void OutputOverrides_AreFieldWise()
    {
        var transport = RtpQosTestData.Qos(RtpDscp.AF41);

        Assert.Equal(new RtpQosPolicy(true, RtpDscp.AF41, 0), RtpQosPolicy.Resolve(transport, null));
        Assert.Equal(new RtpQosPolicy(true, RtpDscp.AF41, RtpEcn.Ect0), RtpQosPolicy.Resolve(transport, RtpQosTestData.Qos(null, RtpEcn.Ect0, enabled: null)));
        Assert.Equal(new RtpQosPolicy(true, RtpDscp.EF, 0), RtpQosPolicy.Resolve(transport, RtpQosTestData.Qos(RtpDscp.EF, enabled: null)));
        Assert.False(RtpQosPolicy.Resolve(transport, RtpQosTestData.Qos(null, enabled: false)).Enabled);
        Assert.Equal(new RtpQosPolicy(true, RtpDscp.CS6, 0), RtpQosPolicy.Resolve(new RtpQosOptions(), RtpQosTestData.Qos(RtpDscp.CS6)));
        Assert.False(RtpQosPolicy.Resolve(new RtpQosOptions { Dscp = RtpDscp.EF }, null).Enabled);

        var options = Bind(new Dictionary<string, string?>(Valid)
        {
            ["Rtp:Transport:Qos:Enabled"] = "true",
            ["Rtp:Transport:Qos:Dscp"] = "34",
            ["Rtp:Outputs:0:Qos:Ecn"] = "1",
            ["Rtp:Outputs:1:Name"] = "studio-b",
            ["Rtp:Outputs:1:Application"] = "live",
            ["Rtp:Outputs:1:Stream"] = "camera1",
            ["Rtp:Outputs:1:Destination"] = "192.0.2.11",
            ["Rtp:Outputs:1:VideoPort"] = "5004",
            ["Rtp:Outputs:1:Qos:Enabled"] = "false",
        });
        Assert.Equal(new RtpQosPolicy(true, 34, 1), RtpQosPolicy.Resolve(options.Transport.Qos, options.Outputs[0].Qos));
        Assert.Equal(new RtpQosPolicy(false, 34, 0), RtpQosPolicy.Resolve(options.Transport.Qos, options.Outputs[1].Qos));
    }

    /// <summary>
    /// Verifies every DSCP/ECN combination: DSCP occupies bits 7-2 and ECN bits 1-0, and replacing either leaves the other unchanged.
    /// </summary>
    [Fact]
    public void TrafficClass_LayoutIsExact_ForEveryCombination()
    {
        var seen = new HashSet<byte>();
        for (var dscp = 0; dscp <= 63; dscp++)
        {
            for (var ecn = 0; ecn <= 3; ecn++)
            {
                var value = RtpTrafficClass.Compose(dscp, ecn);
                Assert.Equal((dscp << 2) | ecn, value);
                Assert.Equal(dscp, (value & 0b1111_1100) >> 2);
                Assert.Equal(ecn, value & 0b0000_0011);
                Assert.Equal((dscp, ecn), (RtpTrafficClass.DscpOf(value), RtpTrafficClass.EcnOf(value)));
                Assert.Equal(value, new RtpQosPolicy(true, dscp, ecn).TrafficClass);
                Assert.True(seen.Add(value));
                for (var other = 0; other <= 63; other++)
                {
                    var changed = RtpTrafficClass.WithDscp(value, other);
                    Assert.Equal((other, ecn), (RtpTrafficClass.DscpOf(changed), RtpTrafficClass.EcnOf(changed)));
                }

                for (var other = 0; other <= 3; other++)
                {
                    var changed = RtpTrafficClass.WithEcn(value, other);
                    Assert.Equal((dscp, other), (RtpTrafficClass.DscpOf(changed), RtpTrafficClass.EcnOf(changed)));
                }
            }
        }

        Assert.Equal(256, seen.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => RtpTrafficClass.Compose(64, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RtpTrafficClass.Compose(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RtpTrafficClass.Compose(0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => RtpTrafficClass.WithEcn(0, -1));
    }

    /// <summary>Verifies the named code points and the policy descriptions used in logs.</summary>
    [Fact]
    public void NamedValues_AreTheStandardCodePoints()
    {
        Assert.Equal([0, 10, 18, 26, 34, 46, 48], new[] { RtpDscp.CS0, RtpDscp.AF11, RtpDscp.AF21, RtpDscp.AF31, RtpDscp.AF41, RtpDscp.EF, RtpDscp.CS6 });
        Assert.Equal([0, 1, 2, 3], new[] { RtpEcn.NotEct, RtpEcn.Ect1, RtpEcn.Ect0, RtpEcn.CongestionExperienced });
        Assert.Equal(0x88, RtpTrafficClass.Compose(RtpDscp.AF41, RtpEcn.NotEct));
        Assert.Equal(0xB8, RtpTrafficClass.Compose(RtpDscp.EF, RtpEcn.NotEct));
        Assert.Equal("DSCP 34 (AF41), ECN 0 (Not-ECT), traffic class 0x88", new RtpQosPolicy(true, 34, 0).ToString());
        Assert.Equal("DSCP 46 (EF), ECN 1 (ECT(1)), traffic class 0xb9", new RtpQosPolicy(true, 46, 1).ToString());
        Assert.Equal("DSCP 33, ECN 3 (CE), traffic class 0x87", new RtpQosPolicy(true, 33, 3).ToString());
    }

    /// <summary>
    /// Verifies a real socket of every family and mode carries the configured byte, read back from the socket option.
    /// Windows rejects ECN bits in this option; there the socket must fail with an actionable error instead.
    /// </summary>
    /// <param name="destination">The destination.</param>
    /// <param name="mode">The mode.</param>
    /// <param name="dscp">The DSCP.</param>
    /// <param name="ecn">The ECN codepoint.</param>
    [Theory]
    [MemberData(nameof(MarkedDestinations))]
    public void Socket_AppliesAndVerifiesMarking(string destination, RtpOutputMode mode, int dscp, int ecn)
    {
        var output = Output(destination, mode);
        var policy = new RtpQosPolicy(true, dscp, ecn);

        if (OperatingSystem.IsWindows() && ecn != RtpEcn.NotEct)
        {
            var ex = Assert.Throws<RtpQosException>(() => new RtpUdpSocket(output, policy).Dispose());
            Assert.Contains("Windows does not accept ECN bits", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Set Ecn to 0", ex.Message, StringComparison.Ordinal);
            Assert.Equal(SocketError.InvalidArgument, Assert.IsType<SocketException>(ex.InnerException).SocketErrorCode);
            return;
        }

        using var socket = new RtpUdpSocket(output, policy);
        Assert.Equal(policy.TrafficClass, socket.TrafficClass);
        Assert.Equal(policy.TrafficClass, socket.ReadTrafficClass());
    }

    /// <summary>Verifies a disabled policy leaves the operating system's default, even when values are configured.</summary>
    /// <param name="destination">The destination.</param>
    /// <param name="mode">The mode.</param>
    [Theory]
    [MemberData(nameof(Destinations))]
    public void Socket_Disabled_KeepsOperatingSystemDefault(string destination, RtpOutputMode mode)
    {
        var output = Output(destination, mode);
        var family = IPAddress.Parse(destination).AddressFamily;
        var expected = RtpQosTestData.OperatingSystemDefault(family);

        using (var socket = new RtpUdpSocket(output, RtpQosPolicy.Disabled))
        {
            Assert.Null(socket.TrafficClass);
            Assert.Equal(expected, socket.ReadTrafficClass());
        }

        using (var socket = new RtpUdpSocket(output, RtpQosPolicy.Resolve(new RtpQosOptions { Enabled = false, Dscp = RtpDscp.EF, Ecn = 1 }, null)))
        {
            Assert.Null(socket.TrafficClass);
            Assert.Equal(expected, socket.ReadTrafficClass());
        }
    }

    /// <summary>Verifies the marked socket is closed by disposal like any other, and that each socket keeps its own value.</summary>
    [Fact]
    public void Sockets_AreIndependentAndDisposed()
    {
        var output = Output("127.0.0.1", RtpOutputMode.Unicast);
        var af41 = new RtpUdpSocket(output, new RtpQosPolicy(true, RtpDscp.AF41, 0));
        using var plain = new RtpUdpSocket(output, RtpQosPolicy.Disabled);
        using var ef = new RtpUdpSocket(output, new RtpQosPolicy(true, RtpDscp.EF, 0));

        Assert.Equal(0x88, af41.ReadTrafficClass());
        Assert.Equal(0xB8, ef.ReadTrafficClass());
        Assert.Equal(RtpQosTestData.OperatingSystemDefault(AddressFamily.InterNetwork), plain.ReadTrafficClass());
        af41.Dispose();
        Assert.Throws<ObjectDisposedException>(() => af41.ReadTrafficClass());
        Assert.Equal(0xB8, ef.ReadTrafficClass());
    }

    /// <summary>
    /// Verifies a unicast session sends its RTP and RTCP with the configured marking: on Linux the byte every datagram
    /// arrived with is checked; on Windows the sending socket's option value is.
    /// </summary>
    /// <param name="address">The loopback address.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task UnicastSession_SendsMarkedRtpAndRtcp(string address)
    {
        using var receiver = RtpQosTestData.Receiver(IPAddress.Parse(address));
        var output = RtpTestData.Output(videoPort: receiver.Port, audioPort: null);
        output.Destination = address;
        var sockets = new RecordingSocketFactory();
        var transport = new RtpTransportOptions { Qos = RtpQosTestData.Qos(RtpDscp.AF41) };
        await using var harness = new RtpHarness(sockets.Factory, null, transport, output);
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session);
        var idr = RtpTestData.Nal(5, 4000);
        await harness.FrameAsync(session, 0, keyframe: true, idr);

        var rtp = await ReceiveAccessUnitAsync(receiver.Rtp);
        var rtcp = await ReceiveAsync(receiver.Rtcp);

        Assert.Equal([RtpTestData.Sps, RtpTestData.Pps, idr], H264Depacketizer.NalUnits(rtp.Select(static d => RtpPacket.Parse(d.Datagram))));
        if (OperatingSystem.IsLinux())
        {
            Assert.All(rtp, static d => Assert.Equal((byte)0x88, d.TrafficClass));
            Assert.Equal((byte)0x88, rtcp.TrafficClass);
        }

        var (_, qos, socket) = Assert.Single(sockets.Created);
        Assert.Equal(new RtpQosPolicy(true, RtpDscp.AF41, 0), qos);
        Assert.Equal(0x88, socket.ReadTrafficClass());
        var status = Assert.Single(harness.Handler.GetOutputs());
        Assert.Equal((byte)0x88, status.Session!.TrafficClass);
        Assert.Contains(harness.Logs.GetSnapshot(), static r => r.Id.Id == 9409 && r.Message.Contains("DSCP 34, ECN 0, traffic class 0x88", StringComparison.Ordinal));
        Assert.Contains(harness.Logs.GetSnapshot(), r => r.Id.Id == 9400 && r.Message.Contains($"({(address.Contains(':', StringComparison.Ordinal) ? "IPv6" : "IPv4")})", StringComparison.Ordinal)
            && r.Message.Contains("QoS DSCP 34 (AF41), ECN 0 (Not-ECT), traffic class 0x88, applied (socket reports 0x88)", StringComparison.Ordinal));
        await harness.EndAsync(session);
    }

    /// <summary>
    /// Verifies a multicast session to a group joined on loopback sends marked RTP, with the same semantics as unicast.
    /// Skipped where the host cannot loop multicast back (the existing multicast test's condition).
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task MulticastSession_SendsMarkedRtp()
    {
        var group = IPAddress.Parse("239.255.77.30");
        using var receiver = RtpQosTestData.Receiver(IPAddress.Any);
        try
        {
            receiver.Rtp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group, IPAddress.Loopback));
        }
        catch (SocketException ex)
        {
            Assert.Skip($"Joining a multicast group on loopback is not supported here: {ex.SocketErrorCode}.");
        }

        var output = RtpTestData.Output(videoPort: receiver.Port, audioPort: null);
        output.Mode = RtpOutputMode.Multicast;
        output.Destination = group.ToString();
        output.SourceAddress = "127.0.0.1";
        output.MulticastLoopback = true;
        output.Qos = RtpQosTestData.Qos(RtpDscp.EF);
        var sockets = new RecordingSocketFactory();
        await using var harness = new RtpHarness(sockets.Factory, null, output);
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session);
        var idr = RtpTestData.Nal(5, 3000);
        await harness.FrameAsync(session, 0, keyframe: true, idr);

        var rtp = await ReceiveAccessUnitAsync(receiver.Rtp);

        Assert.Equal([RtpTestData.Sps, RtpTestData.Pps, idr], H264Depacketizer.NalUnits(rtp.Select(static d => RtpPacket.Parse(d.Datagram))));
        if (OperatingSystem.IsLinux())
        {
            Assert.All(rtp, static d => Assert.Equal((byte)0xB8, d.TrafficClass));
        }

        Assert.Equal(0xB8, Assert.Single(sockets.Created).Socket.ReadTrafficClass());
        await harness.EndAsync(session);
    }

    /// <summary>
    /// Verifies ECN marking per platform. Linux sends the full byte, ECN bits included. Windows rejects ECN bits: the
    /// startup check logs the actionable reason (9410), the output's session fails to start (9402) without affecting
    /// the other output, and nothing is sent unmarked by the failed output.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task EcnMarking_IsAppliedOnLinux_AndRejectedActionablyOnWindows()
    {
        using var ecnReceiver = RtpQosTestData.Receiver(IPAddress.Loopback);
        using var plainReceiver = RtpQosTestData.Receiver(IPAddress.Loopback);
        var ecn = RtpTestData.Output("ecn", ecnReceiver.Port, null);
        ecn.Qos = RtpQosTestData.Qos(RtpDscp.EF, RtpEcn.Ect1);
        var plain = RtpTestData.Output("plain", plainReceiver.Port, null);
        var sockets = new RecordingSocketFactory();
        await using var harness = new RtpHarness(sockets.Factory, null, ecn, plain);
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session);
        await harness.FrameAsync(session, 0, keyframe: true, RtpTestData.Nal(5, 500));

        var plainPackets = await ReceiveAccessUnitAsync(plainReceiver.Rtp);
        Assert.NotEmpty(plainPackets);
        var statuses = harness.Handler.GetOutputs();
        var logs = harness.Logs.GetSnapshot();
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(RtpOutputSessionState.Failed, statuses[0].Session!.State);
            Assert.Null(statuses[0].Session!.TrafficClass);
            Assert.Contains(logs, static r => r.Id.Id == 9410 && r.Message.Contains("ecn", StringComparison.Ordinal) && r.Message.Contains("Set Ecn to 0", StringComparison.Ordinal));
            Assert.Contains(logs, static r => r.Id.Id == 9402 && r.Exception is RtpQosException q && q.Message.Contains("Windows does not accept ECN bits", StringComparison.Ordinal));
            Assert.DoesNotContain(sockets.Created, static c => c.Output == "ecn");
            Assert.Equal(0, ecnReceiver.Rtp.Available);
        }
        else
        {
            var marked = await ReceiveAccessUnitAsync(ecnReceiver.Rtp);
            Assert.Equal(RtpOutputSessionState.Active, statuses[0].Session!.State);
            Assert.Equal((byte)0xB9, statuses[0].Session!.TrafficClass);
            Assert.Contains(logs, static r => r.Id.Id == 9409 && r.Message.Contains("traffic class 0xb9", StringComparison.Ordinal));
            if (OperatingSystem.IsLinux())
            {
                Assert.All(marked, static d => Assert.Equal((byte)0xB9, d.TrafficClass));
            }
        }

        Assert.Equal(RtpOutputSessionState.Active, statuses[1].Session!.State);
        if (OperatingSystem.IsLinux())
        {
            Assert.All(plainPackets, static d => Assert.Equal((byte)0, d.TrafficClass));
        }

        await harness.EndAsync(session);
    }

    /// <summary>Verifies a QoS failure from the socket factory fails only its own output, with the reason logged.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task QosFailure_IsIsolatedToItsOutput()
    {
        var healthy = new FakeDatagramSocket();
        await using var harness = new RtpHarness(
            (o, q) => q.Enabled ? throw new RtpQosException("Marking rejected for test.") : healthy,
            null,
            new RtpTransportOptions { Qos = RtpQosTestData.Qos(RtpDscp.AF31) },
            RtpTestData.Output("marked", 5004, null),
            WithQos(RtpTestData.Output("unmarked", 5010, null), RtpQosTestData.Qos(null, enabled: false)));
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session);
        await harness.FrameAsync(session, 0, keyframe: true, RtpTestData.Nal(5, 200));

        await Eventually.WaitUntilAsync(() => !healthy.Sent.IsEmpty, "the unmarked output sends");
        var statuses = harness.Handler.GetOutputs();
        Assert.Equal(RtpOutputSessionState.Failed, statuses[0].Session!.State);
        Assert.Equal(RtpOutputSessionState.Active, statuses[1].Session!.State);
        Assert.Contains(harness.Logs.GetSnapshot(), static r => r.Id.Id == 9402 && r.Exception?.Message == "Marking rejected for test.");
        Assert.Single(harness.Inner.Video);
        await harness.EndAsync(session);
    }

    /// <summary>
    /// Verifies two outputs of one stream, one marked EF and one unmarked, over three publications. Every session opens
    /// its own socket with its own output's policy, so no policy leaks between sessions or publications. The RTP
    /// packetisation of both outputs is identical apart from the per-session SSRC, sequence and timestamp offset.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Sessions_NeverInheritAnotherSessionsPolicy_AndPacketisationIsUnchanged()
    {
        using var markedReceiver = RtpQosTestData.Receiver(IPAddress.Loopback);
        using var plainReceiver = RtpQosTestData.Receiver(IPAddress.Loopback);
        var marked = WithQos(RtpTestData.Output("marked", markedReceiver.Port, null), RtpQosTestData.Qos(RtpDscp.EF));
        var plain = RtpTestData.Output("plain", plainReceiver.Port, null);
        var sockets = new RecordingSocketFactory();
        await using var harness = new RtpHarness(sockets.Factory, null, marked, plain);
        var systemDefault = RtpQosTestData.OperatingSystemDefault(AddressFamily.InterNetwork);

        for (var publication = 0; publication < 3; publication++)
        {
            var session = await harness.StartAsync();
            await harness.ConfigureAsync(session);
            var nalUnits = new[] { RtpTestData.Nal(9, 2), RtpTestData.Nal(6, 20, publication), RtpTestData.Nal(5, 3500, publication) };
            await harness.FrameAsync(session, 0, keyframe: true, nalUnits);

            var markedPackets = await ReceiveAccessUnitAsync(markedReceiver.Rtp);
            var plainPackets = await ReceiveAccessUnitAsync(plainReceiver.Rtp);
            var markedRtp = markedPackets.Select(static d => RtpPacket.Parse(d.Datagram)).ToList();
            var plainRtp = plainPackets.Select(static d => RtpPacket.Parse(d.Datagram)).ToList();
            Assert.Equal(markedRtp.Select(static p => (p.Marker, p.PayloadType, p.Payload.Length)), plainRtp.Select(static p => (p.Marker, p.PayloadType, p.Payload.Length)));
            Assert.All(markedRtp.Zip(plainRtp), static pair => Assert.Equal(pair.First.Payload, pair.Second.Payload));
            Assert.All(markedRtp.Zip(markedRtp.Skip(1)), static pair => Assert.Equal((ushort)(pair.First.SequenceNumber + 1), pair.Second.SequenceNumber));
            if (OperatingSystem.IsLinux())
            {
                Assert.All(markedPackets, static d => Assert.Equal((byte)0xB8, d.TrafficClass));
                Assert.All(plainPackets, d => Assert.Equal(systemDefault, d.TrafficClass));
            }

            await harness.EndAsync(session);
        }

        Assert.Equal(6, sockets.Created.Count);
        Assert.Equal(6, sockets.Created.Select(static c => c.Socket).Distinct().Count());
        Assert.All(sockets.Created.Where(static c => c.Output == "marked"), static c => Assert.Equal(new RtpQosPolicy(true, RtpDscp.EF, 0), c.Qos));
        Assert.All(sockets.Created.Where(static c => c.Output == "plain"), static c => Assert.Equal(RtpQosPolicy.Disabled, c.Qos));
        Assert.All(sockets.Created.Where(static c => c.Output == "plain"), static c => Assert.Null(c.Socket.TrafficClass));
        Assert.All(sockets.Created.Where(static c => c.Output == "marked"), static c => Assert.Equal((byte)0xB8, c.Socket.TrafficClass));
    }

    /// <summary>Verifies the startup diagnostics: the QoS count, each marked output's policy, and the CE warning.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Startup_LogsEachOutputsPolicy()
    {
        await using var harness = new RtpHarness(
            (_, _) => new FakeDatagramSocket(),
            null,
            new RtpTransportOptions { Qos = RtpQosTestData.Qos(RtpDscp.AF41) },
            RtpTestData.Output("default-af41", 5004, null),
            WithQos(RtpTestData.Output("off", 5010, null), RtpQosTestData.Qos(null, enabled: false)),
            WithQos(RtpTestData.Output("ce", 5020, null), RtpQosTestData.Qos(RtpDscp.CS0, RtpEcn.CongestionExperienced)));

        var logs = harness.Logs.GetSnapshot();
        Assert.Contains(logs, static r => r.Id.Id == 9407 && r.Message == "RTP output enabled with 3 outputs (0 multicast, 2 with QoS marking)");
        Assert.Contains(logs, static r => r.Id.Id == 9409 && r.Message == "RTP output default-af41 QoS marking enabled for IPv4: DSCP 34, ECN 0, traffic class 0x88; accepted and verified on a probe socket");
        Assert.DoesNotContain(logs, static r => r.Message.Contains("RTP output off QoS", StringComparison.Ordinal));
        Assert.Contains(logs, static r => r.Id.Id == 9411 && r.Level == Microsoft.Extensions.Logging.LogLevel.Warning && r.Message.Contains("RTP output ce sets ECN to CE", StringComparison.Ordinal));
        Assert.Contains(logs, static r => r.Id.Id == (OperatingSystem.IsWindows() ? 9410 : 9409) && r.Message.StartsWith("RTP output ce QoS marking", StringComparison.Ordinal));
        var statuses = harness.Handler.GetOutputs();
        Assert.Equal([new RtpQosPolicy(true, 34, 0), RtpQosPolicy.Disabled with { Dscp = 34 }, new RtpQosPolicy(true, 0, 3)], statuses.Select(static s => s.Qos));
    }

    /// <summary>Returns an output with a QoS section.</summary>
    /// <param name="output">The output.</param>
    /// <param name="qos">The QoS section.</param>
    /// <returns>The output.</returns>
    private static RtpOutputOptions WithQos(RtpOutputOptions output, RtpQosOptions qos)
    {
        output.Qos = qos;
        return output;
    }

    /// <summary>Returns an output to a destination for socket tests.</summary>
    /// <param name="destination">The destination.</param>
    /// <param name="mode">The mode.</param>
    /// <returns>The output.</returns>
    private static RtpOutputOptions Output(string destination, RtpOutputMode mode)
    {
        var output = RtpTestData.Output();
        output.Destination = destination;
        output.Mode = mode;
        return output;
    }

    /// <summary>Receives one datagram, with its ToS / Traffic Class on Linux.</summary>
    /// <param name="socket">The socket.</param>
    /// <returns>The datagram; the traffic class is 0 where it cannot be observed.</returns>
    private static async Task<MarkedDatagram> ReceiveAsync(Socket socket) => OperatingSystem.IsLinux()
        ? await LinuxTrafficClassReceiver.ReceiveAsync(socket).WithinSafetyTimeout()
        : new MarkedDatagram(await UdpReceiver.ReceiveAsync(socket), 0);

    /// <summary>Receives RTP datagrams up to the first one with the marker bit.</summary>
    /// <param name="socket">The RTP socket.</param>
    /// <returns>The datagrams.</returns>
    private static async Task<List<MarkedDatagram>> ReceiveAccessUnitAsync(Socket socket)
    {
        var datagrams = new List<MarkedDatagram>();
        while (datagrams.Count == 0 || !RtpPacket.Parse(datagrams[^1].Datagram).Marker)
        {
            datagrams.Add(await ReceiveAsync(socket));
        }

        return datagrams;
    }

    /// <summary>Binds and validates the RTP options from configuration.</summary>
    /// <param name="values">The configuration.</param>
    /// <returns>The options.</returns>
    private static RtpOptions Bind(IReadOnlyDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var provider = new ServiceCollection().AddIngestServiceOptions(configuration).BuildServiceProvider();
        return provider.GetRequiredService<IOptions<RtpOptions>>().Value;
    }
}
