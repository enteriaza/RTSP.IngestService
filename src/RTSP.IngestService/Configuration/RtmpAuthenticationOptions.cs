// Settings for ConfiguredPublisherAuthenticator, bound from "Rtmp:Authentication". Publishers are configured with a
// one-way key hash, never a plaintext stream key; the hashes are still sensitive and belong only in the git-ignored
// appsettings.{Environment}.json files or environment variables, and are never logged.

using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Rtmp.Publishing;

namespace RTSP.IngestService.Configuration;

/// <summary>
/// The publisher credentials and failure rate limit used by <see cref="ConfiguredPublisherAuthenticator"/>.
/// </summary>
/// <remarks>
/// With the default empty <see cref="Publishers"/> list every publish request is rejected. <see cref="Publishers"/> is
/// reloaded when the configuration changes; <see cref="RateLimit"/> is read once at startup.
/// </remarks>
public sealed class RtmpAuthenticationOptions : IValidatableObject
{
    /// <summary>The largest number of configured publishers; each publish attempt verifies against all of them.</summary>
    public const int MaxPublishers = 1000;

    /// <summary>
    /// Gets or sets the accepted publisher credentials. Names and key hashes must each be unique; at most
    /// <see cref="MaxPublishers"/> entries.
    /// </summary>
    [ValidateEnumeratedItems]
    public IList<RtmpPublisherCredential> Publishers { get; set; } = [];

    /// <summary>Gets or sets the limit on failed publish attempts per remote address.</summary>
    [ValidateObjectMembers]
    public RtmpAuthenticationRateLimitOptions RateLimit { get; set; } = new();

    /// <summary>Rejects too many publishers, duplicate names and duplicate key hashes.</summary>
    /// <param name="validationContext">The validation context.</param>
    /// <returns>One failure per problem; a duplicated hash is reported without its value.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Publishers.Count > MaxPublishers)
        {
            yield return new ValidationResult($"At most {MaxPublishers} publishers may be configured; found {Publishers.Count}.", [nameof(Publishers)]);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var publisher in Publishers)
        {
            if (publisher.Name is { } name && !names.Add(name))
            {
                yield return new ValidationResult($"Publisher name '{name}' is configured more than once.", [nameof(Publishers)]);
            }

            if (publisher.KeyHash is { Length: > 0 } hash && !hashes.Add(hash))
            {
                yield return new ValidationResult($"Publisher '{publisher.Name}' uses a key hash that is already assigned to another publisher.", [nameof(Publishers)]);
            }
        }
    }
}

/// <summary>
/// One publisher credential accepted by <see cref="ConfiguredPublisherAuthenticator"/>.
/// </summary>
public sealed class RtmpPublisherCredential : IValidatableObject
{
    /// <summary>
    /// Gets or sets the credential's unique, non-secret identifier, which is also the stream identity used for the
    /// published stream in sessions, logs, the registry, HLS URLs, events and the stream catalog. Must be a letter or
    /// digit followed by up to 63 of <c>A-Z a-z 0-9 . _ -</c>.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the RTMP application this credential publishes to, for example <c>live</c> in
    /// <c>rtmp://host/live</c>. The publisher must connect to exactly this application (ordinal comparison); the
    /// credential, not the publisher, determines it. Same pattern as <see cref="Name"/>.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    public string Application { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the one-way hash of the stream key, <c>hmac-sha256$&lt;salt&gt;$&lt;digest&gt;</c>, as printed by
    /// <c>RTSP.IngestService publisher-key generate</c> or <c>publisher-key hash</c>. Sensitive; never logged.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the credential is accepted. Defaults to <see langword="true"/>; set to
    /// <see langword="false"/> to revoke it while keeping the entry. Revocation does not end sessions already
    /// publishing.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Rejects a key hash that is not in the supported format.</summary>
    /// <param name="validationContext">The validation context.</param>
    /// <returns>A failure, without the hash value, when <see cref="KeyHash"/> cannot be parsed.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (KeyHash is { Length: > 0 } && !PublisherKeyHash.TryParse(KeyHash, out _))
        {
            yield return new ValidationResult(
                $"Publisher '{Name}' has a KeyHash that is not a valid {PublisherKeyHash.Algorithm} key hash.", [nameof(KeyHash)]);
        }
    }
}

/// <summary>
/// The token-bucket limit on failed publish attempts from one remote IP address.
/// </summary>
public sealed class RtmpAuthenticationRateLimitOptions
{
    /// <summary>
    /// Gets or sets the number of failed attempts an address may make in a burst; the allowance refills evenly over
    /// <see cref="Window"/>. Defaults to 10; valid range is 1 to 1000.
    /// </summary>
    [Range(1, 1000)]
    public int FailureLimit { get; set; } = 10;

    /// <summary>
    /// Gets or sets the time in which an exhausted allowance fully refills. Defaults to one minute; valid range is one
    /// second to one hour.
    /// </summary>
    [TimeSpanRange("00:00:01", "01:00:00")]
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}
