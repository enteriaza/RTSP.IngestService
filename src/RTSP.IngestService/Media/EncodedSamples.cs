// Encoded media samples handed to downstream processing. Each sample takes ownership of the PublishedMessage that
// carried it and exposes slices of that message's pooled payload, so the encoded bitstream is delivered without
// copying and byte-for-byte as received. Disposing the sample returns the payload to the pool.

using RTSP.IngestService.Rtmp.Publishing;

namespace RTSP.IngestService.Media;

/// <summary>One NAL unit within an <see cref="EncodedVideoSample"/>.</summary>
/// <param name="Offset">Offset of the NAL unit (after its length prefix) within <see cref="EncodedSample.Data"/>.</param>
/// <param name="Length">Length of the NAL unit in bytes, excluding the length prefix.</param>
/// <param name="Type">The <c>nal_unit_type</c> (low 5 bits of the NAL header), for example 5 for an IDR slice.</param>
public readonly record struct NalUnit(int Offset, int Length, byte Type);

/// <summary>
/// Base class of encoded samples: ownership of the source payload and the sample's position on the media timeline.
/// </summary>
/// <remarks>
/// <para>
/// Ownership: the instance owns the pooled payload of the message that carried it. The consumer that receives a
/// sample must call <see cref="Dispose()"/> exactly once when it no longer needs the data, whether it handles the
/// sample synchronously or later. After disposal every data accessor throws <see cref="ObjectDisposedException"/>,
/// and memory obtained earlier must no longer be read. Copy the bytes to keep them longer.
/// </para>
/// <para>
/// <see cref="Dispose()"/> is thread-safe and idempotent. Reading data concurrently with disposal is not safe.
/// </para>
/// </remarks>
public abstract class EncodedSample : IDisposable
{
    /// <summary>The message owning the payload.</summary>
    private readonly PublishedMessage _source;

    /// <summary>Offset of <see cref="Data"/> within the message payload (the FLV packet header length).</summary>
    private readonly int _dataOffset;

    /// <summary>Initializes a new instance of the <see cref="EncodedSample"/> class, taking ownership of <paramref name="source"/>.</summary>
    /// <param name="source">The message whose payload holds the sample.</param>
    /// <param name="dataOffset">Length of the FLV packet header before the sample data.</param>
    /// <param name="timestamps">The sample timing.</param>
    /// <param name="sequenceNumber">The sample's position within its track.</param>
    /// <param name="discontinuity">How the decode timestamp relates to the previous sample of the track.</param>
    /// <param name="decodeTimestampDelta">Decode-time step from the previous sample of the track.</param>
    private protected EncodedSample(PublishedMessage source, int dataOffset, MediaTimestamps timestamps, long sequenceNumber, TimestampDiscontinuity discontinuity, long? decodeTimestampDelta)
    {
        _source = source;
        _dataOffset = dataOffset;
        Length = source.Length - dataOffset;
        Timestamps = timestamps;
        SequenceNumber = sequenceNumber;
        Discontinuity = discontinuity;
        DecodeTimestampDelta = decodeTimestampDelta;
    }

    /// <summary>Gets the sample timing.</summary>
    public MediaTimestamps Timestamps { get; }

    /// <summary>Gets the zero-based position of the sample among the delivered samples of its track.</summary>
    public long SequenceNumber { get; }

    /// <summary>Gets how the decode timestamp relates to the previous sample of the same track.</summary>
    public TimestampDiscontinuity Discontinuity { get; }

    /// <summary>Gets the decode-time step in milliseconds from the previous sample of the track; <see langword="null"/> for the first.</summary>
    public long? DecodeTimestampDelta { get; }

    /// <summary>Gets the length of <see cref="Data"/> in bytes.</summary>
    public int Length { get; }

    /// <summary>Gets the encoded sample bytes exactly as received, without the FLV packet header.</summary>
    /// <exception cref="ObjectDisposedException">The sample has been disposed.</exception>
    public ReadOnlyMemory<byte> Data => _source.Payload[_dataOffset..];

    /// <summary>Returns the payload to the pool. Subsequent calls do nothing.</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Returns the payload to the pool when <paramref name="disposing"/> is <see langword="true"/>.</summary>
    /// <param name="disposing">Whether the call comes from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source.Dispose();
        }
    }
}

