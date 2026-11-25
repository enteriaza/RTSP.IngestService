// The publishing session model: one published stream on one RTMP message stream of one connection. It records the
// identity established by authentication, timestamps, the latest stream metadata, message counters and, once ended,
// the termination reason. The connection loop is the only writer; other threads may read it at any time.

using System.Net;
using RTSP.IngestService.Rtmp.Amf;

namespace RTSP.IngestService.Rtmp.Publishing;

/// <summary>The state of a <see cref="PublisherSession"/>.</summary>
public enum PublisherSessionState
{
    /// <summary>The stream is being published and messages are delivered.</summary>
    Publishing,

    /// <summary>The session has ended; see <see cref="PublisherSession.EndReason"/>.</summary>
    Ended,
}

/// <summary>Why a <see cref="PublisherSession"/> ended.</summary>
public enum PublishEndReason
{
    /// <summary>The publisher sent <c>closeStream</c>.</summary>
    Unpublished,

    /// <summary>The publisher sent <c>deleteStream</c> for the stream.</summary>
    StreamDeleted,

    /// <summary>The publisher closed or reset the TCP connection.</summary>
    ClientDisconnected,

    /// <summary>No data arrived from the publisher, or a write to it did not complete, within the connection timeout.</summary>
    ConnectionTimeout,

    /// <summary>The publisher violated the RTMP protocol or exceeded a protocol limit.</summary>
    ProtocolError,

    /// <summary>The TCP connection failed for another reason.</summary>
    ConnectionError,

    /// <summary>The service is shutting down.</summary>
    ServerShutdown,

    /// <summary>The <see cref="IPublishSessionHandler"/> threw.</summary>
    HandlerFailed,
}

/// <summary>
/// A stream being published by an RTMP client.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from the TCP connection (<see cref="ConnectionId"/>), the RTMP message stream
/// (<see cref="MessageStreamId"/>) and the application. One connection may publish several sessions over its
/// lifetime, one per message stream at a time.
/// </para>
/// <para>
/// Thread-safety: mutated only by the owning connection; all members may be read from any thread. Counters are read
/// atomically but are not mutually consistent snapshots.
/// </para>
/// </remarks>
public sealed class PublisherSession
{
    /// <summary>Guards <see cref="_endReason"/> and <see cref="_endedAt"/>.</summary>
    private readonly Lock _gate = new();

    /// <summary>Latest metadata properties.</summary>
    private IReadOnlyList<KeyValuePair<string, AmfValue>> _metadata = [];

    /// <summary>Audio messages received.</summary>
    private long _audioMessages;

    /// <summary>Video messages received.</summary>
    private long _videoMessages;

    /// <summary>Data messages received.</summary>
    private long _dataMessages;

    /// <summary>Payload bytes received across all kinds.</summary>
    private long _payloadBytes;

    /// <summary>Why the session ended, once it has.</summary>
    private PublishEndReason? _endReason;

    /// <summary>When the session ended, once it has.</summary>
    private DateTimeOffset? _endedAt;

    /// <summary>The registration generation; see <see cref="Generation"/>.</summary>
    private long _generation;

    /// <summary>1 once an HLS stream was opened for this publication; see <see cref="HlsStreamOpened"/>.</summary>
    private int _hlsStreamOpened;

    /// <summary>Initializes a new instance of the <see cref="PublisherSession"/> class in the <see cref="PublisherSessionState.Publishing"/> state.</summary>
    /// <param name="connectionId">The owning connection's identifier.</param>
    /// <param name="remoteEndPoint">The publisher's address.</param>
    /// <param name="application">The RTMP application name from <c>connect</c>.</param>
    /// <param name="streamIdentity">The non-secret stream identity returned by authentication.</param>
    /// <param name="publishType">The publishing type from <c>publish</c> (<c>live</c>, <c>record</c> or <c>append</c>).</param>
    /// <param name="messageStreamId">The RTMP message stream carrying the stream.</param>
    /// <param name="connectedAt">When the TCP connection was accepted.</param>
    /// <param name="startedAt">When publishing started.</param>
    internal PublisherSession(
        long connectionId,
        EndPoint? remoteEndPoint,
        string application,
        string streamIdentity,
        string publishType,
        uint messageStreamId,
        DateTimeOffset connectedAt,
        DateTimeOffset startedAt)
    {
        SessionId = Guid.CreateVersion7(startedAt);
        IncarnationId = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        ConnectionId = connectionId;
        RemoteEndPoint = remoteEndPoint;
        Application = application;
        StreamIdentity = streamIdentity;
        PublishType = publishType;
        MessageStreamId = messageStreamId;
        ConnectedAt = connectedAt;
        StartedAt = startedAt;
    }

    /// <summary>Gets the unique, time-ordered session identifier.</summary>
    public Guid SessionId { get; }

    /// <summary>
    /// Gets the public identifier of this publication, used by stream lifecycle events: 32 lowercase hexadecimal
    /// digits (128 random bits), new for every session and independent of <see cref="SessionId"/>, the application and
    /// the stream identity.
    /// </summary>
    public string IncarnationId { get; }

