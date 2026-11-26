// RFC 3640 packetisation of raw AAC access units in the mpeg4-generic AAC-hbr mode: each packet carries an
// AU-headers-length field and one 16-bit AU header (13-bit AU-size, 3-bit AU-Index of 0), followed by the access unit
// or, when it exceeds the payload limit, one fragment of it. RTP timestamps count samples at the RTP clock rate and
// continue frame by frame while the source timestamps agree, so millisecond rounding never accumulates.

using RTSP.IngestService.Media;

namespace RTSP.IngestService.Rtp;

/// <summary>Packetises AAC access units (RFC 3640, mode AAC-hbr).</summary>
/// <remarks>
/// <para>
/// One access unit per packet: the pipeline delivers access units one at a time, and aggregating several would delay
/// every one but the last. An access unit larger than one packet's payload is fragmented (RFC 3640, section 3.2.3):
/// every fragment repeats the AU header with the size of the whole AU, and only the packet with the last fragment has
/// the marker bit set. AAC-hbr limits an AU to 8191 bytes (13 bits); larger ones are not sent.
/// </para>
/// <para>Not thread-safe; used by one publish's pipeline thread.</para>
/// </remarks>
internal sealed class AacRtpPacketizer
{
    /// <summary>Largest AU-size representable with sizeLength=13.</summary>
    public const int MaxAccessUnitSize = (1 << 13) - 1;

    /// <summary>Bytes of AU-headers-length plus one AU header.</summary>
    public const int AuHeaderSectionLength = 4;

    /// <summary>The samples per access unit at the AAC sampling rate.</summary>
    private readonly int _frameLength;

    /// <summary>The AAC sampling rate.</summary>
    private readonly int _sampleRate;

    /// <summary>The media time, in clock ticks, at which the next access unit is expected to start.</summary>
    private long? _next;

    /// <summary>Initializes a new instance of the <see cref="AacRtpPacketizer"/> class.</summary>
    /// <param name="stream">The RTP session state; its clock rate is the RTP clock rate.</param>
    /// <param name="sampleRate">The AAC sampling rate.</param>
    /// <param name="frameLength">The samples per access unit (1024 or 960 for AAC-LC).</param>
    public AacRtpPacketizer(RtpStream stream, int sampleRate, int frameLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameLength);
        Stream = stream;
        _sampleRate = sampleRate;
        _frameLength = frameLength;
    }

    /// <summary>Gets the RTP session state.</summary>
    public RtpStream Stream { get; }

    /// <summary>Gets the duration of one access unit in RTP clock ticks.</summary>
    public long FrameTicks => (long)_frameLength * Stream.ClockRate / _sampleRate;

    /// <summary>
    /// Returns the RTP timestamp of the next access unit: one frame after the previous one when the source timestamp
    /// agrees within half a frame, otherwise (the first AU, a gap or a regression) the source timestamp itself.
    /// </summary>
    /// <param name="timestamps">The sample timing (milliseconds).</param>
    /// <returns>The RTP timestamp.</returns>
    public uint Timestamp(MediaTimestamps timestamps)
    {
        var fromSource = timestamps.DecodeTimestamp * Stream.ClockRate / 1000;
        var ticks = _next is { } next && Math.Abs(fromSource - next) <= FrameTicks / 2 ? next : fromSource;
        _next = ticks + FrameTicks;
        return Stream.Timestamp(ticks);
    }

    /// <summary>Packetises one access unit into <paramref name="ring"/> and publishes it, or queues nothing.</summary>
    /// <param name="ring">The output's ring.</param>
    /// <param name="channel">The ring channel of this track.</param>
    /// <param name="sample">The access unit.</param>
    /// <param name="packets">The number of packets queued.</param>
    /// <param name="bytes">The number of bytes queued.</param>
    /// <returns>The outcome.</returns>
    public RtpPacketizeResult Packetize(RtpPacketRing ring, byte channel, EncodedAudioSample sample, out int packets, out int bytes) =>
        Packetize(ring, channel, sample.Data.Span, Timestamp(sample.Timestamps), out packets, out bytes);

    /// <summary>Packetises one access unit with a given timestamp.</summary>
    /// <param name="ring">The ring.</param>
    /// <param name="channel">The channel.</param>
    /// <param name="accessUnit">The raw AAC access unit.</param>
    /// <param name="timestamp">Its RTP timestamp.</param>
    /// <param name="packets">The number of packets queued.</param>
    /// <param name="bytes">The number of bytes queued.</param>
    /// <returns>The outcome.</returns>
    internal RtpPacketizeResult Packetize(RtpPacketRing ring, byte channel, ReadOnlySpan<byte> accessUnit, uint timestamp, out int packets, out int bytes)
    {
        packets = 0;
        bytes = 0;
        if (accessUnit.IsEmpty)
        {
            return RtpPacketizeResult.Empty;
        }

        if (accessUnit.Length > MaxAccessUnitSize)
        {
            return RtpPacketizeResult.TooLarge;
        }

        var saved = Stream.NextSequenceNumber;
        var chunk = ring.SlotSize - RtpHeader.Length - AuHeaderSectionLength;
        var remaining = accessUnit;
        var count = 0;
        var total = 0;
        while (remaining.Length > 0)
        {
            if (!ring.TryWrite(count, out var slot))
            {
                Stream.Rewind(saved);
                return RtpPacketizeResult.QueueFull;
            }

            var take = Math.Min(chunk, remaining.Length);
            Stream.WriteHeader(slot, marker: take == remaining.Length, timestamp);
            var header = slot[RtpHeader.Length..];
            header[0] = 0x00;
            header[1] = 0x10;
            header[2] = (byte)(accessUnit.Length >> 5);
            header[3] = (byte)((accessUnit.Length << 3) & 0xF8);
            remaining[..take].CopyTo(header[AuHeaderSectionLength..]);
            var length = RtpHeader.Length + AuHeaderSectionLength + take;
            ring.Complete(count++, length, channel);
            total += length;
            remaining = remaining[take..];
        }

        ring.Publish(count);
        packets = count;
        bytes = total;
        return RtpPacketizeResult.Queued;
    }
}
