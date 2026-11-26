// Validation of the video configuration records carried by legacy and Enhanced RTMP sequence headers: the
// AVCDecoderConfigurationRecord (ISO/IEC 14496-15 5.3.3), HEVCDecoderConfigurationRecord (8.3.3), the
// AV1CodecConfigurationRecord (AV1-ISOBMFF 2.3) and the VPCodecConfigurationRecord (VP codec ISO binding). Each parser
// checks the structure and every declared length, and reads only what admission control needs (picture size, bit
// depth). Nothing is decoded; the transcoder's decoder performs full validation of the bitstream.

using System.Buffers.Binary;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Media;

namespace RTSP.IngestService.Transcoding.Codecs;

/// <summary>Parses source video configuration records and keyframe headers.</summary>
internal static class SourceVideoConfigurations
{
    /// <summary>Largest accepted configuration record, in bytes.</summary>
    public const int MaxRecordSize = 64 * 1024;

    /// <summary>Largest picture side accepted by the parsers.</summary>
    private const int MaxDimension = 16384;

    /// <summary>Parses a configuration record.</summary>
    /// <param name="codec">The codec.</param>
    /// <param name="record">The record bytes.</param>
    /// <param name="configuration">The configuration when valid.</param>
    /// <param name="error">Why it is invalid.</param>
    /// <returns><see langword="true"/> if valid.</returns>
    public static bool TryParse(SourceVideoCodec codec, ReadOnlySpan<byte> record, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SourceVideoConfiguration? configuration, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        configuration = null;
        if (record.Length > MaxRecordSize)
        {
            error = $"The {codec} configuration record is {record.Length} bytes; at most {MaxRecordSize} are accepted.";
            return false;
        }

        return codec switch
        {
            SourceVideoCodec.H264 => TryParseAvc(record, out configuration, out error),
            SourceVideoCodec.Hevc => TryParseHevc(record, out configuration, out error),
            SourceVideoCodec.Av1 => TryParseAv1(record, out configuration, out error),
            _ => TryParseVp9(record, out configuration, out error),
        };
    }

    /// <summary>
    /// Reads the picture size from a keyframe, for codecs whose configuration may not carry it (AV1 without
    /// configuration OBUs, VP9).
    /// </summary>
    /// <param name="codec">The codec.</param>
    /// <param name="data">The coded keyframe.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <returns><see langword="true"/> if the keyframe carries a readable picture size.</returns>
    public static bool TryReadKeyframeSize(SourceVideoCodec codec, ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        return codec switch
        {
            SourceVideoCodec.Av1 => TryFindAv1SequenceHeader(data, out width, out height, out _),
            SourceVideoCodec.Vp9 => TryReadVp9KeyframeSize(data, out width, out height),
            _ => false,
        };
    }

    /// <summary>Parses an AVCDecoderConfigurationRecord and its first SPS.</summary>
    /// <param name="record">The record.</param>
    /// <param name="configuration">The configuration.</param>
    /// <param name="error">Why it is invalid.</param>
    /// <returns><see langword="true"/> if valid.</returns>
    private static bool TryParseAvc(ReadOnlySpan<byte> record, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SourceVideoConfiguration? configuration, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        configuration = null;
        if (!AvcDecoderConfiguration.TryParse(record, out var avc, out error))
        {
            return false;
        }

        if (!AvcSequenceParameterSet.TryParse(avc.SequenceParameterSets[0].Span, out var sps, out error))
        {
            return false;
        }

        configuration = new SourceVideoConfiguration(
            SourceVideoCodec.H264,
            avc.Record.ToArray(),
            avc.NalLengthSize,
            sps.Width,
            sps.Height,
            sps.BitDepthLuma,
            $"{avc.CodecString} {sps.Width}x{sps.Height} {sps.BitDepthLuma}-bit");
        return true;
    }

