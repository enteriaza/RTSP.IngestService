// Validation of source audio configurations: the AAC AudioSpecificConfig, the Opus identification header (RFC 7845
// 5.1), the FLAC STREAMINFO block (RFC 9639 8.2) and, for codecs without a sequence header, the first frame header
// (MPEG audio layer III, AC-3 and E-AC-3 sync frames). Only the sample rate and channel count are extracted; the
// transcoder's decoder validates the coded data.

using System.Buffers.Binary;
using RTSP.IngestService.Media;

namespace RTSP.IngestService.Transcoding.Codecs;

/// <summary>Parses source audio configurations.</summary>
internal static class SourceAudioConfigurations
{
    /// <summary>Largest accepted configuration record, in bytes.</summary>
    public const int MaxRecordSize = 4096;

    /// <summary>Whether the codec's configuration comes from a sequence header (rather than from its first frame).</summary>
    /// <param name="codec">The codec.</param>
    /// <returns><see langword="true"/> for AAC, Opus and FLAC.</returns>
    public static bool HasSequenceHeader(SourceAudioCodec codec) => codec is SourceAudioCodec.Aac or SourceAudioCodec.Opus or SourceAudioCodec.Flac;

    /// <summary>Parses a sequence header.</summary>
    /// <param name="codec">The codec; one with <see cref="HasSequenceHeader"/>.</param>
    /// <param name="record">The sequence header body.</param>
    /// <param name="configuration">The configuration when valid.</param>
    /// <param name="error">Why it is invalid.</param>
    /// <returns><see langword="true"/> if valid.</returns>
    public static bool TryParseSequenceHeader(SourceAudioCodec codec, ReadOnlySpan<byte> record, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SourceAudioConfiguration? configuration, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        configuration = null;
        if (record.Length > MaxRecordSize)
        {
            error = $"The {codec} sequence header is {record.Length} bytes; at most {MaxRecordSize} are accepted.";
            return false;
        }

        switch (codec)
        {
            case SourceAudioCodec.Aac:
                if (!AacAudioConfiguration.TryParse(record, out var aac, out error))
                {
                    return false;
                }

                var channels = aac.ChannelConfiguration switch { 7 => 8, >= 1 and <= 6 => aac.ChannelConfiguration, _ => 0 };
                if (channels == 0)
                {
                    error = $"AAC channel configuration {aac.ChannelConfiguration} is not supported; 1 to 7 are.";
                    return false;
                }

                configuration = new SourceAudioConfiguration(codec, record.ToArray(), aac.SampleRate, channels, $"{aac.CodecString} {aac.SampleRate} Hz {channels} ch");
                return true;
            case SourceAudioCodec.Opus:
                return TryParseOpus(record, out configuration, out error);
            case SourceAudioCodec.Flac:
                return TryParseFlac(record, out configuration, out error);
            default:
                error = $"{codec} has no sequence header.";
                return false;
        }
    }

    /// <summary>Reads the configuration of a codec without a sequence header from its first frame.</summary>
    /// <param name="codec">MP3, AC-3 or E-AC-3.</param>
    /// <param name="frame">The coded data.</param>
    /// <param name="configuration">The configuration when valid.</param>
    /// <param name="error">Why it is invalid.</param>
    /// <returns><see langword="true"/> if the data starts with a valid frame header.</returns>
    public static bool TryParseFrame(SourceAudioCodec codec, ReadOnlySpan<byte> frame, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SourceAudioConfiguration? configuration, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error) => codec switch
    {
        SourceAudioCodec.Mp3 => TryParseMp3(frame, out configuration, out error),
        SourceAudioCodec.Ac3 or SourceAudioCodec.Eac3 => TryParseAc3(codec, frame, out configuration, out error),
        _ => Fail($"{codec} is configured by a sequence header.", out configuration, out error),
    };

