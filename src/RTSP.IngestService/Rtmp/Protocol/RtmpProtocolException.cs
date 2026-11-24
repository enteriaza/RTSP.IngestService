// Exceptions raised when a peer violates the RTMP handshake, chunk, message or AMF0 format, or exceeds a configured
// protocol limit. The owning connection catches them and closes only that connection; they never reach the listener
// or the host.

namespace RTSP.IngestService.Rtmp.Protocol;

/// <summary>
/// The peer sent data that violates the RTMP protocol or exceeds a protocol limit. The message describes the
/// violation and never contains payload data.
/// </summary>
public class RtmpProtocolException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="RtmpProtocolException"/> class with a generic message.</summary>
    public RtmpProtocolException()
        : base("The peer violated the RTMP protocol.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RtmpProtocolException"/> class.</summary>
    /// <param name="message">Describes the violation.</param>
    public RtmpProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RtmpProtocolException"/> class.</summary>
    /// <param name="message">Describes the violation.</param>
    /// <param name="innerException">The exception that exposed the violation.</param>
    public RtmpProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The RTMP handshake failed: unsupported version or a C2 packet that does not echo S1.
/// </summary>
public sealed class RtmpHandshakeException : RtmpProtocolException
{
    /// <summary>Initializes a new instance of the <see cref="RtmpHandshakeException"/> class with a generic message.</summary>
    public RtmpHandshakeException()
        : base("The RTMP handshake failed.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RtmpHandshakeException"/> class.</summary>
    /// <param name="message">Describes the failure.</param>
    public RtmpHandshakeException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RtmpHandshakeException"/> class.</summary>
    /// <param name="message">Describes the failure.</param>
    /// <param name="innerException">The exception that exposed the failure.</param>
    public RtmpHandshakeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