/// <summary>
/// One H.264 access unit: the NAL units of one picture, length-prefixed as in the FLV packet (AVCC format).
/// </summary>
public sealed class EncodedVideoSample : EncodedSample
{
    /// <summary>The NAL units.</summary>
    private readonly NalUnit[] _nalUnits;

    /// <summary>Initializes a new instance of the <see cref="EncodedVideoSample"/> class, taking ownership of <paramref name="source"/>.</summary>
    /// <param name="source">The message whose payload holds the access unit.</param>
    /// <param name="track">The track configuration the sample was coded with.</param>
    /// <param name="isKeyframe">Whether the FLV frame type is keyframe.</param>
    /// <param name="nalUnits">The NAL units, validated against the payload.</param>
    /// <param name="timestamps">The sample timing.</param>
    /// <param name="sequenceNumber">The sample's position within the track.</param>
    /// <param name="discontinuity">The timestamp discontinuity classification.</param>
    /// <param name="decodeTimestampDelta">Decode-time step from the previous video sample.</param>
    internal EncodedVideoSample(PublishedMessage source, VideoTrackInfo track, bool isKeyframe, NalUnit[] nalUnits, MediaTimestamps timestamps, long sequenceNumber, TimestampDiscontinuity discontinuity, long? decodeTimestampDelta)
        : base(source, FlvMediaParser.VideoHeaderLength, timestamps, sequenceNumber, discontinuity, decodeTimestampDelta)
    {
        Track = track;
        IsKeyframe = isKeyframe;
        _nalUnits = nalUnits;
        ContainsIdr = nalUnits.Any(static n => n.Type == 5);
    }

    /// <summary>Gets the video track configuration in effect for this sample (its SPS/PPS and NAL length size).</summary>
    public VideoTrackInfo Track { get; }

    /// <summary>Gets a value indicating whether the publisher marked the sample as a keyframe (FLV frame type 1).</summary>
    public bool IsKeyframe { get; }

    /// <summary>Gets a value indicating whether the access unit contains an IDR slice (NAL unit type 5).</summary>
    public bool ContainsIdr { get; }

    /// <summary>Gets the NAL units, in order, as offsets into <see cref="EncodedSample.Data"/>.</summary>
    public IReadOnlyList<NalUnit> NalUnits => _nalUnits;

    /// <summary>Returns the bytes of NAL unit <paramref name="index"/>, without its length prefix.</summary>
    /// <param name="index">Index into <see cref="NalUnits"/>.</param>
    /// <returns>The NAL unit bytes.</returns>
    /// <exception cref="ObjectDisposedException">The sample has been disposed.</exception>
    public ReadOnlyMemory<byte> GetNalUnitData(int index)
    {
        var nal = _nalUnits[index];
        return Data.Slice(nal.Offset, nal.Length);
    }
}

/// <summary>
/// One raw AAC access unit (no ADTS header), exactly as carried in the FLV packet.
/// </summary>
public sealed class EncodedAudioSample : EncodedSample
{
    /// <summary>Initializes a new instance of the <see cref="EncodedAudioSample"/> class, taking ownership of <paramref name="source"/>.</summary>
    /// <param name="source">The message whose payload holds the access unit.</param>
    /// <param name="track">The track configuration the sample was coded with.</param>
    /// <param name="timestamps">The sample timing.</param>
    /// <param name="sequenceNumber">The sample's position within the track.</param>
    /// <param name="discontinuity">The timestamp discontinuity classification.</param>
    /// <param name="decodeTimestampDelta">Decode-time step from the previous audio sample.</param>
    internal EncodedAudioSample(PublishedMessage source, AudioTrackInfo track, MediaTimestamps timestamps, long sequenceNumber, TimestampDiscontinuity discontinuity, long? decodeTimestampDelta)
        : base(source, FlvMediaParser.AudioHeaderLength, timestamps, sequenceNumber, discontinuity, decodeTimestampDelta)
    {
        Track = track;
    }

    /// <summary>Gets the audio track configuration in effect for this sample.</summary>
    public AudioTrackInfo Track { get; }
}