    /// <summary>Parses an HEVCDecoderConfigurationRecord and its first SPS.</summary>
    /// <param name="record">The record.</param>
    /// <param name="configuration">The configuration.</param>
    /// <param name="error">Why it is invalid.</param>
    /// <returns><see langword="true"/> if valid.</returns>
    private static bool TryParseHevc(ReadOnlySpan<byte> record, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SourceVideoConfiguration? configuration, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        const int HeaderLength = 23;
        configuration = null;
        if (record.Length < HeaderLength)
        {
            error = $"The HEVC configuration record is {record.Length} bytes; at least {HeaderLength} are required.";
            return false;
        }

        if (record[0] != 1)
        {
            error = $"The HEVC configuration record has version {record[0]}; only version 1 is defined.";
            return false;
        }

        var profile = record[1] & 0x1F;
        var level = record[12];
        var lengthSize = (record[21] & 0x03) + 1;
        if (lengthSize == 3)
        {
            error = "The HEVC configuration record declares a NAL unit length size of 3 bytes, which is not allowed.";
            return false;
        }

        var arrays = record[22];
        var position = HeaderLength;
        ReadOnlySpan<byte> sps = default;
        var foundVps = false;
        var foundPps = false;
        for (var i = 0; i < arrays; i++)
        {
            if (record.Length - position < 3)
            {
                error = $"The HEVC configuration record ends inside NAL unit array {i + 1} of {arrays}.";
                return false;
            }

            var type = record[position] & 0x3F;
            int count = BinaryPrimitives.ReadUInt16BigEndian(record[(position + 1)..]);
            position += 3;
            for (var n = 0; n < count; n++)
            {
                if (record.Length - position < 2)
                {
                    error = $"The HEVC configuration record ends inside a NAL unit of array {i + 1}.";
                    return false;
                }

                int length = BinaryPrimitives.ReadUInt16BigEndian(record[position..]);
                position += 2;
                if (length < 2 || length > record.Length - position)
                {
                    error = $"The HEVC configuration record declares a {length}-byte NAL unit; {record.Length - position} bytes remain.";
                    return false;
                }

                var nal = record.Slice(position, length);
                if ((nal[0] & 0x80) != 0 || ((nal[0] >> 1) & 0x3F) != type)
                {
                    error = $"An HEVC configuration record NAL unit does not match its array type {type}.";
                    return false;
                }

                foundVps |= type == 32;
                foundPps |= type == 34;
                if (type == 33 && sps.IsEmpty)
                {
                    sps = nal;
                }

                position += length;
            }
        }

        if (!foundVps || sps.IsEmpty || !foundPps)
        {
            error = "The HEVC configuration record must contain a VPS, an SPS and a PPS.";
            return false;
        }

        if (!TryReadHevcSps(sps, out var width, out var height, out var bitDepth))
        {
            error = "The HEVC sequence parameter set is truncated or has out-of-range picture fields.";
            return false;
        }

        configuration = new SourceVideoConfiguration(
            SourceVideoCodec.Hevc,
            record.ToArray(),
            lengthSize,
            width,
            height,
            bitDepth,
            $"hevc profile {profile} level {level / 30.0:0.#} {width}x{height} {bitDepth}-bit");
        error = null;
        return true;
    }

