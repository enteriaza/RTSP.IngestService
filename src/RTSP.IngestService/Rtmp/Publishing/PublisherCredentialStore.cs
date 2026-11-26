// Holds the current immutable snapshot of publisher credentials and replaces it when the "Rtmp" configuration section
// reloads. A reload is bound and validated through the same strict options pipeline as startup; one that fails keeps
// the last known-good snapshot. Sessions that are already publishing are never affected by a reload.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using RTSP.IngestService.Configuration;

namespace RTSP.IngestService.Rtmp.Publishing;

/// <summary>
/// The reloadable source of publisher credentials for <see cref="ConfiguredPublisherAuthenticator"/>.
/// </summary>
/// <remarks>
/// <para>
/// The initial snapshot comes from the validated <see cref="RtmpOptions"/>, so invalid credentials at startup fail
/// startup with a configuration error. Afterwards every change notification from the registered
/// <see cref="IOptionsChangeTokenSource{TOptions}"/> for <see cref="RtmpOptions"/> (a change to any configuration source
/// that reloads: <c>appsettings*.json</c> files are watched by default) creates the options again through
/// <see cref="IOptionsFactory{TOptions}"/>, which applies strict binding and validation. A valid result atomically
/// replaces the snapshot; an invalid result is logged without configuration values and the previous snapshot stays in
/// effect. Only the credentials are reloaded; the other RTMP settings, including the rate limit, are read at startup.
/// </para>
/// <para>
/// Readers take <see cref="Current"/> once per authentication, so each attempt sees one consistent snapshot even
/// while a reload is in progress. Thread-safe.
/// </para>
/// </remarks>
internal sealed partial class PublisherCredentialStore : IDisposable
{
    /// <summary>Serialises reloads.</summary>
    private readonly Lock _reloadGate = new();

    /// <summary>Creates validated options on reload.</summary>
    private readonly IOptionsFactory<RtmpOptions> _factory;

    /// <summary>The metrics.</summary>
    private readonly RtmpMetrics _metrics;

    /// <summary>The logger.</summary>
    private readonly ILogger _logger;

    /// <summary>Change-token registrations, disposed with the store.</summary>
    private readonly List<IDisposable> _registrations = [];

    /// <summary>The current snapshot.</summary>
    private PublisherCredentialSnapshot _current;

    /// <summary>Initializes a new instance of the <see cref="PublisherCredentialStore"/> class.</summary>
    /// <param name="options">The validated startup options.</param>
    /// <param name="factory">Creates validated options when the configuration reloads.</param>
    /// <param name="changeTokenSources">The change-token sources registered for <see cref="RtmpOptions"/>.</param>
    /// <param name="metrics">The RTMP metrics.</param>
    /// <param name="logger">The logger.</param>
    public PublisherCredentialStore(
        IOptions<RtmpOptions> options,
        IOptionsFactory<RtmpOptions> factory,
        IEnumerable<IOptionsChangeTokenSource<RtmpOptions>> changeTokenSources,
        RtmpMetrics metrics,
        ILogger<PublisherCredentialStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(changeTokenSources);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _factory = factory;
        _metrics = metrics;
        _logger = logger;
        _current = PublisherCredentialSnapshot.Create(options.Value.Authentication.Publishers);
        LogLoaded(_logger, _current.Credentials.Length, _current.EnabledCount);

        foreach (var source in changeTokenSources)
        {
            if (string.Equals(source.Name ?? Options.DefaultName, Options.DefaultName, StringComparison.Ordinal))
            {
                _registrations.Add(ChangeToken.OnChange(source.GetChangeToken, () => Reload()));
            }
        }
    }

    /// <summary>Raised after a reload has replaced the snapshot.</summary>
    public event Action<PublisherCredentialSnapshot>? Reloaded;

    /// <summary>Raised after a reload was rejected; the previous snapshot remains current.</summary>
    public event Action? ReloadRejected;

    /// <summary>Gets the snapshot in effect. Read it once per authentication.</summary>
    public PublisherCredentialSnapshot Current => Volatile.Read(ref _current);

    /// <summary>Creates the options from the current configuration and replaces the snapshot if they are valid.</summary>
    /// <returns><see langword="true"/> if the snapshot was replaced.</returns>
    public bool Reload()
    {
        PublisherCredentialSnapshot snapshot;
        lock (_reloadGate)
        {
            try
            {
                var options = _factory.Create(Options.DefaultName);
                snapshot = PublisherCredentialSnapshot.Create(options.Authentication.Publishers);
            }
            catch (OptionsValidationException ex)
            {
                _metrics.CredentialReload(succeeded: false);
                LogReloadRejected(_logger, ex.Failures.Count(), string.Join("; ", ex.Failures));
                ReloadRejected?.Invoke();
                return false;
            }
            catch (Exception ex)
            {
                _metrics.CredentialReload(succeeded: false);
                LogReloadFailed(_logger, ex.GetType().Name);
                ReloadRejected?.Invoke();
                return false;
            }

            Volatile.Write(ref _current, snapshot);
        }

        _metrics.CredentialReload(succeeded: true);
        LogReloaded(_logger, snapshot.Credentials.Length, snapshot.EnabledCount);
        Reloaded?.Invoke(snapshot);
        return true;
    }

    /// <summary>Stops observing configuration changes.</summary>
    public void Dispose()
    {
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }

