// The one-way representation of a publisher stream key: a random per-credential salt and the HMAC-SHA256 of the key
// under that salt, encoded as "hmac-sha256$<salt>$<digest>" with unpadded base64url. Only the hash is stored in
// configuration; the key itself is shown once when it is generated and is never retained by the service.

using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace RTSP.IngestService.Rtmp.Publishing;

/// <summary>
/// A parsed publisher key hash that can verify a presented stream key in constant time.
/// </summary>
/// <remarks>
/// <para>
/// HMAC-SHA256 with a 128-bit random salt is a fast one-way function. It is appropriate because the supported keys are
/// generated with 256 bits of entropy (<see cref="GenerateKey"/>), which makes offline guessing infeasible even if the
/// configuration leaks; the salt prevents precomputation and makes equal keys hash differently. A slow password hash
/// would add per-attempt CPU cost for every configured credential without improving the security of such keys. Keys
/// chosen by people do not have that entropy and should be replaced with generated keys.
/// </para>
/// <para>
/// <see cref="ToString"/> never returns the salt or digest, so an instance that reaches a log or exception message
/// does not disclose the hash. Instances are immutable and thread-safe.
/// </para>
/// </remarks>
public sealed class PublisherKeyHash
{
    /// <summary>The algorithm prefix of the encoded form.</summary>
    public const string Algorithm = "hmac-sha256";

    /// <summary>The minimum length, in characters, of a stream key that can be hashed or verified.</summary>
    public const int MinKeyLength = 16;

    /// <summary>The maximum length, in characters, of a stream key that can be hashed or verified.</summary>
    public const int MaxKeyLength = 256;

    /// <summary>Salt length in bytes.</summary>
    private const int SaltLength = 16;

    /// <summary>Digest length in bytes.</summary>
    private const int DigestLength = 32;

    /// <summary>Entropy of a generated key, in bytes.</summary>
    private const int GeneratedKeyBytes = 32;

    /// <summary>The per-credential salt, used as the HMAC key.</summary>
    private readonly byte[] _salt;

    /// <summary>The HMAC-SHA256 of the UTF-8 stream key.</summary>
    private readonly byte[] _digest;

    /// <summary>Initializes a new instance of the <see cref="PublisherKeyHash"/> class.</summary>
    /// <param name="salt">The salt.</param>
    /// <param name="digest">The digest.</param>
    private PublisherKeyHash(byte[] salt, byte[] digest)
    {
        _salt = salt;
        _digest = digest;
    }

    /// <summary>Generates a new stream key with 256 bits of entropy, encoded as 43 base64url characters.</summary>
    /// <returns>The key. It is a secret: give it to the encoder operator once and store only its hash.</returns>
    public static string GenerateKey()
    {
        Span<byte> bytes = stackalloc byte[GeneratedKeyBytes];
        RandomNumberGenerator.Fill(bytes);
        var key = Base64Url.EncodeToString(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        return key;
    }

    /// <summary>
    /// Determines whether <paramref name="key"/> has an acceptable length and only printable ASCII characters other than
    /// <c>?</c>, which RTMP treats as the start of the stream name's query string.
    /// </summary>
    /// <param name="key">The candidate key.</param>
    /// <returns><see langword="true"/> if the key can be hashed and presented by an encoder.</returns>
    public static bool IsAcceptableKey(ReadOnlySpan<char> key) =>
        key.Length is >= MinKeyLength and <= MaxKeyLength && !key.ContainsAnyExceptInRange('!', '~') && !key.Contains('?');

    /// <summary>Hashes <paramref name="key"/> with a new random salt.</summary>
    /// <param name="key">The stream key.</param>
    /// <returns>The encoded hash, for the <c>KeyHash</c> setting.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> fails <see cref="IsAcceptableKey"/>.</exception>
    public static string Create(ReadOnlySpan<char> key)
    {
        if (!IsAcceptableKey(key))
        {
            throw new ArgumentException(
                $"A stream key must be {MinKeyLength} to {MaxKeyLength} printable ASCII characters without spaces.", nameof(key));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        Span<byte> keyBytes = stackalloc byte[MaxKeyLength];
        Span<byte> digest = stackalloc byte[DigestLength];
        var length = Encoding.ASCII.GetBytes(key, keyBytes);
        HMACSHA256.HashData(salt, keyBytes[..length], digest);
        CryptographicOperations.ZeroMemory(keyBytes);
        return $"{Algorithm}${Base64Url.EncodeToString(salt)}${Base64Url.EncodeToString(digest)}";
    }

    /// <summary>Parses an encoded hash.</summary>
    /// <param name="text">The encoded hash.</param>
    /// <param name="hash">The parsed hash when successful.</param>
    /// <returns><see langword="true"/> if <paramref name="text"/> is a well-formed <see cref="Algorithm"/> hash.</returns>
    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out PublisherKeyHash? hash)
    {
        hash = null;
        if (text is null)
        {
            return false;
        }

        var parts = text.Split('$');
        if (parts.Length != 3 || !string.Equals(parts[0], Algorithm, StringComparison.Ordinal))
        {
            return false;
        }

        var salt = new byte[SaltLength];
        var digest = new byte[DigestLength];
        if (!TryDecode(parts[1], salt) || !TryDecode(parts[2], digest))
        {
            return false;
        }

        hash = new PublisherKeyHash(salt, digest);
        return true;
    }

    /// <summary>Determines in constant time whether <paramref name="keyBytes"/> is the key this hash was created from.</summary>
    /// <param name="keyBytes">The ASCII bytes of the presented key.</param>
    /// <returns><see langword="true"/> on a match.</returns>
    public bool Matches(ReadOnlySpan<byte> keyBytes)
    {
        Span<byte> computed = stackalloc byte[DigestLength];
        HMACSHA256.HashData(_salt, keyBytes, computed);
        return CryptographicOperations.FixedTimeEquals(computed, _digest);
    }

    /// <summary>Returns the algorithm name only; the salt and digest are never formatted.</summary>
    /// <returns>A redacted description.</returns>
    public override string ToString() => $"{Algorithm}$<redacted>";

    /// <summary>Decodes unpadded base64url text that must fill <paramref name="destination"/> exactly.</summary>
    /// <param name="text">The encoded text.</param>
    /// <param name="destination">The destination, whose length is the required decoded length.</param>
    /// <returns><see langword="true"/> on success.</returns>
    private static bool TryDecode(string text, Span<byte> destination) =>
        text.Length == Base64Url.GetEncodedLength(destination.Length)
        && Base64Url.IsValid(text)
        && Base64Url.TryDecodeFromChars(text, destination, out var written)
        && written == destination.Length;
}
