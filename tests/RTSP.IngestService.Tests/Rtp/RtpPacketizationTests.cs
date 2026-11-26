// Tests of RTP packetisation without any socket: RFC 3550 headers (version, payload type, SSRC, sequence and
// timestamp wraparound, marker), RFC 6184 H.264 single NAL unit, STAP-A and FU-A packets within the packet size limit,
// in-band parameter sets, RFC 3640 AAC-hbr AU headers, fragmentation and timestamps, the RTCP compound layout, and
// allocation-free steady-state packetisation.

using System.Buffers.Binary;
using System.Net.Sockets;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtp;
using RTSP.IngestService.Tests.Media;

namespace RTSP.IngestService.Tests.Rtp;

/// <summary>Tests for <see cref="RtpStream"/>, <see cref="H264RtpPacketizer"/>, <see cref="AacRtpPacketizer"/> and <see cref="RtcpPackets"/>.</summary>
public sealed class RtpPacketizationTests
{
    /// <summary>A packet size limit small enough to force fragmentation with short test data.</summary>
    private const int SmallSlot = 200;

    /// <summary>Verifies the fixed header: version 2, no padding, extension or CSRCs, the payload type, sequence number, timestamp and SSRC.</summary>
    [Fact]
    public void Header_HasRfc3550Fields()
    {
        var stream = new RtpStream(101, 90000, ssrc: 0x11223344, sequenceNumber: 4321, timestampOffset: 1000);
        var ring = new RtpPacketRing(16, 1472);
        var packetizer = new H264RtpPacketizer(stream);
        packetizer.SetNalUnits([RtpTestData.Nal(1, 50)]);

        Assert.Equal(RtpPacketizeResult.Queued, packetizer.Packetize(ring, 0, stream.Timestamp(90), out var packets, out var bytes));

        var packet = Assert.Single(Drain(ring));
        Assert.Equal((1, 62), (packets, bytes));
        Assert.Equal(new RtpPacket(2, false, false, 0, true, 101, 4321, 1090, 0x11223344, RtpTestData.Nal(1, 50)), packet, RtpPacketComparer.Instance);
        Assert.Equal(4322, stream.NextSequenceNumber);
    }

    /// <summary>Verifies the sequence number wraps from 65535 to 0 and keeps counting.</summary>
    [Fact]
    public void SequenceNumber_WrapsAround()
    {
        var stream = new RtpStream(96, 90000, 1, 65534, 0);
        var ring = new RtpPacketRing(16, 1472);
        var packetizer = new H264RtpPacketizer(stream);
        packetizer.SetNalUnits([RtpTestData.Nal(1, 3000)]);

        packetizer.Packetize(ring, 0, 0, out var count, out _);

        Assert.Equal(3, count);
        Assert.Equal([65534, 65535, 0], Drain(ring).Select(static p => (int)p.SequenceNumber));
        Assert.Equal(1, stream.NextSequenceNumber);
    }

    /// <summary>Verifies the timestamp is the random offset plus the media time and wraps modulo 2^32.</summary>
    [Fact]
    public void Timestamp_WrapsAround()
    {
        var stream = new RtpStream(96, 90000, 1, 0, 0xFFFF_FF00);
        var packetizer = new H264RtpPacketizer(stream);

        Assert.Equal(0xFFFF_FF00u, stream.Timestamp(0));
        Assert.Equal(0x0000_0100u, stream.Timestamp(0x200));
        Assert.Equal(unchecked(0xFFFF_FF00u + 90_000u), packetizer.Timestamp(new MediaTimestamps(1000, 1000, 0)));
        Assert.Equal(unchecked(0xFFFF_FF00u + (uint)(100_000L * 90)), packetizer.Timestamp(new MediaTimestamps(0, 100_000, 0)));
        Assert.Equal(unchecked(0xFFFF_FF00u + (uint)(1040L * 90)), packetizer.Timestamp(new MediaTimestamps(1000, 1000, 40)));
    }

