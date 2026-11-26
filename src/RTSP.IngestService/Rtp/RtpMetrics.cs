// System.Diagnostics.Metrics instruments for RTP/UDP output, created from the host's IMeterFactory. Tags are fixed
// enumerations (track, mode, drop reason, error kind); output names, streams, addresses and SSRCs are never tags. Rates
// are observable gauges summed over the active sessions, which each compute their own rate once per second.

using System.Diagnostics.Metrics;

namespace RTSP.IngestService.Rtp;

/// <summary>Why RTP packets were not sent.</summary>
internal enum RtpDropReason
{
    /// <summary>The output's packet queue could not take the whole access unit.</summary>
    QueueFull,

    /// <summary>Video after a loss, before the next IDR access unit.</summary>
    AwaitingKeyframe,

    /// <summary>The access unit cannot be carried by the payload format.</summary>
    TooLarge,

    /// <summary>A send failed with a transient error.</summary>
    SendFailed,

    /// <summary>The output failed or stopped with packets still queued, or media arrived after it failed.</summary>
    OutputUnavailable,
}

/// <summary>RTP output metrics, published on the <see cref="MeterName"/> meter.</summary>
/// <remarks>Thread-safe.</remarks>
internal sealed class RtpMetrics
{
    /// <summary>Name of the meter.</summary>
    public const string MeterName = "RTSP.IngestService.Rtp";

    /// <summary>The live sessions whose rates are reported.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<RtpOutputStatistics, byte> _sessions = new();

    /// <summary>Active sessions.</summary>
    private readonly UpDownCounter<long> _active;

    /// <summary>Packets sent.</summary>
    private readonly Counter<long> _packets;

    /// <summary>Bytes sent.</summary>
    private readonly Counter<long> _bytes;

    /// <summary>Packets not sent.</summary>
    private readonly Counter<long> _dropped;

    /// <summary>Send errors.</summary>
    private readonly Counter<long> _errors;

    /// <summary>RTCP compounds sent.</summary>
    private readonly Counter<long> _rtcp;

    /// <summary>Initializes a new instance of the <see cref="RtpMetrics"/> class.</summary>
    /// <param name="meterFactory">Factory owning the meter's lifetime.</param>
    public RtpMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _active = meter.CreateUpDownCounter<long>("rtp.sessions.active", "{session}", "RTP output sessions sending a published stream; tag mode.");
        _packets = meter.CreateCounter<long>("rtp.packets.sent", "{packet}", "RTP packets sent; tags track and mode.");
        _bytes = meter.CreateCounter<long>("rtp.bytes.sent", "By", "RTP bytes sent (UDP payload, RTP header included); tags track and mode.");
        _dropped = meter.CreateCounter<long>("rtp.packets.dropped", "{packet}", "RTP packets not sent; tags track and reason.");
        _errors = meter.CreateCounter<long>("rtp.send.errors", "{error}", "Failed datagram sends; tag kind.");
        _rtcp = meter.CreateCounter<long>("rtp.rtcp.sent", "{packet}", "RTCP compound packets sent.");
        meter.CreateObservableGauge("rtp.bitrate", () => Sum(static s => s.Bitrate), "bit/s", "Current RTP send rate over all sessions.");
        meter.CreateObservableGauge("rtp.packet_rate", () => Sum(static s => s.PacketRate), "{packet}/s", "Current RTP packet rate over all sessions.");
    }

    /// <summary>Records a session that started sending; its rates are reported until <see cref="SessionEnded"/>.</summary>
    /// <param name="mode">The session's mode.</param>
    /// <param name="statistics">The session's statistics.</param>
    public void SessionStarted(Configuration.RtpOutputMode mode, RtpOutputStatistics statistics)
    {
        if (_sessions.TryAdd(statistics, 0))
        {
            _active.Add(1, Mode(mode));
        }
    }

    /// <summary>Records a session that stopped or failed.</summary>
    /// <param name="mode">The session's mode.</param>
    /// <param name="statistics">The session's statistics.</param>
    public void SessionEnded(Configuration.RtpOutputMode mode, RtpOutputStatistics statistics)
    {
        if (_sessions.TryRemove(statistics, out _))
        {
            _active.Add(-1, Mode(mode));
        }
    }

    /// <summary>Records sent packets.</summary>
    /// <param name="video">Whether they are video packets.</param>
    /// <param name="multicast">Whether the output is multicast.</param>
    /// <param name="packets">The packets.</param>
    /// <param name="bytes">Their bytes.</param>
    public void Sent(bool video, bool multicast, long packets, long bytes)
    {
        if (packets > 0)
        {
            var track = Track(video);
            var mode = new KeyValuePair<string, object?>("mode", multicast ? "multicast" : "unicast");
            _packets.Add(packets, track, mode);
            _bytes.Add(bytes, track, mode);
        }
    }

    /// <summary>Records packets that were not sent.</summary>
    /// <param name="video">Whether they are video packets.</param>
    /// <param name="reason">Why.</param>
    /// <param name="packets">The number of packets.</param>
    public void Dropped(bool video, RtpDropReason reason, long packets)
    {
        if (packets > 0)
        {
            _dropped.Add(packets, Track(video), new KeyValuePair<string, object?>("reason", ReasonTag(reason)));
        }
    }

    /// <summary>Records a failed send.</summary>
    /// <param name="transient">Whether the failure was transient.</param>
    public void SendError(bool transient) => _errors.Add(1, new KeyValuePair<string, object?>("kind", transient ? "transient" : "terminal"));

    /// <summary>Records an RTCP compound packet.</summary>
    public void RtcpSent() => _rtcp.Add(1);

    /// <summary>Returns the tag value of a drop reason.</summary>
    /// <param name="reason">The reason.</param>
    /// <returns>The tag value.</returns>
    internal static string ReasonTag(RtpDropReason reason) => reason switch
    {
        RtpDropReason.QueueFull => "queue_full",
        RtpDropReason.AwaitingKeyframe => "awaiting_keyframe",
        RtpDropReason.TooLarge => "too_large",
        RtpDropReason.SendFailed => "send_failed",
        _ => "output_unavailable",
    };

    /// <summary>Returns the track tag.</summary>
    /// <param name="video">Whether it is video.</param>
    /// <returns>The tag.</returns>
    private static KeyValuePair<string, object?> Track(bool video) => new("track", video ? "video" : "audio");

    /// <summary>Returns the mode tag.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns>The tag.</returns>
    private static KeyValuePair<string, object?> Mode(Configuration.RtpOutputMode mode) => new("mode", mode == Configuration.RtpOutputMode.Multicast ? "multicast" : "unicast");

    /// <summary>Sums a rate over the active sessions.</summary>
    /// <param name="rate">The rate of one session.</param>
    /// <returns>The sum.</returns>
    private double Sum(Func<RtpOutputStatistics, double> rate)
    {
        var sum = 0.0;
        foreach (var session in _sessions.Keys)
        {
            sum += rate(session);
        }

        return sum;
    }
}
