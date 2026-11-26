// RFC 6184 packetisation of H.264 access units in non-interleaved mode (packetization-mode=1): consecutive NAL units
// that fit together in one packet are aggregated into a STAP-A, a NAL unit that fits alone is sent as a single NAL
// unit packet, and a NAL unit larger than the payload limit is fragmented into FU-A packets. The marker bit is set on
// the last packet of the access unit. Packets are written straight into the output's packet ring; the NAL unit bytes
// are copied once, from the sample's pooled payload into the datagram.

using RTSP.IngestService.Media;

namespace RTSP.IngestService.Rtp;

/// <summary>Outcome of packetising one access unit.</summary>
internal enum RtpPacketizeResult
{
    /// <summary>All packets of the access unit were queued.</summary>
    Queued,

    /// <summary>The ring had too little space; nothing was queued and the sequence number is unchanged.</summary>
    QueueFull,

    /// <summary>The access unit cannot be carried by the payload format; nothing was queued.</summary>
    TooLarge,

    /// <summary>The access unit contained nothing to send.</summary>
    Empty,
}

/// <summary>Packetises H.264 access units (RFC 6184, packetization-mode=1).</summary>
/// <remarks>
/// <para>
/// Parameter sets: FLV carries SPS and PPS out of band, in the AVC sequence header. Before an access unit containing
/// an IDR slice, the configured SPS and PPS that the access unit does not itself contain are sent in band (after an
/// access unit delimiter, which stays first, and in the same
/// STAP-A as the following NAL units when they fit), so a receiver can start decoding at any IDR without the SDP's
/// <c>sprop-parameter-sets</c>.
/// </para>
/// <para>NAL unit types 0 and 24–31 (unspecified, and reserved for RTP packet types) are not sent.</para>
/// <para>Not thread-safe; used by one publish's pipeline thread.</para>
/// </remarks>
internal sealed class H264RtpPacketizer
{
    /// <summary>The H.264 RTP clock rate (RFC 6184, section 8.2.1).</summary>
    public const int ClockRate = 90000;

    /// <summary>STAP-A packet type.</summary>
    public const byte StapA = 24;

    /// <summary>FU-A packet type.</summary>
    public const byte FuA = 28;

    /// <summary>The NAL units of the access unit being packetised; reused, so steady-state packetisation does not allocate.</summary>
    private readonly List<ReadOnlyMemory<byte>> _nals = new(16);

    /// <summary>Initializes a new instance of the <see cref="H264RtpPacketizer"/> class.</summary>
    /// <param name="stream">The RTP session state.</param>
    public H264RtpPacketizer(RtpStream stream)
    {
        Stream = stream;
    }

    /// <summary>Gets the RTP session state.</summary>
    public RtpStream Stream { get; }

    /// <summary>Returns the RTP timestamp of an access unit: its presentation time at 90 kHz.</summary>
    /// <param name="timestamps">The sample timing (milliseconds).</param>
    /// <returns>The RTP timestamp.</returns>
    public uint Timestamp(MediaTimestamps timestamps) => Stream.Timestamp(timestamps.PresentationTimestamp * (ClockRate / 1000));

    /// <summary>Packetises one access unit into <paramref name="ring"/> and publishes it, or queues nothing.</summary>
    /// <param name="ring">The output's ring.</param>
    /// <param name="channel">The ring channel of this track.</param>
    /// <param name="sample">The access unit.</param>
    /// <param name="packets">The number of packets queued.</param>
    /// <param name="bytes">The number of bytes queued.</param>
    /// <returns>The outcome.</returns>
    public RtpPacketizeResult Packetize(RtpPacketRing ring, byte channel, EncodedVideoSample sample, out int packets, out int bytes)
    {
        Collect(sample);
        return Packetize(ring, channel, Timestamp(sample.Timestamps), out packets, out bytes);
    }

