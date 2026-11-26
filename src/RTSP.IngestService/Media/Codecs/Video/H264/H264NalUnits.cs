// NAL unit framing (ITU-T H.264 7.3.1 and Annex B). An access unit arrives either as an Annex B byte stream (start
// codes) or as AVC length-prefixed NAL units (ISO/IEC 14496-15). Each NAL unit's payload is converted to its RBSP by
// removing emulation prevention bytes, and the RBSP's trailing bits locate the end of the syntax.

using RTSP.IngestService.Media.Codecs.Common.Bitstreams;

namespace RTSP.IngestService.Media.Codecs.Video.H264;

/// <summary>nal_unit_type values (Table 7-1).</summary>
internal enum H264NalUnitType : byte
{
    /// <summary>Coded slice of a non-IDR picture.</summary>
    Slice = 1,

    /// <summary>Coded slice data partition A.</summary>
    PartitionA = 2,

    /// <summary>Coded slice data partition B.</summary>
    PartitionB = 3,

    /// <summary>Coded slice data partition C.</summary>
    PartitionC = 4,

    /// <summary>Coded slice of an IDR picture.</summary>
    IdrSlice = 5,

    /// <summary>Supplemental enhancement information.</summary>
    Sei = 6,

    /// <summary>Sequence parameter set.</summary>
    Sps = 7,

    /// <summary>Picture parameter set.</summary>
    Pps = 8,

    /// <summary>Access unit delimiter.</summary>
    AccessUnitDelimiter = 9,

    /// <summary>End of sequence.</summary>
    EndOfSequence = 10,

    /// <summary>End of stream.</summary>
    EndOfStream = 11,
}

/// <summary>Splits access units into NAL units and extracts RBSPs.</summary>
internal static class H264NalUnits
{
    /// <summary>Splits an Annex B byte stream into NAL units, without start codes and trailing zero bytes.</summary>
    /// <param name="data">The byte stream.</param>
    /// <param name="units">Receives (offset, length) per NAL unit.</param>
    /// <param name="maxUnits">The most NAL units accepted.</param>
    /// <exception cref="CodecException">The data does not start with a start code, or has too many NAL units.</exception>
    public static void SplitAnnexB(ReadOnlySpan<byte> data, List<(int Offset, int Length)> units, int maxUnits)
    {
        units.Clear();
        var start = -1;
        var i = 0;
        while (i + 2 < data.Length)
        {
            if (data[i + 2] > 1)
            {
                i += 3;
            }
            else if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                if (start < 0)
                {
                    if (data[..i].ContainsAnyExcept((byte)0))
                    {
                        CodecException.InvalidData("An Annex B access unit has data before its first start code.");
                    }
                }
                else
                {
                    Add(data, start, i, units, maxUnits);
                }

                i += 3;
                start = i;
            }
            else
            {
                i++;
            }
        }

        if (start < 0)
        {
            CodecException.InvalidData("An Annex B access unit has no start code.");
        }

