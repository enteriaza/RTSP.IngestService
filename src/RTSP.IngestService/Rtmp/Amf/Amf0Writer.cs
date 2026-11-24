// AMF0 encoder for the values the server sends in RTMP command responses and status messages. Strings longer than
// 65535 UTF-8 bytes are written with the long-string marker; property names are limited to 65535 bytes as AMF0
// requires.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace RTSP.IngestService.Rtmp.Amf;

/// <summary>
/// Writes AMF0 values to an <see cref="IBufferWriter{T}"/>.
/// </summary>
internal static class Amf0Writer
{
    /// <summary>Writes <paramref name="value"/> and, recursively, its properties or items.</summary>
    /// <param name="output">Destination.</param>
    /// <param name="value">The value.</param>
    /// <exception cref="ArgumentException">A property name exceeds 65535 UTF-8 bytes.</exception>
    public static void WriteValue(IBufferWriter<byte> output, AmfValue value)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(value);

        switch (value.Kind)
        {
            case AmfValueKind.Number:
                var number = output.GetSpan(9);
                number[0] = 0x00;
                BinaryPrimitives.WriteDoubleBigEndian(number[1..], value.NumberValue);
                output.Advance(9);
                break;

            case AmfValueKind.Boolean:
                var boolean = output.GetSpan(2);
                boolean[0] = 0x01;
                boolean[1] = value.BooleanValue ? (byte)1 : (byte)0;
                output.Advance(2);
                break;

            case AmfValueKind.Text:
                WriteString(output, value.StringValue);
                break;

            case AmfValueKind.AnonymousObject:
                WriteMarker(output, 0x03);
                WriteProperties(output, value.Properties);
                break;

            case AmfValueKind.Null:
                WriteMarker(output, 0x05);
                break;

            case AmfValueKind.Undefined:
                WriteMarker(output, 0x06);
                break;

            case AmfValueKind.EcmaArray:
                var header = output.GetSpan(5);
                header[0] = 0x08;
                BinaryPrimitives.WriteUInt32BigEndian(header[1..], (uint)value.Properties.Count);
                output.Advance(5);
                WriteProperties(output, value.Properties);
                break;

            case AmfValueKind.StrictArray:
                var array = output.GetSpan(5);
                array[0] = 0x0A;
                BinaryPrimitives.WriteUInt32BigEndian(array[1..], (uint)value.Items.Count);
                output.Advance(5);
                foreach (var item in value.Items)
                {
                    WriteValue(output, item);
                }

                break;

            case AmfValueKind.Date:
                var date = output.GetSpan(11);
                date[0] = 0x0B;
                BinaryPrimitives.WriteDoubleBigEndian(date[1..], value.NumberValue);
                BinaryPrimitives.WriteInt16BigEndian(date[9..], value.TimeZone);
                output.Advance(11);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(value), value.Kind, "Unsupported AMF value kind.");
        }
    }

    /// <summary>Writes a string with the short (0x02) or long (0x0C) string marker.</summary>
    /// <param name="output">Destination.</param>
    /// <param name="value">The string.</param>
    private static void WriteString(IBufferWriter<byte> output, string value)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        if (length <= ushort.MaxValue)
        {
            var span = output.GetSpan(3 + length);
            span[0] = 0x02;
            BinaryPrimitives.WriteUInt16BigEndian(span[1..], (ushort)length);
            Encoding.UTF8.GetBytes(value, span[3..]);
            output.Advance(3 + length);
        }
        else
        {
            var span = output.GetSpan(5 + length);
            span[0] = 0x0C;
            BinaryPrimitives.WriteUInt32BigEndian(span[1..], (uint)length);
            Encoding.UTF8.GetBytes(value, span[5..]);
            output.Advance(5 + length);
        }
    }

    /// <summary>Writes name/value pairs followed by the object-end marker.</summary>
    /// <param name="output">Destination.</param>
    /// <param name="properties">The properties.</param>
    private static void WriteProperties(IBufferWriter<byte> output, IReadOnlyList<KeyValuePair<string, AmfValue>> properties)
    {
        foreach (var (name, value) in properties)
        {
            var length = Encoding.UTF8.GetByteCount(name);
            if (length is 0 or > ushort.MaxValue)
            {
                throw new ArgumentException("AMF0 property names must be 1 to 65535 UTF-8 bytes.", nameof(properties));
            }

            var span = output.GetSpan(2 + length);
            BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)length);
            Encoding.UTF8.GetBytes(name, span[2..]);
            output.Advance(2 + length);
            WriteValue(output, value);
        }

        var end = output.GetSpan(3);
        end[0] = 0;
        end[1] = 0;
        end[2] = 0x09;
        output.Advance(3);
    }

    /// <summary>Writes a single marker byte.</summary>
    /// <param name="output">Destination.</param>
    /// <param name="marker">The marker.</param>
    private static void WriteMarker(IBufferWriter<byte> output, byte marker)
    {
        output.GetSpan(1)[0] = marker;
        output.Advance(1);
    }
}