    /// <summary>Verifies new sessions draw random SSRCs, sequence numbers and timestamp offsets, and avoid an excluded SSRC.</summary>
    [Fact]
    public void RandomStreams_DrawRandomInitialState()
    {
        var streams = Enumerable.Range(0, 16).Select(static _ => new RtpStream(96, 90000)).ToList();

        Assert.True(streams.Select(static s => s.Ssrc).Distinct().Count() > 1);
        Assert.True(streams.Select(static s => s.InitialSequenceNumber).Distinct().Count() > 1);
        Assert.True(streams.Select(static s => s.TimestampOffset).Distinct().Count() > 1);
        Assert.All(streams, static s => Assert.Equal(s.InitialSequenceNumber, s.NextSequenceNumber));
        foreach (var stream in streams)
        {
            Assert.NotEqual(stream.Ssrc, new RtpStream(97, 48000, stream.Ssrc).Ssrc);
        }
    }

    /// <summary>Verifies the packet size limit is the MTU less the IP and UDP headers of the destination's family.</summary>
    [Fact]
    public void PacketSizeLimit_ExcludesIpAndUdpHeaders()
    {
        Assert.Equal(1472, RtpOutputSession.PacketSizeLimit(1500, AddressFamily.InterNetwork));
        Assert.Equal(1452, RtpOutputSession.PacketSizeLimit(1500, AddressFamily.InterNetworkV6));
        Assert.Equal(548, RtpOutputSession.PacketSizeLimit(576, AddressFamily.InterNetwork));
    }

    /// <summary>Verifies a NAL unit that fits alone, with no neighbour that fits with it, is a single NAL unit packet.</summary>
    [Fact]
    public void H264_LargeNalUnit_IsSingleNalUnitPacket()
    {
        var nal = RtpTestData.Nal(5, SmallSlot - 12);
        var packets = PacketizeH264(SmallSlot, nal);

        var packet = Assert.Single(packets);
        Assert.Equal(nal, packet.Payload);
        Assert.True(packet.Marker);
        Assert.Equal(SmallSlot, packet.Payload.Length + 12);
    }

    /// <summary>Verifies small NAL units are aggregated into a STAP-A whose F and NRI are those of its units.</summary>
    [Fact]
    public void H264_SmallNalUnits_AreAggregatedIntoStapA()
    {
        byte[] sei = [0x06, 0x05, 0x01, 0x80];
        var slice = RtpTestData.Nal(5, 60);
        var packets = PacketizeH264(1472, RtpTestData.Sps, RtpTestData.Pps, sei, slice);

        var packet = Assert.Single(packets);
        Assert.Equal(H264RtpPacketizer.StapA, packet.Payload[0] & 0x1F);
        Assert.Equal(0x60, packet.Payload[0] & 0x60);
        Assert.Equal(0, packet.Payload[0] & 0x80);
        Assert.True(packet.Marker);
        Assert.Equal([RtpTestData.Sps, RtpTestData.Pps, sei, slice], H264Depacketizer.NalUnits(packets));
        Assert.Equal(1 + (4 * 2) + RtpTestData.Sps.Length + RtpTestData.Pps.Length + sei.Length + slice.Length, packet.Payload.Length);
    }

    /// <summary>Verifies a NAL unit larger than the payload limit becomes FU-A fragments with S and E bits, all within the limit.</summary>
    [Fact]
    public void H264_OversizedNalUnit_IsFragmentedAsFuA()
    {
        var nal = RtpTestData.Nal(5, 1000);
        var packets = PacketizeH264(SmallSlot, nal);

        Assert.Equal((int)Math.Ceiling(999.0 / (SmallSlot - 14)), packets.Count);
        Assert.All(packets, static p => Assert.True(p.Payload.Length + 12 <= SmallSlot));
        Assert.All(packets, static p => Assert.Equal(0x60 | H264RtpPacketizer.FuA, p.Payload[0]));
        Assert.Equal(0x80 | 5, packets[0].Payload[1]);
        Assert.Equal(0x40 | 5, packets[^1].Payload[1]);
        Assert.All(packets.Skip(1).SkipLast(1), static p => Assert.Equal(5, p.Payload[1]));
        Assert.Equal([.. Enumerable.Repeat(false, packets.Count - 1), true], packets.Select(static p => p.Marker));
        Assert.Single(packets.Select(static p => p.Timestamp).Distinct());
        Assert.Equal(nal, Assert.Single(H264Depacketizer.NalUnits(packets)));
    }

