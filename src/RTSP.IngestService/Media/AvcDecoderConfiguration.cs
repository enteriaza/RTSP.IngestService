// Parser and model for the AVCDecoderConfigurationRecord (ISO/IEC 14496-15, section 5.3.3.1) carried by FLV AVC
// sequence headers. It validates the record's structure and every declared length, and keeps a copy of the record
// with its sequence and picture parameter sets. Parameter sets are not decoded.

namespace RTSP.IngestService.Media;

/// <summary>
/// An H.264 decoder configuration: profile, level, NAL unit length size and parameter sets.
/// </summary>
/// <remarks>
/// Immutable. <see cref="Record"/> is a private copy of the record as received, so it stays valid independently of
/// the message that carried it. Trailing bytes after the picture parameter sets (the High-profile extension fields)
/// are kept in <see cref="Record"/> but not interpreted.
/// </remarks>
public sealed class AvcDecoderConfiguration
{
    /// <summary>Largest accepted configuration record, in bytes.</summary>
    public const int MaxRecordSize = 64 * 1024;

    /// <summary>Fixed header length: version, profile, compatibility, level, length size and SPS count.</summary>
    private const int HeaderLength = 6;

    /// <summary>Initializes a new instance of the <see cref="AvcDecoderConfiguration"/> class.</summary>
    /// <param name="record">The copied record.</param>
    /// <param name="sequenceParameterSets">SPS slices of <paramref name="record"/>.</param>
    /// <param name="pictureParameterSets">PPS slices of <paramref name="record"/>.</param>
    private AvcDecoderConfiguration(byte[] record, ReadOnlyMemory<byte>[] sequenceParameterSets, ReadOnlyMemory<byte>[] pictureParameterSets)
    {
        Record = record;
        Profile = record[1];
        ProfileCompatibility = record[2];
        Level = record[3];
        NalLengthSize = (record[4] & 0x03) + 1;
        SequenceParameterSets = sequenceParameterSets;
        PictureParameterSets = pictureParameterSets;
    }

    /// <summary>Gets the <c>AVCProfileIndication</c> (for example 66 Baseline, 77 Main, 100 High).</summary>
    public byte Profile { get; }

    /// <summary>Gets the <c>profile_compatibility</c> constraint flags.</summary>
    public byte ProfileCompatibility { get; }

    /// <summary>Gets the <c>AVCLevelIndication</c> (level × 10, for example 31 for level 3.1).</summary>
    public byte Level { get; }

    /// <summary>Gets the size in bytes (1, 2 or 4) of the length prefix before each NAL unit in coded samples.</summary>
    public int NalLengthSize { get; }

    /// <summary>Gets the sequence parameter set NAL units, without length prefixes.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> SequenceParameterSets { get; }

    /// <summary>Gets the picture parameter set NAL units, without length prefixes.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> PictureParameterSets { get; }

    /// <summary>Gets the complete record as received.</summary>
    public ReadOnlyMemory<byte> Record { get; }

    /// <summary>Gets the RFC 6381 codec string, for example <c>avc1.4D401E</c>.</summary>
    public string CodecString => $"avc1.{Profile:X2}{ProfileCompatibility:X2}{Level:X2}";

    /// <summary>Parses and validates a record.</summary>
    /// <param name="data">The record bytes (the FLV AVC sequence header body).</param>
    /// <param name="configuration">The configuration when valid.</param>
    /// <param name="error">Why the record is invalid, when it is.</param>
    /// <returns><see langword="true"/> if the record is valid.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AvcDecoderConfiguration? configuration, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        configuration = null;
        if (data.Length < HeaderLength + 1)
        {
            error = $"The AVC configuration record is {data.Length} bytes; at least {HeaderLength + 1} are required.";
            return false;
        }

        if (data.Length > MaxRecordSize)
        {
            error = $"The AVC configuration record is {data.Length} bytes; at most {MaxRecordSize} are accepted.";
            return false;
        }

        if (data[0] != 1)
        {
            error = $"The AVC configuration record has version {data[0]}; only version 1 is defined.";
            return false;
        }

