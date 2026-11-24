// Value types of the media model: codecs, tracks, sample timestamps, timestamp discontinuities and media-processing
// errors. Timestamps are milliseconds on the publisher's media timeline; decode and presentation time are distinct,
// and the RTMP timestamp the publisher sent is always kept alongside them.

namespace RTSP.IngestService.Media;

/// <summary>A media track of a published stream.</summary>
public enum MediaTrack
{
    /// <summary>The video track.</summary>
    Video,

    /// <summary>The audio track.</summary>
    Audio,
}

/// <summary>Supported video codecs.</summary>
public enum VideoCodec
{
    /// <summary>H.264 / AVC (FLV codec ID 7), carried as length-prefixed NAL units.</summary>
    H264,
}

/// <summary>Supported audio codecs.</summary>
public enum AudioCodec
{
    /// <summary>AAC (FLV sound format 10), carried as raw access units without ADTS headers.</summary>
    Aac,
}

/// <summary>Why a media packet was not delivered as a sample.</summary>
public enum MediaErrorKind
{
    /// <summary>The packet uses a codec or packet framing other than H.264 or AAC.</summary>
    UnsupportedCodec,

    /// <summary>The FLV packet header or the NAL unit framing is invalid or truncated.</summary>
    MalformedPacket,

    /// <summary>A sequence header carried an invalid codec configuration; the track's configuration was cleared.</summary>
    InvalidConfiguration,

    /// <summary>A coded sample arrived before a valid sequence header for its track.</summary>
    MissingConfiguration,

    /// <summary>The sample exceeds <see cref="Configuration.MediaOptions.MaxSampleSize"/>.</summary>
    SampleTooLarge,

    /// <summary>The access unit exceeds <see cref="Configuration.MediaOptions.MaxNalUnitsPerSample"/>.</summary>
    TooManyNalUnits,
}

/// <summary>How a sample's decode timestamp relates to the previous sample of the same track.</summary>
public enum TimestampDiscontinuity
{
    /// <summary>The first sample of the track, or a step from 0 up to the discontinuity threshold.</summary>
    None,

    /// <summary>The decode timestamp is earlier than the previous sample's.</summary>
    Regression,

    /// <summary>The decode timestamp is later than the previous sample's by more than the discontinuity threshold.</summary>
    Jump,
}

/// <summary>
/// Timing of one encoded sample, in milliseconds.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SourceTimestamp"/> is the 32-bit RTMP timestamp exactly as received. <see cref="DecodeTimestamp"/> is
/// the same instant on the publisher's 64-bit media timeline: RTMP timestamps are unwrapped across 2^32 rollover using
/// the signed 32-bit difference from the previous audio or video message of the session, so audio and video share
/// one clock and keep their relative timing. The timeline starts at the first message's RTMP timestamp; it is not
/// rebased to zero and discontinuities are not hidden.
/// </para>
/// <para>
/// <see cref="PresentationTimestamp"/> is <see cref="DecodeTimestamp"/> plus <see cref="CompositionTimeOffset"/>, the
/// signed 24-bit AVC composition time (zero for audio). Either timestamp can be negative after a timestamp regression
/// or a negative composition offset.
/// </para>
/// </remarks>
/// <param name="SourceTimestamp">The RTMP message timestamp.</param>
/// <param name="DecodeTimestamp">The decode timestamp on the session's media timeline.</param>
/// <param name="CompositionTimeOffset">The composition time offset: presentation minus decode time.</param>
public readonly record struct MediaTimestamps(uint SourceTimestamp, long DecodeTimestamp, int CompositionTimeOffset)
{
    /// <summary>Gets the presentation timestamp: <see cref="DecodeTimestamp"/> plus <see cref="CompositionTimeOffset"/>.</summary>
    public long PresentationTimestamp => DecodeTimestamp + CompositionTimeOffset;
}

/// <summary>
/// A media packet that was not delivered as a sample.
/// </summary>
/// <param name="Kind">Why it was not delivered.</param>
/// <param name="Track">The track the packet belongs to.</param>
/// <param name="SourceTimestamp">The packet's RTMP timestamp.</param>
/// <param name="Detail">A description of the problem. It never contains payload bytes or publisher-supplied text.</param>
public sealed record MediaProcessingError(MediaErrorKind Kind, MediaTrack Track, uint SourceTimestamp, string Detail);
