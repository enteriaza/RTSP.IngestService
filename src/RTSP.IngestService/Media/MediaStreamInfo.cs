// Stream-level media description: the current video and audio track configuration and the publisher's metadata.
// Codec configuration records are authoritative for codec properties; publisher metadata (onMetaData) is untrusted,
// range-checked and kept separately, and only fills in properties that no configuration record provides.

using RTSP.IngestService.Rtmp.Amf;

namespace RTSP.IngestService.Media;

/// <summary>
/// Properties from the publisher's <c>onMetaData</c>, each range-checked; invalid or absent values are <see langword="null"/>.
/// </summary>
/// <remarks>
/// Metadata is supplied by the publisher and is not verified against the bitstream. Codec properties (sample rate,
/// channels, profile) must be taken from the track configuration, not from here.
/// </remarks>
public sealed record PublisherMetadata
{
    /// <summary>Longest encoder string kept.</summary>
    public const int MaxEncoderLength = 128;

    /// <summary>Gets metadata with no values.</summary>
    public static PublisherMetadata Empty { get; } = new();

    /// <summary>Gets the declared video width in pixels (1–16384).</summary>
    public int? Width { get; init; }

    /// <summary>Gets the declared video height in pixels (1–16384).</summary>
    public int? Height { get; init; }

    /// <summary>Gets the declared frame rate (greater than 0, at most 1000).</summary>
    public double? FrameRate { get; init; }

    /// <summary>Gets the declared FLV video codec ID (0–255), when sent as a number.</summary>
    public int? VideoCodecId { get; init; }

    /// <summary>Gets the declared FLV sound format (0–255), when sent as a number.</summary>
    public int? AudioCodecId { get; init; }

    /// <summary>Gets the declared audio sample rate in hertz (1–384000).</summary>
    public int? AudioSampleRate { get; init; }

    /// <summary>Gets the declared audio channel count (1–64), from <c>audiochannels</c> or else <c>stereo</c>.</summary>
    public int? AudioChannels { get; init; }

    /// <summary>Gets the declared duration (0 to 10 years); live publishers usually send 0.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Gets the declared video bit rate in kbit/s (0–1000000).</summary>
    public double? VideoDataRate { get; init; }

    /// <summary>Gets the declared audio bit rate in kbit/s (0–1000000).</summary>
    public double? AudioDataRate { get; init; }

    /// <summary>Gets the declared encoder name: printable ASCII, at most <see cref="MaxEncoderLength"/> characters.</summary>
    public string? Encoder { get; init; }

    /// <summary>Extracts and range-checks known properties; unknown properties are ignored.</summary>
    /// <param name="properties">The <c>onMetaData</c> properties.</param>
    /// <returns>The metadata.</returns>
    internal static PublisherMetadata FromProperties(IReadOnlyList<KeyValuePair<string, AmfValue>> properties)
    {
        double? Number(string name) =>
            Find(properties, name) is { Kind: AmfValueKind.Number } value && double.IsFinite(value.NumberValue) ? value.NumberValue : null;

        int? Integer(string name, int min, int max) =>
            Number(name) is { } number && number == Math.Floor(number) && number >= min && number <= max ? (int)number : null;

        double? Bounded(string name, double minExclusive, double max) =>
            Number(name) is { } number && number > minExclusive && number <= max ? number : null;

        var channels = Integer("audiochannels", 1, 64);
        if (channels is null && Find(properties, "stereo") is { Kind: AmfValueKind.Boolean } stereo)
        {
            channels = stereo.BooleanValue ? 2 : 1;
        }

        var encoder = Find(properties, "encoder") is { Kind: AmfValueKind.Text } text
            && text.StringValue.Length is > 0 and <= MaxEncoderLength
            && text.StringValue.All(static c => c is >= ' ' and <= '~')
                ? text.StringValue
                : null;

        return new PublisherMetadata
        {
            Width = Integer("width", 1, 16384),
            Height = Integer("height", 1, 16384),
            FrameRate = Bounded("framerate", 0, 1000),
            VideoCodecId = Integer("videocodecid", 0, 255),
            AudioCodecId = Integer("audiocodecid", 0, 255),
            AudioSampleRate = Integer("audiosamplerate", 1, 384000),
            AudioChannels = channels,
            Duration = Bounded("duration", -1, 10 * 365.25 * 86400) is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            VideoDataRate = Bounded("videodatarate", -1, 1_000_000),
            AudioDataRate = Bounded("audiodatarate", -1, 1_000_000),
            Encoder = encoder,
        };
    }

