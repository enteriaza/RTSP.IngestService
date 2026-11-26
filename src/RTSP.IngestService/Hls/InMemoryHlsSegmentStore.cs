// Bounded in-memory IHlsSegmentStore. Each stream keeps its live playlist, the segments it lists, the segments
// removed from it that are still within their RFC 8216 availability period, and the initialization segments they
// reference. Limits are enforced by rejecting new segments; a stored segment is only ever released after its
// availability deadline. Expiry is evaluated lazily on each call, so no background worker is needed.

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Streams;

namespace RTSP.IngestService.Hls;

/// <summary>
/// Stores HLS segments and playlists in memory within configured limits.
/// </summary>
/// <remarks>
/// <para>
/// Availability (RFC 8216, sections 6.2.1 and 6.2.2): a segment listed by the published playlist is never released.
/// When a segment is removed from the playlist, or the whole playlist is withdrawn (the publisher aborted, its ended
/// playlist's retention passed, or a new publisher replaced it), the segment stays retrievable until
/// <c>removal time + segment duration + duration of the longest published playlist that listed it</c>. Only
/// playlists actually published to readers count. Initialization segments are kept while any kept segment, or the
/// stream's next segment, needs them.
/// </para>
/// <para>
/// Limits: a segment whose footprint (<see cref="HlsSegment.Footprint"/>) would exceed the stream's or the global
/// byte limit is rejected with
/// <see cref="HlsAppendResult.StorageFull"/> and nothing changes for that stream; no listed or still-available
/// segment is ever released early, and other streams' playlists are never shrunk. Under global pressure, ended
/// streams' playlists are withdrawn early (their segments then drain on their availability deadlines). Space returns
/// as deadlines pass.
/// </para>
/// <para>
/// Withdrawn streams: after a playlist is withdrawn, its remaining segments stay retrievable under the same key until
/// they expire. A new stream on that key continues the key's segment numbering, even after everything has drained,
/// so a URI never names two different segments while the process runs.
/// </para>
/// <para>
/// Generation: published playlists reference segments as <c>{<see cref="Generation"/>}/{name}</c>. The generation is
/// random per store instance (one per process in the service), so URIs from an earlier process never name this
/// process's media, although numbering restarts with the process.
/// </para>
/// <para>
/// Lifecycle events: withdrawing a playlist raises <see cref="StreamEventType.PlaylistWithdrawn"/>, and releasing the
/// last segment of a withdrawn stream raises <see cref="StreamEventType.Drained"/>, each once per stream instance.
/// They are raised under the lock and published by the same call after the lock is released; like every other
/// transition here, retention expiry and drain are evaluated lazily, so they are observed on the next store call.
/// </para>
/// <para>
/// Thread-safe. All state is guarded by one lock; writer operations are short and bounded and only visit the stream
/// they change plus the streams with a due deadline (an expiry index), or, under global storage pressure, the ended
/// streams. Readers use immutable per-key views republished under the lock whenever a key changes, so they take the
/// lock only when a deadline is due, to run the lazy expiry first.
/// </para>
/// </remarks>
public sealed class InMemoryHlsSegmentStore : IHlsSegmentStore, IHlsStreamInventory, IDisposable
{
    /// <summary>Guards all state.</summary>
    private readonly Lock _gate = new();

    /// <summary>Streams by key.</summary>
    private readonly Dictionary<(string Application, string Identity), KeyState> _keys = [];

    /// <summary>
    /// The next numbering of keys that currently hold nothing, so a key's segment and initialization names are never
    /// reused while the process runs and a URI never names two different segments. One small entry per stream identity
    /// ever opened; identities come from the publisher authenticator.
    /// </summary>
    private readonly Dictionary<(string Application, string Identity), HlsStreamNumbering> _numbering = [];

    /// <summary>The version of the last published playlist snapshot, across all streams.</summary>
    private long _lastVersion;

    /// <summary>Active (not ended) streams by writer session.</summary>
    private readonly Dictionary<Guid, StreamState> _bySession = [];

    /// <summary>Settings.</summary>
    private readonly HlsOptions _options;

    /// <summary>Clock for availability deadlines.</summary>
    private readonly TimeProvider _time;

    /// <summary>Metrics.</summary>
    private readonly HlsMetrics _metrics;

