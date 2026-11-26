// Bounds-checked big-endian bit reader shared by the source codec header parsers. Every read reports truncation
// instead of throwing, so malformed publisher data is rejected through the normal error path.

namespace RTSP.IngestService.Transcoding.Codecs;

/// <summary>Reads bits, Exp-Golomb codes (H.264/HEVC) and uvlc/leb128 values (AV1) from a byte span.</summary>
/// <param name="data">The data.</param>
internal ref struct BitReader(ReadOnlySpan<byte> data)
{
    /// <summary>The data.</summary>
    private readonly ReadOnlySpan<byte> _data = data;

    /// <summary>Bits consumed.</summary>
    private long _bit;

    /// <summary>Gets the number of bits not yet read.</summary>
    public readonly long RemainingBits => (_data.Length * 8L) - _bit;

    /// <summary>Reads up to 32 bits.</summary>
    /// <param name="count">Number of bits, 0 to 32.</param>
    /// <param name="value">The value.</param>
    /// <returns><see langword="false"/> if fewer bits remain.</returns>
    public bool TryRead(int count, out uint value)
    {
        value = 0;
        if (count > RemainingBits)
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

    /// <summary>Skips bits.</summary>
    /// <param name="count">Number of bits.</param>
    /// <returns><see langword="false"/> if fewer bits remain.</returns>
    public bool TrySkip(long count)
    {
        if (count > RemainingBits)
        {
            return false;
        }

        _bit += count;
        return true;
    }

    /// <summary>Reads an unsigned Exp-Golomb value (<c>ue(v)</c>) no greater than <paramref name="max"/>.</summary>
    /// <param name="max">The largest valid value.</param>
    /// <param name="value">The value.</param>
    /// <returns><see langword="false"/> if truncated, longer than 32 bits or above <paramref name="max"/>.</returns>
    public bool TryReadExpGolomb(uint max, out uint value)
    {
        value = 0;
        var leadingZeros = 0;
        while (true)
        {
            if (!TryRead(1, out var bit))
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

        if (!TryRead(leadingZeros, out var suffix))
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

    /// <summary>Reads an AV1 <c>uvlc()</c> value, saturating at <see cref="uint.MaxValue"/> as the specification does.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="false"/> if truncated.</returns>
    public bool TryReadUvlc(out uint value)
    {
        value = 0;
        var leadingZeros = 0;
        while (true)
        {
            if (!TryRead(1, out var bit))
            {
                return false;
            }

            if (bit == 1)
            {
                break;
            }

            leadingZeros++;
            if (leadingZeros >= 32)
            {
                value = uint.MaxValue;
                return true;
            }
        }

        if (!TryRead(leadingZeros, out var suffix))
        {
            return false;
        }

        value = (uint)((1UL << leadingZeros) - 1 + suffix);
        return true;
    }

    /// <summary>Returns the RBSP of a NAL unit payload: every <c>0x03</c> after two zero bytes is removed.</summary>
    /// <param name="payload">The NAL unit payload after its header.</param>
    /// <returns>The RBSP.</returns>
    public static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> payload)
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

    /// <summary>Reads an AV1 <c>leb128()</c> value of at most eight bytes from <paramref name="data"/>.</summary>
    /// <param name="data">The data.</param>
    /// <param name="value">The value.</param>
    /// <param name="length">Bytes consumed.</param>
    /// <returns><see langword="false"/> if truncated or larger than <see cref="int.MaxValue"/>.</returns>
    public static bool TryReadLeb128(ReadOnlySpan<byte> data, out int value, out int length)
    {
        value = 0;
        length = 0;
        ulong result = 0;
        for (var i = 0; i < 8; i++)
        {
            if (i >= data.Length)
            {
                return false;
            }

            var b = data[i];
            result |= (ulong)(b & 0x7F) << (i * 7);
            if ((b & 0x80) == 0)
            {
                if (result > int.MaxValue)
                {
                    return false;
                }

                value = (int)result;
                length = i + 1;
                return true;
            }
        }

        return false;
    }
}
