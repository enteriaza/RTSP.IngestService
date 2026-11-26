// One RTP output for one publication: the video and audio RTP sessions sent to one destination, with their packet
// ring, UDP socket and sender task. The media pipeline thread packetises samples into the ring and returns at once; the
// sender task alone touches the socket, so a slow or failing destination can only lose its own packets. Access units
// are queued whole or dropped whole, and after a lost video access unit video resumes at the next IDR.

using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;

namespace RTSP.IngestService.Rtp;

/// <summary>The lifecycle state of an <see cref="RtpOutputSession"/>.</summary>
public enum RtpOutputSessionState
{
    /// <summary>Created; the socket is not open yet.</summary>
    Starting,

    /// <summary>Sending.</summary>
    Active,

    /// <summary>Draining queued packets before closing.</summary>
    Stopping,

    /// <summary>Closed after a normal stop.</summary>
    Stopped,

    /// <summary>Closed after a terminal failure; nothing more is sent for this publication.</summary>
    Failed,
}

/// <summary>A point-in-time copy of an output session's counters and rates.</summary>
/// <param name="PacketsSent">RTP packets sent.</param>
/// <param name="BytesSent">RTP bytes sent (UDP payload).</param>
/// <param name="PacketsDropped">RTP packets not sent (queued and dropped, or estimated for dropped access units).</param>
/// <param name="SendErrors">Failed sends.</param>
/// <param name="RtcpPacketsSent">RTCP compound packets sent.</param>
/// <param name="Bitrate">Send rate over the last second, in bit/s.</param>
/// <param name="PacketRate">Packet rate over the last second, in packets/s.</param>
public sealed record RtpOutputStatisticsSnapshot(long PacketsSent, long BytesSent, long PacketsDropped, long SendErrors, long RtcpPacketsSent, double Bitrate, double PacketRate);

/// <summary>Counters and rates of one output session; thread-safe.</summary>
internal sealed class RtpOutputStatistics
{
    /// <summary>A rate older than this is reported as zero (the sender only updates rates while it sends).</summary>
    private static readonly TimeSpan RateExpiry = TimeSpan.FromSeconds(2);

    /// <summary>The clock.</summary>
    private readonly TimeProvider _time;

    /// <summary>Counter.</summary>
    private long _packets;

    /// <summary>Counter.</summary>
    private long _bytes;

    /// <summary>Counter.</summary>
    private long _dropped;

    /// <summary>Counter.</summary>
    private long _errors;

    /// <summary>Counter.</summary>
    private long _rtcp;

    /// <summary>The last computed bit rate.</summary>
    private double _bitrate;

    /// <summary>The last computed packet rate.</summary>
    private double _packetRate;

    /// <summary>When the rates were computed.</summary>
    private long _rateTimestamp;

    /// <summary>Packets at the start of the rate window; sender only.</summary>
    private long _windowPackets;

    /// <summary>Bytes at the start of the rate window; sender only.</summary>
    private long _windowBytes;

    /// <summary>Start of the rate window; sender only.</summary>
    private long _windowStart;

    /// <summary>Initializes a new instance of the <see cref="RtpOutputStatistics"/> class.</summary>
    /// <param name="time">The clock.</param>
    public RtpOutputStatistics(TimeProvider time)
    {
        _time = time;
        _windowStart = time.GetTimestamp();
        _rateTimestamp = _windowStart;
    }

    /// <summary>Gets the current bit rate, zero once stale.</summary>
    public double Bitrate => Fresh() ? Volatile.Read(ref _bitrate) : 0;

    /// <summary>Gets the current packet rate, zero once stale.</summary>
    public double PacketRate => Fresh() ? Volatile.Read(ref _packetRate) : 0;

    /// <summary>Records sent packets; sender only.</summary>
    /// <param name="packets">Packets.</param>
    /// <param name="bytes">Bytes.</param>
    public void Sent(long packets, long bytes)
    {
        Interlocked.Add(ref _packets, packets);
        Interlocked.Add(ref _bytes, bytes);
    }

    /// <summary>Records packets not sent.</summary>
    /// <param name="packets">Packets.</param>
    public void Dropped(long packets) => Interlocked.Add(ref _dropped, packets);

    /// <summary>Records a failed send.</summary>
    public void Error() => Interlocked.Increment(ref _errors);

    /// <summary>Records an RTCP compound.</summary>
    public void Rtcp() => Interlocked.Increment(ref _rtcp);