    /// <summary>Packetises the NAL units in <see cref="_nals"/>.</summary>
    /// <param name="ring">The ring.</param>
    /// <param name="channel">The channel.</param>
    /// <param name="timestamp">The access unit's RTP timestamp.</param>
    /// <param name="packets">The number of packets queued.</param>
    /// <param name="bytes">The number of bytes queued.</param>
    /// <returns>The outcome.</returns>
    internal RtpPacketizeResult Packetize(RtpPacketRing ring, byte channel, uint timestamp, out int packets, out int bytes)
    {
        packets = 0;
        bytes = 0;
        if (_nals.Count == 0)
        {
            return RtpPacketizeResult.Empty;
        }

        var saved = Stream.NextSequenceNumber;
        var maxPayload = ring.SlotSize - RtpHeader.Length;
        var count = 0;
        var total = 0;
        var i = 0;
        while (i < _nals.Count)
        {
            var nal = _nals[i].Span;
            if (nal.Length > maxPayload)
            {
                if (!WriteFragments(ring, channel, timestamp, nal, maxPayload, ref count, ref total))
                {
                    return Abandon(saved);
                }

                i++;
                continue;
            }

            var end = i + 1;
            var aggregate = 1 + 2 + nal.Length;
            while (end < _nals.Count && aggregate + 2 + _nals[end].Length <= maxPayload)
            {
                aggregate += 2 + _nals[end].Length;
                end++;
            }

            if (!ring.TryWrite(count, out var slot))
            {
                return Abandon(saved);
            }

            Stream.WriteHeader(slot, marker: false, timestamp);
            int length;
            if (end - i == 1)
            {
                nal.CopyTo(slot[RtpHeader.Length..]);
                length = RtpHeader.Length + nal.Length;
            }
            else
            {
                var forbidden = 0;
                var nri = 0;
                var position = RtpHeader.Length + 1;
                for (var k = i; k < end; k++)
                {
                    var unit = _nals[k].Span;
                    forbidden |= unit[0] & 0x80;
                    nri = Math.Max(nri, unit[0] & 0x60);
                    slot[position] = (byte)(unit.Length >> 8);
                    slot[position + 1] = (byte)unit.Length;
                    unit.CopyTo(slot[(position + 2)..]);
                    position += 2 + unit.Length;
                }

                slot[RtpHeader.Length] = (byte)(forbidden | nri | StapA);
                length = position;
            }

            ring.Complete(count++, length, channel);
            total += length;
            i = end;
        }

        ring.TryWrite(count - 1, out var last);
        RtpHeader.SetMarker(last);
        ring.Publish(count);
        packets = count;
        bytes = total;
        return RtpPacketizeResult.Queued;
    }

    /// <summary>Gathers the NAL units to send for <paramref name="sample"/> into <see cref="_nals"/>.</summary>
    /// <param name="sample">The access unit.</param>
    internal void Collect(EncodedVideoSample sample)
    {
        _nals.Clear();
        var units = sample.NalUnits;
        var first = 0;
        if (units.Count > 0 && units[0].Type == 9)
        {
            _nals.Add(sample.GetNalUnitData(0));
            first = 1;
        }

        if (sample.ContainsIdr)
        {
            var hasSps = false;
            var hasPps = false;
            for (var i = 0; i < units.Count; i++)
            {
                hasSps |= units[i].Type == 7;
                hasPps |= units[i].Type == 8;
            }

            var configuration = sample.Track.Configuration;
            if (!hasSps)
            {
                for (var i = 0; i < configuration.SequenceParameterSets.Count; i++)
                {
                    _nals.Add(configuration.SequenceParameterSets[i]);
                }
            }

            if (!hasPps)
            {
                for (var i = 0; i < configuration.PictureParameterSets.Count; i++)
                {
                    _nals.Add(configuration.PictureParameterSets[i]);
                }
            }
        }

        for (var i = first; i < units.Count; i++)
        {
            if (units[i].Type is not (0 or >= 24))
            {
                _nals.Add(sample.GetNalUnitData(i));
            }
        }
    }

    /// <summary>Replaces the collected NAL units; for tests.</summary>
    /// <param name="nals">The NAL units.</param>
    internal void SetNalUnits(IEnumerable<ReadOnlyMemory<byte>> nals)
    {
        _nals.Clear();
        _nals.AddRange(nals);
    }

    /// <summary>Writes one NAL unit as FU-A fragments.</summary>
    /// <param name="ring">The ring.</param>
    /// <param name="channel">The channel.</param>
    /// <param name="timestamp">The RTP timestamp.</param>
    /// <param name="nal">The NAL unit, longer than <paramref name="maxPayload"/>.</param>
    /// <param name="maxPayload">The largest RTP payload.</param>
    /// <param name="count">The packets written in the batch so far.</param>
    /// <param name="total">The bytes written in the batch so far.</param>
    /// <returns><see langword="false"/> if the ring ran out of space.</returns>
    private bool WriteFragments(RtpPacketRing ring, byte channel, uint timestamp, ReadOnlySpan<byte> nal, int maxPayload, ref int count, ref int total)
    {
        var indicator = (byte)((nal[0] & 0xE0) | FuA);
        var type = (byte)(nal[0] & 0x1F);
        var data = nal[1..];
        var chunk = maxPayload - 2;
        var first = true;
        while (data.Length > 0)
        {
            if (!ring.TryWrite(count, out var slot))
            {
                return false;
            }

            var take = Math.Min(chunk, data.Length);
            var last = take == data.Length;
            Stream.WriteHeader(slot, marker: false, timestamp);
            slot[RtpHeader.Length] = indicator;
            slot[RtpHeader.Length + 1] = (byte)((first ? 0x80 : 0) | (last ? 0x40 : 0) | type);
            data[..take].CopyTo(slot[(RtpHeader.Length + 2)..]);
            var length = RtpHeader.Length + 2 + take;
            ring.Complete(count++, length, channel);
            total += length;
            data = data[take..];
            first = false;
        }

        return true;
    }

    /// <summary>Discards a partly written access unit.</summary>
    /// <param name="sequenceNumber">The sequence number before it.</param>
    /// <returns><see cref="RtpPacketizeResult.QueueFull"/>.</returns>
    private RtpPacketizeResult Abandon(ushort sequenceNumber)
    {
        Stream.Rewind(sequenceNumber);
        return RtpPacketizeResult.QueueFull;
    }
}