    /// <summary>Verifies a mix of unit sizes keeps NAL unit order and uses each packet type where it applies.</summary>
    [Fact]
    public void H264_MixedAccessUnit_KeepsOrder()
    {
        var aud = new byte[] { 0x09, 0xF0 };
        var big = RtpTestData.Nal(5, 700, seed: 1);
        var tail1 = RtpTestData.Nal(5, 40, seed: 2);
        var tail2 = RtpTestData.Nal(5, 40, seed: 3);
        var middle = RtpTestData.Nal(5, SmallSlot - 20, seed: 4);

        var packets = PacketizeH264(SmallSlot, aud, big, middle, tail1, tail2);

        Assert.Equal([aud, big, middle, tail1, tail2], H264Depacketizer.NalUnits(packets));
        Assert.Equal(aud, packets[0].Payload);
        Assert.Equal(H264RtpPacketizer.FuA, packets[1].Payload[0] & 0x1F);
        Assert.Equal(H264RtpPacketizer.StapA, packets[^1].Payload[0] & 0x1F);
        Assert.All(packets, static p => Assert.True(p.Payload.Length + 12 <= SmallSlot));
        Assert.Equal(packets.Count, packets.Select(static p => p.SequenceNumber).Distinct().Count());
        Assert.True(Assert.Single(packets, static p => p.Marker) == packets[^1]);
    }

    /// <summary>Verifies an access unit that does not fit in the ring queues nothing and leaves the sequence number unchanged.</summary>
    [Fact]
    public void H264_QueueFull_QueuesNothing()
    {
        var stream = new RtpStream(96, 90000, 1, 100, 0);
        var ring = new RtpPacketRing(4, SmallSlot);
        var packetizer = new H264RtpPacketizer(stream);
        packetizer.SetNalUnits([RtpTestData.Nal(5, 2000)]);

        Assert.Equal(RtpPacketizeResult.QueueFull, packetizer.Packetize(ring, 0, 0, out var packets, out _));

        Assert.Equal(0, packets);
        Assert.Equal(0, ring.Count);
        Assert.Equal(100, stream.NextSequenceNumber);
        packetizer.SetNalUnits([RtpTestData.Nal(1, 50)]);
        Assert.Equal(RtpPacketizeResult.Queued, packetizer.Packetize(ring, 0, 0, out _, out _));
        Assert.Equal(100, Assert.Single(Drain(ring)).SequenceNumber);
    }