        if ((data[4] & 0x03) == 2)
        {
            error = "The AVC configuration record declares a NAL unit length size of 3 bytes, which is not allowed.";
            return false;
        }

        var position = HeaderLength - 1;
        if (!TryReadParameterSets(data, ref position, data[position] & 0x1F, 7, "sequence", out var spsRanges, out error)
            || !TryReadCount(data, ref position, out var ppsCount, out error)
            || !TryReadParameterSets(data, ref position, ppsCount, 8, "picture", out var ppsRanges, out error))
        {
            return false;
        }

        var record = data.ToArray();
        configuration = new AvcDecoderConfiguration(record, Slices(record, spsRanges), Slices(record, ppsRanges));
        return true;
    }

    /// <summary>Whether <paramref name="data"/> is byte-for-byte this configuration's record.</summary>
    /// <param name="data">A record.</param>
    /// <returns><see langword="true"/> if identical.</returns>
    public bool HasRecord(ReadOnlySpan<byte> data) => data.SequenceEqual(Record.Span);

    /// <summary>Reads the PPS count byte.</summary>
    /// <param name="data">The record.</param>
    /// <param name="position">Read position; advanced past the count.</param>
    /// <param name="count">The count.</param>
    /// <param name="error">Why reading failed.</param>
    /// <returns><see langword="true"/> on success.</returns>
    private static bool TryReadCount(ReadOnlySpan<byte> data, ref int position, out int count, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        if (position >= data.Length)
        {
            count = 0;
            error = "The AVC configuration record ends before the picture parameter set count.";
            return false;
        }

        count = data[position];
        error = null;
        return true;
    }

    /// <summary>Reads a count-prefixed list of length-prefixed parameter sets and checks each NAL header.</summary>
    /// <param name="data">The record.</param>
    /// <param name="position">Position of the count byte (already read into <paramref name="count"/>); advanced past the last parameter set.</param>
    /// <param name="count">The number of parameter sets.</param>
    /// <param name="nalType">The required NAL unit type (7 for SPS, 8 for PPS).</param>
    /// <param name="name">Name for messages.</param>
    /// <param name="ranges">Offsets and lengths of each parameter set within <paramref name="data"/>.</param>
    /// <param name="error">Why reading failed.</param>
    /// <returns><see langword="true"/> on success.</returns>
    private static bool TryReadParameterSets(ReadOnlySpan<byte> data, ref int position, int count, int nalType, string name, out (int Offset, int Length)[] ranges, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        ranges = [];
        if (count == 0)
        {
            error = $"The AVC configuration record contains no {name} parameter sets.";
            return false;
        }

        var result = new (int Offset, int Length)[count];
        position++;
        for (var i = 0; i < count; i++)
        {
            if (data.Length - position < 2)
            {
                error = $"The AVC configuration record ends inside {name} parameter set {i + 1} of {count}.";
                return false;
            }

            var length = (data[position] << 8) | data[position + 1];
            position += 2;
            if (length == 0 || length > data.Length - position)
            {
                error = $"The AVC configuration record declares {name} parameter set {i + 1} as {length} bytes; {data.Length - position} remain.";
                return false;
            }

            var header = data[position];
            if ((header & 0x80) != 0 || (header & 0x1F) != nalType)
            {
                error = $"The AVC configuration record's {name} parameter set {i + 1} has NAL unit type {header & 0x1F}; type {nalType} is required.";
                return false;
            }

            result[i] = (position, length);
            position += length;
        }

        ranges = result;
        error = null;
        return true;
    }

    /// <summary>Creates memory slices of <paramref name="record"/>.</summary>
    /// <param name="record">The record copy.</param>
    /// <param name="ranges">Offsets and lengths.</param>
    /// <returns>The slices.</returns>
    private static ReadOnlyMemory<byte>[] Slices(byte[] record, (int Offset, int Length)[] ranges) =>
        [.. ranges.Select(range => new ReadOnlyMemory<byte>(record, range.Offset, range.Length))];
}