    /// <summary>Parses an Opus identification header.</summary>
    /// <param name="record">The header.</param>
    /// <param name="configuration">The configuration.</param>
    /// <param name="error">Why it is invalid.</param>
    /// <returns><see langword="true"/> if valid.</returns>
    private static bool TryParseOpus(ReadOnlySpan<byte> record, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SourceAudioConfiguration? configuration, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        if (record.IsEmpty)
        {
            return Fail("An empty Opus sequence start (no identification header) is not supported.", out configuration, out error);
        }

        if (record.Length < 19 || !record[..8].SequenceEqual("OpusHead"u8))
        {
            return Fail("The Opus sequence start is not an OpusHead identification header.", out configuration, out error);
        }

        if (record[8] >> 4 != 0)
        {
            return Fail($"Opus identification header version {record[8]} is not supported.", out configuration, out error);
        }

        int channels = record[9];
        var family = record[18];
        if (channels == 0 || (family == 0 && channels > 2) || (family != 0 && record.Length < 21 + channels))
        {
            return Fail($"The Opus identification header declares {channels} channels with mapping family {family}, which is inconsistent.", out configuration, out error);
        }

        var inputRate = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        configuration = new SourceAudioConfiguration(SourceAudioCodec.Opus, record.ToArray(), 48000, channels, $"opus {channels} ch (input {inputRate} Hz)");
        error = null;
        return true;
    }

    /// <summary>Parses a FLAC STREAMINFO block, raw (34 bytes) or preceded by the <c>fLaC</c> marker and a block header.</summary>
    /// <param name="record">The sequence header.</param>
    /// <param name="configuration">The configuration.</param>
    /// <param name="error">Why it is invalid.</param>
    /// <returns><see langword="true"/> if valid.</returns>
    private static bool TryParseFlac(ReadOnlySpan<byte> record, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SourceAudioConfiguration? configuration, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        const int StreamInfoLength = 34;
        var info = record;
        if (record.Length >= 4 && record[..4].SequenceEqual("fLaC"u8))
        {
            if (record.Length < 8 + StreamInfoLength || (record[4] & 0x7F) != 0 || ((record[5] << 16) | (record[6] << 8) | record[7]) != StreamInfoLength)
            {
                return Fail("The FLAC sequence start does not begin with a STREAMINFO block.", out configuration, out error);
            }

            info = record.Slice(8, StreamInfoLength);
        }
        else if (record.Length != StreamInfoLength)
        {
            return Fail($"The FLAC sequence start is {record.Length} bytes; a 34-byte STREAMINFO block is required.", out configuration, out error);
        }

        var sampleRate = (info[10] << 12) | (info[11] << 4) | (info[12] >> 4);
        var channels = ((info[12] >> 1) & 0x07) + 1;
        var bits = (((info[12] & 0x01) << 4) | (info[13] >> 4)) + 1;
        if (sampleRate is < 1 or > 655350 || bits < 4)
        {
            return Fail("The FLAC STREAMINFO block has an invalid sample rate or sample size.", out configuration, out error);
        }

        configuration = new SourceAudioConfiguration(SourceAudioCodec.Flac, record.ToArray(), sampleRate, channels, $"flac {sampleRate} Hz {channels} ch {bits}-bit");
        error = null;
        return true;
    }

    /// <summary>Parses an MPEG audio layer III frame header.</summary>
    /// <param name="frame">The data.</param>
    /// <param name="configuration">The configuration.</param>
    /// <param name="error">Why it is invalid.</param>
    /// <returns><see langword="true"/> if valid.</returns>
    private static bool TryParseMp3(ReadOnlySpan<byte> frame, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SourceAudioConfiguration? configuration, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        if (frame.Length < 4 || frame[0] != 0xFF || (frame[1] & 0xE0) != 0xE0)
        {
            return Fail("The MP3 data does not start with an MPEG audio frame sync word.", out configuration, out error);
        }

        var version = (frame[1] >> 3) & 0x03;
        var layer = (frame[1] >> 1) & 0x03;
        var bitrate = frame[2] >> 4;
        var rateIndex = (frame[2] >> 2) & 0x03;
        if (version == 1 || layer != 1 || bitrate is 0 or 15 || rateIndex == 3)
        {
            return Fail("The MPEG audio frame header is not a valid layer III header (reserved version, layer, bitrate or sample rate).", out configuration, out error);
        }

        int[] rates = version switch { 3 => [44100, 48000, 32000], 2 => [22050, 24000, 16000], _ => [11025, 12000, 8000] };
        var sampleRate = rates[rateIndex];
        var channels = (frame[3] >> 6) == 3 ? 1 : 2;
        configuration = new SourceAudioConfiguration(SourceAudioCodec.Mp3, [], sampleRate, channels, $"mp3 {sampleRate} Hz {channels} ch");
        error = null;
        return true;
    }

