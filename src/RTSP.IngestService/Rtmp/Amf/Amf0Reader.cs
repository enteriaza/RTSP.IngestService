// Bounded AMF0 decoder for RTMP command and data message payloads. It reads values from a contiguous span (message
// payloads are fully reassembled before decoding) and enforces limits on nesting depth, collection size and the total
// number of values, so a malformed or hostile payload cannot force deep recursion or large allocations. AMF3 values
// (the avmplus-object marker), references, typed objects and XML are rejected as unsupported.

using System.Buffers.Binary;
using System.Text;
using RTSP.IngestService.Rtmp.Protocol;

namespace RTSP.IngestService.Rtmp.Amf;

/// <summary>
/// Reads AMF0 values from a byte span.
/// </summary>
/// <remarks>
/// Every malformed, truncated, unsupported or over-limit input raises <see cref="RtmpProtocolException"/>. Strings must
/// be valid UTF-8. The declared count of an ECMA array is ignored (encoders are inconsistent); the array ends at its
/// object-end marker. Allocation is bounded by the input length and <see cref="MaxValues"/>.
/// </remarks>
internal ref struct Amf0Reader
{
    /// <summary>Maximum nesting depth of objects and arrays.</summary>
    public const int MaxDepth = 16;

    /// <summary>Maximum number of properties or items in one object or array.</summary>
    public const int MaxCollectionItems = 1024;

    /// <summary>Maximum number of values, at every depth, decoded by one reader.</summary>
    public const int MaxValues = 4096;

    /// <summary>UTF-8 decoder that throws on invalid sequences.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The encoded data.</summary>
    private readonly ReadOnlySpan<byte> _data;

    /// <summary>Read position.</summary>
    private int _position;

    /// <summary>Values decoded so far.</summary>
    private int _valueCount;

    /// <summary>Initializes a new instance of the <see cref="Amf0Reader"/> struct.</summary>
    /// <param name="data">The encoded data.</param>
    public Amf0Reader(ReadOnlySpan<byte> data)
    {
        _data = data;
    }

    /// <summary>Gets a value indicating whether all data has been read.</summary>
    public readonly bool End => _position >= _data.Length;

    /// <summary>Gets the read position.</summary>
    public readonly int Position => _position;

    /// <summary>Decodes every value in <paramref name="data"/>.</summary>
    /// <param name="data">The encoded data.</param>
    /// <returns>The top-level values in order.</returns>
    /// <exception cref="RtmpProtocolException">The data is malformed, truncated, unsupported or over a limit.</exception>
    public static List<AmfValue> ReadAll(ReadOnlySpan<byte> data)
    {
        var reader = new Amf0Reader(data);
        var values = new List<AmfValue>();
        while (!reader.End)
        {
            values.Add(reader.ReadValue());
        }

        return values;
    }

    /// <summary>Decodes the next value.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="RtmpProtocolException">The data is malformed, truncated, unsupported or over a limit.</exception>
    public AmfValue ReadValue() => ReadValue(0);

    /// <summary>Decodes the next value at <paramref name="depth"/>.</summary>
    /// <param name="depth">Nesting depth of the value; 0 for top-level values.</param>
    /// <returns>The value.</returns>
    private AmfValue ReadValue(int depth)
    {
        if (++_valueCount > MaxValues)
        {
            throw new RtmpProtocolException($"The AMF0 data contains more than {MaxValues} values.");
        }

        var marker = ReadByte();
        switch (marker)
        {
            case 0x00:
                return AmfValue.FromNumber(BinaryPrimitives.ReadDoubleBigEndian(Take(8)));

            case 0x01:
                return AmfValue.FromBoolean(ReadByte() != 0);

            case 0x02:
                return AmfValue.FromString(ReadString(BinaryPrimitives.ReadUInt16BigEndian(Take(2))));

            case 0x03:
                return AmfValue.CreateObject(ReadProperties(depth + 1));

            case 0x05:
                return AmfValue.Null;

            case 0x06:
                return AmfValue.Undefined;

            case 0x08:
                Take(4);
                return AmfValue.CreateEcmaArray(ReadProperties(depth + 1));

            case 0x09:
                throw new RtmpProtocolException("Unexpected AMF0 object-end marker.");

            case 0x0A:
                return AmfValue.CreateStrictArray(ReadItems(depth + 1));

            case 0x0B:
                var milliseconds = BinaryPrimitives.ReadDoubleBigEndian(Take(8));
                var timeZone = BinaryPrimitives.ReadInt16BigEndian(Take(2));
                return AmfValue.FromDate(milliseconds, timeZone);

            case 0x0C:
                var length = BinaryPrimitives.ReadUInt32BigEndian(Take(4));
                if (length > int.MaxValue)
                {
                    throw Truncated();
                }

                return AmfValue.FromString(ReadString((int)length));

            case 0x11:
                throw new RtmpProtocolException("AMF3 values (avmplus-object marker) are not supported.");

            default:
                throw new RtmpProtocolException($"Unsupported AMF0 type marker 0x{marker:X2}.");
        }
    }

    /// <summary>Reads object or ECMA array properties up to and including the object-end marker.</summary>
    /// <param name="depth">Depth of the container.</param>
    /// <returns>The properties.</returns>
    private List<KeyValuePair<string, AmfValue>> ReadProperties(int depth)
    {
        EnterContainer(depth);
        var properties = new List<KeyValuePair<string, AmfValue>>();
        while (true)
        {
            var nameLength = BinaryPrimitives.ReadUInt16BigEndian(Take(2));
            if (nameLength == 0)
            {
                if (ReadByte() != 0x09)
                {
                    throw new RtmpProtocolException("An AMF0 property has an empty name.");
                }

                return properties;
            }

            if (properties.Count >= MaxCollectionItems)
            {
                throw new RtmpProtocolException($"An AMF0 object has more than {MaxCollectionItems} properties.");
            }

            var name = ReadString(nameLength);
            properties.Add(new KeyValuePair<string, AmfValue>(name, ReadValue(depth)));
        }
    }

    /// <summary>Reads a strict array's count and items.</summary>
    /// <param name="depth">Depth of the container.</param>
    /// <returns>The items.</returns>
    private List<AmfValue> ReadItems(int depth)
    {
        EnterContainer(depth);
        var count = BinaryPrimitives.ReadUInt32BigEndian(Take(4));
        if (count > MaxCollectionItems)
        {
            throw new RtmpProtocolException($"An AMF0 strict array declares {count} items; the limit is {MaxCollectionItems}.");
        }

        if (count > _data.Length - _position)
        {
            throw Truncated();
        }

        var items = new List<AmfValue>((int)count);
        for (var i = 0; i < count; i++)
        {
            items.Add(ReadValue(depth));
        }

        return items;
    }

    /// <summary>Rejects containers nested deeper than <see cref="MaxDepth"/>.</summary>
    /// <param name="depth">Depth of the container.</param>
    private static void EnterContainer(int depth)
    {
        if (depth > MaxDepth)
        {
            throw new RtmpProtocolException($"AMF0 values are nested deeper than {MaxDepth} levels.");
        }
    }

    /// <summary>Reads <paramref name="length"/> bytes as strict UTF-8.</summary>
    /// <param name="length">Byte length.</param>
    /// <returns>The string.</returns>
    private string ReadString(int length)
    {
        var bytes = Take(length);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new RtmpProtocolException("An AMF0 string is not valid UTF-8.", ex);
        }
    }

    /// <summary>Reads one byte.</summary>
    /// <returns>The byte.</returns>
    private byte ReadByte() => Take(1)[0];

    /// <summary>Consumes <paramref name="count"/> bytes.</summary>
    /// <param name="count">Number of bytes.</param>
    /// <returns>The bytes.</returns>
    private ReadOnlySpan<byte> Take(int count)
    {
        if (count > _data.Length - _position)
        {
            throw Truncated();
        }

        var slice = _data.Slice(_position, count);
        _position += count;
        return slice;
    }

    /// <summary>Creates the exception for data that ends inside a value.</summary>
    /// <returns>The exception.</returns>
    private static RtmpProtocolException Truncated() => new("The AMF0 data is truncated.");
}
