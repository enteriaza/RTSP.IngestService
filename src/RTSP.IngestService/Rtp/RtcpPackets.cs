// RTCP packets a sender emits (RFC 3550, section 6): a compound of a Sender Report with no reception report blocks and
// a Source Description with the CNAME, and on stop the same compound followed by BYE. The Sender Report pairs a wall
// clock (NTP) time with the RTP timestamp of the same instant, which is how receivers synchronise the audio and video
// RTP sessions of one output; both sessions use the same CNAME.

using System.Buffers.Binary;
using System.Text;

namespace RTSP.IngestService.Rtp;

/// <summary>Writes sender RTCP compound packets.</summary>
internal static class RtcpPackets
{
    /// <summary>Sender Report packet type.</summary>
    public const byte SenderReport = 200;

    /// <summary>Source Description packet type.</summary>
    public const byte SourceDescription = 202;

    /// <summary>BYE packet type.</summary>
    public const byte Goodbye = 203;

    /// <summary>Length of a Sender Report without report blocks.</summary>
    public const int SenderReportLength = 28;

    /// <summary>Longest CNAME accepted (an SDES item length is one byte).</summary>
    public const int MaxCanonicalNameLength = 255;

    /// <summary>Seconds from the NTP epoch (1900-01-01) to the Unix epoch.</summary>
    private const long NtpUnixOffsetSeconds = 2_208_988_800;

    /// <summary>Returns the largest compound this class writes for a CNAME.</summary>
    /// <param name="canonicalName">The CNAME.</param>
    /// <returns>The buffer size needed.</returns>
    public static int MaxCompoundLength(string canonicalName) => SenderReportLength + SourceDescriptionLength(canonicalName) + 8;

    /// <summary>Writes SR + SDES(CNAME), and BYE when <paramref name="goodbye"/>.</summary>
    /// <param name="buffer">The destination, at least <see cref="MaxCompoundLength"/> bytes.</param>
    /// <param name="ssrc">The sender's SSRC.</param>
    /// <param name="wallClock">The wall-clock time of the report.</param>
    /// <param name="rtpTimestamp">The RTP timestamp of the same instant.</param>
    /// <param name="packetCount">RTP data packets sent.</param>
    /// <param name="octetCount">RTP payload octets sent.</param>
    /// <param name="canonicalName">The CNAME, ASCII.</param>
    /// <param name="goodbye">Whether to append BYE.</param>
    /// <returns>The compound length.</returns>
    public static int WriteCompound(Span<byte> buffer, uint ssrc, DateTimeOffset wallClock, uint rtpTimestamp, long packetCount, long octetCount, string canonicalName, bool goodbye)
    {
        var (seconds, fraction) = ToNtp(wallClock);
        buffer[0] = 0x80;
        buffer[1] = SenderReport;
        BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], (SenderReportLength / 4) - 1);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[4..], ssrc);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[8..], seconds);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[12..], fraction);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[16..], rtpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[20..], unchecked((uint)packetCount));
        BinaryPrimitives.WriteUInt32BigEndian(buffer[24..], unchecked((uint)octetCount));
        var position = SenderReportLength;

        var sdes = SourceDescriptionLength(canonicalName);
        var chunk = buffer.Slice(position, sdes);
        chunk.Clear();
        chunk[0] = 0x81;
        chunk[1] = SourceDescription;
        BinaryPrimitives.WriteUInt16BigEndian(chunk[2..], (ushort)((sdes / 4) - 1));
        BinaryPrimitives.WriteUInt32BigEndian(chunk[4..], ssrc);
        chunk[8] = 1;
        chunk[9] = (byte)Encoding.ASCII.GetBytes(canonicalName, chunk[10..]);
        position += sdes;

        if (goodbye)
        {
            buffer[position] = 0x81;
            buffer[position + 1] = Goodbye;
            BinaryPrimitives.WriteUInt16BigEndian(buffer[(position + 2)..], 1);
            BinaryPrimitives.WriteUInt32BigEndian(buffer[(position + 4)..], ssrc);
            position += 8;
        }

        return position;
    }

    /// <summary>Converts a time to the 64-bit NTP format.</summary>
    /// <param name="time">The time.</param>
    /// <returns>Seconds since 1900 and the binary fraction of a second.</returns>
    public static (uint Seconds, uint Fraction) ToNtp(DateTimeOffset time)
    {
        var sinceUnix = time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var seconds = Math.DivRem(sinceUnix, TimeSpan.TicksPerSecond, out var remainder);
        return (unchecked((uint)(seconds + NtpUnixOffsetSeconds)), (uint)((ulong)remainder * (1UL << 32) / TimeSpan.TicksPerSecond));
    }

    /// <summary>Returns the padded length of an SDES packet with one chunk holding one CNAME item.</summary>
    /// <param name="canonicalName">The CNAME.</param>
    /// <returns>The length, a multiple of four.</returns>
    private static int SourceDescriptionLength(string canonicalName)
    {
        // Header, SSRC, item type and length, text, and at least one terminating null octet.
        var unpadded = 4 + 4 + 2 + canonicalName.Length + 1;
        return (unpadded + 3) & ~3;
    }
}