    /// <summary>Reads the picture size and bit depth from an HEVC SPS (ITU-T H.265 7.3.2.2.1).</summary>
    /// <param name="nal">The SPS NAL unit including its two-byte header.</param>
    /// <param name="width">The cropped width.</param>
    /// <param name="height">The cropped height.</param>
    /// <param name="bitDepth">The luma bit depth.</param>
    /// <returns><see langword="true"/> on success.</returns>
    private static bool TryReadHevcSps(ReadOnlySpan<byte> nal, out int width, out int height, out int bitDepth)
    {
        width = 0;
        height = 0;
        bitDepth = 0;
        var reader = new BitReader(BitReader.RemoveEmulationPrevention(nal[2..]));
        if (!reader.TryRead(4, out _) || !reader.TryRead(3, out var maxSubLayersMinus1) || !reader.TryRead(1, out _)
            || !reader.TrySkip(96))
        {
            return false;
        }

        Span<bool> profilePresent = stackalloc bool[8];
        Span<bool> levelPresent = stackalloc bool[8];
        for (var i = 0; i < maxSubLayersMinus1; i++)
        {
            if (!reader.TryRead(1, out var p) || !reader.TryRead(1, out var l))
            {
                return false;
            }

            profilePresent[i] = p == 1;
            levelPresent[i] = l == 1;
        }

        if (maxSubLayersMinus1 > 0 && !reader.TrySkip(2 * (8 - maxSubLayersMinus1)))
        {
            return false;
        }

        for (var i = 0; i < maxSubLayersMinus1; i++)
        {
            if ((profilePresent[i] && !reader.TrySkip(88)) || (levelPresent[i] && !reader.TrySkip(8)))
            {
                return false;
            }
        }

        if (!reader.TryReadExpGolomb(15, out _) || !reader.TryReadExpGolomb(3, out var chromaFormat))
        {
            return false;
        }

        uint separateColourPlane = 0;
        if (chromaFormat == 3 && !reader.TryRead(1, out separateColourPlane))
        {
            return false;
        }

        if (!reader.TryReadExpGolomb(MaxDimension, out var picWidth) || !reader.TryReadExpGolomb(MaxDimension, out var picHeight)
            || !reader.TryRead(1, out var conformanceWindow))
        {
            return false;
        }

        uint left = 0, right = 0, top = 0, bottom = 0;
        if (conformanceWindow == 1
            && (!reader.TryReadExpGolomb(MaxDimension, out left) || !reader.TryReadExpGolomb(MaxDimension, out right)
                || !reader.TryReadExpGolomb(MaxDimension, out top) || !reader.TryReadExpGolomb(MaxDimension, out bottom)))
        {
            return false;
        }

        if (!reader.TryReadExpGolomb(8, out var bitDepthMinus8))
        {
            return false;
        }

        var chroma = separateColourPlane == 1 ? 0 : chromaFormat;
        long subWidth = chroma is 1 or 2 ? 2 : 1;
        long subHeight = chroma == 1 ? 2 : 1;
        var w = picWidth - (subWidth * (left + right));
        var h = picHeight - (subHeight * (top + bottom));
        if (w is < 1 or > MaxDimension || h is < 1 or > MaxDimension)
        {
            return false;
        }

        width = (int)w;
        height = (int)h;
        bitDepth = (int)bitDepthMinus8 + 8;
        return true;
    }

    /// <summary>Parses an AV1CodecConfigurationRecord and, when present, its sequence header OBU.</summary>
    /// <param name="record">The record.</param>
    /// <param name="configuration">The configuration.</param>
    /// <param name="error">Why it is invalid.</param>
    /// <returns><see langword="true"/> if valid.</returns>
    private static bool TryParseAv1(ReadOnlySpan<byte> record, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SourceVideoConfiguration? configuration, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        configuration = null;
        if (record.Length < 4)
        {
            error = $"The AV1 configuration record is {record.Length} bytes; at least 4 are required.";
            return false;
        }

        if (record[0] != 0x81)
        {
            error = "The AV1 configuration record does not start with marker 1 and version 1.";
            return false;
        }

        var profile = record[1] >> 5;
        var level = record[1] & 0x1F;
        var highBitDepth = (record[2] & 0x40) != 0;
        var twelveBit = (record[2] & 0x20) != 0;
        var bitDepth = profile == 2 && highBitDepth ? (twelveBit ? 12 : 10) : highBitDepth ? 10 : 8;
        int? width = null;
        int? height = null;
        var obus = record[4..];
        if (!obus.IsEmpty)
        {
            if (!TryValidateObus(obus, out error))
            {
                return false;
            }

            if (TryFindAv1SequenceHeader(obus, out var w, out var h, out var found))
            {
                width = w;
                height = h;
            }
            else if (found)
            {
                error = "The AV1 configuration record's sequence header OBU is truncated or invalid.";
                return false;
            }
        }

        configuration = new SourceVideoConfiguration(
            SourceVideoCodec.Av1,
            record.ToArray(),
            0,
            width,
            height,
            bitDepth,
            $"av1 profile {profile} level {level} {bitDepth}-bit");
        error = null;
        return true;
    }

    /// <summary>Validates that <paramref name="data"/> is a sequence of complete low-overhead OBUs.</summary>
    /// <param name="data">The OBUs.</param>
    /// <param name="error">Why they are invalid.</param>
    /// <returns><see langword="true"/> if valid.</returns>
    public static bool TryValidateObus(ReadOnlySpan<byte> data, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        var position = 0;
        while (position < data.Length)
        {
            if (!TryReadObu(data[position..], out _, out _, out var total))
            {
                error = "The AV1 data contains a truncated or malformed OBU.";
                return false;
            }

            position += total;
        }

        error = null;
        return true;
    }