        Add(data, start, data.Length, units, maxUnits);
    }

    /// <summary>Splits AVC length-prefixed NAL units.</summary>
    /// <param name="data">The access unit.</param>
    /// <param name="lengthSize">The length field size: 1, 2 or 4 bytes.</param>
    /// <param name="units">Receives (offset, length) per NAL unit.</param>
    /// <param name="maxUnits">The most NAL units accepted.</param>
    /// <exception cref="CodecException">A length is zero or runs past the end, or there are too many NAL units.</exception>
    public static void SplitLengthPrefixed(ReadOnlySpan<byte> data, int lengthSize, List<(int Offset, int Length)> units, int maxUnits)
    {
        units.Clear();
        var position = 0;
        while (position < data.Length)
        {
            if (data.Length - position < lengthSize)
            {
                CodecException.InvalidData("A NAL unit length field is truncated.");
            }

            long length = 0;
            for (var k = 0; k < lengthSize; k++)
            {
                length = (length << 8) | data[position + k];
            }

            position += lengthSize;
            if (length == 0 || length > data.Length - position)
            {
                CodecException.InvalidData("A NAL unit length is zero or exceeds the access unit.");
            }

            if (units.Count >= maxUnits)
            {
                throw new CodecException(CodecErrorKind.ResourceLimit, "The access unit has too many NAL units.");
            }

            units.Add((position, (int)length));
            position += (int)length;
        }
    }

    /// <summary>Converts a NAL unit payload (after the header byte) to its RBSP.</summary>
    /// <param name="payload">The payload.</param>
    /// <param name="rbsp">Receives the RBSP; at least as long as <paramref name="payload"/>.</param>
    /// <returns>The RBSP length.</returns>
    /// <exception cref="CodecException">The payload contains a forbidden three-byte sequence.</exception>
    public static int Unescape(ReadOnlySpan<byte> payload, Span<byte> rbsp)
    {
        payload = payload[..(payload.LastIndexOfAnyExcept((byte)0) + 1)];
        var length = 0;
        var zeros = 0;
        for (var i = 0; i < payload.Length; i++)
        {
            var value = payload[i];
            if (zeros >= 2)
            {
                if (value == 3)
                {
                    zeros = 0;
                    if (i + 1 < payload.Length && payload[i + 1] > 3)
                    {
                        CodecException.InvalidData("An emulation prevention byte is followed by a value above 3.");
                    }

                    continue;
                }

                if (value <= 2)
                {
                    CodecException.InvalidData("A NAL unit contains a forbidden start code prefix.");
                }
            }

            zeros = value == 0 ? zeros + 1 : 0;
            rbsp[length++] = value;
        }

        return length;
    }

    /// <summary>Returns the number of syntax bits in an RBSP, which precede its rbsp_stop_one_bit.</summary>
    /// <param name="rbsp">The RBSP, possibly followed by cabac_zero_words.</param>
    /// <returns>The bit index of the stop bit.</returns>
    /// <exception cref="CodecException">The RBSP has no stop bit.</exception>
    public static int PayloadBits(ReadOnlySpan<byte> rbsp)
    {
        var last = rbsp.LastIndexOfAnyExcept((byte)0);
        if (last < 0)
        {
            CodecException.InvalidData("A NAL unit has no rbsp_stop_one_bit.");
        }

        return (last * 8) + 7 - System.Numerics.BitOperations.TrailingZeroCount(rbsp[last]);
    }

    /// <summary>Appends one Annex B NAL unit, trimming trailing zero bytes.</summary>
    /// <param name="data">The byte stream.</param>
    /// <param name="start">First byte after the start code.</param>
    /// <param name="end">Index of the next start code, or the end.</param>
    /// <param name="units">The list.</param>
    /// <param name="maxUnits">The most NAL units accepted.</param>
    private static void Add(ReadOnlySpan<byte> data, int start, int end, List<(int Offset, int Length)> units, int maxUnits)
    {
        while (end > start && data[end - 1] == 0)
        {
            end--;
        }

        if (end == start)
        {
            CodecException.InvalidData("An Annex B access unit contains an empty NAL unit.");
        }

        if (units.Count >= maxUnits)
        {
            throw new CodecException(CodecErrorKind.ResourceLimit, "The access unit has too many NAL units.");
        }

        units.Add((start, end - start));
    }
}

/// <summary>Range-checked syntax element reads.</summary>
internal static class H264Syntax
{
    /// <summary>Reads ue(v) and checks its range.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="max">The largest legal value.</param>
    /// <param name="name">The syntax element, for the error message.</param>
    /// <returns>The value.</returns>
    /// <exception cref="CodecException">The value exceeds <paramref name="max"/> or the data is truncated.</exception>
    public static int ReadUe(ref BitstreamReader reader, int max, string name)
    {
        var value = reader.ReadExpGolomb();
        if (value > (uint)max)
        {
            CodecException.InvalidData($"{name} is out of range.");
        }

        return (int)value;
    }

    /// <summary>Reads se(v) and checks its range.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="min">The smallest legal value.</param>
    /// <param name="max">The largest legal value.</param>
    /// <param name="name">The syntax element, for the error message.</param>
    /// <returns>The value.</returns>
    /// <exception cref="CodecException">The value is out of range or the data is truncated.</exception>
    public static int ReadSe(ref BitstreamReader reader, int min, int max, string name)
    {
        var value = reader.ReadSignedExpGolomb();
        if (value < min || value > max)
        {
            CodecException.InvalidData($"{name} is out of range.");
        }

        return value;
    }

    /// <summary>Throws an unsupported-feature failure.</summary>
    /// <param name="message">The message.</param>
    /// <exception cref="CodecException">Always.</exception>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    public static void Unsupported(string message) => CodecException.Unsupported(message);
}
