// Unit tests for the trim-safe TimeSpan range validation attribute.

using RTSP.IngestService.Configuration;

namespace RTSP.IngestService.Tests.Configuration;

/// <summary>
/// Tests for <see cref="TimeSpanRangeAttribute"/>.
/// </summary>
public sealed class TimeSpanRangeAttributeTests
{
    /// <summary>Attribute under test with a one-second to one-minute range.</summary>
    private readonly TimeSpanRangeAttribute _attribute = new("00:00:01", "00:01:00");

    /// <summary>Verifies that both bounds and values between them are valid, and values outside are not.</summary>
    /// <param name="seconds">The value in seconds.</param>
    /// <param name="expected">Whether the value is within range.</param>
    [Theory]
    [InlineData(0.999, false)]
    [InlineData(1, true)]
    [InlineData(30, true)]
    [InlineData(60, true)]
    [InlineData(60.001, false)]
    public void IsValid_IsInclusive(double seconds, bool expected) =>
        Assert.Equal(expected, _attribute.IsValid(TimeSpan.FromSeconds(seconds)));

    /// <summary>Verifies that null is valid, leaving mandatory checks to RequiredAttribute.</summary>
    [Fact]
    public void IsValid_Null_IsValid() => Assert.True(_attribute.IsValid(null));

    /// <summary>Verifies that a value of another type is invalid.</summary>
    [Fact]
    public void IsValid_NonTimeSpan_IsInvalid() => Assert.False(_attribute.IsValid(5));

    /// <summary>Verifies that the error message names the member and both bounds in constant format.</summary>
    [Fact]
    public void FormatErrorMessage_IncludesBounds() =>
        Assert.Equal("The field Interval must be between 00:00:01 and 00:01:00.", _attribute.FormatErrorMessage("Interval"));

    /// <summary>Verifies that an inverted range is rejected at construction.</summary>
    [Fact]
    public void Constructor_MinimumGreaterThanMaximum_Throws() =>
        Assert.Throws<ArgumentException>(() => new TimeSpanRangeAttribute("00:01:00", "00:00:01"));

    /// <summary>Verifies that a bound not in constant format is rejected at construction.</summary>
    [Fact]
    public void Constructor_InvalidFormat_Throws() =>
        Assert.Throws<FormatException>(() => new TimeSpanRangeAttribute("one second", "00:00:01"));
}
