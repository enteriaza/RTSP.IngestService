// Tests for the AMF0 reader and writer: byte-exact encodings from the AMF0 specification, round trips of every
// supported type, and rejection of truncated, malformed, unsupported and over-limit input.

using System.Buffers;
using RTSP.IngestService.Rtmp.Amf;
using RTSP.IngestService.Rtmp.Protocol;

namespace RTSP.IngestService.Tests.Rtmp;

/// <summary>
/// Tests for <see cref="Amf0Reader"/> and <see cref="Amf0Writer"/>.
/// </summary>
public sealed class Amf0Tests
{
    /// <summary>Verifies the wire encoding of each scalar type against the AMF0 specification.</summary>
    [Fact]
    public void Writer_ScalarEncodings_MatchSpecification()
    {
        Assert.Equal([0x00, 0x3F, 0xF0, 0, 0, 0, 0, 0, 0], Encode(AmfValue.FromNumber(1)));
        Assert.Equal([0x01, 0x01], Encode(AmfValue.True));
        Assert.Equal([0x01, 0x00], Encode(AmfValue.False));
        Assert.Equal([0x02, 0x00, 0x02, (byte)'h', (byte)'i'], Encode(AmfValue.FromString("hi")));
        Assert.Equal([0x02, 0x00, 0x00], Encode(AmfValue.FromString(string.Empty)));
        Assert.Equal([0x05], Encode(AmfValue.Null));
        Assert.Equal([0x06], Encode(AmfValue.Undefined));
        Assert.Equal([0x0B, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFE], Encode(AmfValue.FromDate(0, -2)));
    }

    /// <summary>Verifies the wire encoding of an object, an ECMA array and a strict array.</summary>
    [Fact]
    public void Writer_ContainerEncodings_MatchSpecification()
    {
        Assert.Equal([0x03, 0x00, 0x01, (byte)'a', 0x01, 0x01, 0x00, 0x00, 0x09], Encode(AmfValue.CreateObject([new("a", AmfValue.True)])));
        Assert.Equal([0x08, 0, 0, 0, 1, 0x00, 0x01, (byte)'a', 0x05, 0x00, 0x00, 0x09], Encode(AmfValue.CreateEcmaArray([new("a", AmfValue.Null)])));
        Assert.Equal([0x0A, 0, 0, 0, 2, 0x05, 0x06], Encode(AmfValue.CreateStrictArray([AmfValue.Null, AmfValue.Undefined])));
    }

    /// <summary>Verifies that strings longer than 65535 UTF-8 bytes use the long-string marker and round trip.</summary>
    [Fact]
    public void LongString_UsesLongStringMarkerAndRoundTrips()
    {
        var text = new string('x', 70_000);
        var bytes = Encode(AmfValue.FromString(text));

        Assert.Equal(0x0C, bytes[0]);
        Assert.Equal(text, Assert.Single(Amf0Reader.ReadAll(bytes)).StringValue);
    }

    /// <summary>Verifies that each supported value round trips and re-encodes to identical bytes.</summary>
    /// <param name="name">Name of the case in <see cref="RoundTripValues"/>.</param>
    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void Value_RoundTripsToIdenticalBytes(string name)
    {
        var value = RoundTripValues[name];
        var bytes = Encode(value);

        var decoded = Assert.Single(Amf0Reader.ReadAll(bytes));

        Assert.Equal(value.Kind, decoded.Kind);
        Assert.Equal(bytes, Encode(decoded));
    }

    /// <summary>Verifies that decoded nested objects expose properties in wire order with typed lookups.</summary>
    [Fact]
    public void NestedObject_ExposesPropertiesInOrder()
    {
        var bytes = Encode(AmfValue.CreateObject(
        [
            new("width", AmfValue.FromNumber(1280)),
            new("encoder", AmfValue.FromString("obs")),
            new("inner", AmfValue.CreateObject([new("deep", AmfValue.CreateStrictArray([AmfValue.FromNumber(3)]))])),
        ]));

        var value = Assert.Single(Amf0Reader.ReadAll(bytes));

        Assert.Equal(["width", "encoder", "inner"], value.Properties.Select(static p => p.Key));
        Assert.Equal(1280, value.GetNumberProperty("width"));
        Assert.Equal("obs", value.GetStringProperty("encoder"));
        Assert.Null(value.GetStringProperty("width"));
        Assert.Equal(3, value.GetProperty("inner")!.GetProperty("deep")!.Items[0].NumberValue);
    }

    /// <summary>Verifies that an ECMA array's declared count is ignored and the array ends at its object-end marker.</summary>
    [Fact]
    public void EcmaArray_DeclaredCountIsIgnored()
    {
        byte[] bytes = [0x08, 0, 0, 0, 99, 0x00, 0x01, (byte)'k', 0x01, 0x00, 0x00, 0x00, 0x09, 0x05];

        var values = Amf0Reader.ReadAll(bytes);

        Assert.Equal(AmfValueKind.EcmaArray, values[0].Kind);
        Assert.Single(values[0].Properties);
        Assert.Equal(AmfValueKind.Null, values[1].Kind);
    }

    /// <summary>Verifies that several top-level values, as in a command message, are read in order.</summary>
    [Fact]
    public void ReadAll_ReadsCommandSequence()
    {
        var writer = new ArrayBufferWriter<byte>();
        Amf0Writer.WriteValue(writer, AmfValue.FromString("connect"));
        Amf0Writer.WriteValue(writer, AmfValue.FromNumber(1));
        Amf0Writer.WriteValue(writer, AmfValue.CreateObject([new("app", AmfValue.FromString("live"))]));

        var values = Amf0Reader.ReadAll(writer.WrittenSpan);

        Assert.Equal("connect", values[0].StringValue);
        Assert.Equal(1, values[1].NumberValue);
        Assert.Equal("live", values[2].GetStringProperty("app"));
    }

