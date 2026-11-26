// The RTP output stage: an IMediaSessionHandler decorator in front of the existing downstream handler (HLS packaging in
// the service host). For a publication that configured outputs name, it packetises each sample into those outputs'
// packet rings and then hands the same sample, with its ownership, to the inner handler. Nothing is decoded, encoded
// or converted, and the inner handler's behaviour, ordering and backpressure are unchanged.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;

namespace RTSP.IngestService.Rtp;

/// <summary>One configured output and its current session, as reported by the management endpoint.</summary>
/// <param name="Output">The settings.</param>
/// <param name="Session">The session sending the current publication, or <see langword="null"/> when the stream is not published.</param>
/// <param name="StreamInfo">The current publication's stream description, when there is one.</param>
/// <param name="Qos">The output's effective QoS marking.</param>
internal sealed record RtpOutputStatus(RtpOutputOptions Output, RtpOutputSession? Session, MediaStreamInfo? StreamInfo, RtpQosPolicy Qos);

/// <summary>Sends published streams to the configured RTP/UDP outputs, then forwards every call to the inner handler.</summary>
/// <remarks>
/// <para>
/// Each publication whose application and stream match one or more outputs gets one <see cref="RtpOutputSession"/>
/// per output. Sessions are created before the inner handler accepts the publication (creation opens nothing) and
/// started after it, so a publication the inner handler rejects never opens a socket. Socket and transmission failures
/// affect only their session; a packetisation failure is contained to its output, which then stops sending.
/// </para>
/// <para>
/// Samples are packetised on the calling (pipeline) thread before they are forwarded; the call returns as soon as the
/// packets are in the rings, so the inner handler and the publisher never wait for the network.
/// </para>
/// </remarks>
public sealed partial class RtpOutputHandler : IMediaSessionHandler, IAsyncDisposable
{
    /// <summary>How long a stopping session may spend draining queued packets before it is cancelled.</summary>
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The handler every call is forwarded to.</summary>
    private readonly IMediaSessionHandler _inner;

    /// <summary>Whether this instance created <see cref="_inner"/> and must dispose it.</summary>
    private readonly bool _ownsInner;

    /// <summary>The outputs in configuration order.</summary>
    private readonly RtpOutputOptions[] _outputs;

    /// <summary>The effective QoS marking of each output, by output name; resolved once at startup.</summary>
    private readonly Dictionary<string, RtpQosPolicy> _qos;

    /// <summary>Outputs by source application and stream.</summary>
    private readonly Dictionary<(string Application, string Stream), RtpOutputOptions[]> _byStream;

    /// <summary>Creates sockets.</summary>
    private readonly RtpSocketFactory _socketFactory;

    /// <summary>Metrics.</summary>
    private readonly RtpMetrics _metrics;

    /// <summary>Logger.</summary>
    private readonly ILogger<RtpOutputHandler> _logger;

    /// <summary>The clock.</summary>
    private readonly TimeProvider _time;

    /// <summary>Sessions by publication.</summary>
    private readonly ConcurrentDictionary<Guid, Publication> _publications = new();

    /// <summary>The current session of each output, by output name.</summary>
    private readonly ConcurrentDictionary<string, Publication> _current = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="RtpOutputHandler"/> class.</summary>
    /// <param name="inner">The handler every call is forwarded to.</param>
    /// <param name="ownsInner">Whether to dispose <paramref name="inner"/> with this instance.</param>
    /// <param name="options">The RTP settings.</param>
    /// <param name="socketFactory">Creates output sockets.</param>
    /// <param name="metrics">Metrics.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="time">The clock.</param>
    internal RtpOutputHandler(IMediaSessionHandler inner, bool ownsInner, IOptions<RtpOptions> options, RtpSocketFactory socketFactory, RtpMetrics metrics, ILogger<RtpOutputHandler> logger, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        _inner = inner;
        _ownsInner = ownsInner;
        _outputs = [.. options.Value.Outputs];
        var transportQos = options.Value.Transport.Qos;
        _qos = _outputs.ToDictionary(static o => o.Name, o => RtpQosPolicy.Resolve(transportQos, o.Qos), StringComparer.Ordinal);
        _byStream = _outputs.GroupBy(static o => (o.Application, o.Stream)).ToDictionary(static g => g.Key, static g => g.ToArray());
        _socketFactory = socketFactory;
        _metrics = metrics;
        _logger = logger;
        _time = time;
        var multicast = _outputs.Count(static o => o.Mode == RtpOutputMode.Multicast);
        var marked = _qos.Values.Count(static q => q.Enabled);
        LogConfigured(_outputs.Length, multicast, marked);
        foreach (var output in _outputs)
        {
            CheckQos(output, _qos[output.Name]);
        }
    }

