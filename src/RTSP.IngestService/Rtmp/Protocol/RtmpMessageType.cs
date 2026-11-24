// RTMP message type identifiers (RTMP specification 1.0, sections 5.4, 6.2 and 7.1) and User Control event types
// (section 7.1.7). Values are the wire values.

namespace RTSP.IngestService.Rtmp.Protocol;

/// <summary>RTMP message type identifiers carried in the chunk message header.</summary>
public enum RtmpMessageType
{
    /// <summary>Protocol control: the sender's new maximum chunk size.</summary>
    SetChunkSize = 1,

    /// <summary>Protocol control: discard the partially received message on a chunk stream.</summary>
    Abort = 2,

    /// <summary>Protocol control: the number of bytes received so far.</summary>
    Acknowledgement = 3,

    /// <summary>User Control events such as Stream Begin and ping.</summary>
    UserControl = 4,

    /// <summary>Protocol control: the window after which the receiver must acknowledge.</summary>
    WindowAcknowledgementSize = 5,

    /// <summary>Protocol control: limits the peer's output bandwidth.</summary>
    SetPeerBandwidth = 6,

    /// <summary>Audio data.</summary>
    Audio = 8,

    /// <summary>Video data.</summary>
    Video = 9,

    /// <summary>Data (metadata) message encoded with AMF3.</summary>
    DataAmf3 = 15,

    /// <summary>Shared object message encoded with AMF3 (not supported).</summary>
    SharedObjectAmf3 = 16,

    /// <summary>Command message encoded with AMF3.</summary>
    CommandAmf3 = 17,

    /// <summary>Data (metadata) message encoded with AMF0.</summary>
    DataAmf0 = 18,

    /// <summary>Shared object message encoded with AMF0 (not supported).</summary>
    SharedObjectAmf0 = 19,

    /// <summary>Command message encoded with AMF0.</summary>
    CommandAmf0 = 20,

    /// <summary>Aggregate of audio, video and data sub-messages (not supported).</summary>
    Aggregate = 22,
}

/// <summary>User Control message event types.</summary>
public enum UserControlEventType
{
    /// <summary>The stream is functional and can be used for communication.</summary>
    StreamBegin = 0,

    /// <summary>Playback of the stream ended.</summary>
    StreamEof = 1,

    /// <summary>The stream has no more data.</summary>
    StreamDry = 2,

    /// <summary>The client's buffer length for a stream.</summary>
    SetBufferLength = 3,

    /// <summary>The stream is a recorded stream.</summary>
    StreamIsRecorded = 4,

    /// <summary>Ping request; the receiver answers with <see cref="PingResponse"/>.</summary>
    PingRequest = 6,

    /// <summary>Answer to <see cref="PingRequest"/>.</summary>
    PingResponse = 7,
}