    /// <summary>
    /// Verifies the NAL units sent for real pipeline samples: the configured SPS and PPS go in band before an IDR
    /// access unit that lacks them (after its access unit delimiter), not before other access units, and NAL unit
    /// types reserved for RTP packet types are not sent.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task H264_Collect_InsertsParameterSetsBeforeIdr()
    {
        using var media = new MediaHarness();
        media.Downstream.RetainSamples = true;
        var session = await media.StartAsync();
        await media.ConfigureAsync(session);
        byte[] aud = [0x09, 0xF0];
        var idr = RtpTestData.Nal(5, 30);
        var slice = RtpTestData.Nal(1, 30);
        byte[] reserved = [0x1E, 0x01];
        await media.VideoAsync(session, 0, FlvPackets.AvcNalu(true, 0, 4, aud, idr));
        await media.VideoAsync(session, 40, FlvPackets.AvcNalu(false, 0, 4, slice, reserved));
        await media.VideoAsync(session, 80, FlvPackets.AvcNalu(true, 0, 4, RtpTestData.Sps, RtpTestData.Pps, idr));
        var samples = media.Downstream.Retained.OfType<EncodedVideoSample>().ToList();
        Assert.Equal(3, samples.Count);

        try
        {
            Assert.Equal([aud, RtpTestData.Sps, RtpTestData.Pps, idr], Sent(samples[0]));
            Assert.Equal([slice], Sent(samples[1]));
            Assert.Equal([RtpTestData.Sps, RtpTestData.Pps, idr], Sent(samples[2]));
        }
        finally
        {
            samples.ForEach(static s => s.Dispose());
        }

        static List<byte[]> Sent(EncodedVideoSample sample)
        {
            var ring = new RtpPacketRing(64, 1472);
            new H264RtpPacketizer(new RtpStream(96, 90000, 1, 0, 0)).Packetize(ring, 0, sample, out _, out _);
            return H264Depacketizer.NalUnits(Drain(ring));
        }
    }

    /// <summary>Verifies an AAC access unit that fits has the AU-headers-length of 16 bits and one AU header with its size and index 0.</summary>
    [Fact]
    public void Aac_AccessUnit_HasAuHeader()
    {
        var stream = new RtpStream(97, 44100, 7, 10, 0);
        var ring = new RtpPacketRing(16, 1472);
        var accessUnit = RtpTestData.Nal(0x21, 371);

        Assert.Equal(RtpPacketizeResult.Queued, new AacRtpPacketizer(stream, 44100, 1024).Packetize(ring, 1, accessUnit, 5000, out _, out _));

        var packet = Assert.Single(Drain(ring));
        Assert.Equal((true, (byte)97, 5000u), (packet.Marker, packet.PayloadType, packet.Timestamp));
        Assert.Equal(16, BinaryPrimitives.ReadUInt16BigEndian(packet.Payload));
        var header = BinaryPrimitives.ReadUInt16BigEndian(packet.Payload.AsSpan(2));
        Assert.Equal((371, 0), (header >> 3, header & 0x7));
        Assert.Equal(accessUnit, packet.Payload[4..]);
    }

    /// <summary>Verifies an AAC access unit larger than one payload is fragmented, each fragment with the whole AU size, the marker on the last.</summary>
    [Fact]
    public void Aac_LargeAccessUnit_IsFragmented()
    {
        var stream = new RtpStream(97, 48000, 7, 65535, 0);
        var ring = new RtpPacketRing(16, SmallSlot);
        var accessUnit = RtpTestData.Nal(0x21, 1000);

        new AacRtpPacketizer(stream, 48000, 1024).Packetize(ring, 1, accessUnit, 77, out var count, out _);

        var packets = Drain(ring);
        Assert.Equal((int)Math.Ceiling(1000.0 / (SmallSlot - 16)), count);
        Assert.All(packets, static p => Assert.Equal(1000, BinaryPrimitives.ReadUInt16BigEndian(p.Payload.AsSpan(2)) >> 3));
        Assert.All(packets, static p => Assert.True(p.Payload.Length + 12 <= SmallSlot));
        Assert.All(packets, static p => Assert.Equal(77u, p.Timestamp));
        Assert.Equal([.. Enumerable.Repeat(false, count - 1), true], packets.Select(static p => p.Marker));
        Assert.Equal(65535, packets[0].SequenceNumber);
        Assert.Equal(0, packets[1].SequenceNumber);
        Assert.Equal(accessUnit, packets.SelectMany(static p => p.Payload[4..]).ToArray());
    }

