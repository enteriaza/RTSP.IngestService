// Parser and model for the MPEG-4 AudioSpecificConfig (ISO/IEC 14496-3, section 1.6.2.1) carried by FLV AAC
// sequence headers. It reads the audio object type (including the escape value and explicit SBR/PS signalling), the
// sampling frequency and the channel configuration with a bounds-checked bit reader. The audio itself is not decoded.

namespace RTSP.IngestService.Media;

/// <summary>
/// An AAC decoder configuration.
/// </summary>
/// <remarks>
/// Immutable. <see cref="Record"/> is a private copy of the AudioSpecificConfig as received. Fields after the channel
/// configuration (GASpecificConfig, program config elements, backward-compatible extensions) are kept in
/// <see cref="Record"/> but not interpreted.
/// </remarks>
public sealed class AacAudioConfiguration
{
    /// <summary>Largest accepted AudioSpecificConfig, in bytes.</summary>
    public const int MaxRecordSize = 1024;

    /// <summary>Sampling frequencies by <c>samplingFrequencyIndex</c> 0–12.</summary>
    private static readonly int[] SamplingFrequencies = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    /// <summary>Initializes a new instance of the <see cref="AacAudioConfiguration"/> class.</summary>
    /// <param name="record">The copied record.</param>
    /// <param name="audioObjectType">The core audio object type.</param>
    /// <param name="samplingFrequencyIndex">The sampling frequency index.</param>
    /// <param name="sampleRate">The core sampling frequency.</param>
    /// <param name="channelConfiguration">The channel configuration.</param>
    /// <param name="extensionAudioObjectType">5 (SBR) or 29 (PS) when explicitly signalled.</param>
    /// <param name="extensionSampleRate">The SBR output sampling frequency when explicitly signalled.</param>
    private AacAudioConfiguration(byte[] record, int audioObjectType, int samplingFrequencyIndex, int sampleRate, int channelConfiguration, int? extensionAudioObjectType, int? extensionSampleRate)
    {
        Record = record;
        AudioObjectType = audioObjectType;
        SamplingFrequencyIndex = samplingFrequencyIndex;
        SampleRate = sampleRate;
        ChannelConfiguration = channelConfiguration;
        ExtensionAudioObjectType = extensionAudioObjectType;
        ExtensionSampleRate = extensionSampleRate;
    }

    /// <summary>Gets the core audio object type, for example 2 for AAC-LC.</summary>
    public int AudioObjectType { get; }

    /// <summary>Gets the sampling frequency index, or 15 when the frequency is given explicitly.</summary>
    public int SamplingFrequencyIndex { get; }

    /// <summary>Gets the core sampling frequency in hertz.</summary>
    public int SampleRate { get; }

    /// <summary>Gets the channel configuration (0 means the layout is defined by a program config element).</summary>
    public int ChannelConfiguration { get; }

    /// <summary>
    /// Gets the number of output channels implied by <see cref="ChannelConfiguration"/>, or <see langword="null"/>
    /// for configuration 0, whose layout is in a program config element that is not parsed.
    /// </summary>
    public int? ChannelCount => ChannelConfiguration switch
    {
        >= 1 and <= 6 => ChannelConfiguration,
        7 or 12 or 14 => 8,
        11 => 7,
        13 => 24,
        _ => null,
    };

    /// <summary>Gets 5 (SBR, HE-AAC) or 29 (PS, HE-AAC v2) when explicitly signalled; otherwise <see langword="null"/>.</summary>
    public int? ExtensionAudioObjectType { get; }

    /// <summary>Gets the SBR output sampling frequency when explicitly signalled; otherwise <see langword="null"/>.</summary>
    public int? ExtensionSampleRate { get; }

    /// <summary>
    /// Gets the number of core-rate samples per access unit, from the <c>frameLengthFlag</c> that starts the
    /// GASpecificConfig (1024 or 960) or the low-delay specific config of object types 23 and 39 (512 or 480), or
    /// <see langword="null"/> when the record ends before the flag.
    /// </summary>
    public int? FrameLength { get; private init; }

    /// <summary>Gets the complete AudioSpecificConfig as received.</summary>
    public ReadOnlyMemory<byte> Record { get; }

    /// <summary>Gets the RFC 6381 codec string, for example <c>mp4a.40.2</c>.</summary>
    public string CodecString => $"mp4a.40.{ExtensionAudioObjectType ?? AudioObjectType}";

    /// <summary>Parses and validates an AudioSpecificConfig.</summary>
    /// <param name="data">The config bytes (the FLV AAC sequence header body).</param>
    /// <param name="configuration">The configuration when valid.</param>
    /// <param name="error">Why the config is invalid, when it is.</param>
    /// <returns><see langword="true"/> if the config is valid.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AacAudioConfiguration? configuration, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        configuration = null;
        if (data.Length < 2)
        {
            error = $"The AAC AudioSpecificConfig is {data.Length} bytes; at least 2 are required.";
            return false;
        }

        if (data.Length > MaxRecordSize)
        {
            error = $"The AAC AudioSpecificConfig is {data.Length} bytes; at most {MaxRecordSize} are accepted.";
            return false;
        }