    /// <summary>Reads one OBU header.</summary>
    /// <param name="data">Data starting at the OBU.</param>
    /// <param name="type">The OBU type.</param>
    /// <param name="payload">The OBU payload.</param>
    /// <param name="total">Total OBU length including the header.</param>
    /// <returns><see langword="true"/> if the OBU is complete.</returns>
    private static bool TryReadObu(ReadOnlySpan<byte> data, out int type, out ReadOnlySpan<byte> payload, out int total)
    {
        type = 0;
        payload = default;
        total = 0;
        if (data.IsEmpty || (data[0] & 0x80) != 0)
        {
            return false;
        }

        type = (data[0] >> 3) & 0x0F;
        var header = (data[0] & 0x04) != 0 ? 2 : 1;
        if (data.Length < header)
        {
            return false;
        }

        int size;
        if ((data[0] & 0x02) != 0)
        {
            if (!BitReader.TryReadLeb128(data[header..], out size, out var sizeLength))
            {
                return false;
            }

            header += sizeLength;
        }
        else
        {
            size = data.Length - header;
        }

        if (size > data.Length - header)
        {
            return false;
        }

        payload = data.Slice(header, size);
        total = header + size;
        return true;
    }

    /// <summary>Finds the first sequence header OBU in <paramref name="data"/> and reads its maximum frame size.</summary>
    /// <param name="data">OBUs.</param>
    /// <param name="width">The maximum frame width.</param>
    /// <param name="height">The maximum frame height.</param>
    /// <param name="found">Whether a sequence header OBU was found (even if unreadable).</param>
    /// <returns><see langword="true"/> if a readable sequence header was found.</returns>
    private static bool TryFindAv1SequenceHeader(ReadOnlySpan<byte> data, out int width, out int height, out bool found)
    {
        width = 0;
        height = 0;
        found = false;
        var position = 0;
        while (position < data.Length && TryReadObu(data[position..], out var type, out var payload, out var total))
        {
            if (type == 1)
            {
                found = true;
                return TryReadAv1SequenceHeader(payload, out width, out height);
            }

            position += total;
        }

        return false;
    }

    /// <summary>Reads <c>max_frame_width_minus_1</c> and <c>max_frame_height_minus_1</c> from a sequence header OBU (AV1 5.5.1).</summary>
    /// <param name="payload">The OBU payload.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <returns><see langword="true"/> on success.</returns>
    private static bool TryReadAv1SequenceHeader(ReadOnlySpan<byte> payload, out int width, out int height)
    {
        width = 0;
        height = 0;
        var reader = new BitReader(payload);
        if (!reader.TryRead(3, out _) || !reader.TryRead(1, out _) || !reader.TryRead(1, out var reduced))
        {
            return false;
        }

        if (reduced == 1)
        {
            if (!reader.TrySkip(5))
            {
                return false;
            }
        }
        else
        {
            if (!reader.TryRead(1, out var timingInfo))
            {
                return false;
            }

            uint decoderModelInfo = 0;
            uint bufferDelayLength = 0;
            if (timingInfo == 1)
            {
                if (!reader.TrySkip(64) || !reader.TryRead(1, out var equalPictureInterval)
                    || (equalPictureInterval == 1 && !reader.TryReadUvlc(out _))
                    || !reader.TryRead(1, out decoderModelInfo))
                {
                    return false;
                }

                if (decoderModelInfo == 1)
                {
                    if (!reader.TryRead(5, out bufferDelayLength) || !reader.TrySkip(32 + 5 + 5))
                    {
                        return false;
                    }

                    bufferDelayLength++;
                }
            }

            if (!reader.TryRead(1, out var initialDisplayDelay) || !reader.TryRead(5, out var operatingPoints))
            {
                return false;
            }

            for (var i = 0; i <= operatingPoints; i++)
            {
                if (!reader.TrySkip(12) || !reader.TryRead(5, out var seqLevel) || (seqLevel > 7 && !reader.TrySkip(1)))
                {
                    return false;
                }

                if (decoderModelInfo == 1)
                {
                    if (!reader.TryRead(1, out var present) || (present == 1 && !reader.TrySkip((2 * bufferDelayLength) + 1)))
                    {
                        return false;
                    }
                }

                if (initialDisplayDelay == 1)
                {
                    if (!reader.TryRead(1, out var present) || (present == 1 && !reader.TrySkip(4)))
                    {
                        return false;
                    }
                }
            }
        }

        if (!reader.TryRead(4, out var widthBits) || !reader.TryRead(4, out var heightBits)
            || !reader.TryRead((int)widthBits + 1, out var maxWidthMinus1) || !reader.TryRead((int)heightBits + 1, out var maxHeightMinus1))
        {
            return false;
        }

        if (maxWidthMinus1 >= MaxDimension || maxHeightMinus1 >= MaxDimension)
        {
            return false;
        }

        width = (int)maxWidthMinus1 + 1;
        height = (int)maxHeightMinus1 + 1;
        return true;
    }