    /// <summary>Verifies access units above the 13-bit AU-size limit, and empty ones, are not sent.</summary>
    [Fact]
    public void Aac_UnrepresentableAccessUnits_AreNotSent()
    {
        var stream = new RtpStream(97, 48000, 7, 5, 0);
        var ring = new RtpPacketRing(64, 1472);
        var packetizer = new AacRtpPacketizer(stream, 48000, 1024);

        Assert.Equal(RtpPacketizeResult.TooLarge, packetizer.Packetize(ring, 1, new byte[8192], 0, out _, out _));
        Assert.Equal(RtpPacketizeResult.Empty, packetizer.Packetize(ring, 1, [], 0, out _, out _));
        Assert.Equal(RtpPacketizeResult.Queued, packetizer.Packetize(ring, 1, new byte[8191], 0, out _, out _));

        Assert.Equal(5, Drain(ring)[0].SequenceNumber);
    }

    /// <summary>
    /// Verifies AAC timestamps advance exactly one frame per access unit while millisecond source timestamps agree
    /// within half a frame, and follow the source across a gap or a regression.
    /// </summary>
    [Fact]
    public void Aac_Timestamps_CountSamples()
    {
        var packetizer = new AacRtpPacketizer(new RtpStream(97, 44100, 1, 0, 0), 44100, 1024);
        long[] milliseconds = [0, 23, 46, 70, 93, 116, 2000, 2023, 500];

        var timestamps = milliseconds.Select(ms => packetizer.Timestamp(new MediaTimestamps((uint)ms, ms, 0))).ToList();

        Assert.Equal([0u, 1024u, 2048u, 3072u, 4096u, 5120u, 88200u, 89224u, 22050u], timestamps);
        Assert.Equal(1920, new AacRtpPacketizer(new RtpStream(97, 90000, 1, 0, 0), 48000, 1024).FrameTicks);
        Assert.Equal(960, new AacRtpPacketizer(new RtpStream(97, 48000, 1, 0, 0), 48000, 960).FrameTicks);
    }

    /// <summary>Verifies the RTCP compound: SR with the NTP time, RTP timestamp and counts, SDES with the CNAME, and BYE on goodbye.</summary>
    [Fact]
    public void Rtcp_Compound_HasSenderReportSdesAndBye()
    {
        var buffer = new byte[RtcpPackets.MaxCompoundLength("abc@rtsp-ingest")];
        var wallClock = DateTimeOffset.UnixEpoch.AddSeconds(10.5);

        var length = RtcpPackets.WriteCompound(buffer, 0xCAFEBABE, wallClock, 123456, 10, 9000, "abc@rtsp-ingest", goodbye: true);

        var packet = buffer.AsSpan(0, length);
        Assert.Equal(0, length % 4);
        Assert.Equal((0x80, RtcpPackets.SenderReport, 6), (packet[0], packet[1], BinaryPrimitives.ReadUInt16BigEndian(packet[2..])));
        Assert.Equal(0xCAFEBABEu, BinaryPrimitives.ReadUInt32BigEndian(packet[4..]));
        Assert.Equal(2_208_988_810u, BinaryPrimitives.ReadUInt32BigEndian(packet[8..]));
        Assert.Equal(0x8000_0000u, BinaryPrimitives.ReadUInt32BigEndian(packet[12..]));
        Assert.Equal((123456u, 10u, 9000u), (BinaryPrimitives.ReadUInt32BigEndian(packet[16..]), BinaryPrimitives.ReadUInt32BigEndian(packet[20..]), BinaryPrimitives.ReadUInt32BigEndian(packet[24..])));
        var sdes = packet[28..];
        var sdesLength = (BinaryPrimitives.ReadUInt16BigEndian(sdes[2..]) + 1) * 4;
        Assert.Equal((0x81, RtcpPackets.SourceDescription), (sdes[0], sdes[1]));
        Assert.Equal(0xCAFEBABEu, BinaryPrimitives.ReadUInt32BigEndian(sdes[4..]));
        Assert.Equal((1, 15), (sdes[8], sdes[9]));
        Assert.Equal("abc@rtsp-ingest"u8.ToArray(), sdes.Slice(10, 15).ToArray());
        Assert.Equal(0, sdes[25]);
        var bye = packet[(28 + sdesLength)..];
        Assert.Equal(8, bye.Length);
        Assert.Equal((0x81, RtcpPackets.Goodbye, 1), (bye[0], bye[1], BinaryPrimitives.ReadUInt16BigEndian(bye[2..])));
        Assert.Equal(28 + sdesLength, RtcpPackets.WriteCompound(buffer, 1, wallClock, 0, 0, 0, "abc@rtsp-ingest", goodbye: false));
    }