    /// <summary>Footprint (<see cref="HlsSegment.Footprint"/>) of the segments held across all streams.</summary>
    private long _totalBytes;

    /// <summary>The <see cref="Generation"/> path segment that prefixes every URI in published playlists.</summary>
    private readonly string _uriPrefix;

    /// <summary>Stream lifecycle event publisher, if any.</summary>
    private readonly IStreamEventPublisher? _events;

    /// <summary>Lifecycle events raised under <see cref="_gate"/>, published by the same call after it is released.</summary>
    private readonly List<StreamEvent> _pendingEvents = [];

    /// <summary>Whether <see cref="Dispose"/> has released everything.</summary>
    private bool _disposed;

    /// <summary>
    /// The streams with a pending deadline (a retired segment's availability or an ended playlist's retention), by
    /// deadline. An entry may be stale; handling one is idempotent. One entry per retired segment and ended playlist.
    /// </summary>
    private readonly PriorityQueue<StreamState, DateTimeOffset> _deadlines = new();

    /// <summary>The earliest deadline in <see cref="_deadlines"/>, in UTC ticks, for readers; <see cref="long.MaxValue"/> if none.</summary>
    private long _nextDeadline = long.MaxValue;

    /// <summary>Keys whose current stream has an ended playlist, withdrawn early under global storage pressure.</summary>
    private readonly HashSet<KeyState> _ended = [];

    /// <summary>Keys changed while <see cref="_gate"/> is held; their read views are replaced before it is released.</summary>
    private readonly HashSet<KeyState> _changed = [];

    /// <summary>
    /// Read views by key: a projection of the guarded state, replaced under <see cref="_gate"/>, that readers use without
    /// the lock. A key has a view exactly while it is in <see cref="_keys"/>.
    /// </summary>
    private readonly ConcurrentDictionary<(string Application, string Identity), KeyView> _views = new();

    /// <summary>Bounds the segments held by readers of <see cref="TryAcquireSegment"/>, stored or already evicted.</summary>
    private readonly HlsReaderMemoryBudget _readers;

    /// <summary>Initializes a new instance of the <see cref="InMemoryHlsSegmentStore"/> class.</summary>
    /// <param name="options">The limits.</param>
    /// <param name="time">The clock.</param>
    /// <param name="metrics">Metrics.</param>
    /// <param name="events">
    /// Receives <see cref="StreamEventType.PlaylistWithdrawn"/> and <see cref="StreamEventType.Drained"/> events for
    /// streams opened with an incarnation identifier, or <see langword="null"/> for none.
    /// </param>
    internal InMemoryHlsSegmentStore(IOptions<HlsOptions> options, TimeProvider time, HlsMetrics metrics, IStreamEventPublisher? events = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _time = time;
        _metrics = metrics;
        _events = events;
        _readers = new HlsReaderMemoryBudget(_options.MaxTotalStorageBytes, metrics);
        Generation = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        _uriPrefix = Generation + "/";
    }

    /// <inheritdoc />
    /// <remarks>32 lowercase hexadecimal digits (128 random bits), fixed for the store's lifetime.</remarks>
    public string Generation { get; }

    /// <summary>
    /// Gets the footprint (<see cref="HlsSegment.Footprint"/>) of the segments held across all streams, including
    /// withdrawn streams' remaining segments.
    /// </summary>
    public long TotalBytes
    {
        get
        {
            using (EnterGate())
            {
                PurgeExpired();
                return _totalBytes;
            }
        }
    }

    /// <summary>Gets the number of streams with an active or ended (not withdrawn) playlist.</summary>
    public int StreamCount
    {
        get
        {
            using (EnterGate())
            {
                PurgeExpired();
                return _keys.Values.Count(static k => k.Current is not null);
            }
        }
    }

    /// <summary>Gets the number of withdrawn streams whose segments are still available.</summary>
    public int WithdrawnStreamCount
    {
        get
        {
            using (EnterGate())
            {
                PurgeExpired();
                return _keys.Values.Sum(static k => k.Withdrawn.Count);
            }
        }
    }