    /// <summary>
    /// Gets the registration generation assigned by <see cref="PublishedStreamRegistry"/>: strictly increasing across
    /// registrations, so a later publication of the same stream always has a larger value; 0 until registered.
    /// </summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>Gets the identifier of the TCP connection carrying the session.</summary>
    public long ConnectionId { get; }

    /// <summary>Gets the publisher's address, if known.</summary>
    public EndPoint? RemoteEndPoint { get; }

    /// <summary>Gets the RTMP application name, without any query string.</summary>
    public string Application { get; }

    /// <summary>
    /// Gets the stream identity assigned by the <see cref="IPublisherAuthenticator"/>. It is not the stream key the
    /// publisher sent, which is never stored on the session.
    /// </summary>
    public string StreamIdentity { get; }

    /// <summary>Gets the publishing type requested by the publisher.</summary>
    public string PublishType { get; }

    /// <summary>Gets the RTMP message stream ID carrying the stream.</summary>
    public uint MessageStreamId { get; }

    /// <summary>Gets when the TCP connection was accepted.</summary>
    public DateTimeOffset ConnectedAt { get; }

    /// <summary>Gets when publishing started.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Gets the session state.</summary>
    public PublisherSessionState State => EndReason is null ? PublisherSessionState.Publishing : PublisherSessionState.Ended;

    /// <summary>Gets why the session ended, or <see langword="null"/> while publishing.</summary>
    public PublishEndReason? EndReason
    {
        get
        {
            lock (_gate)
            {
                return _endReason;
            }
        }
    }

    /// <summary>Gets when the session ended, or <see langword="null"/> while publishing.</summary>
    public DateTimeOffset? EndedAt
    {
        get
        {
            lock (_gate)
            {
                return _endedAt;
            }
        }
    }

    /// <summary>
    /// Gets the properties of the most recent <c>onMetaData</c> (sent directly or through <c>@setDataFrame</c>),
    /// or an empty list if none has been received or it could not be parsed.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, AmfValue>> Metadata => Volatile.Read(ref _metadata);

    /// <summary>Gets the number of audio messages received.</summary>
    public long AudioMessageCount => Interlocked.Read(ref _audioMessages);

    /// <summary>Gets the number of video messages received.</summary>
    public long VideoMessageCount => Interlocked.Read(ref _videoMessages);

    /// <summary>Gets the number of data messages received.</summary>
    public long DataMessageCount => Interlocked.Read(ref _dataMessages);

    /// <summary>Gets the payload bytes received across audio, video and data messages.</summary>
    public long ReceivedPayloadBytes => Interlocked.Read(ref _payloadBytes);

    /// <summary>Counts a received message.</summary>
    /// <param name="kind">The message kind.</param>
    /// <param name="length">The payload length.</param>
    internal void RecordMessage(PublishedMessageKind kind, int length)
    {
        switch (kind)
        {
            case PublishedMessageKind.Audio:
                Interlocked.Increment(ref _audioMessages);
                break;
            case PublishedMessageKind.Video:
                Interlocked.Increment(ref _videoMessages);
                break;
            default:
                Interlocked.Increment(ref _dataMessages);
                break;
        }

        Interlocked.Add(ref _payloadBytes, length);
    }

    /// <summary>Replaces the stored metadata.</summary>
    /// <param name="metadata">The metadata properties.</param>
    internal void SetMetadata(IReadOnlyList<KeyValuePair<string, AmfValue>> metadata) => Volatile.Write(ref _metadata, metadata);

    /// <summary>Sets <see cref="Generation"/>; called by the registry before the session becomes visible.</summary>
    /// <param name="generation">The generation.</param>
    internal void SetGeneration(long generation) => Interlocked.Exchange(ref _generation, generation);

    /// <summary>
    /// Gets a value indicating whether the HLS stage opened a stream in the segment store for this publication. It
    /// never reverts, so it still holds after the store has withdrawn and forgotten the stream, which lets the stream
    /// catalog tell a drained HLS stream from one that never opened. It lives and dies with this session.
    /// </summary>
    internal bool HlsStreamOpened => Volatile.Read(ref _hlsStreamOpened) != 0;

    /// <summary>Records that the HLS stage opened this publication's stream; called after the store accepted it.</summary>
    internal void MarkHlsStreamOpened() => Volatile.Write(ref _hlsStreamOpened, 1);

    /// <summary>Ends the session if it has not already ended.</summary>
    /// <param name="reason">Why it ended.</param>
    /// <param name="endedAt">When it ended.</param>
    /// <returns><see langword="true"/> if this call ended the session.</returns>
    internal bool TryEnd(PublishEndReason reason, DateTimeOffset endedAt)
    {
        lock (_gate)
        {
            if (_endReason is not null)
            {
                return false;
            }

            _endReason = reason;
            _endedAt = endedAt;
            return true;
        }
    }
}