    /// <summary>Gets the handler calls are forwarded to.</summary>
    internal IMediaSessionHandler Inner => _inner;

    /// <summary>Gets the number of publications being sent; for diagnostics and tests.</summary>
    internal int ActivePublicationCount => _publications.Count;

    /// <summary>Returns every configured output with its current session.</summary>
    /// <returns>The outputs in configuration order.</returns>
    internal IReadOnlyList<RtpOutputStatus> GetOutputs() =>
        [.. _outputs.Select(o => _current.TryGetValue(o.Name, out var p) ? new RtpOutputStatus(o, Find(p, o.Name), p.Media.StreamInfo, _qos[o.Name]) : new RtpOutputStatus(o, null, null, _qos[o.Name]))];

    /// <summary>Returns one output with its current session.</summary>
    /// <param name="name">The output name.</param>
    /// <returns>The status, or <see langword="null"/> if no output has the name.</returns>
    internal RtpOutputStatus? GetOutput(string name) => GetOutputs().FirstOrDefault(s => string.Equals(s.Output.Name, name, StringComparison.Ordinal));

    /// <inheritdoc />
    public async ValueTask OnSessionStartedAsync(MediaSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var publisher = session.Publisher;
        var sessions = _byStream.TryGetValue((publisher.Application, publisher.StreamIdentity), out var outputs)
            ? [.. outputs.Select(o => new RtpOutputSession(o, session.SessionId, _socketFactory, _qos[o.Name], _metrics, _logger, _time))]
            : Array.Empty<RtpOutputSession>();

        try
        {
            await _inner.OnSessionStartedAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            foreach (var output in sessions)
            {
                output.Dispose();
            }

            throw;
        }

        if (sessions.Length == 0)
        {
            return;
        }

        var publication = new Publication(session, sessions);
        _publications[session.SessionId] = publication;
        foreach (var output in sessions)
        {
            output.Start();
            _current[output.Name] = publication;
        }
    }

    /// <inheritdoc />
    public ValueTask OnStreamInfoChangedAsync(MediaSession session, MediaStreamInfo streamInfo, CancellationToken cancellationToken) =>
        _inner.OnStreamInfoChangedAsync(session, streamInfo, cancellationToken);

    /// <inheritdoc />
    public ValueTask OnVideoSampleAsync(MediaSession session, EncodedVideoSample sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sample);
        if (_publications.TryGetValue(session.SessionId, out var publication))
        {
            foreach (var output in publication.Outputs)
            {
                try
                {
                    output.OnVideo(sample);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Contain(publication, output, ex);
                }
            }
        }

