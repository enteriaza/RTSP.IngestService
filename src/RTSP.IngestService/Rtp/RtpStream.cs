// RTP data-packet framing (RFC 3550, section 5.1) and the per-track sender state of one RTP session: a random SSRC,
// a random initial sequence number and timestamp offset (section 5.1 recommends both be random), and unchecked 16- and
// 32-bit arithmetic so that sequence numbers and timestamps wrap exactly as the RFC requires.

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RTSP.IngestService.Rtp;

/// <summary>Writes and reads the fixed 12-byte RTP header.</summary>
internal static class RtpHeader
{
    /// <summary>Length of the fixed header without CSRCs or extensions.</summary>
    public const int Length = 12;

    /// <summary>RTP version 2 with no padding, extension or CSRCs.</summary>
    private const byte Version2 = 0x80;

    /// <summary>Writes the fixed header.</summary>
    /// <param name="packet">The packet; at least <see cref="Length"/> bytes.</param>
    /// <param name="marker">The marker bit.</param>
    /// <param name="payloadType">The 7-bit payload type.</param>
    /// <param name="sequenceNumber">The sequence number.</param>
    /// <param name="timestamp">The RTP timestamp.</param>
    /// <param name="ssrc">The synchronisation source.</param>
    public static void Write(Span<byte> packet, bool marker, byte payloadType, ushort sequenceNumber, uint timestamp, uint ssrc)
    {
        packet[0] = Version2;
        packet[1] = (byte)((marker ? 0x80 : 0) | (payloadType & 0x7F));
        BinaryPrimitives.WriteUInt16BigEndian(packet[2..], sequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(packet[4..], timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet[8..], ssrc);
    }

    /// <summary>Sets the marker bit of an already written header.</summary>
    /// <param name="packet">The packet.</param>
    public static void SetMarker(Span<byte> packet) => packet[1] |= 0x80;
}

/// <summary>
/// The sender state of one RTP session (one track of one output): SSRC, payload type, clock rate, the next sequence
/// number and the random timestamp offset.
/// </summary>
/// <remarks>
/// Written only by the media pipeline thread of the owning publish (the producer). <see cref="PacketCount"/> and
/// <see cref="OctetCount"/> are written only by the sender task, which also reads the immutable members.
/// </remarks>
internal sealed class RtpStream
{
    /// <summary>The next sequence number.</summary>
    private ushort _sequenceNumber;

    /// <summary>Initializes a new instance of the <see cref="RtpStream"/> class with random SSRC, sequence and offset.</summary>
    /// <param name="payloadType">The payload type (96–127).</param>
    /// <param name="clockRate">The RTP clock rate in hertz.</param>
    /// <param name="excludedSsrc">An SSRC this stream must not use (the other track's), or <see langword="null"/>.</param>
    public RtpStream(byte payloadType, int clockRate, uint? excludedSsrc = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clockRate);
        PayloadType = payloadType;
        ClockRate = clockRate;
        Span<byte> random = stackalloc byte[10];
        do
        {
            RandomNumberGenerator.Fill(random);
            Ssrc = BinaryPrimitives.ReadUInt32BigEndian(random);
        }
        while (Ssrc == 0 || Ssrc == excludedSsrc);

        _sequenceNumber = BinaryPrimitives.ReadUInt16BigEndian(random[4..]);
        TimestampOffset = BinaryPrimitives.ReadUInt32BigEndian(random[6..]);
        InitialSequenceNumber = _sequenceNumber;
    }

    /// <summary>Initializes a new instance of the <see cref="RtpStream"/> class with explicit state; for tests.</summary>
    /// <param name="payloadType">The payload type.</param>
    /// <param name="clockRate">The clock rate.</param>
    /// <param name="ssrc">The SSRC.</param>
    /// <param name="sequenceNumber">The first sequence number.</param>
    /// <param name="timestampOffset">The timestamp offset.</param>
    internal RtpStream(byte payloadType, int clockRate, uint ssrc, ushort sequenceNumber, uint timestampOffset)
    {
        PayloadType = payloadType;
        ClockRate = clockRate;
        Ssrc = ssrc;
        _sequenceNumber = sequenceNumber;
        InitialSequenceNumber = sequenceNumber;
        TimestampOffset = timestampOffset;
    }

    /// <summary>Gets the payload type.</summary>
    public byte PayloadType { get; }

    /// <summary>Gets the RTP clock rate in hertz.</summary>
    public int ClockRate { get; }

    /// <summary>Gets the synchronisation source identifier.</summary>
    public uint Ssrc { get; }

    /// <summary>Gets the random offset added to every media timestamp.</summary>
    public uint TimestampOffset { get; }

    /// <summary>Gets the first sequence number.</summary>
    public ushort InitialSequenceNumber { get; }

    /// <summary>Gets the sequence number the next packet will use.</summary>
    public ushort NextSequenceNumber => _sequenceNumber;

    /// <summary>Gets or sets the RTP packets sent; written by the sender only.</summary>
    public long PacketCount { get; set; }

    /// <summary>Gets or sets the RTP payload octets sent (headers excluded, as RTCP requires); written by the sender only.</summary>
    public long OctetCount { get; set; }

    /// <summary>Converts a media time in clock ticks to an RTP timestamp, wrapping modulo 2^32.</summary>
    /// <param name="ticks">Media time in ticks of <see cref="ClockRate"/>; may be negative.</param>
    /// <returns>The RTP timestamp.</returns>
    public uint Timestamp(long ticks) => unchecked(TimestampOffset + (uint)ticks);

    /// <summary>Writes the header of the next packet and advances the sequence number, wrapping modulo 2^16.</summary>
    /// <param name="packet">The packet.</param>
    /// <param name="marker">The marker bit.</param>
    /// <param name="timestamp">The RTP timestamp.</param>
    public void WriteHeader(Span<byte> packet, bool marker, uint timestamp) =>
        RtpHeader.Write(packet, marker, PayloadType, _sequenceNumber++, timestamp, Ssrc);

    /// <summary>Restores the sequence number saved before an access unit that was not sent.</summary>
    /// <param name="sequenceNumber">The saved value of <see cref="NextSequenceNumber"/>.</param>
    public void Rewind(ushort sequenceNumber) => _sequenceNumber = sequenceNumber;
}
