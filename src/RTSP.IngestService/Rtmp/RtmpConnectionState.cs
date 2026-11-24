// Lifecycle states of an RTMP TCP connection and the reasons a connection can close. Both are low-cardinality and
// are used as log properties and metric dimensions.

namespace RTSP.IngestService.Rtmp;

/// <summary>The lifecycle state of an RTMP connection.</summary>
/// <remarks>
/// <c>Accepted → Handshaking → Connected ⇄ Publishing → Closing → Closed | Failed</c>. Any state before
/// <see cref="Closing"/> can move to <see cref="Closing"/>.
/// </remarks>
public enum RtmpConnectionState
{
    /// <summary>The socket was accepted and admitted under the connection limit.</summary>
    Accepted,

    /// <summary>The RTMP handshake is in progress.</summary>
    Handshaking,

    /// <summary>The handshake completed; RTMP messages are being exchanged and no stream is publishing.</summary>
    Connected,

    /// <summary>At least one message stream on the connection is publishing.</summary>
    Publishing,

    /// <summary>The connection is ending its sessions and releasing resources.</summary>
    Closing,

    /// <summary>The connection ended normally: client disconnect, rejection by the server or server shutdown.</summary>
    Closed,

    /// <summary>The connection ended because of a protocol violation, timeout, transport failure or internal error.</summary>
    Failed,
}

/// <summary>Why an RTMP connection closed.</summary>
public enum RtmpCloseReason
{
    /// <summary>The client closed or reset the connection.</summary>
    ClientDisconnected,

    /// <summary>The server closed the connection after rejecting <c>connect</c> or <c>publish</c>.</summary>
    Rejected,

    /// <summary>The service is shutting down.</summary>
    ServerShutdown,

    /// <summary>The handshake did not complete within the handshake timeout.</summary>
    HandshakeTimeout,

    /// <summary>The handshake data was invalid or unsupported.</summary>
    HandshakeFailed,

    /// <summary>No data arrived, or a write did not complete, within the connection timeout.</summary>
    IdleTimeout,

    /// <summary>The client violated the RTMP protocol or exceeded a protocol limit.</summary>
    ProtocolError,

    /// <summary>The socket failed for a reason other than a reset by the client.</summary>
    TransportError,

    /// <summary>The publish session handler or authenticator failed.</summary>
    HandlerFailed,

    /// <summary>An unexpected server error.</summary>
    InternalError,
}
