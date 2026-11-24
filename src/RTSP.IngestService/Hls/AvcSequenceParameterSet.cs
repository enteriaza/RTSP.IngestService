// Minimal H.264 sequence parameter set reader (ITU-T H.264, section 7.3.2.1.1) for packaging. It reads only the
// fields up to frame cropping that determine the coded picture dimensions written into the MP4 visual sample entry
// and track header. Nothing after frame cropping (VUI) is interpreted, and no slice data is ever parsed.

namespace RTSP.IngestService.Hls;

/// <summary>
/// The picture dimensions and format fields of an H.264 SPS.
/// </summary>
internal sealed class AvcSequenceParameterSet
{
    /// <summary>Largest accepted width or height in pixels.</summary>
    public const int MaxDimension = 16384;

    /// <summary>Profiles whose SPS carries chroma format, bit depth and scaling matrices.</summary>
    private static readonly int[] HighProfiles = [100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135];

    /// <summary>Gets <c>profile_idc</c>.</summary>
    public int Profile { get; private init; }

    /// <summary>Gets <c>level_idc</c>.</summary>
    public int Level { get; private init; }

    /// <summary>Gets <c>chroma_format_idc</c> (1, 4:2:0, unless signalled by a high profile).</summary>
    public int ChromaFormat { get; private init; } = 1;

    /// <summary>Gets the luma bit depth.</summary>
    public int BitDepthLuma { get; private init; } = 8;

    /// <summary>Gets the cropped picture width in pixels.</summary>
    public int Width { get; private init; }

    /// <summary>Gets the cropped picture height in pixels (frame height for interlaced streams).</summary>
    public int Height { get; private init; }

    /// <summary>Parses the dimensions from an SPS NAL unit.</summary>
    /// <param name="nalUnit">The SPS NAL unit including its one-byte header, with emulation prevention bytes.</param>
    /// <param name="sps">The fields when valid.</param>
    /// <param name="error">Why the SPS is invalid.</param>
    /// <returns><see langword="true"/> if the SPS is valid up to frame cropping and gives dimensions in range.</returns>
    public static bool TryParse(ReadOnlySpan<byte> nalUnit, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AvcSequenceParameterSet? sps, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        sps = null;
        if (nalUnit.Length < 4 || (nalUnit[0] & 0x1F) != 7)
        {
            error = "The SPS NAL unit is shorter than 4 bytes or is not NAL unit type 7.";
            return false;
        }

        var reader = new ExpGolombReader(RemoveEmulationPrevention(nalUnit[1..]));
        if (!TryRead(ref reader, out var result, out error))
        {
            return false;
        }

        sps = result;
        return true;
    }

    /// <summary>Reads the SPS fields.</summary>
    /// <param name="reader">The RBSP reader, positioned at <c>profile_idc</c>.</param>
    /// <param name="sps">The fields.</param>
    /// <param name="error">Why reading failed.</param>
    /// <returns><see langword="true"/> on success.</returns>
    private static bool TryRead(ref ExpGolombReader reader, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AvcSequenceParameterSet? sps, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        sps = null;
        const string Truncated = "The SPS ends before frame cropping.";
        error = Truncated;
        if (!reader.TryReadBits(8, out var profile) || !reader.TryReadBits(8, out _) || !reader.TryReadBits(8, out var level)
            || !reader.TryReadUnsigned(31, out _))
        {
            return false;
        }

        uint chromaFormat = 1;
        uint separateColourPlane = 0;
        uint bitDepthLuma = 0;
        if (HighProfiles.Contains((int)profile))
        {
            if (!reader.TryReadUnsigned(3, out chromaFormat))
            {
                error = "The SPS chroma_format_idc is truncated or above 3.";
                return false;
            }

            if ((chromaFormat == 3 && !reader.TryReadBits(1, out separateColourPlane))
                || !reader.TryReadUnsigned(6, out bitDepthLuma) || !reader.TryReadUnsigned(6, out _)
                || !reader.TryReadBits(1, out _) || !reader.TryReadBits(1, out var scalingMatrixPresent))
            {
                error = "The SPS bit depth or scaling matrix fields are truncated or out of range.";
                return false;
            }

            if (scalingMatrixPresent == 1 && !TrySkipScalingLists(ref reader, chromaFormat == 3 ? 12 : 8))
            {
                error = "The SPS scaling lists are truncated or invalid.";
                return false;
            }
        }

        if (!reader.TryReadUnsigned(12, out _) || !reader.TryReadUnsigned(2, out var pocType))
        {
            error = "The SPS frame number or picture order count fields are truncated or out of range.";
            return false;
        }

        if (pocType == 0 && !reader.TryReadUnsigned(12, out _))
        {
            return false;
        }

        if (pocType == 1)
        {
            if (!reader.TryReadBits(1, out _) || !reader.TryReadSigned(out _) || !reader.TryReadSigned(out _)
                || !reader.TryReadUnsigned(255, out var cycle))
            {
                return false;
            }

            for (var i = 0; i < cycle; i++)
            {
                if (!reader.TryReadSigned(out _))
                {
                    return false;
                }
            }
        }

        if (!reader.TryReadUnsigned(32, out _) || !reader.TryReadBits(1, out _)
            || !reader.TryReadUnsigned(MaxDimension / 16, out var widthInMbsMinus1)
            || !reader.TryReadUnsigned(MaxDimension / 16, out var heightInMapUnitsMinus1)
            || !reader.TryReadBits(1, out var frameMbsOnly))
        {
            error = "The SPS picture size fields are truncated or out of range.";
            return false;
        }

        if ((frameMbsOnly == 0 && !reader.TryReadBits(1, out _)) || !reader.TryReadBits(1, out _) || !reader.TryReadBits(1, out var cropping))
        {
            return false;
        }

        uint left = 0, right = 0, top = 0, bottom = 0;
        if (cropping == 1
            && (!reader.TryReadUnsigned(MaxDimension, out left) || !reader.TryReadUnsigned(MaxDimension, out right)
                || !reader.TryReadUnsigned(MaxDimension, out top) || !reader.TryReadUnsigned(MaxDimension, out bottom)))
        {
            error = "The SPS frame cropping offsets are truncated or out of range.";
            return false;
        }

        var chromaArrayType = separateColourPlane == 1 ? 0 : chromaFormat;
        var fieldFactor = 2 - (long)frameMbsOnly;
        long cropUnitX = chromaArrayType is 1 or 2 ? 2 : 1;
        var cropUnitY = chromaArrayType == 1 ? 2 * fieldFactor : fieldFactor;
        var width = ((widthInMbsMinus1 + 1L) * 16) - (cropUnitX * (left + right));
        var height = (fieldFactor * (heightInMapUnitsMinus1 + 1L) * 16) - (cropUnitY * (top + bottom));
        if (width is < 1 or > MaxDimension || height is < 1 or > MaxDimension)
        {
            error = $"The SPS gives a cropped picture of {width}x{height}; 1 to {MaxDimension} pixels per side are accepted.";
            return false;
        }

        sps = new AvcSequenceParameterSet
        {
            Profile = (int)profile,
            Level = (int)level,
            ChromaFormat = (int)chromaFormat,
            BitDepthLuma = (int)bitDepthLuma + 8,
            Width = (int)width,
            Height = (int)height,
        };
        error = null;
        return true;
    }

