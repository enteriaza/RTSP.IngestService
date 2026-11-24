// System.Diagnostics.Metrics instruments for the RTMP ingest server, created from the host's IMeterFactory. All
// dimensions are fixed, low-cardinality enumerations; stream names, keys, identities and addresses are never used as
// tags. The meter can be observed with dotnet-counters or any OpenTelemetry exporter added to the host.

using System.Diagnostics.Metrics;
using RTSP.IngestService.Rtmp.Publishing;

namespace RTSP.IngestService.Rtmp;

/// <summary>
/// RTMP ingest metrics, published on the <see cref="MeterName"/> meter.
/// </summary>
/// <remarks>Thread-safe.</remarks>
internal sealed class RtmpMetrics
{
    /// <summary>Name of the meter.</summary>
    public const string MeterName = "RTSP.IngestService.Rtmp";

    /// <summary>Accepted connections.</summary>
    private readonly Counter<long> _accepted;

    /// <summary>Connections rejected by the connection limit.</summary>
    private readonly Counter<long> _rejected;

    /// <summary>Open connections.</summary>
    private readonly UpDownCounter<long> _active;

    /// <summary>Connection duration by close reason.</summary>
    private readonly Histogram<double> _duration;

    /// <summary>Handshakes by result.</summary>
    private readonly Counter<long> _handshakes;

    /// <summary>Rejected publish requests.</summary>
    private readonly Counter<long> _authenticationFailures;

    /// <summary>Connections closed for protocol violations.</summary>
    private readonly Counter<long> _protocolErrors;

    /// <summary>Publishing sessions in progress.</summary>
    private readonly UpDownCounter<long> _activeSessions;

    /// <summary>Ended publishing sessions by reason.</summary>
    private readonly Counter<long> _publisherDisconnects;

    /// <summary>Bytes received from clients.</summary>
    private readonly Counter<long> _bytesReceived;

    /// <summary>Messages received by category.</summary>
    private readonly Counter<long> _messagesReceived;

    /// <summary>Initializes a new instance of the <see cref="RtmpMetrics"/> class.</summary>
    /// <param name="meterFactory">Factory owning the meter's lifetime.</param>
    public RtmpMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);

        _accepted = meter.CreateCounter<long>("rtmp.connections.accepted", "{connection}", "TCP connections admitted under the connection limit.");
        _rejected = meter.CreateCounter<long>("rtmp.connections.rejected", "{connection}", "TCP connections closed on arrival; tag reason.");
        _active = meter.CreateUpDownCounter<long>("rtmp.connections.active", "{connection}", "Open RTMP TCP connections.");
        _duration = meter.CreateHistogram<double>("rtmp.connection.duration", "s", "Duration of closed RTMP connections; tag close_reason.");
        _handshakes = meter.CreateCounter<long>("rtmp.handshakes", "{handshake}", "Completed or failed RTMP handshakes; tag result.");
        _authenticationFailures = meter.CreateCounter<long>("rtmp.authentication.failures", "{request}", "Publish requests rejected by the authenticator.");
        _protocolErrors = meter.CreateCounter<long>("rtmp.protocol_errors", "{connection}", "Connections closed because of RTMP protocol violations.");
        _activeSessions = meter.CreateUpDownCounter<long>("rtmp.sessions.active", "{session}", "Streams currently being published.");
        _publisherDisconnects = meter.CreateCounter<long>("rtmp.publisher.disconnects", "{session}", "Ended publishing sessions; tag reason.");
        _bytesReceived = meter.CreateCounter<long>("rtmp.bytes_received", "By", "Bytes received from RTMP clients, including the handshake.");
        _messagesReceived = meter.CreateCounter<long>("rtmp.messages_received", "{message}", "Complete RTMP messages received; tag type.");
    }

    /// <summary>Records an admitted connection.</summary>
    public void ConnectionAccepted()
    {
        _accepted.Add(1);
        _active.Add(1);
    }

    /// <summary>Records a connection closed by the connection limit.</summary>
    public void ConnectionRejected() => _rejected.Add(1, new KeyValuePair<string, object?>("reason", "connection_limit"));

    /// <summary>Records a closed connection.</summary>
    /// <param name="duration">How long it was open.</param>
    /// <param name="reason">Why it closed.</param>
    public void ConnectionClosed(TimeSpan duration, RtmpCloseReason reason)
    {
        _active.Add(-1);
        _duration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("close_reason", Name(reason)));
        if (reason == RtmpCloseReason.ProtocolError)
        {
            _protocolErrors.Add(1);
        }
    }

    /// <summary>Records a handshake outcome.</summary>
    /// <param name="succeeded">Whether it completed.</param>
    public void Handshake(bool succeeded) => _handshakes.Add(1, new KeyValuePair<string, object?>("result", succeeded ? "success" : "failure"));

    /// <summary>Records a rejected publish request.</summary>
    public void AuthenticationFailed() => _authenticationFailures.Add(1);

    /// <summary>Records a started publishing session.</summary>
    public void SessionStarted() => _activeSessions.Add(1);

    /// <summary>Records an ended publishing session.</summary>
    /// <param name="reason">Why it ended.</param>
    public void SessionEnded(PublishEndReason reason)
    {
        _activeSessions.Add(-1);
        _publisherDisconnects.Add(1, new KeyValuePair<string, object?>("reason", Name(reason)));
    }

    /// <summary>Records received bytes.</summary>
    /// <param name="count">Number of bytes.</param>
    public void BytesReceived(long count) => _bytesReceived.Add(count);

    /// <summary>Records a received message.</summary>
    /// <param name="category">One of <c>audio</c>, <c>video</c>, <c>data</c>, <c>command</c>, <c>control</c> or <c>other</c>.</param>
    public void MessageReceived(string category) => _messagesReceived.Add(1, new KeyValuePair<string, object?>("type", category));

    /// <summary>Returns the snake-case metric value of <paramref name="reason"/>.</summary>
    /// <param name="reason">The close reason.</param>
    /// <returns>The tag value.</returns>
    private static string Name(RtmpCloseReason reason) => reason switch
    {
        RtmpCloseReason.ClientDisconnected => "client_disconnected",
        RtmpCloseReason.Rejected => "rejected",
        RtmpCloseReason.ServerShutdown => "server_shutdown",
        RtmpCloseReason.HandshakeTimeout => "handshake_timeout",
        RtmpCloseReason.HandshakeFailed => "handshake_failed",
        RtmpCloseReason.IdleTimeout => "idle_timeout",
        RtmpCloseReason.ProtocolError => "protocol_error",
        RtmpCloseReason.TransportError => "transport_error",
        RtmpCloseReason.HandlerFailed => "handler_failed",
        _ => "internal_error",
    };

    /// <summary>Returns the snake-case metric value of <paramref name="reason"/>.</summary>
    /// <param name="reason">The end reason.</param>
    /// <returns>The tag value.</returns>
    private static string Name(PublishEndReason reason) => reason switch
    {
        PublishEndReason.Unpublished => "unpublished",
        PublishEndReason.StreamDeleted => "stream_deleted",
        PublishEndReason.ClientDisconnected => "client_disconnected",
        PublishEndReason.ConnectionTimeout => "connection_timeout",
        PublishEndReason.ProtocolError => "protocol_error",
        PublishEndReason.ConnectionError => "connection_error",
        PublishEndReason.ServerShutdown => "server_shutdown",
        _ => "handler_failed",
    };
}
