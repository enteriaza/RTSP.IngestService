// Parsing of the FLV audio and video tag bodies that RTMP carries as audio (type 8) and video (type 9) message
// payloads. An RTMP media message body is exactly an FLV tag body: no FLV tag header or PreviousTagSize, and the
// timestamp comes from the RTMP message header. Every length is checked against the payload before it is used.

using System.Buffers.Binary;

namespace RTSP.IngestService.Media;

/// <summary>What an FLV media packet carries.</summary>
internal enum FlvPacketKind
{
    /// <summary>A codec configuration (AVC or AAC sequence header).</summary>
    SequenceHeader,

    /// <summary>A coded sample (AVC NALUs or AAC raw access unit).</summary>
    CodedSample,

    /// <summary>An AVC end-of-sequence marker; carries no sample.</summary>
    EndOfSequence,

    /// <summary>An FLV video info/command frame (frame type 5); carries no sample.</summary>
    Command,
}

/// <summary>The parsed header of an FLV video or audio packet.</summary>
/// <param name="Kind">What the packet carries.</param>
/// <param name="IsKeyframe">For video, whether the frame type is keyframe (1).</param>
/// <param name="CompositionTimeOffset">For AVC coded samples, the signed 24-bit composition time; otherwise 0.</param>
internal readonly record struct FlvPacketHeader(FlvPacketKind Kind, bool IsKeyframe, int CompositionTimeOffset);

/// <summary>
/// Parses FLV video (H.264) and audio (AAC) packet headers and AVC NAL unit framing.
/// </summary>
/// <remarks>
/// Only the legacy FLV framing is supported: video codec ID 7 (AVC) and sound format 10 (AAC). Enhanced RTMP
/// (FourCC) video packets, which set the high bit of the first byte, and every other codec are reported as
/// <see cref="MediaErrorKind.UnsupportedCodec"/>.
/// </remarks>
internal static class FlvMediaParser
{
    /// <summary>Length of the AVC video packet header: frame type/codec, packet type, composition time.</summary>
    public const int VideoHeaderLength = 5;

    /// <summary>Length of the AAC audio packet header: sound format/flags, packet type.</summary>
    public const int AudioHeaderLength = 2;

    /// <summary>FLV video codec ID for AVC.</summary>
    private const int AvcCodecId = 7;

    /// <summary>FLV sound format for AAC.</summary>
    private const int AacSoundFormat = 10;

    /// <summary>Parses an FLV video packet header.</summary>
    /// <param name="payload">The RTMP video message payload.</param>
    /// <param name="header">The header when valid.</param>
    /// <param name="errorKind">The error kind when invalid.</param>
    /// <param name="error">Why the packet is invalid.</param>
    /// <returns><see langword="true"/> if the header is a valid AVC packet header or a command frame.</returns>
    public static bool TryParseVideoHeader(ReadOnlySpan<byte> payload, out FlvPacketHeader header, out MediaErrorKind errorKind, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        header = default;
        errorKind = MediaErrorKind.MalformedPacket;
        if (payload.IsEmpty)
        {
            error = "The video packet is empty.";
            return false;
        }

        var first = payload[0];
        if ((first & 0x80) != 0)
        {
            errorKind = MediaErrorKind.UnsupportedCodec;
            error = "Enhanced RTMP (FourCC) video packets are not supported.";
            return false;
        }

        var frameType = first >> 4;
        var codecId = first & 0x0F;
        if (frameType == 5)
        {
            header = new FlvPacketHeader(FlvPacketKind.Command, false, 0);
            error = null;
            return true;
        }

        if (codecId != AvcCodecId)
        {
            errorKind = MediaErrorKind.UnsupportedCodec;
            error = $"FLV video codec ID {codecId} is not supported; only 7 (AVC) is.";
            return false;
        }

        if (frameType is < 1 or > 3)
        {
            error = $"FLV video frame type {frameType} is not valid for a published AVC packet.";
            return false;
        }

        if (payload.Length < VideoHeaderLength)
        {
            error = $"The AVC packet is {payload.Length} bytes; the header needs {VideoHeaderLength}.";
            return false;
        }

        var kind = payload[1] switch
        {
            0 => FlvPacketKind.SequenceHeader,
            1 => FlvPacketKind.CodedSample,
            2 => FlvPacketKind.EndOfSequence,
            _ => (FlvPacketKind?)null,
        };
        if (kind is null)
        {
            error = $"AVC packet type {payload[1]} is not defined.";
            return false;
        }

        var composition = ((payload[2] << 16) | (payload[3] << 8) | payload[4]) << 8 >> 8;
        header = new FlvPacketHeader(kind.Value, frameType == 1, kind == FlvPacketKind.CodedSample ? composition : 0);
        error = null;
        return true;
    }

