// Track descriptions used by the MP4 writers: the H.264 and AAC sample-entry inputs derived from validated Phase 3
// configurations, and the per-fragment sample tables. Track IDs and timescales are fixed here so the initialization
// segment and every media fragment agree.

namespace RTSP.IngestService.Hls;

/// <summary>An H.264 track of the presentation.</summary>
/// <param name="Width">Cropped coded width from the SPS.</param>
/// <param name="Height">Cropped coded height from the SPS.</param>
/// <param name="ConfigurationRecord">The AVCDecoderConfigurationRecord, written verbatim as <c>avcC</c>.</param>
internal sealed record Mp4VideoTrack(int Width, int Height, ReadOnlyMemory<byte> ConfigurationRecord)
{
    /// <summary>Track ID of the video track.</summary>
    public const uint Id = 1;

    /// <summary>Media timescale: 90 kHz, so millisecond timestamps convert exactly (×90).</summary>
    public const int Timescale = 90000;
}

/// <summary>An AAC track of the presentation.</summary>
/// <param name="SampleRate">The core sampling frequency; also the media timescale.</param>
/// <param name="ChannelCount">The output channel count.</param>
/// <param name="AudioSpecificConfig">The AudioSpecificConfig, written verbatim in <c>esds</c>.</param>
/// <param name="FrameLength">Core-rate samples per access unit: the duration of every sample.</param>
internal sealed record Mp4AudioTrack(int SampleRate, int ChannelCount, ReadOnlyMemory<byte> AudioSpecificConfig, int FrameLength)
{
    /// <summary>Track ID of the audio track.</summary>
    public const uint Id = 2;
}

/// <summary>One sample of a track fragment.</summary>
/// <param name="Duration">Duration in the track timescale.</param>
/// <param name="Flags">ISO BMFF sample flags.</param>
/// <param name="CompositionOffset">Presentation minus decode time in the track timescale (video only).</param>
/// <param name="Data">The sample bytes, copied into <c>mdat</c> verbatim.</param>
internal readonly record struct Mp4Sample(uint Duration, uint Flags, int CompositionOffset, ReadOnlyMemory<byte> Data)
{
    /// <summary>Flags of a sync sample: depends on no other sample.</summary>
    public const uint SyncFlags = 0x02000000;

    /// <summary>Flags of a non-sync sample: depends on others and <c>sample_is_non_sync_sample</c> set.</summary>
    public const uint NonSyncFlags = 0x01010000;
}

/// <summary>The samples of one track in one fragment.</summary>
/// <param name="TrackId">The track ID.</param>
/// <param name="BaseDecodeTime">The <c>tfdt</c> decode time of the first sample, in the track timescale.</param>
/// <param name="IsVideo">Whether the run carries per-sample flags and composition offsets.</param>
/// <param name="Samples">The samples in decode order.</param>
internal sealed record Mp4TrackFragment(uint TrackId, ulong BaseDecodeTime, bool IsVideo, IReadOnlyList<Mp4Sample> Samples);
