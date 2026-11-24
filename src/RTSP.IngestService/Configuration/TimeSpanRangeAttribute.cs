// Trim-safe inclusive range validation for TimeSpan option properties. The framework alternative,
// RangeAttribute(typeof(TimeSpan), ...), relies on TypeConverter reflection and is not Native AOT compatible.

using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace RTSP.IngestService.Configuration;

/// <summary>
/// Validates that a <see cref="TimeSpan"/> property lies within an inclusive range. A <see langword="null"/> value is
/// considered valid; combine with <see cref="RequiredAttribute"/> where a value is mandatory.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class TimeSpanRangeAttribute : ValidationAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TimeSpanRangeAttribute"/> class.
    /// </summary>
    /// <param name="minimum">Inclusive lower bound in constant ("c") format, for example <c>00:00:01</c>.</param>
    /// <param name="maximum">Inclusive upper bound in constant ("c") format.</param>
    /// <exception cref="FormatException">A bound is not in constant format.</exception>
    /// <exception cref="ArgumentException"><paramref name="minimum"/> is greater than <paramref name="maximum"/>.</exception>
    public TimeSpanRangeAttribute(string minimum, string maximum)
        : base("The field {0} must be between {1} and {2}.")
    {
        Minimum = TimeSpan.ParseExact(minimum, "c", CultureInfo.InvariantCulture);
        Maximum = TimeSpan.ParseExact(maximum, "c", CultureInfo.InvariantCulture);

        if (Minimum > Maximum)
        {
            throw new ArgumentException("The minimum must not be greater than the maximum.", nameof(minimum));
        }
    }

    /// <summary>Gets the inclusive lower bound.</summary>
    public TimeSpan Minimum { get; }

    /// <summary>Gets the inclusive upper bound.</summary>
    public TimeSpan Maximum { get; }

    /// <summary>
    /// Determines whether <paramref name="value"/> is <see langword="null"/> or a <see cref="TimeSpan"/> within range.
    /// </summary>
    /// <param name="value">The value to validate.</param>
    /// <returns><see langword="true"/> if the value is valid.</returns>
    public override bool IsValid(object? value) => value switch
    {
        null => true,
        TimeSpan timeSpan => timeSpan >= Minimum && timeSpan <= Maximum,
        _ => false,
    };

    /// <summary>Formats the error message with the member name and both bounds.</summary>
    /// <param name="name">The display name of the validated member.</param>
    /// <returns>The formatted error message.</returns>
    public override string FormatErrorMessage(string name) =>
        string.Format(CultureInfo.InvariantCulture, ErrorMessageString, name, Minimum.ToString("c", CultureInfo.InvariantCulture), Maximum.ToString("c", CultureInfo.InvariantCulture));
}