    /// <summary>Parses an FLV audio packet header.</summary>
    /// <param name="payload">The RTMP audio message payload.</param>
    /// <param name="header">The header when valid.</param>
    /// <param name="errorKind">The error kind when invalid.</param>
    /// <param name="error">Why the packet is invalid.</param>
    /// <returns><see langword="true"/> if the header is a valid AAC packet header.</returns>
    public static bool TryParseAudioHeader(ReadOnlySpan<byte> payload, out FlvPacketHeader header, out MediaErrorKind errorKind, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        header = default;
        errorKind = MediaErrorKind.MalformedPacket;
        if (payload.IsEmpty)
        {
            error = "The audio packet is empty.";
            return false;
        }

        var format = payload[0] >> 4;
        if (format != AacSoundFormat)
        {
            errorKind = MediaErrorKind.UnsupportedCodec;
            error = $"FLV sound format {format} is not supported; only 10 (AAC) is.";
            return false;
        }

        if (payload.Length < AudioHeaderLength)
        {
            error = "The AAC packet has no packet type.";
            return false;
        }

        switch (payload[1])
        {
            case 0:
                header = new FlvPacketHeader(FlvPacketKind.SequenceHeader, false, 0);
                break;
            case 1:
                header = new FlvPacketHeader(FlvPacketKind.CodedSample, false, 0);
                break;
            default:
                error = $"AAC packet type {payload[1]} is not defined.";
                return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Splits AVCC access-unit data into NAL units, validating every length prefix against the remaining data.
    /// </summary>
    /// <param name="data">The access unit after the FLV packet header.</param>
    /// <param name="nalLengthSize">Length-prefix size from the configuration: 1, 2 or 4.</param>
    /// <param name="maxNalUnits">The most NAL units accepted.</param>
    /// <param name="nalUnits">The NAL units when valid.</param>
    /// <param name="errorKind">The error kind when invalid.</param>
    /// <param name="error">Why the data is invalid.</param>
    /// <returns><see langword="true"/> if the data is one or more well-formed NAL units.</returns>
    public static bool TryReadNalUnits(ReadOnlySpan<byte> data, int nalLengthSize, int maxNalUnits, out NalUnit[] nalUnits, out MediaErrorKind errorKind, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        nalUnits = [];
        errorKind = MediaErrorKind.MalformedPacket;
        if (data.IsEmpty)
        {
            error = "The AVC access unit contains no NAL units.";
            return false;
        }

        var count = 0;
        var position = 0;
        while (position < data.Length)
        {
            if (!TryReadNalAt(data, position, nalLengthSize, count, out var length, out error))
            {
                return false;
            }

            if (++count > maxNalUnits)
            {
                errorKind = MediaErrorKind.TooManyNalUnits;
                error = $"The AVC access unit contains more than {maxNalUnits} NAL units.";
                return false;
            }

            position += nalLengthSize + length;
        }

        var units = new NalUnit[count];
        position = 0;
        for (var i = 0; i < count; i++)
        {
            var length = ReadLength(data[position..], nalLengthSize);
            var offset = position + nalLengthSize;
            units[i] = new NalUnit(offset, (int)length, (byte)(data[offset] & 0x1F));
            position = offset + (int)length;
        }

        nalUnits = units;
        error = null;
        return true;
    }

    /// <summary>Validates the NAL unit whose length prefix starts at <paramref name="position"/>.</summary>
    /// <param name="data">The access unit.</param>
    /// <param name="position">Offset of the length prefix.</param>
    /// <param name="nalLengthSize">Length-prefix size.</param>
    /// <param name="index">Index of the NAL unit, for messages.</param>
    /// <param name="length">The NAL unit length.</param>
    /// <param name="error">Why the NAL unit is invalid.</param>
    /// <returns><see langword="true"/> if valid.</returns>
    private static bool TryReadNalAt(ReadOnlySpan<byte> data, int position, int nalLengthSize, int index, out int length, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        length = 0;
        var remaining = data.Length - position;
        if (remaining < nalLengthSize)
        {
            error = $"NAL unit {index + 1} has a truncated {nalLengthSize}-byte length prefix.";
            return false;
        }

        var declared = ReadLength(data[position..], nalLengthSize);
        if (declared == 0 || declared > (uint)(remaining - nalLengthSize))
        {
            error = $"NAL unit {index + 1} declares {declared} bytes; {remaining - nalLengthSize} remain.";
            return false;
        }

        if ((data[position + nalLengthSize] & 0x80) != 0)
        {
            error = $"NAL unit {index + 1} has the forbidden_zero_bit set.";
            return false;
        }

        length = (int)declared;
        error = null;
        return true;
    }

    /// <summary>Reads a big-endian NAL length prefix.</summary>
    /// <param name="data">Data starting at the prefix; at least <paramref name="size"/> bytes.</param>
    /// <param name="size">1, 2 or 4.</param>
    /// <returns>The length.</returns>
    private static uint ReadLength(ReadOnlySpan<byte> data, int size) => size switch
    {
        1 => data[0],
        2 => BinaryPrimitives.ReadUInt16BigEndian(data),
        _ => BinaryPrimitives.ReadUInt32BigEndian(data),
    };
}
