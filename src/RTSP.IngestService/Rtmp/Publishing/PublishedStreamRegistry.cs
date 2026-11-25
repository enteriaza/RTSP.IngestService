// Process-wide registry of active publishing sessions keyed by application and stream identity. It enforces that a
// stream identity is published by at most one session at a time and lets other components observe active sessions
// without referencing connections.

using System.Collections.Concurrent;

namespace RTSP.IngestService.Rtmp.Publishing;

/// <summary>
/// The set of sessions currently publishing.
/// </summary>
/// <remarks>Thread-safe. Sessions are added when publishing starts and removed when they end.</remarks>
public sealed class PublishedStreamRegistry
{
    /// <summary>Active sessions by key.</summary>
    private readonly ConcurrentDictionary<string, PublisherSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>The last generation assigned.</summary>
    private long _lastGeneration;

    /// <summary>Gets the number of active sessions.</summary>
    public int Count => _sessions.Count;

    /// <summary>Gets a snapshot of the active sessions.</summary>
    public IReadOnlyCollection<PublisherSession> Sessions => [.. _sessions.Values];

    /// <summary>Finds the active session publishing <paramref name="streamIdentity"/> in <paramref name="application"/>.</summary>
    /// <param name="application">The application name.</param>
    /// <param name="streamIdentity">The stream identity.</param>
    /// <returns>The session, or <see langword="null"/> if none is publishing.</returns>
    public PublisherSession? Find(string application, string streamIdentity) =>
        _sessions.TryGetValue(Key(application, streamIdentity), out var session) ? session : null;

    /// <summary>
    /// Adds <paramref name="session"/> unless its identity is already being published. The session first gets the
    /// next <see cref="PublisherSession.Generation"/>; a later registration of the same identity can only succeed
    /// after this one is removed, so it always gets a larger generation.
    /// </summary>
    /// <param name="session">The new session.</param>
    /// <returns><see langword="true"/> if added.</returns>
    internal bool TryAdd(PublisherSession session)
    {
        session.SetGeneration(Interlocked.Increment(ref _lastGeneration));
        return _sessions.TryAdd(Key(session.Application, session.StreamIdentity), session);
    }

    /// <summary>Removes <paramref name="session"/> if it is the registered session for its identity.</summary>
    /// <param name="session">The ending session.</param>
    internal void Remove(PublisherSession session) =>
        _sessions.TryRemove(new KeyValuePair<string, PublisherSession>(Key(session.Application, session.StreamIdentity), session));

    /// <summary>Builds the registry key; the separator cannot occur in validated names.</summary>
    /// <param name="application">The application name.</param>
    /// <param name="streamIdentity">The stream identity.</param>
    /// <returns>The key.</returns>
    private static string Key(string application, string streamIdentity) => string.Concat(application, "\0", streamIdentity);
}
