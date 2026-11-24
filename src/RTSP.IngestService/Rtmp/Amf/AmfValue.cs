// Immutable AMF0 value model covering the types used by RTMP connection, publishing and metadata messages: number,
// boolean, string, object, null, undefined, ECMA array, strict array and date. Object and ECMA array properties keep
// their wire order; duplicate names are preserved and lookups return the first match.

namespace RTSP.IngestService.Rtmp.Amf;

/// <summary>The kind of an <see cref="AmfValue"/>.</summary>
public enum AmfValueKind
{
    /// <summary>IEEE 754 double.</summary>
    Number,

    /// <summary>Boolean.</summary>
    Boolean,

    /// <summary>UTF-8 string (short or long string marker on the wire).</summary>
    Text,

    /// <summary>Anonymous object: ordered name/value pairs.</summary>
    AnonymousObject,

    /// <summary>Null.</summary>
    Null,

    /// <summary>Undefined.</summary>
    Undefined,

    /// <summary>ECMA (associative) array: ordered name/value pairs.</summary>
    EcmaArray,

    /// <summary>Strict (dense) array.</summary>
    StrictArray,

    /// <summary>Date: milliseconds since the Unix epoch, with a time-zone field.</summary>
    Date,
}

/// <summary>
/// An immutable AMF0 value.
/// </summary>
/// <remarks>Instances are immutable and safe to share between threads.</remarks>
public sealed class AmfValue
{
    /// <summary>Empty property list shared by values without properties.</summary>
    private static readonly KeyValuePair<string, AmfValue>[] NoProperties = [];

    /// <summary>Empty item list shared by values without items.</summary>
    private static readonly AmfValue[] NoItems = [];

    /// <summary>Numeric value for <see cref="AmfValueKind.Number"/> and <see cref="AmfValueKind.Date"/>.</summary>
    private readonly double _number;

    /// <summary>Value for <see cref="AmfValueKind.Boolean"/>.</summary>
    private readonly bool _boolean;

    /// <summary>Value for <see cref="AmfValueKind.Text"/>.</summary>
    private readonly string? _string;

    /// <summary>Initializes a new instance of the <see cref="AmfValue"/> class.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="number">Numeric value.</param>
    /// <param name="boolean">Boolean value.</param>
    /// <param name="text">String value.</param>
    /// <param name="properties">Properties.</param>
    /// <param name="items">Items.</param>
    /// <param name="timeZone">Date time-zone field.</param>
    private AmfValue(
        AmfValueKind kind,
        double number = 0,
        bool boolean = false,
        string? text = null,
        IReadOnlyList<KeyValuePair<string, AmfValue>>? properties = null,
        IReadOnlyList<AmfValue>? items = null,
        short timeZone = 0)
    {
        Kind = kind;
        _number = number;
        _boolean = boolean;
        _string = text;
        Properties = properties ?? NoProperties;
        Items = items ?? NoItems;
        TimeZone = timeZone;
    }

    /// <summary>Gets the AMF0 null value.</summary>
    public static AmfValue Null { get; } = new(AmfValueKind.Null);

    /// <summary>Gets the AMF0 undefined value.</summary>
    public static AmfValue Undefined { get; } = new(AmfValueKind.Undefined);

    /// <summary>Gets the boolean <see langword="true"/> value.</summary>
    public static AmfValue True { get; } = new(AmfValueKind.Boolean, boolean: true);

    /// <summary>Gets the boolean <see langword="false"/> value.</summary>
    public static AmfValue False { get; } = new(AmfValueKind.Boolean, boolean: false);

    /// <summary>Gets the kind of value.</summary>
    public AmfValueKind Kind { get; }

    /// <summary>Gets the number, or the date's milliseconds since the Unix epoch.</summary>
    /// <exception cref="InvalidOperationException">The value is not a number or date.</exception>
    public double NumberValue => Kind is AmfValueKind.Number or AmfValueKind.Date ? _number : throw WrongKind(AmfValueKind.Number);

    /// <summary>Gets the boolean.</summary>
    /// <exception cref="InvalidOperationException">The value is not a boolean.</exception>
    public bool BooleanValue => Kind == AmfValueKind.Boolean ? _boolean : throw WrongKind(AmfValueKind.Boolean);

