// Settings for ConfiguredPublisherAuthenticator, bound from "Rtmp:Authentication". Stream keys are secrets: they
// belong only in the git-ignored appsettings.{Environment}.json files and are never logged.

using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace RTSP.IngestService.Configuration;

/// <summary>
/// The application name and the publishers accepted by <see cref="Rtmp.Publishing.ConfiguredPublisherAuthenticator"/>.
/// </summary>
/// <remarks>
/// With the default empty <see cref="Publishers"/> list every publish request is rejected.
/// </remarks>
public sealed class RtmpAuthenticationOptions : IValidatableObject
{
    /// <summary>
    /// Gets or sets the only RTMP application name publishers may connect to, for example <c>live</c> in
    /// <c>rtmp://host/live</c>. Compared ordinally. Defaults to <c>live</c>; must be a letter or digit followed by up
    /// to 63 of <c>A-Z a-z 0-9 . _ -</c>.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    public string Application { get; set; } = "live";

    /// <summary>
    /// Gets or sets the accepted publishers. Names and stream keys must each be unique.
    /// </summary>
    [ValidateEnumeratedItems]
    public IList<RtmpPublisherCredential> Publishers { get; set; } = [];

    /// <summary>Rejects duplicate publisher names and duplicate stream keys.</summary>
    /// <param name="validationContext">The validation context.</param>
    /// <returns>One failure per duplicated name or key; a duplicated key is reported without its value.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var publisher in Publishers)
        {
            if (publisher.Name is { } name && !names.Add(name))
            {
                yield return new ValidationResult($"Publisher name '{name}' is configured more than once.", [nameof(Publishers)]);
            }

            if (publisher.StreamKey is { } key && !keys.Add(key))
            {
                yield return new ValidationResult($"Publisher '{publisher.Name}' uses a stream key that is already assigned to another publisher.", [nameof(Publishers)]);
            }
        }
    }
}

/// <summary>
/// One publisher accepted by <see cref="Rtmp.Publishing.ConfiguredPublisherAuthenticator"/>.
/// </summary>
public sealed class RtmpPublisherCredential
{
    /// <summary>
    /// Gets or sets the publisher's stream identity: the non-secret name used for the published stream in sessions,
    /// logs and the registry. Must be a letter or digit followed by up to 63 of <c>A-Z a-z 0-9 . _ -</c>.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the secret stream key the encoder sends as the RTMP stream name (the "Stream Key" in OBS).
    /// Must be 16 to 256 characters.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [StringLength(256, MinimumLength = 16)]
    public string StreamKey { get; set; } = string.Empty;
}
