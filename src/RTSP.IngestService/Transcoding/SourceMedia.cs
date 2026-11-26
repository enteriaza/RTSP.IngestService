// The publisher-side ("source") media model of the transcoding stage: which codec each track uses, the validated
// configuration needed to start a decoder, and the immutable configuration snapshot every queued sample refers to.
// Source media never reaches HLS packaging; only the transcoder's canonical output does.

using RTSP.IngestService.Media;
using RTSP.IngestService.Rtmp.Publishing;

namespace RTSP.IngestService.Transcoding;

/// <summary>A video codec accepted from publishers.</summary>
public enum SourceVideoCodec
{
    /// <summary>H.264 / AVC (legacy FLV CodecID 7 or E-RTMP <c>avc1</c>).</summary>
    H264,

    /// <summary>HEVC / H.265 (E-RTMP <c>hvc1</c>).</summary>
    Hevc,

    /// <summary>AV1 (E-RTMP <c>av01</c>).</summary>
    Av1,

    /// <summary>VP9 (E-RTMP <c>vp09</c>).</summary>
    Vp9,
}

/// <summary>An audio codec accepted from publishers.</summary>
public enum SourceAudioCodec
{
    /// <summary>AAC (legacy SoundFormat 10 or E-RTMP <c>mp4a</c>).</summary>
    Aac,

    /// <summary>MP3 (legacy SoundFormat 2/14 or E-RTMP <c>.mp3</c>).</summary>
    Mp3,

    /// <summary>Opus (E-RTMP <c>Opus</c>).</summary>
    Opus,

    /// <summary>FLAC (E-RTMP <c>fLaC</c>).</summary>
    Flac,

    /// <summary>AC-3 (E-RTMP <c>ac-3</c>).</summary>
    Ac3,

    /// <summary>E-AC-3 (E-RTMP <c>ec-3</c>).</summary>
    Eac3,
}

/// <summary>A validated source video configuration.</summary>
/// <param name="Codec">The codec.</param>
/// <param name="Record">A private copy of the configuration record (empty when the codec has none).</param>
/// <param name="NalLengthSize">For H.264/HEVC, the NAL length-prefix size; otherwise 0.</param>
/// <param name="Width">The coded picture width, when known from the configuration.</param>
/// <param name="Height">The coded picture height, when known from the configuration.</param>
/// <param name="BitDepth">The luma bit depth, when known.</param>
/// <param name="Description">A short description for logs, without publisher text (for example <c>hevc main10 L5.1</c>).</param>
internal sealed record SourceVideoConfiguration(SourceVideoCodec Codec, byte[] Record, int NalLengthSize, int? Width, int? Height, int? BitDepth, string Description)
{
    /// <summary>Whether <paramref name="record"/> is byte-for-byte this configuration's record.</summary>
    /// <param name="codec">The codec of the new record.</param>
    /// <param name="record">The record.</param>
    /// <returns><see langword="true"/> if identical.</returns>
    public bool IsSame(SourceVideoCodec codec, ReadOnlySpan<byte> record) => codec == Codec && record.SequenceEqual(Record);
}

/// <summary>A validated source audio configuration.</summary>
/// <param name="Codec">The codec.</param>
/// <param name="Record">A private copy of the configuration (AudioSpecificConfig, OpusHead, STREAMINFO; empty for MP3, AC-3, E-AC-3).</param>
/// <param name="SampleRate">The sample rate in Hz.</param>
/// <param name="Channels">The channel count.</param>
/// <param name="Description">A short description for logs.</param>
internal sealed record SourceAudioConfiguration(SourceAudioCodec Codec, byte[] Record, int SampleRate, int Channels, string Description)
{
    /// <summary>Whether this configuration equals another, comparing the record by content.</summary>
    /// <param name="other">The other configuration.</param>
    /// <returns><see langword="true"/> if equivalent.</returns>
    public bool IsSame(SourceAudioConfiguration other) =>
        other.Codec == Codec && other.SampleRate == SampleRate && other.Channels == Channels && other.Record.AsSpan().SequenceEqual(Record);
}