    /// <summary>Gets the string.</summary>
    /// <exception cref="InvalidOperationException">The value is not a string.</exception>
    public string StringValue => Kind == AmfValueKind.Text ? _string! : throw WrongKind(AmfValueKind.Text);

    /// <summary>Gets the ordered properties of an object or ECMA array; empty for other kinds.</summary>
    public IReadOnlyList<KeyValuePair<string, AmfValue>> Properties { get; }

    /// <summary>Gets the items of a strict array; empty for other kinds.</summary>
    public IReadOnlyList<AmfValue> Items { get; }

    /// <summary>Gets the time-zone field of a date (AMF0 reserves it and writers send 0); 0 for other kinds.</summary>
    public short TimeZone { get; }

    /// <summary>Creates a number.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The value.</returns>
    public static AmfValue FromNumber(double value) => new(AmfValueKind.Number, number: value);

    /// <summary>Returns <see cref="True"/> or <see cref="False"/>.</summary>
    /// <param name="value">The boolean.</param>
    /// <returns>The value.</returns>
    public static AmfValue FromBoolean(bool value) => value ? True : False;

    /// <summary>Creates a string.</summary>
    /// <param name="value">The string.</param>
    /// <returns>The value.</returns>
    public static AmfValue FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new AmfValue(AmfValueKind.Text, text: value);
    }

    /// <summary>Creates a date.</summary>
    /// <param name="millisecondsSinceEpoch">Milliseconds since 1970-01-01T00:00:00Z.</param>
    /// <param name="timeZone">The time-zone field.</param>
    /// <returns>The value.</returns>
    public static AmfValue FromDate(double millisecondsSinceEpoch, short timeZone = 0) => new(AmfValueKind.Date, number: millisecondsSinceEpoch, timeZone: timeZone);

    /// <summary>Creates an anonymous object.</summary>
    /// <param name="properties">Properties in wire order; copied.</param>
    /// <returns>The value.</returns>
    public static AmfValue CreateObject(IEnumerable<KeyValuePair<string, AmfValue>> properties) => new(AmfValueKind.AnonymousObject, properties: [.. properties]);

    /// <summary>Creates an ECMA array.</summary>
    /// <param name="properties">Properties in wire order; copied.</param>
    /// <returns>The value.</returns>
    public static AmfValue CreateEcmaArray(IEnumerable<KeyValuePair<string, AmfValue>> properties) => new(AmfValueKind.EcmaArray, properties: [.. properties]);

    /// <summary>Creates a strict array.</summary>
    /// <param name="items">Items; copied.</param>
    /// <returns>The value.</returns>
    public static AmfValue CreateStrictArray(IEnumerable<AmfValue> items) => new(AmfValueKind.StrictArray, items: [.. items]);

    /// <summary>Returns the first property named <paramref name="name"/> (ordinal comparison).</summary>
    /// <param name="name">The property name.</param>
    /// <returns>The value, or <see langword="null"/> if absent or this value has no properties.</returns>
    public AmfValue? GetProperty(string name)
    {
        foreach (var property in Properties)
        {
            if (string.Equals(property.Key, name, StringComparison.Ordinal))
            {
                return property.Value;
            }
        }

        return null;
    }

    /// <summary>Returns the first property named <paramref name="name"/> if it is a string.</summary>
    /// <param name="name">The property name.</param>
    /// <returns>The string, or <see langword="null"/> if absent or of another kind.</returns>
    public string? GetStringProperty(string name) => GetProperty(name) is { Kind: AmfValueKind.Text } value ? value.StringValue : null;

    /// <summary>Returns the first property named <paramref name="name"/> if it is a number.</summary>
    /// <param name="name">The property name.</param>
    /// <returns>The number, or <see langword="null"/> if absent or of another kind.</returns>
    public double? GetNumberProperty(string name) => GetProperty(name) is { Kind: AmfValueKind.Number } value ? value.NumberValue : null;

    /// <summary>Creates the exception thrown when an accessor does not match <see cref="Kind"/>.</summary>
    /// <param name="expected">The kind the accessor requires.</param>
    /// <returns>The exception.</returns>
    private InvalidOperationException WrongKind(AmfValueKind expected) => new($"The AMF value is {Kind}, not {expected}.");
}