    /// <summary>Verifies that nesting up to the maximum depth is accepted and one level deeper is rejected.</summary>
    [Fact]
    public void Nesting_IsLimitedToMaxDepth()
    {
        Assert.Single(Amf0Reader.ReadAll(Encode(Nested(Amf0Reader.MaxDepth))));

        Assert.Throws<RtmpProtocolException>(() => Amf0Reader.ReadAll(Encode(Nested(Amf0Reader.MaxDepth + 1))));
    }

    /// <summary>Verifies that an object with more than the maximum number of properties is rejected.</summary>
    [Fact]
    public void Object_WithTooManyProperties_IsRejected()
    {
        var bytes = Encode(AmfValue.CreateObject(Enumerable.Range(0, Amf0Reader.MaxCollectionItems + 1).Select(static i => new KeyValuePair<string, AmfValue>($"p{i}", AmfValue.Null))));

        Assert.Throws<RtmpProtocolException>(() => Amf0Reader.ReadAll(bytes));
    }

    /// <summary>Verifies that more than the maximum total number of values is rejected even when each container is within limits.</summary>
    [Fact]
    public void TotalValues_AreLimited()
    {
        var bytes = Enumerable.Repeat((byte)0x05, Amf0Reader.MaxValues + 1).ToArray();

        Assert.Throws<RtmpProtocolException>(() => Amf0Reader.ReadAll(bytes));
    }

    /// <summary>Verifies that malformed, truncated and unsupported inputs raise <see cref="RtmpProtocolException"/>.</summary>
    /// <param name="hex">The input bytes in hexadecimal.</param>
    [Theory]
    [InlineData("00 3F F0 00 00")]                    // number truncated
    [InlineData("01")]                                // boolean without value
    [InlineData("02 00 05 61 62")]                    // string shorter than its length
    [InlineData("02 00")]                             // string length truncated
    [InlineData("0C 00 01 00 00 61")]                 // long string shorter than its length
    [InlineData("0C FF FF FF FF")]                    // long string length beyond int range
    [InlineData("03 00 01 61 05")]                    // object without end marker
    [InlineData("03 00 00 05")]                       // empty property name not followed by object end
    [InlineData("03 00 01 61")]                       // property without value
    [InlineData("08 00 00 00 01")]                    // ECMA array truncated
    [InlineData("0A 00 00 00 03 05")]                 // strict array with fewer items than declared
    [InlineData("0A 00 00 04 01")]                    // strict array declaring more items than the limit
    [InlineData("0A 00 00 00")]                       // strict array count truncated
    [InlineData("0B 00 00 00 00 00 00 00 00 00")]     // date without full time zone
    [InlineData("09")]                                // object end outside an object
    [InlineData("11 00")]                             // AMF3 switch
    [InlineData("07 00 01")]                          // reference
    [InlineData("10 00 01 61 00 00 09")]              // typed object
    [InlineData("0D")]                                // unsupported
    [InlineData("0F 00 00 00 00")]                    // XML document
    [InlineData("02 00 02 C3 28")]                    // invalid UTF-8
    public void MalformedInput_IsRejected(string hex)
    {
        var bytes = Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal));

        Assert.Throws<RtmpProtocolException>(() => Amf0Reader.ReadAll(bytes));
    }

    /// <summary>Verifies that an empty payload yields no values.</summary>
    [Fact]
    public void EmptyInput_YieldsNoValues()
    {
        Assert.Empty(Amf0Reader.ReadAll([]));
    }

    /// <summary>Gets the names of the round-trip cases.</summary>
    public static TheoryData<string> RoundTripCases => [.. RoundTripValues.Keys];

    /// <summary>Gets one value of every supported kind, including edge cases.</summary>
    private static Dictionary<string, AmfValue> RoundTripValues => new()
    {
        ["number"] = AmfValue.FromNumber(-1234.5),
        ["number-nan"] = AmfValue.FromNumber(double.NaN),
        ["boolean"] = AmfValue.True,
        ["string"] = AmfValue.FromString("NetStream.Publish.Start"),
        ["string-empty"] = AmfValue.FromString(string.Empty),
        ["string-unicode"] = AmfValue.FromString("caméra ✓"),
        ["null"] = AmfValue.Null,
        ["undefined"] = AmfValue.Undefined,
        ["date"] = AmfValue.FromDate(1_700_000_000_000),
        ["object-empty"] = AmfValue.CreateObject([]),
        ["object-duplicate-keys"] = AmfValue.CreateObject([new("a", AmfValue.True), new("a", AmfValue.False)]),
        ["ecma-array"] = AmfValue.CreateEcmaArray([new("duration", AmfValue.FromNumber(0)), new("encoder", AmfValue.FromString("x"))]),
        ["strict-array"] = AmfValue.CreateStrictArray([AmfValue.FromNumber(1), AmfValue.FromString("two"), AmfValue.Null]),
        ["strict-array-empty"] = AmfValue.CreateStrictArray([]),
        ["nested"] = Nested(8),
    };

    /// <summary>Encodes one value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The bytes.</returns>
    private static byte[] Encode(AmfValue value)
    {
        var writer = new ArrayBufferWriter<byte>();
        Amf0Writer.WriteValue(writer, value);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>Builds objects nested <paramref name="depth"/> levels deep.</summary>
    /// <param name="depth">Number of nested objects.</param>
    /// <returns>The outermost object.</returns>
    private static AmfValue Nested(int depth)
    {
        var value = AmfValue.CreateObject([]);
        for (var i = 1; i < depth; i++)
        {
            value = AmfValue.CreateObject([new("child", value)]);
        }

        return value;
    }
}