    /// <summary>Recomputes the rates when at least a second has passed; sender only.</summary>
    public void UpdateRates()
    {
        var now = _time.GetTimestamp();
        var elapsed = _time.GetElapsedTime(_windowStart, now).TotalSeconds;
        if (elapsed < 1)
        {
            return;
        }

        var packets = Interlocked.Read(ref _packets);
        var bytes = Interlocked.Read(ref _bytes);
        Volatile.Write(ref _bitrate, (bytes - _windowBytes) * 8 / elapsed);
        Volatile.Write(ref _packetRate, (packets - _windowPackets) / elapsed);
        Volatile.Write(ref _rateTimestamp, now);
        _windowPackets = packets;
        _windowBytes = bytes;
        _windowStart = now;
    }

    /// <summary>Returns a snapshot.</summary>
    /// <returns>The snapshot.</returns>
    public RtpOutputStatisticsSnapshot Snapshot() => new(
        Interlocked.Read(ref _packets),
        Interlocked.Read(ref _bytes),
        Interlocked.Read(ref _dropped),
        Interlocked.Read(ref _errors),
        Interlocked.Read(ref _rtcp),
        Bitrate,
        PacketRate);

    /// <summary>Determines whether the rates are recent.</summary>
    /// <returns><see langword="true"/> if computed within <see cref="RateExpiry"/>.</returns>
    private bool Fresh() => _time.GetElapsedTime(Volatile.Read(ref _rateTimestamp)) <= RateExpiry;
}

/// <summary>Sends the media of one publication to one configured RTP output.</summary>
/// <remarks>
/// <para>
/// Threads: <see cref="OnVideo"/>, <see cref="OnAudio"/> and <see cref="Start"/> are called by the publication's media
/// pipeline (one call at a time); the sender task is the ring's only consumer and the socket's only user;
/// <see cref="StopAsync"/> is called once, after the last media call. Snapshots may be read from any thread.
/// </para>
/// <para>
/// Loss policy: an access unit that does not fit in the ring is not queued (and its sequence numbers are not used);
/// after a lost video access unit, video waits for the next IDR so the receiver never decodes from a missing reference.
/// Video also starts at the first IDR. A transient send error drops that packet; a terminal one fails the session.
/// </para>
/// </remarks>
internal sealed partial class RtpOutputSession : IDisposable
{
    /// <summary>Ring channel of video RTP packets.</summary>
    internal const byte VideoChannel = 0;

    /// <summary>Ring channel of audio RTP packets.</summary>
    internal const byte AudioChannel = 1;

    /// <summary>Minimum interval between RTCP reports of a sender (RFC 3550, section 6.2).</summary>
    internal static readonly TimeSpan RtcpInterval = TimeSpan.FromSeconds(5);

    /// <summary>Bytes of the IPv4 and UDP headers.</summary>
    private const int Ipv4UdpOverhead = 20 + 8;

    /// <summary>Bytes of the IPv6 and UDP headers.</summary>
    private const int Ipv6UdpOverhead = 40 + 8;

    /// <summary>The settings.</summary>
    private readonly RtpOutputOptions _output;

    /// <summary>Creates the socket.</summary>
    private readonly RtpSocketFactory _socketFactory;

    /// <summary>The output's effective QoS marking.</summary>
    private readonly RtpQosPolicy _qos;

    /// <summary>Metrics.</summary>
    private readonly RtpMetrics _metrics;

    /// <summary>Logger.</summary>
    private readonly ILogger _logger;

    /// <summary>The clock.</summary>
    private readonly TimeProvider _time;

    /// <summary>The packet hand-off.</summary>
    private readonly RtpPacketRing _ring;

    /// <summary>Wakes the sender.</summary>
    private readonly RtpSenderSignal _signal = new();

    /// <summary>Cancels a sender that does not drain in time.</summary>
    private readonly CancellationTokenSource _abort = new();

    /// <summary>Serialised RTP destinations by channel; <see langword="null"/> for a track that is not sent.</summary>
    private readonly SocketAddress?[] _rtpDestinations = new SocketAddress?[2];

    /// <summary>Serialised RTCP destinations by channel.</summary>
    private readonly SocketAddress?[] _rtcpDestinations = new SocketAddress?[2];

    /// <summary>The RTCP buffer; sender only.</summary>
    private readonly byte[] _rtcpBuffer;

    /// <summary>The RTP session of each channel that the last Sender Report covered; sender only.</summary>
    private readonly RtpStream?[] _reported = new RtpStream?[2];