    /// <summary>Skips <c>seq_scaling_list_present_flag</c> entries and their scaling lists.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="count">Number of lists (8, or 12 for 4:4:4).</param>
    /// <returns><see langword="false"/> if truncated or a delta is out of range.</returns>
    private static bool TrySkipScalingLists(ref ExpGolombReader reader, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (!reader.TryReadBits(1, out var present))
            {
                return false;
            }

            if (present == 0)
            {
                continue;
            }

            var size = i < 6 ? 16 : 64;
            int last = 8, next = 8;
            for (var j = 0; j < size; j++)
            {
                if (next != 0)
                {
                    if (!reader.TryReadSigned(out var delta) || delta is < -128 or > 127)
                    {
                        return false;
                    }

                    next = (last + (int)delta + 256) % 256;
                }

                last = next == 0 ? last : next;
            }
        }

        return true;
    }

    /// <summary>Returns the RBSP of <paramref name="payload"/>: every 0x03 following two zero bytes is removed.</summary>
    /// <param name="payload">The NAL unit payload after its header.</param>
    /// <returns>A new array holding the RBSP.</returns>
    private static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> payload)
    {
        var result = new byte[payload.Length];
        var length = 0;
        var zeros = 0;
        foreach (var value in payload)
        {
            if (zeros >= 2 && value == 0x03)
            {
                zeros = 0;
                continue;
            }

            zeros = value == 0 ? zeros + 1 : 0;
            result[length++] = value;
        }

        return result.AsSpan(0, length).ToArray();
    }

    /// <summary>Bounds-checked big-endian bit reader with Exp-Golomb decoding.</summary>
    /// <param name="data">The RBSP.</param>
    private ref struct ExpGolombReader(ReadOnlySpan<byte> data)
    {
        /// <summary>The data.</summary>
        private readonly ReadOnlySpan<byte> _data = data;

        /// <summary>Bits consumed.</summary>
        private long _bit;

        /// <summary>Reads up to 32 bits.</summary>
        /// <param name="count">Number of bits, 1 to 32.</param>
        /// <param name="value">The value.</param>
        /// <returns><see langword="false"/> if fewer bits remain.</returns>
        public bool TryReadBits(int count, out uint value)
        {
            value = 0;
            if (_bit + count > _data.Length * 8L)
            {
                return false;
            }

            for (var i = 0; i < count; i++)
            {
                value = (value << 1) | (uint)((_data[(int)(_bit >> 3)] >> (7 - (int)(_bit & 7))) & 1);
                _bit++;
            }

            return true;
        }

        /// <summary>Reads an unsigned Exp-Golomb value (<c>ue(v)</c>) no greater than <paramref name="max"/>.</summary>
        /// <param name="max">The largest valid value.</param>
        /// <param name="value">The value.</param>
        /// <returns><see langword="false"/> if truncated, longer than 32 bits or above <paramref name="max"/>.</returns>
        public bool TryReadUnsigned(uint max, out uint value)
        {
            value = 0;
            var leadingZeros = 0;
            while (true)
            {
                if (!TryReadBits(1, out var bit))
                {
                    return false;
                }

                if (bit == 1)
                {
                    break;
                }

                if (++leadingZeros > 31)
                {
                    return false;
                }
            }

            if (!TryReadBits(leadingZeros, out var suffix))
            {
                return false;
            }

            var result = (1UL << leadingZeros) - 1 + suffix;
            if (result > max)
            {
                return false;
            }

            value = (uint)result;
            return true;
        }

        /// <summary>Reads a signed Exp-Golomb value (<c>se(v)</c>).</summary>
        /// <param name="value">The value.</param>
        /// <returns><see langword="false"/> if truncated or out of range.</returns>
        public bool TryReadSigned(out long value)
        {
            value = 0;
            if (!TryReadUnsigned(uint.MaxValue - 1, out var code))
            {
                return false;
            }

            value = (code & 1) == 1 ? (code + 1L) / 2 : -(code / 2L);
            return true;
        }
    }
}