/// <summary>
/// The colour description declared by E-RTMP <c>colorInfo</c> metadata, as ITU-T H.273 code points. Only values the
/// transcoder can map are kept.
/// </summary>
/// <param name="Primaries">Colour primaries (1 BT.709, 9 BT.2020).</param>
/// <param name="Transfer">Transfer characteristics (1 BT.709, 16 PQ, 18 HLG).</param>
/// <param name="Matrix">Matrix coefficients (1 BT.709, 9 BT.2020 non-constant).</param>
internal sealed record VideoColorDescription(int Primaries, int Transfer, int Matrix)
{
    /// <summary>Gets a value indicating whether the description is BT.2100 PQ or HLG.</summary>
    public bool IsHighDynamicRange => Transfer is 16 or 18;
}

/// <summary>
/// The configuration a transcoder process is started with. A new instance is created only when a track's
/// configuration actually changes, so reference equality tells the session whether a restart is needed.
/// </summary>
/// <param name="Video">The video configuration, if the stream has video.</param>
/// <param name="Audio">The audio configuration, if the stream has audio.</param>
/// <param name="Color">The declared HDR colour description, if any.</param>
/// <param name="Version">Increments with every change.</param>
internal sealed record SourceStreamConfiguration(SourceVideoConfiguration? Video, SourceAudioConfiguration? Audio, VideoColorDescription? Color, int Version)
{
    /// <summary>Gets the empty configuration.</summary>
    public static SourceStreamConfiguration Empty { get; } = new(null, null, null, 0);
}

/// <summary>
/// One validated source access unit queued for the transcoder. It owns the <see cref="PublishedMessage"/> whose
/// payload holds the coded data, so nothing is copied; disposing the sample returns the payload to the pool.
/// </summary>
internal sealed class SourceSample : IDisposable
{
    /// <summary>Initializes a new instance of the <see cref="SourceSample"/> class, taking ownership of <paramref name="message"/>.</summary>
    /// <param name="message">The message carrying the data.</param>
    /// <param name="track">The track.</param>
    /// <param name="dataOffset">Offset of the coded data in the payload.</param>
    /// <param name="dataLength">Length of the coded data.</param>
    /// <param name="decodeTimestamp">Decode timestamp on the session timeline, in milliseconds.</param>
    /// <param name="compositionTimeOffset">Presentation minus decode time, in milliseconds.</param>
    /// <param name="isKeyframe">Whether the sample is a video keyframe.</param>
    /// <param name="discontinuity">The timestamp step classification from the previous sample of the track.</param>
    /// <param name="configuration">The configuration the sample belongs to.</param>
    public SourceSample(PublishedMessage message, MediaTrack track, int dataOffset, int dataLength, long decodeTimestamp, int compositionTimeOffset, bool isKeyframe, TimestampDiscontinuity discontinuity, SourceStreamConfiguration configuration)
    {
        Message = message;
        Track = track;
        DataOffset = dataOffset;
        DataLength = dataLength;
        DecodeTimestamp = decodeTimestamp;
        CompositionTimeOffset = compositionTimeOffset;
        IsKeyframe = isKeyframe;
        Discontinuity = discontinuity;
        Configuration = configuration;
    }

    /// <summary>Gets the message that owns the payload.</summary>
    public PublishedMessage Message { get; }

    /// <summary>Gets the track.</summary>
    public MediaTrack Track { get; }

    /// <summary>Gets the offset of the coded data in the payload.</summary>
    public int DataOffset { get; }

    /// <summary>Gets the coded data length.</summary>
    public int DataLength { get; }

    /// <summary>Gets the decode timestamp on the session timeline, in milliseconds.</summary>
    public long DecodeTimestamp { get; }

    /// <summary>Gets the composition time offset in milliseconds.</summary>
    public int CompositionTimeOffset { get; }

    /// <summary>Gets a value indicating whether the sample is a video keyframe.</summary>
    public bool IsKeyframe { get; }

    /// <summary>Gets the timestamp step classification.</summary>
    public TimestampDiscontinuity Discontinuity { get; }

    /// <summary>Gets the configuration the sample belongs to.</summary>
    public SourceStreamConfiguration Configuration { get; }

    /// <summary>Gets the coded data. Valid until <see cref="Dispose"/>.</summary>
    public ReadOnlyMemory<byte> Data => Message.Payload.Slice(DataOffset, DataLength);

    /// <summary>Returns the payload to the pool.</summary>
    public void Dispose() => Message.Dispose();
}
