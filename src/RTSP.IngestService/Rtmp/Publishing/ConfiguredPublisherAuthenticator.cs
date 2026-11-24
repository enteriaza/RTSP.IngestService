// Development and test publisher authenticator: accepts a publish request when the application matches the configured
// application and the stream name equals one of the configured stream keys. Keys are compared as SHA-256 digests in
// constant time, and every configured key is checked so that timing does not reveal which entry matched.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;

namespace RTSP.IngestService.Rtmp.Publishing;

/// <summary>
/// Authenticates publishers against <see cref="RtmpAuthenticationOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// The stream name sent by the encoder (the OBS "Stream Key", or the last path segment of an FFmpeg RTMP URL) is the
/// credential; the query string and <c>tcUrl</c> are ignored. On success the configured publisher name becomes the
/// session's stream identity.
/// </para>
/// <para>
/// Limitations: keys are plain text in configuration and read once at startup; there is no rate limiting, expiry,
/// revocation without restart, or per-key application scoping. Intended for local and interoperability testing; a
/// production deployment should register its own <see cref="IPublisherAuthenticator"/>. Thread-safe.
/// </para>
/// </remarks>
public sealed class ConfiguredPublisherAuthenticator : IPublisherAuthenticator
{
    /// <summary>The accepted application name.</summary>
    private readonly string _application;

    /// <summary>Publisher names with the SHA-256 digest of their stream keys.</summary>
    private readonly (string Name, byte[] KeyHash)[] _publishers;

    /// <summary>Initializes a new instance of the <see cref="ConfiguredPublisherAuthenticator"/> class.</summary>
    /// <param name="options">The RTMP options; their authentication settings are read once.</param>
    public ConfiguredPublisherAuthenticator(IOptions<RtmpOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var authentication = options.Value.Authentication;
        _application = authentication.Application;
        _publishers = [.. authentication.Publishers.Select(static p => (p.Name, Hash(p.StreamKey)))];
    }

    /// <summary>
    /// Accepts the request if the application matches and the stream name is a configured key.
    /// </summary>
    /// <param name="request">The publish request.</param>
    /// <param name="cancellationToken">Not observed; the method completes synchronously.</param>
    /// <returns>
    /// <see cref="PublishAuthenticationResult.Authorize"/> with the publisher name, or a rejection whose reason names
    /// neither the key nor the stream name.
    /// </returns>
    public ValueTask<PublishAuthenticationResult> AuthenticateAsync(PublishAuthenticationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.Application, _application, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(PublishAuthenticationResult.Reject("The application is not accepted for publishing."));
        }

        var presented = Hash(request.StreamName);
        string? match = null;
        foreach (var (name, keyHash) in _publishers)
        {
            if (CryptographicOperations.FixedTimeEquals(presented, keyHash))
            {
                match ??= name;
            }
        }

        return ValueTask.FromResult(match is null
            ? PublishAuthenticationResult.Reject("The stream key is not recognised.")
            : PublishAuthenticationResult.Authorize(match));
    }

    /// <summary>Computes the SHA-256 digest of the UTF-8 encoding of <paramref name="value"/>.</summary>
    /// <param name="value">The key.</param>
    /// <returns>The digest.</returns>
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