    /// <summary>Drop reasons already logged at Warning in this session, as bit flags; pipeline thread only.</summary>
    private int _loggedDrops;

    /// <summary>The state.</summary>
    private int _state = (int)RtpOutputSessionState.Starting;

    /// <summary>Whether a stop was requested.</summary>
    private volatile bool _stopRequested;

    /// <summary>The socket, once opened.</summary>
    private IRtpDatagramSocket? _socket;

    /// <summary>The sender task, once started.</summary>
    private Task? _sender;

    /// <summary>Whether video waits for an IDR; pipeline thread only.</summary>
    private bool _awaitingIdr = true;

    /// <summary>The audio packetiser, created from the first audio configuration; pipeline thread only.</summary>
    private AacRtpPacketizer? _audio;

    /// <summary>The audio configuration the packetiser was created for.</summary>
    private AacAudioConfiguration? _audioConfiguration;

    /// <summary>Seqlock version of the media clock reference.</summary>
    private int _clockVersion;

    /// <summary>Media time (ms) of the clock reference.</summary>
    private long _clockMedia;

    /// <summary><see cref="TimeProvider"/> timestamp of the clock reference.</summary>
    private long _clockTimestamp;

    /// <summary>Whether a transient send error has been logged at Warning; sender only.</summary>
    private bool _loggedTransient;