        const string Truncated = "The AAC AudioSpecificConfig is truncated.";
        var reader = new BitReader(data);
        if (!TryReadObjectType(ref reader, out var objectType))
        {
            error = Truncated;
            return false;
        }

        if (!TryReadFrequency(ref reader, out var frequencyIndex, out var sampleRate, out error))
        {
            return false;
        }

        if (!reader.TryRead(4, out var channelConfiguration))
        {
            error = Truncated;
            return false;
        }

        int? extensionType = null;
        int? extensionRate = null;
        if (objectType is 5 or 29)
        {
            extensionType = objectType;
            if (!TryReadFrequency(ref reader, out _, out var rate, out error))
            {
                return false;
            }

            if (!TryReadObjectType(ref reader, out objectType))
            {
                error = Truncated;
                return false;
            }

            extensionRate = rate;
        }

        if (!IsAacObjectType(objectType))
        {
            error = $"The AudioSpecificConfig declares audio object type {objectType}, which is not an AAC object type.";
            return false;
        }

        if (channelConfiguration is 8 or 9 or 10 or 15)
        {
            error = $"The AAC AudioSpecificConfig declares reserved channel configuration {channelConfiguration}.";
            return false;
        }

        int? frameLength = null;
        if (reader.TryRead(1, out var frameLengthFlag))
        {
            frameLength = objectType is 23 or 39
                ? (frameLengthFlag == 1 ? 480 : 512)
                : (frameLengthFlag == 1 ? 960 : 1024);
        }

        configuration = new AacAudioConfiguration(data.ToArray(), objectType, frequencyIndex, sampleRate, channelConfiguration, extensionType, extensionRate)
        {
            FrameLength = frameLength,
        };
        error = null;
        return true;
    }

    /// <summary>Whether <paramref name="data"/> is byte-for-byte this configuration's record.</summary>
    /// <param name="data">A config.</param>
    /// <returns><see langword="true"/> if identical.</returns>
    public bool HasRecord(ReadOnlySpan<byte> data) => data.SequenceEqual(Record.Span);

    /// <summary>
    /// Whether <paramref name="objectType"/> is an AAC object type: Main, LC, SSR, LTP, Scalable, or the error
    /// resilient LC, LTP, Scalable, LD and ELD types.
    /// </summary>
    /// <param name="objectType">The audio object type.</param>
    /// <returns><see langword="true"/> for AAC object types.</returns>
    private static bool IsAacObjectType(int objectType) => objectType is 1 or 2 or 3 or 4 or 6 or 17 or 19 or 20 or 23 or 39;

    /// <summary>Reads a 5-bit audio object type with its 6-bit escape.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="objectType">The object type.</param>
    /// <returns><see langword="false"/> if the data ends.</returns>
    private static bool TryReadObjectType(ref BitReader reader, out int objectType)
    {
        if (!reader.TryRead(5, out objectType))
        {
            return false;
        }

        if (objectType == 31)
        {
            if (!reader.TryRead(6, out var extended))
            {
                return false;
            }

            objectType = 32 + extended;
        }

        return true;
    }

    /// <summary>Reads a sampling frequency index and, for index 15, the explicit 24-bit frequency.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="index">The index.</param>
    /// <param name="sampleRate">The frequency in hertz.</param>
    /// <param name="error">Why reading failed.</param>
    /// <returns><see langword="false"/> if the data ends or the value is invalid.</returns>
    private static bool TryReadFrequency(ref BitReader reader, out int index, out int sampleRate, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        const string Truncated = "The AAC AudioSpecificConfig is truncated.";
        sampleRate = 0;
        error = null;
        if (!reader.TryRead(4, out index))
        {
            error = Truncated;
            return false;
        }

        if (index == 15)
        {
            if (!reader.TryRead(24, out sampleRate))
            {
                error = Truncated;
                return false;
            }

            if (sampleRate == 0)
            {
                error = "The AAC AudioSpecificConfig declares an explicit sampling frequency of 0 Hz.";
                return false;
            }

            return true;
        }

        if (index >= SamplingFrequencies.Length)
        {
            error = $"The AAC AudioSpecificConfig declares reserved sampling frequency index {index}.";
            return false;
        }

        sampleRate = SamplingFrequencies[index];
        return true;
    }

    /// <summary>Most-significant-bit-first reader over a span that fails instead of reading past the end.</summary>
    /// <param name="data">The data.</param>
    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        /// <summary>The data.</summary>
        private readonly ReadOnlySpan<byte> _data = data;

        /// <summary>Bits consumed.</summary>
        private int _bitPosition;

        /// <summary>Reads up to 24 bits.</summary>
        /// <param name="count">Number of bits, 1 to 24.</param>
        /// <param name="value">The value.</param>
        /// <returns><see langword="false"/> if fewer than <paramref name="count"/> bits remain.</returns>
        public bool TryRead(int count, out int value)
        {
            value = 0;
            if ((long)_bitPosition + count > (long)_data.Length * 8)
            {
                return false;
            }

            for (var i = 0; i < count; i++)
            {
                var bit = (_data[_bitPosition >> 3] >> (7 - (_bitPosition & 7))) & 1;
                value = (value << 1) | bit;
                _bitPosition++;
            }

            return true;
        }
    }
}