    /// <summary>
    /// Parses a VPCodecConfigurationRecord, with or without the four-byte version/flags prefix of the <c>vpcC</c>
    /// box (FFmpeg includes it).
    /// </summary>
    /// <param name="record">The record.</param>
    /// <param name="configuration">The configuration.</param>
    /// <param name="error">Why it is invalid.</param>
    /// <returns><see langword="true"/> if valid.</returns>
    private static bool TryParseVp9(ReadOnlySpan<byte> record, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SourceVideoConfiguration? configuration, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        configuration = null;
        var body = record.Length >= 12 && record[0] == 1 && record[1] == 0 && record[2] == 0 && record[3] == 0 ? record[4..] : record;
        if (body.Length < 8)
        {
            error = $"The VP9 configuration record is {record.Length} bytes; at least 8 are required.";
            return false;
        }

        var profile = body[0];
        var bitDepth = body[2] >> 4;
        int initializationLength = BinaryPrimitives.ReadUInt16BigEndian(body[6..]);
        if (profile > 3 || bitDepth is not (8 or 10 or 12) || initializationLength != body.Length - 8)
        {
            error = "The VP9 configuration record has an invalid profile, bit depth or initialization data length.";
            return false;
        }

        configuration = new SourceVideoConfiguration(SourceVideoCodec.Vp9, record.ToArray(), 0, null, null, bitDepth, $"vp9 profile {profile} {bitDepth}-bit");
        error = null;
        return true;
    }

    /// <summary>Reads the frame size from a VP9 keyframe's uncompressed header (VP9 bitstream 6.2).</summary>
    /// <param name="data">The frame (or superframe; its first frame is read).</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <returns><see langword="true"/> if the data starts with a readable keyframe header.</returns>
    private static bool TryReadVp9KeyframeSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        var reader = new BitReader(data);
        if (!reader.TryRead(2, out var marker) || marker != 2 || !reader.TryRead(1, out var low) || !reader.TryRead(1, out var high))
        {
            return false;
        }

        var profile = (high << 1) | low;
        if ((profile == 3 && !reader.TrySkip(1)) || !reader.TryRead(1, out var showExisting) || showExisting == 1
            || !reader.TryRead(1, out var frameType) || frameType != 0 || !reader.TrySkip(2)
            || !reader.TryRead(24, out var sync) || sync != 0x498342)
        {
            return false;
        }

        if (profile >= 2 && !reader.TrySkip(1))
        {
            return false;
        }

        if (!reader.TryRead(3, out var colorSpace))
        {
            return false;
        }

        if (colorSpace != 7)
        {
            if (!reader.TrySkip(1) || ((profile is 1 or 3) && !reader.TrySkip(3)))
            {
                return false;
            }
        }
        else if ((profile is 1 or 3) && !reader.TrySkip(1))
        {
            return false;
        }

        if (!reader.TryRead(16, out var widthMinus1) || !reader.TryRead(16, out var heightMinus1))
        {
            return false;
        }

        width = (int)widthMinus1 + 1;
        height = (int)heightMinus1 + 1;
        return true;
    }
}