    /// <summary>Initializes a new instance of the <see cref="RtpOutputSession"/> class.</summary>
    /// <param name="output">The validated output settings.</param>
    /// <param name="mediaSessionId">The publication's media session.</param>
    /// <param name="socketFactory">Creates the socket.</param>
    /// <param name="qos">The output's effective QoS marking, applied to this session's own socket.</param>
    /// <param name="metrics">Metrics.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="time">The clock.</param>
    public RtpOutputSession(RtpOutputOptions output, Guid mediaSessionId, RtpSocketFactory socketFactory, RtpQosPolicy qos, RtpMetrics metrics, ILogger logger, TimeProvider time)
    {
        _output = output;
        _socketFactory = socketFactory;
        _qos = qos;
        _metrics = metrics;
        _logger = logger;
        _time = time;
        MediaSessionId = mediaSessionId;
        SessionId = Guid.CreateVersion7(time.GetUtcNow());
        Destination = IPAddress.Parse(output.Destination);
        StartedAt = time.GetUtcNow();
        Statistics = new RtpOutputStatistics(time);
        CanonicalName = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)) + "@rtsp-ingest";
        _rtcpBuffer = new byte[RtcpPackets.MaxCompoundLength(CanonicalName)];
        _ring = new RtpPacketRing(output.QueueCapacity, PacketSizeLimit(output.Mtu, Destination.AddressFamily));
        if (output.VideoPort is not null)
        {
            Video = new H264RtpPacketizer(new RtpStream((byte)output.VideoPayloadType, H264RtpPacketizer.ClockRate));
        }

        SetDestinations(VideoChannel, output.VideoPort);
        SetDestinations(AudioChannel, output.AudioPort);
    }

    /// <summary>Gets the output settings.</summary>
    public RtpOutputOptions Output => _output;

    /// <summary>Gets the output's name.</summary>
    public string Name => _output.Name;

    /// <summary>Gets the addressing mode.</summary>
    public RtpOutputMode Mode => _output.Mode;

    /// <summary>Gets the QoS marking configured for the session's socket.</summary>
    public RtpQosPolicy Qos => _qos;

    /// <summary>Gets the ToS / Traffic Class the socket reported after marking was applied, or <see langword="null"/> when marking is off or the socket is not open.</summary>
    public byte? TrafficClass => _socket?.TrafficClass;

    /// <summary>Gets this session's identifier.</summary>
    public Guid SessionId { get; }

    /// <summary>Gets the media session (publication) it sends.</summary>
    public Guid MediaSessionId { get; }

    /// <summary>Gets the destination address.</summary>
    public IPAddress Destination { get; }

    /// <summary>Gets when the session was created.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Gets the RTCP CNAME shared by the output's tracks.</summary>
    public string CanonicalName { get; }

    /// <summary>Gets the state.</summary>
    public RtpOutputSessionState State => (RtpOutputSessionState)Volatile.Read(ref _state);

    /// <summary>Gets the counters and rates.</summary>
    public RtpOutputStatistics Statistics { get; }

    /// <summary>Gets the video packetiser, or <see langword="null"/> when the output sends no video.</summary>
    public H264RtpPacketizer? Video { get; }

    /// <summary>Gets the audio RTP session, once audio has been configured.</summary>
    public RtpStream? AudioStream => Volatile.Read(ref _audio)?.Stream;

    /// <summary>Gets the largest RTP packet of the output.</summary>
    public int MaxPacketSize => _ring.SlotSize;

    /// <summary>Gets the local endpoint of the socket, once open; for diagnostics and tests.</summary>
    internal EndPoint? LocalEndPoint => _socket?.LocalEndPoint;

    /// <summary>Gets the packets waiting in the ring; for tests.</summary>
    internal int QueuedPackets => _ring.Count;

    /// <summary>Returns the largest RTP packet for an IP MTU: the MTU less the IP and UDP headers.</summary>
    /// <param name="mtu">The IP MTU.</param>
    /// <param name="family">The address family.</param>
    /// <returns>The RTP packet size limit.</returns>
    public static int PacketSizeLimit(int mtu, AddressFamily family) => mtu - (family == AddressFamily.InterNetworkV6 ? Ipv6UdpOverhead : Ipv4UdpOverhead);

    /// <summary>Opens the socket and starts the sender. A failure is logged and leaves the session <see cref="RtpOutputSessionState.Failed"/>.</summary>
    /// <returns><see langword="true"/> if the session is sending.</returns>
    public bool Start()
    {
        try
        {
            _socket = _socketFactory(_output, _qos);
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException or NetworkInformationException or ArgumentException)
        {
            Volatile.Write(ref _state, (int)RtpOutputSessionState.Failed);
            LogStartFailed(Name, MediaSessionId, ex);
            return false;
        }

        Volatile.Write(ref _state, (int)RtpOutputSessionState.Active);
        _metrics.SessionStarted(Mode, Statistics);
        _sender = Task.Run(SendLoopAsync);
        var videoSsrc = Ssrc(Video?.Stream);
        var family = Destination.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";
        var qos = _socket.TrafficClass is { } trafficClass ? $"{_qos}, applied (socket reports 0x{trafficClass:x2})" : _qos.ToString();
        LogStarted(Name, MediaSessionId, SessionId, _output.Mode, _output.Destination, family, _output.VideoPort, _output.AudioPort, videoSsrc, _ring.SlotSize, qos);
        return true;
    }

    /// <summary>Packetises and queues a video access unit. Pipeline thread only; never throws for transport problems.</summary>
    /// <param name="sample">The access unit; not disposed.</param>
    public void OnVideo(EncodedVideoSample sample)
    {
        if (Video is null)
        {
            return;
        }

        if (_audio is null)
        {
            RecordClock(sample.Timestamps.DecodeTimestamp);
        }

        if (State != RtpOutputSessionState.Active)
        {
            Drop(video: true, RtpDropReason.OutputUnavailable, sample.Length);
            return;
        }

        if (_awaitingIdr && !sample.ContainsIdr)
        {
            Drop(video: true, RtpDropReason.AwaitingKeyframe, sample.Length);
            return;
        }

        var result = Video.Packetize(_ring, VideoChannel, sample, out _, out _);
        switch (result)
        {
            case RtpPacketizeResult.Queued:
                _awaitingIdr = false;
                _signal.Set();
                break;
            case RtpPacketizeResult.QueueFull:
                _awaitingIdr = true;
                Drop(video: true, RtpDropReason.QueueFull, sample.Length);
                break;
        }
    }

    /// <summary>Packetises and queues an audio access unit. Pipeline thread only; never throws for transport problems.</summary>
    /// <param name="sample">The access unit; not disposed.</param>
    public void OnAudio(EncodedAudioSample sample)
    {
        if (_rtpDestinations[AudioChannel] is null)
        {
            return;
        }

        var audio = AudioPacketizer(sample.Track.Configuration);
        RecordClock(sample.Timestamps.DecodeTimestamp);
        if (State != RtpOutputSessionState.Active)
        {
            Drop(video: false, RtpDropReason.OutputUnavailable, sample.Length);
            return;
        }

        switch (audio.Packetize(_ring, AudioChannel, sample, out _, out _))
        {
            case RtpPacketizeResult.Queued:
                _signal.Set();
                break;
            case RtpPacketizeResult.QueueFull:
                Drop(video: false, RtpDropReason.QueueFull, sample.Length);
                break;
            case RtpPacketizeResult.TooLarge:
                Drop(video: false, RtpDropReason.TooLarge, sample.Length);
                break;
        }
    }

    /// <summary>Drains queued packets, sends RTCP BYE and closes the socket; bounded by <paramref name="drainTimeout"/>.</summary>
    /// <param name="drainTimeout">How long the sender may take to drain before it is cancelled.</param>
    /// <returns>A task that completes when the socket is closed and the sender has exited.</returns>
    public async Task StopAsync(TimeSpan drainTimeout)
    {
        Interlocked.CompareExchange(ref _state, (int)RtpOutputSessionState.Stopping, (int)RtpOutputSessionState.Active);
        _stopRequested = true;
        _signal.Set();
        try
        {
            if (_sender is { } sender)
            {
                try
                {
                    await sender.WaitAsync(drainTimeout, _time).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    LogStopTimedOut(Name, MediaSessionId, _ring.Count);
                    await _abort.CancelAsync().ConfigureAwait(false);
                    _socket?.Dispose();
                    await sender.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
        }
        finally
        {
            _socket?.Dispose();
            DiscardQueued();
            Interlocked.CompareExchange(ref _state, (int)RtpOutputSessionState.Stopped, (int)RtpOutputSessionState.Stopping);
            _metrics.SessionEnded(Mode, Statistics);
            Dispose();
            var stats = Statistics.Snapshot();
            LogEnded(Name, MediaSessionId, State, stats.PacketsSent, stats.BytesSent, stats.PacketsDropped, stats.SendErrors);
        }
    }

    /// <summary>Closes the socket without draining; <see cref="StopAsync"/> calls it once the sender has exited.</summary>
    public void Dispose()
    {
        _socket?.Dispose();
        _abort.Dispose();
    }

    /// <summary>Counts the packets left in the ring once the sender has exited, and releases them.</summary>
    private void DiscardQueued()
    {
        long video = 0, audio = 0;
        while (_ring.TryPeek(out _, out var channel))
        {
            if (channel == VideoChannel)
            {
                video++;
            }
            else
            {
                audio++;
            }

            _ring.Advance();
        }

        Statistics.Dropped(video + audio);
        _metrics.Dropped(video: true, RtpDropReason.OutputUnavailable, video);
        _metrics.Dropped(video: false, RtpDropReason.OutputUnavailable, audio);
    }

    /// <summary>Marks the session failed after an unexpected packetisation error; queued packets are still sent.</summary>
    public void FailPacketization() => Volatile.Write(ref _state, (int)RtpOutputSessionState.Failed);

    /// <summary>Formats an SSRC for logs and reports.</summary>
    /// <param name="stream">The stream.</param>
    /// <returns>Eight hexadecimal digits, or <see langword="null"/>.</returns>
    internal static string? Ssrc(RtpStream? stream) => stream?.Ssrc.ToString("x8", CultureInfo.InvariantCulture);

    /// <summary>Returns the RTP timestamp of a track at the current wall-clock time, for a Sender Report.</summary>
    /// <param name="stream">The track.</param>
    /// <param name="now">The current <see cref="TimeProvider"/> timestamp.</param>
    /// <param name="timestamp">The RTP timestamp.</param>
    /// <returns><see langword="false"/> if no media has been seen.</returns>
    internal bool TryCurrentRtpTimestamp(RtpStream stream, long now, out uint timestamp)
    {
        long media;
        long at;
        int version;
        do
        {
            version = Volatile.Read(ref _clockVersion);
            media = Volatile.Read(ref _clockMedia);
            at = Volatile.Read(ref _clockTimestamp);
        }
        while ((version & 1) != 0 || version != Volatile.Read(ref _clockVersion));

        if (version == 0)
        {
            timestamp = 0;
            return false;
        }

        var elapsed = _time.GetElapsedTime(at, now);
        var ticks = ((media * stream.ClockRate) / 1000) + (long)(elapsed.TotalSeconds * stream.ClockRate);
        timestamp = stream.Timestamp(ticks);
        return true;
    }

    /// <summary>Returns the audio packetiser for a configuration, creating it (and, for a new clock rate, a new RTP session).</summary>
    /// <param name="configuration">The AAC configuration of the sample.</param>
    /// <returns>The packetiser.</returns>
    private AacRtpPacketizer AudioPacketizer(AacAudioConfiguration configuration)
    {
        if (_audio is { } current && ReferenceEquals(configuration, _audioConfiguration))
        {
            return current;
        }

        var clock = _output.AudioClockRate ?? configuration.SampleRate;
        var stream = _audio?.Stream is { } existing && existing.ClockRate == clock
            ? existing
            : new RtpStream((byte)_output.AudioPayloadType, clock, Video?.Stream.Ssrc);
        var packetizer = new AacRtpPacketizer(stream, configuration.SampleRate, configuration.FrameLength ?? 1024);
        _audioConfiguration = configuration;
        Volatile.Write(ref _audio, packetizer);
        return packetizer;
    }

    /// <summary>Records the media clock reference used by Sender Reports.</summary>
    /// <param name="mediaTime">The sample's decode time (ms).</param>
    private void RecordClock(long mediaTime)
    {
        var version = _clockVersion;
        Volatile.Write(ref _clockVersion, version + 1);
        Volatile.Write(ref _clockMedia, mediaTime);
        Volatile.Write(ref _clockTimestamp, _time.GetTimestamp());
        Volatile.Write(ref _clockVersion, version + 2);
    }

    /// <summary>Counts and, the first time per reason, logs a dropped access unit.</summary>
    /// <param name="video">Whether it is video.</param>
    /// <param name="reason">Why.</param>
    /// <param name="length">Its size, from which its packets are estimated.</param>
    private void Drop(bool video, RtpDropReason reason, int length)
    {
        var payload = _ring.SlotSize - RtpHeader.Length - 4;
        var packets = Math.Max(1, (length + payload - 1) / payload);
        Statistics.Dropped(packets);
        _metrics.Dropped(video, reason, packets);
        var flag = 1 << (int)reason;
        if (reason != RtpDropReason.AwaitingKeyframe && (_loggedDrops & flag) == 0)
        {
            _loggedDrops |= flag;
            LogDropped(Name, MediaSessionId, video ? "video" : "audio", RtpMetrics.ReasonTag(reason));
        }
    }

    /// <summary>Serialises the RTP and RTCP destinations of a track.</summary>
    /// <param name="channel">The channel.</param>
    /// <param name="port">The RTP port, or <see langword="null"/> when the track is not sent.</param>
    private void SetDestinations(byte channel, int? port)
    {
        if (port is { } rtp)
        {
            _rtpDestinations[channel] = new IPEndPoint(Destination, rtp).Serialize();
            _rtcpDestinations[channel] = new IPEndPoint(Destination, rtp + 1).Serialize();
        }
    }

    /// <summary>Sends queued packets until stopped, failed or cancelled.</summary>
    /// <returns>A task that completes when the sender exits.</returns>
    private async Task SendLoopAsync()
    {
        var socket = _socket!;
        var token = _abort.Token;
        var lastRtcp = (long?)null;
        try
        {
            while (true)
            {
                long videoPackets = 0, videoBytes = 0, audioPackets = 0, audioBytes = 0;
                while (_ring.TryPeek(out var packet, out var channel))
                {
                    var sent = await SendAsync(socket, packet, _rtpDestinations[channel]!, channel == VideoChannel, token).ConfigureAwait(false);
                    _ring.Advance();
                    if (sent is null)
                    {
                        Record(videoPackets, videoBytes, audioPackets, audioBytes);
                        return;
                    }

                    if (sent.Value)
                    {
                        var stream = channel == VideoChannel ? Video!.Stream : Volatile.Read(ref _audio)!.Stream;
                        stream.PacketCount++;
                        stream.OctetCount += packet.Length - RtpHeader.Length;
                        if (channel == VideoChannel)
                        {
                            videoPackets++;
                            videoBytes += packet.Length;
                        }
                        else
                        {
                            audioPackets++;
                            audioBytes += packet.Length;
                        }
                    }
                }

                Record(videoPackets, videoBytes, audioPackets, audioBytes);
                var now = _time.GetTimestamp();
                if (_output.RtcpEnabled && (lastRtcp is null || _time.GetElapsedTime(lastRtcp.Value, now) >= RtcpInterval || HasUnreportedTrack()) && await SendRtcpAsync(socket, now, goodbye: false, token).ConfigureAwait(false))
                {
                    lastRtcp = now;
                }

                if (_stopRequested && _ring.Count == 0)
                {
                    break;
                }

                await _signal.WaitAsync().ConfigureAwait(false);
            }

            if (_output.RtcpEnabled)
            {
                await SendRtcpAsync(socket, _time.GetTimestamp(), goodbye: true, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    /// <summary>Records a batch of sent packets in the statistics and metrics.</summary>
    /// <param name="videoPackets">Video packets.</param>
    /// <param name="videoBytes">Video bytes.</param>
    /// <param name="audioPackets">Audio packets.</param>
    /// <param name="audioBytes">Audio bytes.</param>
    private void Record(long videoPackets, long videoBytes, long audioPackets, long audioBytes)
    {
        var multicast = _output.Mode == RtpOutputMode.Multicast;
        Statistics.Sent(videoPackets + audioPackets, videoBytes + audioBytes);
        _metrics.Sent(video: true, multicast, videoPackets, videoBytes);
        _metrics.Sent(video: false, multicast, audioPackets, audioBytes);
        Statistics.UpdateRates();
    }

    /// <summary>Sends one datagram and classifies a failure.</summary>
    /// <param name="socket">The socket.</param>
    /// <param name="datagram">The datagram.</param>
    /// <param name="destination">The destination.</param>
    /// <param name="video">Whether it is a video packet, for drop accounting.</param>
    /// <param name="token">Cancellation.</param>
    /// <returns><see langword="true"/> if sent, <see langword="false"/> if dropped after a transient error, <see langword="null"/> after a terminal error.</returns>
    private async ValueTask<bool?> SendAsync(IRtpDatagramSocket socket, ReadOnlyMemory<byte> datagram, SocketAddress destination, bool video, CancellationToken token)
    {
        try
        {
            await socket.SendToAsync(datagram, destination, token).ConfigureAwait(false);
            return true;
        }
        catch (SocketException ex) when (RtpTransportErrors.IsTransient(ex.SocketErrorCode))
        {
            Statistics.Error();
            Statistics.Dropped(1);
            _metrics.SendError(transient: true);
            _metrics.Dropped(video, RtpDropReason.SendFailed, 1);
            if (!_loggedTransient)
            {
                _loggedTransient = true;
                LogTransientError(Name, MediaSessionId, ex.SocketErrorCode);
            }

            return false;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException or ArgumentException)
        {
            Statistics.Error();
            Statistics.Dropped(1);
            _metrics.SendError(transient: false);
            _metrics.Dropped(video, RtpDropReason.OutputUnavailable, 1);
            Fail(ex);
            return null;
        }
    }

    /// <summary>Sends a Sender Report (and BYE when stopping) for each track that has sent packets.</summary>
    /// <param name="socket">The socket.</param>
    /// <param name="now">The current timestamp.</param>
    /// <param name="goodbye">Whether to append BYE.</param>
    /// <param name="token">Cancellation.</param>
    /// <returns><see langword="true"/> if a report was sent.</returns>
    private async ValueTask<bool> SendRtcpAsync(IRtpDatagramSocket socket, long now, bool goodbye, CancellationToken token)
    {
        var sent = false;
        var wallClock = _time.GetUtcNow();
        for (byte channel = 0; channel < 2; channel++)
        {
            var stream = channel == VideoChannel ? Video?.Stream : Volatile.Read(ref _audio)?.Stream;
            if (stream is null || stream.PacketCount == 0 || _rtcpDestinations[channel] is not { } destination || !TryCurrentRtpTimestamp(stream, now, out var rtpTimestamp))
            {
                continue;
            }

            var length = RtcpPackets.WriteCompound(_rtcpBuffer, stream.Ssrc, wallClock, rtpTimestamp, stream.PacketCount, stream.OctetCount, CanonicalName, goodbye);
            if (await SendAsync(socket, _rtcpBuffer.AsMemory(0, length), destination, channel == VideoChannel, token).ConfigureAwait(false) is not true)
            {
                return sent;
            }

            Statistics.Rtcp();
            _metrics.RtcpSent();
            _reported[channel] = stream;
            sent = true;
        }

        return sent;
    }

    /// <summary>Determines whether a track has sent RTP packets but no Sender Report yet; sender only.</summary>
    /// <returns><see langword="true"/> if a Sender Report is due before the interval.</returns>
    /// <remarks>Receivers such as FFmpeg cannot timestamp a track's packets until its first Sender Report.</remarks>
    private bool HasUnreportedTrack()
    {
        for (byte channel = 0; channel < 2; channel++)
        {
            var stream = channel == VideoChannel ? Video?.Stream : Volatile.Read(ref _audio)?.Stream;
            if (stream is { PacketCount: > 0 } && _rtcpDestinations[channel] is not null && !ReferenceEquals(stream, _reported[channel]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Marks the session failed after a terminal transport error.</summary>
    /// <param name="exception">The error.</param>
    private void Fail(Exception exception)
    {
        var previous = (RtpOutputSessionState)Interlocked.Exchange(ref _state, (int)RtpOutputSessionState.Failed);
        if (previous != RtpOutputSessionState.Failed)
        {
            LogFailed(Name, MediaSessionId, exception);
        }
    }

    /// <summary>Logs a started session.</summary>
    /// <param name="output">The output.</param>
    /// <param name="sessionId">The publication.</param>
    /// <param name="rtpSessionId">The RTP output session.</param>
    /// <param name="mode">The mode.</param>
    /// <param name="destination">The destination address.</param>
    /// <param name="family">The address family.</param>
    /// <param name="videoPort">The video port.</param>
    /// <param name="audioPort">The audio port.</param>
    /// <param name="videoSsrc">The video SSRC.</param>
    /// <param name="maxPacketSize">The largest RTP packet.</param>
    /// <param name="qos">The QoS marking and the value the socket reported.</param>
    [LoggerMessage(EventId = 9400, Level = LogLevel.Information, Message = "RTP output {Output} started for session {SessionId} (RTP session {RtpSessionId}): {Mode} to {Destination} ({Family}), video port {VideoPort}, audio port {AudioPort}, video SSRC {VideoSsrc}, packets up to {MaxPacketSize} bytes; QoS {Qos}")]
    private partial void LogStarted(string output, Guid sessionId, Guid rtpSessionId, RtpOutputMode mode, string destination, string family, int? videoPort, int? audioPort, string? videoSsrc, int maxPacketSize, string qos);

    /// <summary>Logs an ended session.</summary>
    /// <param name="output">The output.</param>
    /// <param name="sessionId">The publication.</param>
    /// <param name="state">The final state.</param>
    /// <param name="packets">Packets sent.</param>
    /// <param name="bytes">Bytes sent.</param>
    /// <param name="dropped">Packets dropped.</param>
    /// <param name="errors">Send errors.</param>
    [LoggerMessage(EventId = 9401, Level = LogLevel.Information, Message = "RTP output {Output} ended for session {SessionId} in state {State}: {Packets} packets, {Bytes} bytes sent, {Dropped} packets dropped, {Errors} send errors")]
    private partial void LogEnded(string output, Guid sessionId, RtpOutputSessionState state, long packets, long bytes, long dropped, long errors);

    /// <summary>Logs a socket that could not be opened.</summary>
    /// <param name="output">The output.</param>
    /// <param name="sessionId">The publication.</param>
    /// <param name="exception">The failure.</param>
    [LoggerMessage(EventId = 9402, Level = LogLevel.Error, Message = "RTP output {Output} could not start for session {SessionId}; it sends nothing for this publication")]
    private partial void LogStartFailed(string output, Guid sessionId, Exception exception);

    /// <summary>Logs a terminal transport failure.</summary>
    /// <param name="output">The output.</param>
    /// <param name="sessionId">The publication.</param>
    /// <param name="exception">The failure.</param>
    [LoggerMessage(EventId = 9403, Level = LogLevel.Error, Message = "RTP output {Output} failed for session {SessionId}; it sends nothing more for this publication")]
    private partial void LogFailed(string output, Guid sessionId, Exception exception);

    /// <summary>Logs the first transient send error of a session.</summary>
    /// <param name="output">The output.</param>
    /// <param name="sessionId">The publication.</param>
    /// <param name="error">The socket error.</param>
    [LoggerMessage(EventId = 9404, Level = LogLevel.Warning, Message = "RTP output {Output} send failed for session {SessionId} ({Error}); packets are dropped while the error persists (further errors are counted, not logged)")]
    private partial void LogTransientError(string output, Guid sessionId, SocketError error);

    /// <summary>Logs the first dropped access unit of a reason in a session.</summary>
    /// <param name="output">The output.</param>
    /// <param name="sessionId">The publication.</param>
    /// <param name="track">The track.</param>
    /// <param name="reason">The reason tag.</param>
    [LoggerMessage(EventId = 9405, Level = LogLevel.Warning, Message = "RTP output {Output} dropped {Track} media for session {SessionId} ({Reason}); further drops of this kind are counted, not logged")]
    private partial void LogDropped(string output, Guid sessionId, string track, string reason);

    /// <summary>Logs a sender that did not drain within the stop timeout.</summary>
    /// <param name="output">The output.</param>
    /// <param name="sessionId">The publication.</param>
    /// <param name="queued">Packets still queued.</param>
    [LoggerMessage(EventId = 9406, Level = LogLevel.Warning, Message = "RTP output {Output} did not drain within the stop timeout for session {SessionId}; {Queued} queued packets are discarded")]
    private partial void LogStopTimedOut(string output, Guid sessionId, int queued);
}