    /// <summary>Verifies steady-state packetisation and draining (H.264 STAP-A and FU-A, AAC) allocate nothing.</summary>
    [Fact]
    public void Packetization_SteadyState_DoesNotAllocate()
    {
        var ring = new RtpPacketRing(256, 1472);
        var video = new H264RtpPacketizer(new RtpStream(96, 90000, 1, 0, 0));
        var audio = new AacRtpPacketizer(new RtpStream(97, 48000, 2, 0, 0), 48000, 1024);
        video.SetNalUnits([RtpTestData.Nal(6, 20), RtpTestData.Nal(5, 30_000), RtpTestData.Nal(1, 400), RtpTestData.Nal(1, 300)]);
        var accessUnit = new byte[600];
        Assert.Equal(20, Run(10));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var queued = Run(1000);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(2000, queued);
        Assert.Equal(0, allocated);

        int Run(int iterations)
        {
            var count = 0;
            for (var i = 0; i < iterations; i++)
            {
                count += video.Packetize(ring, 0, (uint)i, out _, out _) == RtpPacketizeResult.Queued ? 1 : 0;
                count += audio.Packetize(ring, 1, accessUnit, (uint)i, out _, out _) == RtpPacketizeResult.Queued ? 1 : 0;
                while (ring.TryPeek(out _, out _))
                {
                    ring.Advance();
                }
            }

            return count;
        }
    }

    /// <summary>Packetises NAL units as one access unit.</summary>
    /// <param name="slotSize">The packet size limit.</param>
    /// <param name="nals">The NAL units.</param>
    /// <returns>The packets.</returns>
    private static List<RtpPacket> PacketizeH264(int slotSize, params byte[][] nals)
    {
        var ring = new RtpPacketRing(256, slotSize);
        var packetizer = new H264RtpPacketizer(new RtpStream(96, 90000, 1, 0, 0));
        packetizer.SetNalUnits(nals.Select(static n => new ReadOnlyMemory<byte>(n)));
        Assert.Equal(RtpPacketizeResult.Queued, packetizer.Packetize(ring, 0, 1234, out var count, out var bytes));
        var packets = Drain(ring);
        Assert.Equal(count, packets.Count);
        Assert.Equal(bytes, packets.Sum(static p => p.Payload.Length + 12));
        return packets;
    }

    /// <summary>Removes and parses every published packet.</summary>
    /// <param name="ring">The ring.</param>
    /// <returns>The packets.</returns>
    private static List<RtpPacket> Drain(RtpPacketRing ring)
    {
        var packets = new List<RtpPacket>();
        while (ring.TryPeek(out var packet, out _))
        {
            packets.Add(RtpPacket.Parse(packet.Span));
            ring.Advance();
        }

        return packets;
    }

    /// <summary>Compares packets by value, including their payload bytes.</summary>
    private sealed class RtpPacketComparer : IEqualityComparer<RtpPacket>
    {
        /// <summary>The instance.</summary>
        public static readonly RtpPacketComparer Instance = new();

        /// <inheritdoc />
        public bool Equals(RtpPacket? x, RtpPacket? y) =>
            x is not null && y is not null && (x with { Payload = [] }) == (y with { Payload = [] }) && x.Payload.AsSpan().SequenceEqual(y.Payload);

        /// <inheritdoc />
        public int GetHashCode(RtpPacket obj) => obj.SequenceNumber;
    }
}
