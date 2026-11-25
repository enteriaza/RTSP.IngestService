// The configuration-based publisher authenticator: rate-limits failed attempts per remote address, verifies the
// presented stream key against the one-way hash of every configured credential in constant time, and assigns the
// matched credential's application and name to the session. No plaintext key is stored or compared.

using System.Security.Cryptography;
using System.Text;

namespace RTSP.IngestService.Rtmp.Publishing;

/// <summary>
/// Authenticates publishers against the credentials in <see cref="PublisherCredentialStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// The stream name sent by the encoder (the OBS "Stream Key", or the last path segment of an FFmpeg RTMP URL) is the
/// key; the query string and <c>tcUrl</c> are ignored. Order of checks for one attempt:
/// </para>
/// <list type="number">
///   <item><description>The rate limiter reserves a failure token for the TCP peer address; when none is left the
///   attempt is rejected before any hashing.</description></item>
///   <item><description>A key that is not 16 to 256 printable ASCII characters is rejected without hashing.</description></item>
///   <item><description>The key is verified against every credential in one snapshot, enabled or not, so the time taken
///   does not depend on which credential matches. No match, more than one match, a disabled match, or a match whose
///   application differs from the application the publisher connected to is rejected.</description></item>
///   <item><description>On success the reserved token is returned and the result carries the credential's name as the
///   stream identity and its application, which the session, registry, HLS store, events and catalog use.</description></item>
/// </list>
/// <para>
/// Rejection reasons are fixed strings that name neither keys, hashes nor credentials. Thread-safe.
/// </para>
/// </remarks>
internal sealed class ConfiguredPublisherAuthenticator : IPublisherAuthenticator
{
    /// <summary>The credential source.</summary>
    private readonly PublisherCredentialStore _store;

    /// <summary>The failure rate limiter.</summary>
    private readonly PublisherAuthenticationRateLimiter _rateLimiter;

    /// <summary>The metrics.</summary>
    private readonly RtmpMetrics _metrics;

    /// <summary>Initializes a new instance of the <see cref="ConfiguredPublisherAuthenticator"/> class.</summary>
    /// <param name="store">The credential source.</param>
    /// <param name="rateLimiter">The failure rate limiter.</param>
    /// <param name="metrics">The RTMP metrics.</param>
    public ConfiguredPublisherAuthenticator(PublisherCredentialStore store, PublisherAuthenticationRateLimiter rateLimiter, RtmpMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(rateLimiter);
        ArgumentNullException.ThrowIfNull(metrics);
        _store = store;
        _rateLimiter = rateLimiter;
        _metrics = metrics;
    }

    /// <summary>Authenticates a publish request.</summary>
    /// <param name="request">The publish request.</param>
    /// <param name="cancellationToken">Not observed; the method completes synchronously.</param>
    /// <returns>An authorization with the credential's name and application, or a rejection.</returns>
    public ValueTask<PublishAuthenticationResult> AuthenticateAsync(PublishAuthenticationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = _store.Current;
        if (!_rateLimiter.TryAcquire(request.RemoteEndPoint, out var address))
        {
            return Fail(AuthenticationResult.RateLimited, "Too many failed publish attempts from this address.");
        }

        var (outcome, credential) = Verify(snapshot, request);
        if (outcome != AuthenticationResult.Success)
        {
            return Fail(outcome, outcome switch
            {
                AuthenticationResult.MalformedKey => "The stream key is not in an accepted format.",
                AuthenticationResult.Disabled => "The credential is disabled.",
                AuthenticationResult.Ambiguous => "The stream key matches more than one credential.",
                AuthenticationResult.ApplicationMismatch => "The application is not accepted for this credential.",
                _ => "The stream key is not recognised.",
            });
        }

        _rateLimiter.Release(address);
        _metrics.AuthenticationAttempt(AuthenticationResult.Success);
        return ValueTask.FromResult(PublishAuthenticationResult.Authorize(credential!.Name, credential.Application));
    }

    /// <summary>Verifies the presented key against every credential in <paramref name="snapshot"/>.</summary>
    /// <param name="snapshot">The credential snapshot.</param>
    /// <param name="request">The request.</param>
    /// <returns>The outcome and, on success, the matched credential.</returns>
    private static (AuthenticationResult Outcome, PublisherCredential? Credential) Verify(PublisherCredentialSnapshot snapshot, PublishAuthenticationRequest request)
    {
        if (!PublisherKeyHash.IsAcceptableKey(request.StreamName))
        {
            return (AuthenticationResult.MalformedKey, null);
        }

        Span<byte> key = stackalloc byte[PublisherKeyHash.MaxKeyLength];
        var length = Encoding.ASCII.GetBytes(request.StreamName, key);
        PublisherCredential? match = null;
        var matches = 0;
        try
        {
            foreach (var credential in snapshot.Credentials)
            {
                if (credential.KeyHash.Matches(key[..length]))
                {
                    match ??= credential;
                    matches++;
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return matches switch
        {
            0 => (AuthenticationResult.InvalidCredential, null),
            > 1 => (AuthenticationResult.Ambiguous, null),
            _ when !match!.Enabled => (AuthenticationResult.Disabled, null),
            _ when !string.Equals(match.Application, request.Application, StringComparison.Ordinal) => (AuthenticationResult.ApplicationMismatch, null),
            _ => (AuthenticationResult.Success, match),
        };
    }

    /// <summary>Records a failed attempt and creates its rejection.</summary>
    /// <param name="outcome">The failure.</param>
    /// <param name="reason">The fixed, non-secret reason.</param>
    /// <returns>The rejection.</returns>
    private ValueTask<PublishAuthenticationResult> Fail(AuthenticationResult outcome, string reason)
    {
        _metrics.AuthenticationAttempt(outcome);
        return ValueTask.FromResult(PublishAuthenticationResult.Reject(reason));
    }
}

/// <summary>The outcome of one publish authentication attempt, recorded as the <c>result</c> metric tag.</summary>
internal enum AuthenticationResult
{
    /// <summary>The key matched one enabled credential for the requested application.</summary>
    Success,

    /// <summary>The key matched no credential.</summary>
    InvalidCredential,

    /// <summary>The key matched a disabled credential.</summary>
    Disabled,

    /// <summary>The key matched more than one credential.</summary>
    Ambiguous,

    /// <summary>The key matched a credential for a different application.</summary>
    ApplicationMismatch,

    /// <summary>The key was not 16 to 256 printable ASCII characters.</summary>
    MalformedKey,

    /// <summary>The remote address exhausted its failure allowance.</summary>
    RateLimited,
}