    /// <summary>Parses an AC-3 or E-AC-3 sync frame header (ATSC A/52 5.3 and E.1.2).</summary>
    /// <param name="codec">AC-3 or E-AC-3.</param>
    /// <param name="frame">The data.</param>
    /// <param name="configuration">The configuration.</param>
    /// <param name="error">Why it is invalid.</param>
    /// <returns><see langword="true"/> if valid.</returns>
    private static bool TryParseAc3(SourceAudioCodec codec, ReadOnlySpan<byte> frame, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SourceAudioConfiguration? configuration, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        ReadOnlySpan<int> channelsByMode = [2, 1, 2, 3, 3, 4, 4, 5];
        if (frame.Length < 7 || frame[0] != 0x0B || frame[1] != 0x77)
        {
            return Fail($"The {codec} data does not start with a sync frame.", out configuration, out error);
        }

        var reader = new BitReader(frame[2..]);
        int sampleRate;
        uint mode;
        uint lfe;
        if (codec == SourceAudioCodec.Ac3)
        {
            if (!reader.TrySkip(16) || !reader.TryRead(2, out var fscod) || !reader.TryRead(6, out var frmsizecod)
                || !reader.TryRead(5, out var bsid) || !reader.TrySkip(3) || !reader.TryRead(3, out mode))
            {
                return Fail("The AC-3 sync frame header is truncated.", out configuration, out error);
            }

            if (fscod == 3 || frmsizecod > 37 || bsid > 10)
            {
                return Fail("The AC-3 sync frame header has a reserved sample rate, frame size or bitstream identification.", out configuration, out error);
            }

            if (((mode & 1) != 0 && mode != 1 && !reader.TrySkip(2)) || ((mode & 4) != 0 && !reader.TrySkip(2))
                || (mode == 2 && !reader.TrySkip(2)) || !reader.TryRead(1, out lfe))
            {
                return Fail("The AC-3 bitstream information is truncated.", out configuration, out error);
            }

            sampleRate = fscod switch { 0 => 48000, 1 => 44100, _ => 32000 };
        }
        else
        {
            if (!reader.TrySkip(2 + 3 + 11) || !reader.TryRead(2, out var fscod) || !reader.TryRead(2, out var fscod2)
                || !reader.TryRead(3, out mode) || !reader.TryRead(1, out lfe) || !reader.TryRead(5, out var bsid))
            {
                return Fail("The E-AC-3 sync frame header is truncated.", out configuration, out error);
            }

            if (bsid is < 11 or > 16 || (fscod == 3 && fscod2 == 3))
            {
                return Fail("The E-AC-3 sync frame header has a reserved sample rate or bitstream identification.", out configuration, out error);
            }

            sampleRate = fscod switch { 0 => 48000, 1 => 44100, 2 => 32000, _ => fscod2 switch { 0 => 24000, 1 => 22050, _ => 16000 } };
        }

        var channels = channelsByMode[(int)mode] + (int)lfe;
        var name = codec == SourceAudioCodec.Ac3 ? "ac-3" : "e-ac-3";
        configuration = new SourceAudioConfiguration(codec, [], sampleRate, channels, $"{name} {sampleRate} Hz {channels} ch");
        error = null;
        return true;
    }

    /// <summary>Returns a failure.</summary>
    /// <param name="message">The reason.</param>
    /// <param name="configuration">Set to <see langword="null"/>.</param>
    /// <param name="error">Set to <paramref name="message"/>.</param>
    /// <returns><see langword="false"/>.</returns>
    private static bool Fail(string message, out SourceAudioConfiguration? configuration, out string error)
    {
        configuration = null;
        error = message;
        return false;
    }
}