        return _inner.OnVideoSampleAsync(session, sample, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask OnAudioSampleAsync(MediaSession session, EncodedAudioSample sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sample);
        if (_publications.TryGetValue(session.SessionId, out var publication))
        {
            foreach (var output in publication.Outputs)
            {
                try
                {
                    output.OnAudio(sample);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Contain(publication, output, ex);
                }
            }
        }

        return _inner.OnAudioSampleAsync(session, sample, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask OnMediaErrorAsync(MediaSession session, MediaProcessingError mediaError, CancellationToken cancellationToken) =>
        _inner.OnMediaErrorAsync(session, mediaError, cancellationToken);

    /// <inheritdoc />
    public async ValueTask OnSessionEndedAsync(MediaSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            await _inner.OnSessionEndedAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_publications.TryRemove(session.SessionId, out var publication))
            {
                await StopAsync(publication).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Stops every remaining session and disposes the inner handler if this instance created it.</summary>
    /// <returns>A task that completes when every socket is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        foreach (var id in _publications.Keys)
        {
            if (_publications.TryRemove(id, out var publication))
            {
                await StopAsync(publication).ConfigureAwait(false);
            }
        }

        if (_ownsInner)
        {
            switch (_inner)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    /// <summary>Returns the session of an output within a publication.</summary>
    /// <param name="publication">The publication.</param>
    /// <param name="name">The output name.</param>
    /// <returns>The session.</returns>
    private static RtpOutputSession? Find(Publication publication, string name) =>
        Array.Find(publication.Outputs, s => string.Equals(s.Name, name, StringComparison.Ordinal));

    /// <summary>Stops a publication's sessions concurrently and releases their output names.</summary>
    /// <param name="publication">The publication.</param>
    /// <returns>A task that completes when all are stopped.</returns>
    private async Task StopAsync(Publication publication)
    {
        foreach (var output in publication.Outputs)
        {
            _current.TryRemove(new KeyValuePair<string, Publication>(output.Name, publication));
        }

        await Task.WhenAll(publication.Outputs.Select(o => o.StopAsync(DrainTimeout))).ConfigureAwait(false);
    }

    /// <summary>
    /// Logs an output's QoS marking at startup and checks, on a throwaway socket of the output's address family, that
    /// this system accepts it. A failure is logged; every session of the output will then fail to start (9402) with the
    /// same reason until the configuration changes.
    /// </summary>
    /// <param name="output">The output.</param>
    /// <param name="qos">Its effective policy.</param>
    private void CheckQos(RtpOutputOptions output, RtpQosPolicy qos)
    {
        if (!qos.Enabled)
        {
            return;
        }

        var family = IPAddress.Parse(output.Destination).AddressFamily;
        var familyName = family == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";
        if (qos.Ecn == RtpEcn.CongestionExperienced)
        {
            LogCongestionExperienced(output.Name);
        }

        try
        {
            using var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            var effective = $"0x{RtpSocketQos.Apply(socket, qos):x2}";
            LogQos(output.Name, familyName, qos.Dscp, qos.Ecn, effective);
        }
        catch (Exception ex) when (ex is RtpQosException or SocketException)
        {
            var requested = $"0x{qos.TrafficClass:x2}";
            LogQosUnsupported(output.Name, familyName, qos.Dscp, qos.Ecn, requested, ex.Message);
        }
    }

    /// <summary>Isolates an unexpected packetisation failure to its output.</summary>
    /// <param name="publication">The publication.</param>
    /// <param name="output">The output.</param>
    /// <param name="exception">The failure.</param>
    private void Contain(Publication publication, RtpOutputSession output, Exception exception)
    {
        LogPacketizationFailed(output.Name, publication.Media.SessionId, exception);
        output.FailPacketization();
    }

    /// <summary>Logs the configured outputs.</summary>
    /// <param name="outputs">The number of outputs.</param>
    /// <param name="multicast">How many are multicast.</param>
    /// <param name="qosMarked">How many set QoS marking.</param>
    [LoggerMessage(EventId = 9407, Level = LogLevel.Information, Message = "RTP output enabled with {Outputs} outputs ({Multicast} multicast, {QosMarked} with QoS marking)")]
    private partial void LogConfigured(int outputs, int multicast, int qosMarked);

    /// <summary>Logs an output's QoS marking, verified on a probe socket.</summary>
    /// <param name="output">The output.</param>
    /// <param name="family">The address family.</param>
    /// <param name="dscp">The DSCP.</param>
    /// <param name="ecn">The ECN codepoint.</param>
    /// <param name="trafficClass">The byte the probe socket reported.</param>
    [LoggerMessage(EventId = 9409, Level = LogLevel.Information, Message = "RTP output {Output} QoS marking enabled for {Family}: DSCP {Dscp}, ECN {Ecn}, traffic class {TrafficClass}; accepted and verified on a probe socket")]
    private partial void LogQos(string output, string family, int dscp, int ecn, string trafficClass);

    /// <summary>Logs an output whose QoS marking this system does not accept.</summary>
    /// <param name="output">The output.</param>
    /// <param name="family">The address family.</param>
    /// <param name="dscp">The DSCP.</param>
    /// <param name="ecn">The ECN codepoint.</param>
    /// <param name="trafficClass">The requested byte.</param>
    /// <param name="reason">Why, with the corrective action.</param>
    [LoggerMessage(EventId = 9410, Level = LogLevel.Error, Message = "RTP output {Output} QoS marking for {Family} (DSCP {Dscp}, ECN {Ecn}, traffic class {TrafficClass}) cannot be applied on this system; every session of the output will fail to start until it is changed: {Reason}")]
    private partial void LogQosUnsupported(string output, string family, int dscp, int ecn, string trafficClass, string reason);

    /// <summary>Warns that an output marks its packets Congestion Experienced.</summary>
    /// <param name="output">The output.</param>
    [LoggerMessage(EventId = 9411, Level = LogLevel.Warning, Message = "RTP output {Output} sets ECN to CE (3), which network devices set to signal congestion; senders should not set it (RFC 3168, section 5)")]
    private partial void LogCongestionExperienced(string output);

    /// <summary>Logs an unexpected packetisation failure.</summary>
    /// <param name="output">The output.</param>
    /// <param name="sessionId">The publication.</param>
    /// <param name="exception">The failure.</param>
    [LoggerMessage(EventId = 9408, Level = LogLevel.Error, Message = "RTP output {Output} failed to packetise media for session {SessionId}; it sends nothing more for this publication")]
    private partial void LogPacketizationFailed(string output, Guid sessionId, Exception exception);

    /// <summary>The RTP sessions of one publication.</summary>
    /// <param name="Media">The media session.</param>
    /// <param name="Outputs">Its output sessions.</param>
    private sealed record Publication(MediaSession Media, RtpOutputSession[] Outputs);
}
