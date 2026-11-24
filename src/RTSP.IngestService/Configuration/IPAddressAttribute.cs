// Trim-safe validation that a string option is a literal IP address. Host names are deliberately rejected: the RTMP
// listener binds to an address, and resolving names at startup would make binding depend on DNS.

using System.ComponentModel.DataAnnotations;
using System.Net;

namespace RTSP.IngestService.Configuration;

/// <summary>
/// Validates that a string property is a literal IPv4 address in dotted form or an IPv6 address. Values that
/// <see cref="IPAddress.TryParse(string?, out IPAddress?)"/> would accept only through legacy shorthand (for example
/// <c>1</c> for <c>0.0.0.1</c>) are rejected because they contain neither a dot nor a colon. A <see langword="null"/>
/// value is considered valid; combine with <see cref="RequiredAttribute"/> where a value is mandatory.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class IPAddressAttribute : ValidationAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IPAddressAttribute"/> class.
    /// </summary>
    public IPAddressAttribute()
        : base("The field {0} must be a literal IPv4 or IPv6 address.")
    {
    }

    /// <summary>Determines whether <paramref name="value"/> is <see langword="null"/> or a literal IP address.</summary>
    /// <param name="value">The value to validate.</param>
    /// <returns><see langword="true"/> if the value is valid.</returns>
    public override bool IsValid(object? value) => value switch
    {
        null => true,
        string text => text.AsSpan().IndexOfAny('.', ':') >= 0 && IPAddress.TryParse(text, out _),
        _ => false,
    };
}