        _registrations.Clear();
    }

    /// <summary>Logs the startup credentials.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="count">The number of credentials.</param>
    /// <param name="enabled">The number of enabled credentials.</param>
    [LoggerMessage(EventId = 5300, Level = LogLevel.Information, Message = "Publisher credentials loaded: {Count} configured, {Enabled} enabled")]
    private static partial void LogLoaded(ILogger logger, int count, int enabled);

    /// <summary>Logs an applied reload.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="count">The number of credentials.</param>
    /// <param name="enabled">The number of enabled credentials.</param>
    [LoggerMessage(EventId = 5301, Level = LogLevel.Information, Message = "Publisher credentials reloaded: {Count} configured, {Enabled} enabled")]
    private static partial void LogReloaded(ILogger logger, int count, int enabled);

    /// <summary>Logs a reload that failed validation.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="failureCount">The number of validation failures.</param>
    /// <param name="failures">The validation messages, which name settings but never contain key hashes.</param>
    [LoggerMessage(EventId = 5302, Level = LogLevel.Error, Message = "Publisher credential reload rejected with {FailureCount} validation failure(s); the previous credentials remain in effect: {Failures}")]
    private static partial void LogReloadRejected(ILogger logger, int failureCount, string failures);

    /// <summary>Logs a reload that failed unexpectedly. The exception is not logged because its message could quote configuration.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exceptionType">The exception type name.</param>
    [LoggerMessage(EventId = 5303, Level = LogLevel.Error, Message = "Publisher credential reload failed ({ExceptionType}); the previous credentials remain in effect")]
    private static partial void LogReloadFailed(ILogger logger, string exceptionType);
}

/// <summary>
/// An immutable set of publisher credentials, with a bounded memo of the keys it has verified.
/// </summary>
/// <remarks>
/// The memo maps a tag of a presented key, the HMAC-SHA256 of the key under a random pepper that exists only in this
/// snapshot's memory, to the credential the key authenticated as: an unambiguous match of an enabled credential. It
/// holds neither keys nor configured hashes, is never persisted or logged, and holds at most one entry per credential,
/// because only an attempt that matched exactly one credential is recorded. A reload creates a new snapshot with an
/// empty memo, so revoked, disabled and changed credentials are never served from it.
/// </remarks>
internal sealed class PublisherCredentialSnapshot
{
    /// <summary>The length of the tag pepper in bytes.</summary>
    private const int PepperLength = 32;

    /// <summary>The random key of <see cref="Tag"/>; never leaves this instance.</summary>
    private readonly byte[] _pepper = RandomNumberGenerator.GetBytes(PepperLength);

    /// <summary>Credential indexes by key tag, of keys verified as an unambiguous match of an enabled credential.</summary>
    private readonly ConcurrentDictionary<UInt128, int> _verified = new();

    /// <summary>The number of entries reserved in <see cref="_verified"/>; at most the number of credentials.</summary>
    private int _verifiedCount;

    /// <summary>Initializes a new instance of the <see cref="PublisherCredentialSnapshot"/> class.</summary>
    /// <param name="credentials">The credentials.</param>
    private PublisherCredentialSnapshot(PublisherCredential[] credentials)
    {
        Credentials = credentials;
        EnabledCount = credentials.Count(static c => c.Enabled);
    }

    /// <summary>Gets the credentials, in configuration order.</summary>
    public PublisherCredential[] Credentials { get; }

    /// <summary>Gets the number of enabled credentials.</summary>
    public int EnabledCount { get; }

    /// <summary>Creates a snapshot from validated configuration.</summary>
    /// <param name="publishers">The configured publishers.</param>
    /// <returns>The snapshot.</returns>
    /// <exception cref="InvalidOperationException">A key hash cannot be parsed; validation should have rejected it.</exception>
    public static PublisherCredentialSnapshot Create(IEnumerable<RtmpPublisherCredential> publishers)
    {
        ArgumentNullException.ThrowIfNull(publishers);
        var credentials = new List<PublisherCredential>();
        foreach (var publisher in publishers)
        {
            if (!PublisherKeyHash.TryParse(publisher.KeyHash, out var hash))
            {
                throw new InvalidOperationException($"Publisher '{publisher.Name}' has an invalid key hash.");
            }

            credentials.Add(new PublisherCredential(publisher.Name, publisher.Application, hash, publisher.Enabled));
        }

        return new PublisherCredentialSnapshot([.. credentials]);
    }

    /// <summary>Computes the memo tag of a presented key.</summary>
    /// <param name="keyBytes">The ASCII bytes of the presented key.</param>
    /// <returns>The tag.</returns>
    public UInt128 Tag(ReadOnlySpan<byte> keyBytes)
    {
        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(_pepper, keyBytes, mac);
        var tag = BinaryPrimitives.ReadUInt128LittleEndian(mac);
        CryptographicOperations.ZeroMemory(mac);
        return tag;
    }

    /// <summary>Returns the index of the credential a key with this tag was verified as, if any.</summary>
    /// <param name="tag">The tag.</param>
    /// <param name="index">The credential index.</param>
    /// <returns><see langword="true"/> if the tag is memoized.</returns>
    public bool TryGetVerified(UInt128 tag, out int index) => _verified.TryGetValue(tag, out index);

    /// <summary>Memoizes that a key with this tag matched exactly one credential, which is enabled.</summary>
    /// <param name="tag">The tag.</param>
    /// <param name="index">The credential index.</param>
    public void AddVerified(UInt128 tag, int index)
    {
        if (Interlocked.Increment(ref _verifiedCount) > Credentials.Length || !_verified.TryAdd(tag, index))
        {
            Interlocked.Decrement(ref _verifiedCount);
        }
    }
}

/// <summary>One credential in a <see cref="PublisherCredentialSnapshot"/>.</summary>
/// <param name="Name">The non-secret identifier and stream identity.</param>
/// <param name="Application">The application the credential publishes to.</param>
/// <param name="KeyHash">The key hash.</param>
/// <param name="Enabled">Whether the credential is accepted.</param>
internal sealed record PublisherCredential(string Name, string Application, PublisherKeyHash KeyHash, bool Enabled);
