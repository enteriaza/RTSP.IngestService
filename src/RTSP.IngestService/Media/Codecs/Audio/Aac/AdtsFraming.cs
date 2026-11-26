// ADTS framing (ISO/IEC 13818-7 / 14496-3 1.A.2): splitting an ADTS byte stream into raw AAC access units with the
// AudioSpecificConfig its fixed header implies, and wrapping raw access units in headers. Used by the managed codec
// smoke command; the ingest pipeline carries raw access units with an AudioSpecificConfig and does not use ADTS.

using RTSP.IngestService.Media.Codecs.Common.Bitstreams;

namespace RTSP.IngestService.Media.Codecs.Audio.Aac;

/// <summary>ADTS parsing and writing.</summary>
internal static class AdtsFraming
{
    /// <summary>The header length without CRC.</summary>
    public const int HeaderLength = 7;

    /// <summary>Splits an ADTS stream into raw access units.</summary>
    /// <param name="data">The stream.</param>
    /// <param name="audioSpecificConfig">The two-byte AudioSpecificConfig of the first header.</param>
    /// <returns>The raw access units, in order.</returns>
    /// <exception cref="CodecException">The stream is malformed, changes format or uses multiple raw blocks per frame.</exception>
    public static List<ReadOnlyMemory<byte>> Read(ReadOnlyMemory<byte> data, out byte[] audioSpecificConfig)
    {
        var units = new List<ReadOnlyMemory<byte>>();
        audioSpecificConfig = [];
        var position = 0;
        while (position < data.Length)
        {
            if (data.Length - position < HeaderLength)
            {
                CodecException.InvalidData("The ADTS stream ends inside a header.");
            }

            var reader = new BitstreamReader(data.Span.Slice(position, HeaderLength));
            if (reader.Read(12) != 0xFFF)
            {
                CodecException.InvalidData("An ADTS sync word is missing.");
            }

            reader.Skip(3);
            var protectionAbsent = reader.ReadBit();
            var objectType = reader.Read(2) + 1;
            var frequencyIndex = reader.Read(4);
            reader.Skip(1);
            var channels = reader.Read(3);
            reader.Skip(4);
            var length = (int)reader.Read(13);
            reader.Skip(11);
            var blocks = reader.Read(2);
            var header = HeaderLength + (protectionAbsent ? 0 : 2);
            if (blocks != 0)
            {
                CodecException.Unsupported("ADTS frames with more than one raw data block are not supported.");
            }

            if (length <= header || position + length > data.Length)
            {
                CodecException.InvalidData("An ADTS frame length is invalid.");
            }

            byte[] config = [(byte)((objectType << 3) | (frequencyIndex >> 1)), (byte)(((frequencyIndex & 1) << 7) | (channels << 3))];
            if (units.Count == 0)
            {
                audioSpecificConfig = config;
            }
            else if (!config.AsSpan().SequenceEqual(audioSpecificConfig))
            {
                CodecException.Unsupported("The ADTS stream changes format.");
            }

            units.Add(data.Slice(position + header, length - header));
            position += length;
        }

        return units;
    }

    /// <summary>Writes one access unit with an ADTS header (no CRC).</summary>
    /// <param name="output">The destination.</param>
    /// <param name="audioSpecificConfig">A two-byte AAC-LC AudioSpecificConfig.</param>
    /// <param name="accessUnit">The raw access unit.</param>
    public static void Write(Stream output, ReadOnlySpan<byte> audioSpecificConfig, ReadOnlySpan<byte> accessUnit)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfLessThan(audioSpecificConfig.Length, 2, nameof(audioSpecificConfig));
        var objectType = audioSpecificConfig[0] >> 3;
        var frequencyIndex = ((audioSpecificConfig[0] & 7) << 1) | (audioSpecificConfig[1] >> 7);
        var channels = (audioSpecificConfig[1] >> 3) & 15;
        var length = HeaderLength + accessUnit.Length;
        if (objectType is < 1 or > 4 || channels > 7 || length >= 1 << 13)
        {
            throw new ArgumentException("The configuration or access unit cannot be expressed in ADTS.", nameof(audioSpecificConfig));
        }

        Span<byte> header = stackalloc byte[HeaderLength];
        var writer = new BitstreamWriter(header);
        writer.Write(0xFFF, 12);
        writer.Write(0, 3);
        writer.WriteBit(true);
        writer.Write((uint)objectType - 1, 2);
        writer.Write((uint)frequencyIndex, 4);
        writer.WriteBit(false);
        writer.Write((uint)channels, 3);
        writer.Write(0, 4);
        writer.Write((uint)length, 13);
        writer.Write(0x7FF, 11);
        writer.Write(0, 2);
        output.Write(header);
        output.Write(accessUnit);
    }
}
