// The media view of one publishing session: the current stream description and counters for delivered samples,
// configurations, errors and timestamp discontinuities. The media processor is the only writer; any thread may read.

using RTSP.IngestService.Rtmp.Publishing;

namespace RTSP.IngestService.Media;

/// <summary>
/// Media state of one published stream, as seen by an <see cref="IMediaSessionHandler"/>.
/// </summary>
/// <remarks>
/// Created when publishing starts and discarded when it ends. Counters are read atomically but are not mutually
/// consistent snapshots.
/// </remarks>
public sealed class MediaSession
{
    /// <summary>The current stream description.</summary>
    private MediaStreamInfo _streamInfo = MediaStreamInfo.Empty;

    /// <summary>Counter.</summary>
    private long _videoSamples;

    /// <summary>Counter.</summary>
    private long _keyframes;

    /// <summary>Counter.</summary>
    private long _audioSamples;

    /// <summary>Counter.</summary>
    private long _configurations;

    /// <summary>Counter.</summary>
    private long _errors;

    /// <summary>Counter.</summary>
    private long _discontinuities;

    /// <summary>Initializes a new instance of the <see cref="MediaSession"/> class.</summary>
    /// <param name="publisher">The publishing session.</param>
    internal MediaSession(PublisherSession publisher)
    {
        Publisher = publisher;
    }

    /// <summary>Gets the publishing session (identity, application, lifecycle).</summary>
    public PublisherSession Publisher { get; }

    /// <summary>Gets the publishing session's identifier.</summary>
    public Guid SessionId => Publisher.SessionId;

    /// <summary>Gets the current stream description.</summary>
    public MediaStreamInfo StreamInfo => Volatile.Read(ref _streamInfo);

    /// <summary>Gets the number of video samples delivered.</summary>
    public long VideoSampleCount => Interlocked.Read(ref _videoSamples);

    /// <summary>Gets the number of delivered video samples marked as keyframes.</summary>
    public long KeyframeCount => Interlocked.Read(ref _keyframes);

    /// <summary>Gets the number of audio samples delivered.</summary>
    public long AudioSampleCount => Interlocked.Read(ref _audioSamples);

    /// <summary>Gets the number of distinct valid codec configurations received, across both tracks.</summary>
    public long ConfigurationCount => Interlocked.Read(ref _configurations);

    /// <summary>Gets the number of media packets dropped with a <see cref="MediaProcessingError"/>.</summary>
    public long ErrorCount => Interlocked.Read(ref _errors);

    /// <summary>Gets the number of delivered samples flagged with a timestamp discontinuity.</summary>
    public long DiscontinuityCount => Interlocked.Read(ref _discontinuities);

    /// <summary>Replaces the stream description.</summary>
    /// <param name="info">The new description.</param>
    internal void SetStreamInfo(MediaStreamInfo info) => Volatile.Write(ref _streamInfo, info);

    /// <summary>Counts a delivered sample.</summary>
    /// <param name="track">Its track.</param>
    /// <param name="keyframe">Whether it is a keyframe.</param>
    /// <param name="discontinuity">Its timestamp classification.</param>
    internal void RecordSample(MediaTrack track, bool keyframe, TimestampDiscontinuity discontinuity)
    {
        Interlocked.Increment(ref track == MediaTrack.Video ? ref _videoSamples : ref _audioSamples);
        if (keyframe)
        {
            Interlocked.Increment(ref _keyframes);
        }

        if (discontinuity != TimestampDiscontinuity.None)
        {
            Interlocked.Increment(ref _discontinuities);
        }
    }

    /// <summary>Counts a configuration.</summary>
    internal void RecordConfiguration() => Interlocked.Increment(ref _configurations);

    /// <summary>Counts an error.</summary>
    internal void RecordError() => Interlocked.Increment(ref _errors);
}