    /// <summary>Finds the first property named <paramref name="name"/>.</summary>
    /// <param name="properties">The properties.</param>
    /// <param name="name">The name.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    private static AmfValue? Find(IReadOnlyList<KeyValuePair<string, AmfValue>> properties, string name)
    {
        foreach (var property in properties)
        {
            if (string.Equals(property.Key, name, StringComparison.Ordinal))
            {
                return property.Value;
            }
        }

        return null;
    }
}

/// <summary>The configured H.264 video track.</summary>
public sealed record VideoTrackInfo
{
    /// <summary>Gets the codec.</summary>
    public VideoCodec Codec { get; init; } = VideoCodec.H264;

    /// <summary>Gets the decoder configuration from the most recent valid sequence header.</summary>
    public required AvcDecoderConfiguration Configuration { get; init; }

    /// <summary>Gets the NAL unit length-prefix size from <see cref="Configuration"/>.</summary>
    public int NalLengthSize => Configuration.NalLengthSize;

    /// <summary>Gets how many distinct configurations the track has had; 1 for the first.</summary>
    public int ConfigurationVersion { get; init; }

    /// <summary>Gets the width declared in publisher metadata, if any; it is not read from the SPS.</summary>
    public int? Width { get; init; }

    /// <summary>Gets the height declared in publisher metadata, if any; it is not read from the SPS.</summary>
    public int? Height { get; init; }

    /// <summary>Gets the frame rate declared in publisher metadata, if any.</summary>
    public double? FrameRate { get; init; }
}

/// <summary>The configured AAC audio track.</summary>
public sealed record AudioTrackInfo
{
    /// <summary>Gets the codec.</summary>
    public AudioCodec Codec { get; init; } = AudioCodec.Aac;

    /// <summary>Gets the decoder configuration from the most recent valid sequence header.</summary>
    public required AacAudioConfiguration Configuration { get; init; }

    /// <summary>Gets the audio object type from <see cref="Configuration"/>.</summary>
    public int AudioObjectType => Configuration.AudioObjectType;

    /// <summary>Gets the core sample rate from <see cref="Configuration"/>.</summary>
    public int SampleRate => Configuration.SampleRate;

    /// <summary>Gets the channel configuration from <see cref="Configuration"/>.</summary>
    public int ChannelConfiguration => Configuration.ChannelConfiguration;

    /// <summary>Gets the channel count from <see cref="Configuration"/>, when derivable.</summary>
    public int? ChannelCount => Configuration.ChannelCount;

    /// <summary>Gets how many distinct configurations the track has had; 1 for the first.</summary>
    public int ConfigurationVersion { get; init; }
}

/// <summary>
/// A snapshot of a published stream's media description. A new instance replaces the previous one whenever a track
/// configuration or the metadata changes.
/// </summary>
public sealed record MediaStreamInfo
{
    /// <summary>Gets a description with no tracks and no metadata.</summary>
    public static MediaStreamInfo Empty { get; } = new();

    /// <summary>Gets the video track, or <see langword="null"/> until a valid AVC sequence header arrives.</summary>
    public VideoTrackInfo? Video { get; init; }

    /// <summary>Gets the audio track, or <see langword="null"/> until a valid AAC sequence header arrives.</summary>
    public AudioTrackInfo? Audio { get; init; }

    /// <summary>Gets the most recent publisher metadata.</summary>
    public PublisherMetadata Metadata { get; init; } = PublisherMetadata.Empty;

    /// <summary>Gets a counter incremented on every change; 0 for the initial empty description.</summary>
    public int Version { get; init; }
}