    /// <inheritdoc />
    public bool TryOpenStream(Guid sessionId, string application, string streamIdentity, int targetDurationSeconds, out HlsStreamNumbering numbering, string? incarnationId = null, long generation = 0)
    {
        using (EnterGate())
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PurgeExpired();
            numbering = default;
            var key = (application, streamIdentity);
            _keys.TryGetValue(key, out var keyState);
            if (keyState?.Current is { Ended: false } || _bySession.Count >= _options.MaxSessions || _bySession.ContainsKey(sessionId))
            {
                return false;
            }

            if (keyState is null)
            {
                keyState = new KeyState(key);
                if (_numbering.Remove(key, out var continued))
                {
                    (keyState.NextSequence, keyState.NextInitialization) = (continued.FirstSequence, continued.FirstInitialization);
                }

                _keys[key] = keyState;
            }
            else if (keyState.Current is { } ended)
            {
                Withdraw(keyState, ended, StreamEventReasons.Replaced);
            }

            var state = new StreamState(keyState, sessionId, new HlsPlaylist(targetDurationSeconds, _options.PlaylistWindowSize), incarnationId, generation);
            keyState.Current = state;
            _changed.Add(keyState);
            _bySession[sessionId] = state;
            numbering = new HlsStreamNumbering(keyState.NextSequence, keyState.NextInitialization);
            return true;
        }
    }

    /// <inheritdoc />
    public HlsAppendResult AddInitialization(Guid sessionId, HlsSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        using (EnterGate())
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PurgeExpired();
            var state = Writer(sessionId);
            var keyState = _keys[state.Key];
            EnsureUnique(keyState, segment.Name);
            if (HasRoom(state, segment.Footprint) is var room and not HlsAppendResult.Published)
            {
                return room;
            }

            var previous = state.CurrentInitialization;
            Store(state, segment);
            state.CurrentInitialization = segment.Name;
            keyState.NextInitialization++;
            if (previous is not null && !IsReferenced(state, previous))
            {
                Release(state, previous, "unreferenced");
            }

            return HlsAppendResult.Published;
        }
    }

    /// <inheritdoc />
    public HlsAppendResult AppendSegment(Guid sessionId, HlsSegment segment, HlsSegmentEntry entry)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(entry);
        using (EnterGate())
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PurgeExpired();
            var state = Writer(sessionId);
            var keyState = _keys[state.Key];
            if (!state.Segments.ContainsKey(entry.InitializationName))
            {
                throw new InvalidOperationException($"Initialization segment {entry.InitializationName} has not been stored.");
            }

            if (!string.Equals(segment.Name, entry.Name, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Segment {segment.Name} does not match its playlist entry {entry.Name}.", nameof(entry));
            }

            EnsureUnique(keyState, segment.Name);
            state.Playlist.EnsureCanAppend(entry);
            if (!state.Playlist.HasEntryRoom(entry.Duration))
            {
                return HlsAppendResult.PlaylistFull;
            }

            if (HasRoom(state, segment.Footprint) is var room and not HlsAppendResult.Published)
            {
                return room;
            }

            Store(state, segment);
            var now = _time.GetUtcNow();
            foreach (var retired in state.Playlist.Append(entry))
            {
                Retire(state, retired, now, "retired");
            }

            keyState.NextSequence = entry.Sequence + 1;
            ReleaseUnreferencedInitializations(state);
            Publish(state);
            return HlsAppendResult.Published;
        }
    }

    /// <inheritdoc />
    public void CloseStream(Guid sessionId, bool completed)
    {
        using (EnterGate())
        {
            PurgeExpired();
            if (!_bySession.Remove(sessionId, out var state))
            {
                return;
            }

            var keyState = _keys[state.Key];
            var withdrawal = !completed ? StreamEventReasons.Aborted
                : _options.EndedStreamRetention == TimeSpan.Zero ? StreamEventReasons.RetentionDisabled
                : state.Playlist.Entries.Count == 0 ? StreamEventReasons.Empty
                : null;
            if (withdrawal is not null)
            {
                Withdraw(keyState, state, withdrawal);
                TryRemoveKey(keyState);
                return;
            }

            state.Playlist.End();
            state.Ended = true;
            state.ExpiresAt = _time.GetUtcNow() + _options.EndedStreamRetention;
            _deadlines.Enqueue(state, state.ExpiresAt);
            _ended.Add(keyState);
            Publish(state);
        }
    }

    /// <inheritdoc />
    public HlsPlaylistSnapshot? GetPlaylist(string application, string streamIdentity)
    {
        PurgeIfDue();
        return _views.TryGetValue((application, streamIdentity), out var view) ? view.Playlist : null;
    }

    /// <inheritdoc />
    public HlsSegment? GetSegment(string application, string streamIdentity, string name)
    {
        PurgeIfDue();
        return Find(application, streamIdentity, name);
    }

    /// <inheritdoc />
    /// <remarks>A segment the store releases concurrently may be reported as not stored; a leased one stays valid.</remarks>
    public HlsSegmentLease? AcquireSegment(string application, string streamIdentity, string name)
    {
        PurgeIfDue();
        return Find(application, streamIdentity, name)?.TryAcquire();
    }

    /// <inheritdoc />
    /// <remarks>
    /// The budget is <see cref="HlsOptions.MaxTotalStorageMegabytes"/>, so segment memory stays within twice the
    /// storage limit: stored segments plus evicted ones still being read.
    /// </remarks>
    public HlsSegmentAccess TryAcquireSegment(string application, string streamIdentity, string name, out HlsSegmentLease? lease)
    {
        PurgeIfDue();
        lease = null;
        var access = Find(application, streamIdentity, name)?.TryAcquire(_readers, out lease) ?? HlsSegmentAccess.NotStored;
        if (access == HlsSegmentAccess.ReaderMemoryExhausted)
        {
            _metrics.ReaderRefused();
        }

        return access;
    }

    /// <summary>Gets the reader memory budget.</summary>
    internal HlsReaderMemoryBudget ReaderBudget => _readers;

    /// <summary>
    /// Releases every stored segment. Leases already taken stay valid until disposed; afterwards nothing is stored,
    /// readers find nothing and writers are refused.
    /// </summary>
    public void Dispose()
    {
        using (EnterGate())
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var keyState in _keys.Values)
            {
                foreach (var state in keyState.Instances())
                {
                    foreach (var segment in state.Segments.Values)
                    {
                        segment.Release();
                    }

                    _metrics.StorageChanged(-state.Bytes);
                    state.Segments.Clear();
                    state.Bytes = 0;
                }
            }

            _keys.Clear();
            _views.Clear();
            _bySession.Clear();
            _deadlines.Clear();
            _ended.Clear();
            _changed.Clear();
            _totalBytes = 0;
        }
    }

    /// <summary>Returns a stored segment from the read views.</summary>
    /// <param name="application">The application.</param>
    /// <param name="streamIdentity">The stream identity.</param>
    /// <param name="name">The segment URI.</param>
    /// <returns>The segment, or <see langword="null"/>.</returns>
    private HlsSegment? Find(string application, string streamIdentity, string name) =>
        _views.TryGetValue((application, streamIdentity), out var view) && view.Segments.TryGetValue(name, out var segment) ? segment : null;

    /// <summary>Runs the lazy expiry under the lock, but only when a deadline is due; readers otherwise need no lock.</summary>
    private void PurgeIfDue()
    {
        if (_time.GetUtcNow().UtcTicks >= Volatile.Read(ref _nextDeadline))
        {
            using (EnterGate())
            {
                PurgeExpired();
            }
        }
    }

    /// <inheritdoc />
    public bool ContainsStream(string application, string streamIdentity)
    {
        PurgeIfDue();
        return _views.ContainsKey((application, streamIdentity));
    }

    /// <inheritdoc />
    /// <remarks>Expiry is evaluated first, as on every call, so the list reflects the current time.</remarks>
    public IReadOnlyList<HlsStreamStatus> GetStreams()
    {
        using (EnterGate())
        {
            PurgeExpired();
            var streams = new List<HlsStreamStatus>();
            foreach (var keyState in _keys.Values)
            {
                foreach (var state in keyState.Instances())
                {
                    if (state.IncarnationId is { } incarnationId)
                    {
                        var status = state.Withdrawn ? HlsStreamState.Withdrawn : state.Ended ? HlsStreamState.Ended : HlsStreamState.Live;
                        var served = ReferenceEquals(keyState.Current, state) && state.Snapshot is not null;
                        streams.Add(new HlsStreamStatus(state.Key.Application, state.Key.Identity, incarnationId, state.Generation, status, served));
                    }
                }
            }

            return streams.AsReadOnly();
        }
    }

    /// <summary>Returns the availability deadline a segment would get if removed from its playlist now.</summary>
    /// <param name="segmentDuration">The segment duration.</param>
    /// <param name="longestPlaylist">The duration of the longest published playlist that listed it.</param>
    /// <param name="removedAt">When it is removed.</param>
    /// <returns><paramref name="removedAt"/> + segment duration + longest playlist duration (RFC 8216, section 6.2.2).</returns>
    internal static DateTimeOffset AvailabilityDeadline(TimeSpan segmentDuration, TimeSpan longestPlaylist, DateTimeOffset removedAt) =>
        removedAt + segmentDuration + longestPlaylist;

    /// <summary>Throws if <paramref name="name"/> is already used by any instance of the key.</summary>
    /// <param name="keyState">The key.</param>
    /// <param name="name">The segment name.</param>
    private static void EnsureUnique(KeyState keyState, string name)
    {
        if (keyState.Instances().Any(s => s.Segments.ContainsKey(name)))
        {
            throw new InvalidOperationException($"Segment {name} is already stored for this stream.");
        }
    }

    /// <summary>Whether a listed or retired segment of <paramref name="state"/> needs <paramref name="initialization"/>.</summary>
    /// <param name="state">The stream.</param>
    /// <param name="initialization">The initialization segment name.</param>
    /// <returns><see langword="true"/> if referenced.</returns>
    private static bool IsReferenced(StreamState state, string initialization) =>
        (!state.Withdrawn && state.Playlist.Entries.Any(e => string.Equals(e.InitializationName, initialization, StringComparison.Ordinal)))
        || state.Retired.Any(r => string.Equals(r.Entry.InitializationName, initialization, StringComparison.Ordinal));

    /// <summary>Publishes a new snapshot and records, for every listed segment, the longest playlist that listed it.</summary>
    /// <param name="state">The stream.</param>
    private void Publish(StreamState state)
    {
        _changed.Add(state.Owner);
        var playlist = state.Playlist;
        if (playlist.Entries.Count == 0)
        {
            state.Snapshot = null;
            return;
        }

        state.Snapshot = new HlsPlaylistSnapshot(playlist.Render(_uriPrefix), playlist.MediaSequence, [.. playlist.Entries], playlist.Ended) { Version = ++_lastVersion };
        foreach (var entry in playlist.Entries)
        {
            if (!state.LongestPlaylist.TryGetValue(entry.Name, out var longest) || longest < playlist.Duration)
            {
                state.LongestPlaylist[entry.Name] = playlist.Duration;
            }
        }
    }

    /// <summary>Returns the active stream written by <paramref name="sessionId"/>.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The stream.</returns>
    private StreamState Writer(Guid sessionId) =>
        _bySession.TryGetValue(sessionId, out var state) ? state : throw new InvalidOperationException($"Session {sessionId} has no open stream.");

    /// <summary>
    /// Whether <paramref name="bytes"/> more fit within both limits. Nothing still available is released; under global
    /// pressure ended streams' playlists are withdrawn early so their segments start draining.
    /// </summary>
    /// <param name="state">The stream receiving the bytes.</param>
    /// <param name="bytes">The footprint of the new segment.</param>
    /// <returns><see cref="HlsAppendResult.Published"/> if the segment fits; otherwise why not.</returns>
    private HlsAppendResult HasRoom(StreamState state, long bytes)
    {
        if (state.Bytes + bytes > _options.MaxSessionStorageBytes)
        {
            return state.Retired.Count == 0 ? HlsAppendResult.StreamLimitExceeded : HlsAppendResult.StorageFull;
        }

        if (_totalBytes + bytes <= _options.MaxTotalStorageBytes)
        {
            return HlsAppendResult.Published;
        }

        if (_ended.Count > 0)
        {
            foreach (var keyState in _ended.ToArray())
            {
                Withdraw(keyState, keyState.Current!, StreamEventReasons.StoragePressure);
                TryRemoveKey(keyState);
            }
        }

        return _totalBytes + bytes <= _options.MaxTotalStorageBytes ? HlsAppendResult.Published : HlsAppendResult.StorageFull;
    }

    /// <summary>Moves a segment removed from a playlist to the retired set with its availability deadline.</summary>
    /// <param name="state">The stream.</param>
    /// <param name="entry">The removed segment.</param>
    /// <param name="now">The removal time.</param>
    /// <param name="reason">The eviction reason tag used when it expires.</param>
    private void Retire(StreamState state, HlsSegmentEntry entry, DateTimeOffset now, string reason)
    {
        state.LongestPlaylist.Remove(entry.Name, out var longest);
        var deadline = AvailabilityDeadline(entry.Duration, longest, now);
        state.Retired.Add(new RetiredSegment(entry, deadline, reason));
        _deadlines.Enqueue(state, deadline);
    }

    /// <summary>
    /// Withdraws a stream's playlist: readers no longer get it, and each listed segment is retired with its
    /// availability deadline (which is never shorter than the playlist duration, as RFC 8216 requires when a whole
    /// presentation is removed).
    /// </summary>
    /// <param name="keyState">The key.</param>
    /// <param name="state">The stream.</param>
    /// <param name="reason">The <see cref="StreamEvent.Reason"/> of the withdrawal.</param>
    private void Withdraw(KeyState keyState, StreamState state, string reason)
    {
        _bySession.Remove(state.SessionId);
        if (ReferenceEquals(keyState.Current, state))
        {
            keyState.Current = null;
            _ended.Remove(keyState);
        }

        _changed.Add(keyState);

        var now = _time.GetUtcNow();
        foreach (var entry in state.Playlist.Entries)
        {
            Retire(state, entry, now, "withdrawn");
        }

        state.Withdrawn = true;
        state.Snapshot = null;
        state.CurrentInitialization = null;
        ReleaseUnreferencedInitializations(state);
        Raise(StreamEventType.PlaylistWithdrawn, state, now, reason);
        if (state.Segments.Count > 0)
        {
            keyState.Withdrawn.Add(state);
        }
        else
        {
            Raise(StreamEventType.Drained, state, now);
        }
    }

    /// <summary>
    /// Releases segments past their availability deadline and withdraws ended playlists past their retention. Only
    /// streams with a due deadline are visited.
    /// </summary>
    private void PurgeExpired()
    {
        var now = _time.GetUtcNow();
        while (_deadlines.TryPeek(out var state, out var deadline) && deadline <= now)
        {
            _deadlines.Dequeue();
            Expire(state, now);
        }
    }

    /// <summary>Handles a due deadline of one stream instance; a stale one changes nothing.</summary>
    /// <param name="state">The stream.</param>
    /// <param name="now">The current time.</param>
    private void Expire(StreamState state, DateTimeOffset now)
    {
        var keyState = state.Owner;
        if (keyState.Removed)
        {
            return;
        }

        if (ReferenceEquals(keyState.Current, state))
        {
            if (state.Ended && state.ExpiresAt <= now)
            {
                Withdraw(keyState, state, StreamEventReasons.RetentionExpired);
            }
        }
        else if (!keyState.Withdrawn.Contains(state))
        {
            return;
        }

        if (state.Retired.Count > 0 && state.Retired.Exists(r => r.ExpiresAt <= now))
        {
            foreach (var expired in state.Retired.FindAll(r => r.ExpiresAt <= now))
            {
                Release(state, expired.Entry.Name, expired.Reason);
            }

            state.Retired.RemoveAll(r => r.ExpiresAt <= now);
            ReleaseUnreferencedInitializations(state);
        }

        RemoveDrained(keyState);
        TryRemoveKey(keyState);
    }

    /// <summary>Forgets withdrawn streams with no remaining segments, raising <see cref="StreamEventType.Drained"/> for each.</summary>
    /// <param name="keyState">The key.</param>
    private void RemoveDrained(KeyState keyState)
    {
        var withdrawn = keyState.Withdrawn;
        var kept = 0;
        for (var i = 0; i < withdrawn.Count; i++)
        {
            var state = withdrawn[i];
            if (state.Segments.Count == 0)
            {
                Raise(StreamEventType.Drained, state, _time.GetUtcNow());
            }
            else
            {
                withdrawn[kept++] = state;
            }
        }

        withdrawn.RemoveRange(kept, withdrawn.Count - kept);
    }

    /// <summary>Records a lifecycle event of a stream instance, to be published once the lock is released.</summary>
    /// <param name="type">The transition.</param>
    /// <param name="state">The stream.</param>
    /// <param name="now">When it happened.</param>
    /// <param name="reason">The reason, if any.</param>
    private void Raise(StreamEventType type, StreamState state, DateTimeOffset now, string? reason = null)
    {
        if (_events is not null && state.IncarnationId is { } incarnationId)
        {
            _pendingEvents.Add(StreamEventFactory.Create(type, state.Key.Application, state.Key.Identity, incarnationId, now, reason));
        }
    }

    /// <summary>Acquires <see cref="_gate"/>; disposing the scope releases it and then publishes the events raised meanwhile.</summary>
    /// <returns>The scope.</returns>
    private GateScope EnterGate()
    {
        _gate.Enter();
        return new GateScope(this);
    }

    /// <summary>Forgets a key with no stream and no remaining segments, keeping its numbering.</summary>
    /// <param name="keyState">The key.</param>
    private void TryRemoveKey(KeyState keyState)
    {
        if (keyState.Removed || keyState.Current is not null || keyState.Withdrawn.Count > 0)
        {
            return;
        }

        _numbering[keyState.Key] = new HlsStreamNumbering(keyState.NextSequence, keyState.NextInitialization);
        _keys.Remove(keyState.Key);
        _views.TryRemove(keyState.Key, out _);
        keyState.Removed = true;
    }

    /// <summary>Replaces the read views of the keys changed under the lock and publishes the next deadline.</summary>
    private void PublishViews()
    {
        foreach (var keyState in _changed)
        {
            if (keyState.Removed)
            {
                continue;
            }

            var segments = new Dictionary<string, HlsSegment>(StringComparer.Ordinal);
            foreach (var state in keyState.Instances())
            {
                foreach (var (name, segment) in state.Segments)
                {
                    segments.TryAdd(name, segment);
                }
            }

            _views[keyState.Key] = new KeyView(keyState.Current?.Snapshot, segments);
        }

        _changed.Clear();
        Volatile.Write(ref _nextDeadline, _deadlines.TryPeek(out _, out var deadline) ? deadline.UtcTicks : long.MaxValue);
    }

    /// <summary>Stores a segment and accounts for its footprint, so the limits bound the memory it holds.</summary>
    /// <param name="state">The stream.</param>
    /// <param name="segment">The segment.</param>
    private void Store(StreamState state, HlsSegment segment)
    {
        _changed.Add(state.Owner);
        state.Segments.Add(segment.Name, segment);
        state.Bytes += segment.Footprint;
        _totalBytes += segment.Footprint;
        _metrics.StorageChanged(segment.Footprint);
    }

    /// <summary>Releases a stored segment.</summary>
    /// <param name="state">The stream.</param>
    /// <param name="name">The segment name.</param>
    /// <param name="reason">The eviction reason tag.</param>
    private void Release(StreamState state, string name, string reason)
    {
        if (state.Segments.Remove(name, out var segment))
        {
            _changed.Add(state.Owner);
            state.Bytes -= segment.Footprint;
            _totalBytes -= segment.Footprint;
            _metrics.StorageChanged(-segment.Footprint);
            _metrics.Evicted(reason);
            segment.Release();
        }
    }

    /// <summary>Releases initialization segments no kept segment needs.</summary>
    /// <param name="state">The stream.</param>
    private void ReleaseUnreferencedInitializations(StreamState state)
    {
        foreach (var segment in state.Segments.Values.Where(s => s.Kind == HlsSegmentKind.Initialization).ToList())
        {
            if (!string.Equals(segment.Name, state.CurrentInitialization, StringComparison.Ordinal) && !IsReferenced(state, segment.Name))
            {
                Release(state, segment.Name, "unreferenced");
            }
        }
    }

    /// <summary>A segment removed from a playlist that is still available.</summary>
    /// <param name="Entry">Its playlist entry.</param>
    /// <param name="ExpiresAt">Its availability deadline.</param>
    /// <param name="Reason">The eviction reason tag: retired or withdrawn.</param>
    private readonly record struct RetiredSegment(HlsSegmentEntry Entry, DateTimeOffset ExpiresAt, string Reason);

    /// <summary>What readers see of one key: replaced, never modified, whenever the key changes.</summary>
    /// <param name="Playlist">The published playlist of the current stream, if any.</param>
    /// <param name="Segments">Every stored segment of every instance of the key, by name.</param>
    private sealed record KeyView(HlsPlaylistSnapshot? Playlist, Dictionary<string, HlsSegment> Segments);

    /// <summary>All instances of one stream key.</summary>
    /// <param name="key">The key.</param>
    private sealed class KeyState((string Application, string Identity) key)
    {
        /// <summary>Gets the key.</summary>
        public (string Application, string Identity) Key { get; } = key;

        /// <summary>Gets or sets a value indicating whether the key has been forgotten; its stream instances are inert.</summary>
        public bool Removed { get; set; }

        /// <summary>Gets or sets the stream whose playlist is published (active or ended), if any.</summary>
        public StreamState? Current { get; set; }

        /// <summary>Gets withdrawn streams whose segments are still available.</summary>
        public List<StreamState> Withdrawn { get; } = [];

        /// <summary>Gets or sets the next media sequence number for the key.</summary>
        public long NextSequence { get; set; }

        /// <summary>Gets or sets the next initialization segment index for the key.</summary>
        public int NextInitialization { get; set; }

        /// <summary>Returns the current stream, then withdrawn ones.</summary>
        /// <returns>The instances.</returns>
        public IEnumerable<StreamState> Instances()
        {
            if (Current is not null)
            {
                yield return Current;
            }

            foreach (var state in Withdrawn)
            {
                yield return state;
            }
        }
    }

    /// <summary>
    /// Holds <see cref="_gate"/>. Disposing takes the pending lifecycle events, releases the lock and only then
    /// publishes them, so publication never runs under the store lock.
    /// </summary>
    /// <param name="store">The store.</param>
    private readonly ref struct GateScope(InMemoryHlsSegmentStore store)
    {
        /// <summary>Replaces changed read views, releases the lock and publishes the events raised while it was held.</summary>
        public void Dispose()
        {
            StreamEvent[]? events = null;
            try
            {
                store.PublishViews();
                if (store._pendingEvents.Count > 0)
                {
                    events = [.. store._pendingEvents];
                    store._pendingEvents.Clear();
                }
            }
            finally
            {
                store._gate.Exit();
            }

            foreach (var streamEvent in events ?? [])
            {
                store._events!.Publish(streamEvent);
            }
        }
    }

    /// <summary>State of one stream instance.</summary>
    /// <param name="owner">The key it belongs to.</param>
    /// <param name="sessionId">The writer session.</param>
    /// <param name="playlist">The playlist.</param>
    /// <param name="incarnationId">The publication's incarnation identifier, if lifecycle events are reported for it.</param>
    /// <param name="generation">The publication's registration generation.</param>
    private sealed class StreamState(KeyState owner, Guid sessionId, HlsPlaylist playlist, string? incarnationId, long generation)
    {
        /// <summary>Gets the publication's registration generation.</summary>
        public long Generation { get; } = generation;

        /// <summary>Gets the key it belongs to.</summary>
        public KeyState Owner { get; } = owner;

        /// <summary>Gets the stream key.</summary>
        public (string Application, string Identity) Key => Owner.Key;

        /// <summary>Gets the writer session.</summary>
        public Guid SessionId { get; } = sessionId;

        /// <summary>Gets the publication's incarnation identifier, or <see langword="null"/> to report no lifecycle events.</summary>
        public string? IncarnationId { get; } = incarnationId;

        /// <summary>Gets the playlist.</summary>
        public HlsPlaylist Playlist { get; } = playlist;

        /// <summary>Gets the stored segments by name.</summary>
        public Dictionary<string, HlsSegment> Segments { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets, for each listed segment, the duration of the longest published playlist that listed it.</summary>
        public Dictionary<string, TimeSpan> LongestPlaylist { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets segments removed from the playlist that are still available.</summary>
        public List<RetiredSegment> Retired { get; } = [];

        /// <summary>Gets or sets the initialization segment of the next media segment.</summary>
        public string? CurrentInitialization { get; set; }

        /// <summary>Gets or sets the bytes held.</summary>
        public long Bytes { get; set; }

        /// <summary>Gets or sets a value indicating whether the playlist has ended (ENDLIST).</summary>
        public bool Ended { get; set; }

        /// <summary>Gets or sets a value indicating whether the playlist has been withdrawn.</summary>
        public bool Withdrawn { get; set; }

        /// <summary>Gets or sets when an ended playlist is withdrawn.</summary>
        public DateTimeOffset ExpiresAt { get; set; }

        /// <summary>Gets or sets the published playlist.</summary>
        public HlsPlaylistSnapshot? Snapshot { get; set; }
    }
}
