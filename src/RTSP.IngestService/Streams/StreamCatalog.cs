// The stream catalog: an in-memory, reconciled read model of active stream incarnations. Its authority is the RTMP
// registry and the HLS store, read through IStreamInventorySource; lifecycle events only accelerate updates of
// incarnations a snapshot already established, and request reconciliation for anything else. Every change derives a
// new immutable StreamCatalogSnapshot from the published one, replacing only the entries that changed (a
// reconciliation still merges every incarnation of its snapshot), and publishes it with one reference write, so
// readers never lock.
//
// Incarnation safety: entries are keyed by incarnation identifier, never by name, so an event or snapshot about one
// incarnation cannot touch another. Within an incarnation, publishing and HLS states only move forward (merges take
// the later state), so a stale snapshot cannot undo an event and a duplicate or late event is a no-op. Entries are
// created only from snapshots; events never create one. A drained, ended incarnation is removed and tombstoned with
// the event epoch, and a snapshot whose acquisition began before that epoch cannot bring it back. The newest
// incarnation of a name is chosen by the registry's monotonic generation, never by identifier or time.

using RTSP.IngestService.Hls;

namespace RTSP.IngestService.Streams;

/// <summary>The reconciled stream catalog; also the stream event consumer.</summary>
/// <remarks>
/// Thread-safe. <see cref="Current"/> is lock-free. Event application and reconciliation publication are serialized
/// by a short writer lock that is never held while acquiring a snapshot or while readers serialize. Reconciliations
/// are serialized among themselves, so each one's snapshot is newer than the previous one's.
/// </remarks>
public sealed partial class StreamCatalog : IStreamEventConsumer, IDisposable
{
    /// <summary>The authoritative sources.</summary>
    private readonly IStreamInventorySource _source;

    /// <summary>The metrics.</summary>
    private readonly StreamCatalogMetrics _metrics;

    /// <summary>The time source.</summary>
    private readonly TimeProvider _time;

    /// <summary>The logger.</summary>
    private readonly ILogger<StreamCatalog> _logger;

    /// <summary>Returns the event channel's dropped count, if events are delivered through one.</summary>
    private readonly Func<long>? _droppedCount;

    /// <summary>Serializes publication of new states.</summary>
    private readonly Lock _writeGate = new();

    /// <summary>Serializes reconciliations.</summary>
    private readonly Lock _reconcileGate = new();

    /// <summary>Released when a reconciliation request becomes pending.</summary>
    private readonly SemaphoreSlim _requested = new(0, 1);

    /// <summary>The event epoch at which each removed incarnation was drained; guarded by <see cref="_writeGate"/>.</summary>
    private readonly Dictionary<string, long> _tombstones = new(StringComparer.Ordinal);

    /// <summary>The event epoch of each incarnation's last event-driven change since the last reconciliation; guarded by <see cref="_writeGate"/>.</summary>
    private readonly Dictionary<string, long> _eventEpochs = new(StringComparer.Ordinal);

    /// <summary>How many incarnations the last reconciliation found not yet settled.</summary>
    private int _unsettled;

    /// <summary>The published state.</summary>
    private volatile StreamCatalogSnapshot _current = StreamCatalogSnapshot.Empty;

    /// <summary>Counts event-driven changes; guarded by <see cref="_writeGate"/>.</summary>
    private long _epoch;

    /// <summary>The last dropped count observed.</summary>
    private long _lastDropped;

    /// <summary>1 while a reconciliation request is pending.</summary>
    private int _pending;

    /// <summary>The timestamp of the last successful reconciliation, or 0.</summary>
    private long _lastSuccess;

    /// <summary>Initializes a new instance of the <see cref="StreamCatalog"/> class.</summary>
    /// <param name="source">The authoritative sources.</param>
    /// <param name="metrics">The metrics.</param>
    /// <param name="time">The time source.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="droppedCount">Returns the event channel's dropped count; <see langword="null"/> if not applicable.</param>
    internal StreamCatalog(IStreamInventorySource source, StreamCatalogMetrics metrics, TimeProvider time, ILogger<StreamCatalog> logger, Func<long>? droppedCount = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _droppedCount = droppedCount;
        _lastDropped = droppedCount?.Invoke() ?? 0;
        _metrics.Attach(() =>
        {
            var last = Interlocked.Read(ref _lastSuccess);
            return (_current.Streams.Count, last == 0 ? null : _time.GetElapsedTime(last));
        });
    }

    /// <summary>Gets the published state; one immutable, internally consistent snapshot.</summary>
    public StreamCatalogSnapshot Current => _current;

    /// <summary>
    /// Gets a value indicating whether the last reconciliation found an incarnation whose sources have not settled:
    /// its HLS stream still live although its RTMP session has left the registry, or live with no playlist published
    /// yet. The HLS close and the first published playlist happen without an event, so only a later reconciliation
    /// can observe them.
    /// </summary>
    internal bool HasUnsettled => Volatile.Read(ref _unsettled) > 0;

    /// <inheritdoc />
    public ValueTask ConsumeAsync(StreamEvent streamEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);
        if (_droppedCount?.Invoke() is { } dropped && dropped > Interlocked.Exchange(ref _lastDropped, dropped))
        {
            LogEventsLost();
            RequestReconciliation(StreamCatalogTrigger.Loss);
        }

        if (Apply(streamEvent))
        {
            RequestReconciliation(StreamCatalogTrigger.Event);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _requested.Dispose();

    /// <summary>Requests a reconciliation; merged into an already pending request if there is one.</summary>
    /// <param name="trigger">Why it is requested.</param>
    internal void RequestReconciliation(StreamCatalogTrigger trigger)
    {
        _metrics.Requested(trigger);
        if (Interlocked.Exchange(ref _pending, 1) == 0)
        {
            _requested.Release();
        }
        else
        {
            _metrics.Coalesced();
        }
    }

    /// <summary>
    /// Waits for a pending reconciliation request and takes it. A request made after this returns becomes pending
    /// again, so it causes a further reconciliation rather than being lost.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task completing when a request was taken.</returns>
    internal async Task WaitForRequestAsync(CancellationToken cancellationToken)
    {
        await _requested.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _pending, 0);
    }

    /// <summary>Takes a pending reconciliation request, if there is one, without waiting.</summary>
    /// <returns><see langword="true"/> if a request was taken.</returns>
    internal bool TryTakeRequest()
    {
        if (!_requested.Wait(0))
        {
            return false;
        }

        Interlocked.Exchange(ref _pending, 0);
        return true;
    }

    /// <summary>Records a request and reconciles immediately on the calling thread.</summary>
    /// <param name="trigger">Why.</param>
    /// <returns><see langword="true"/> if a new state was published.</returns>
    internal bool ReconcileNow(StreamCatalogTrigger trigger)
    {
        _metrics.Requested(trigger);
        return Reconcile();
    }

    /// <summary>
    /// Reconciles from a new snapshot of the authoritative sources and publishes the result. On failure the
    /// published state is kept.
    /// </summary>
    /// <returns><see langword="true"/> if a new state was published.</returns>
    internal bool Reconcile()
    {
        lock (_reconcileGate)
        {
            var started = _time.GetTimestamp();
            try
            {
                long epoch;
                lock (_writeGate)
                {
                    epoch = _epoch;
                }

                var snapshot = _source.Acquire();
                bool first;
                lock (_writeGate)
                {
                    first = !_current.IsInitialized;
                    Publish(snapshot, epoch);
                }

                Interlocked.Exchange(ref _lastSuccess, _time.GetTimestamp());
                if (first)
                {
                    LogInitialized(_current.Streams.Count);
                }

                _metrics.Reconciled(true, _time.GetElapsedTime(started));
                return true;
            }
            catch (Exception ex)
            {
                _metrics.Reconciled(false, _time.GetElapsedTime(started));
                LogReconciliationFailed(ex);
                return false;
            }
        }
    }

    /// <summary>Applies an event to the incarnation it concerns, if the catalog knows it.</summary>
    /// <param name="streamEvent">The event.</param>
    /// <returns><see langword="true"/> if a reconciliation should follow.</returns>
    internal bool Apply(StreamEvent streamEvent)
    {
        lock (_writeGate)
        {
            var current = _current;
            var incarnation = streamEvent.IncarnationId;
            var entry = current.FindIncarnation(incarnation);
            if (entry is null || entry.Application != streamEvent.Application || entry.Stream != streamEvent.Stream)
            {
                return !_tombstones.ContainsKey(incarnation);
            }

            var reconcile = false;
            var next = entry;
            switch (streamEvent.Type)
            {
                case StreamEventType.MediaDescribed when streamEvent.Media is { } media && media != entry.Media:
                    next = entry with { Media = media };
                    break;
                case StreamEventType.Ended:
                    var publishing = entry.Publishing;
                    if (publishing.State != StreamPublishingState.Ended || publishing.EndedAt is null || publishing.EndReason is null)
                    {
                        next = entry with
                        {
                            Publishing = publishing with
                            {
                                State = StreamPublishingState.Ended,
                                EndedAt = publishing.EndedAt ?? streamEvent.Timestamp,
                                EndReason = publishing.EndReason ?? streamEvent.Reason,
                            },
                        };
                    }

                    // The HLS stream closes after the end event, without an event of its own.
                    reconcile = true;
                    break;
                case StreamEventType.PlaylistWithdrawn when entry.Hls.State != StreamHlsState.Withdrawn:
                    next = entry with { Hls = new StreamHlsInfo { State = StreamHlsState.Withdrawn } };
                    break;
                case StreamEventType.Drained when entry.Publishing.State == StreamPublishingState.Ended:
                    next = null;
                    break;
                case StreamEventType.Drained when entry.Hls.State != StreamHlsState.Withdrawn:
                    next = entry with { Hls = new StreamHlsInfo { State = StreamHlsState.Withdrawn } };
                    break;
                default:
                    break;
            }

            if (ReferenceEquals(next, entry))
            {
                return reconcile;
            }

            var epoch = ++_epoch;
            if (next is null)
            {
                _tombstones[incarnation] = epoch;
                _eventEpochs.Remove(incarnation);
                _current = current.With(current.Version + 1, current.IsInitialized, current.ReconciledAt, [], [incarnation]);
            }
            else
            {
                _eventEpochs[incarnation] = epoch;
                _current = current.With(current.Version + 1, current.IsInitialized, current.ReconciledAt, [next], []);
            }

            return reconcile;
        }
    }

    /// <summary>Maps an HLS store state.</summary>
    /// <param name="state">The store state.</param>
    /// <returns>The catalog state.</returns>
    private static StreamHlsState Map(HlsStreamState state) => state switch
    {
        HlsStreamState.Live => StreamHlsState.Live,
        HlsStreamState.Ended => StreamHlsState.Ended,
        _ => StreamHlsState.Withdrawn,
    };

    /// <summary>Merges a snapshot into the published state and publishes the result; called under <see cref="_writeGate"/>.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="epoch">The event epoch when its acquisition began.</param>
    private void Publish(StreamInventorySnapshot snapshot, long epoch)
    {
        var previous = _current;
        var hls = new Dictionary<string, HlsStreamStatus>(StringComparer.Ordinal);
        foreach (var status in snapshot.Hls)
        {
            hls.TryAdd(status.IncarnationId, status);
        }

        var registered = new Dictionary<string, PublishingStatus>(StringComparer.Ordinal);
        foreach (var status in snapshot.Publishing)
        {
            registered.TryAdd(status.IncarnationId, status);
        }

        var changes = new List<StreamCatalogEntry>();
        var removals = new List<string>();
        var unsettled = 0;
        foreach (var session in registered.Values)
        {
            Merge(session.IncarnationId, session, hls.GetValueOrDefault(session.IncarnationId));
        }

        foreach (var stream in hls.Values)
        {
            if (!registered.ContainsKey(stream.IncarnationId))
            {
                Merge(stream.IncarnationId, null, stream);
            }
        }

        foreach (var entry in previous.Streams)
        {
            if (!registered.ContainsKey(entry.IncarnationId) && !hls.ContainsKey(entry.IncarnationId))
            {
                removals.Add(entry.IncarnationId);
            }
        }

        var next = previous.With(previous.Version + 1, true, _time.GetUtcNow(), changes, removals);
        Volatile.Write(ref _unsettled, unsettled);
        _eventEpochs.Clear();
        foreach (var drained in _tombstones.Where(t => t.Value <= epoch).Select(t => t.Key).ToArray())
        {
            _tombstones.Remove(drained);
        }

        _current = next;

        // Adds the incarnation's merged entry to changes unless it equals its prior entry, or to removals if it
        // should not be listed.
        void Merge(string incarnation, PublishingStatus? session, HlsStreamStatus? stream)
        {
            var prior = previous.FindIncarnation(incarnation);
            if (_tombstones.TryGetValue(incarnation, out var drainedAt) && drainedAt > epoch)
            {
                if (prior is not null)
                {
                    removals.Add(incarnation);
                }

                return;
            }

            var isPublishing = session is { Publishing: true } && prior?.Publishing.State != StreamPublishingState.Ended;

            StreamHlsState hlsState;
            var playlistAvailable = false;
            if (stream is not null)
            {
                hlsState = Map(stream.State);
                playlistAvailable = stream.PlaylistAvailable;
            }
            else if (session is { Publishing: true })
            {
                // Opened before the HLS copy yet absent from it means withdrawn and drained; otherwise not opened yet.
                hlsState = session.HlsOpenedBeforeCopy || (prior is not null && prior.Hls.State != StreamHlsState.Pending) ? StreamHlsState.Withdrawn : StreamHlsState.Pending;
            }
            else
            {
                if (prior is not null)
                {
                    removals.Add(incarnation);
                }

                return;
            }

            if (prior is not null && prior.Hls.State > hlsState)
            {
                hlsState = prior.Hls.State;
                playlistAvailable = prior.Hls.PlaylistAvailable;
            }

            // The HLS close after an RTMP end and the first published playlist both happen without an event.
            if (hlsState == StreamHlsState.Live && (!isPublishing || !playlistAvailable))
            {
                unsettled++;
            }

            var eventNewer = _eventEpochs.TryGetValue(incarnation, out var changedAt) && changedAt > epoch;
            var media = eventNewer && prior?.Media is not null ? prior.Media
                : session is { Publishing: true } ? session.Media
                : session?.Media ?? prior?.Media;
            var application = session?.Application ?? stream!.Application;
            var name = session?.Stream ?? stream!.Stream;
            var generation = session?.Generation ?? (stream!.Generation != 0 ? stream.Generation : prior?.Generation ?? 0);
            var state = isPublishing ? StreamPublishingState.Publishing : StreamPublishingState.Ended;
            var startedAt = session?.StartedAt ?? prior?.Publishing.StartedAt;
            var endedAt = session?.EndedAt ?? prior?.Publishing.EndedAt;
            var endReason = session?.EndReason ?? prior?.Publishing.EndReason;
            if (prior is not null
                && string.Equals(prior.Application, application, StringComparison.Ordinal)
                && string.Equals(prior.Stream, name, StringComparison.Ordinal)
                && prior.Generation == generation
                && prior.Publishing.State == state
                && prior.Publishing.StartedAt == startedAt
                && prior.Publishing.EndedAt == endedAt
                && string.Equals(prior.Publishing.EndReason, endReason, StringComparison.Ordinal)
                && prior.Hls.State == hlsState
                && prior.Hls.PlaylistAvailable == playlistAvailable
                && Equals(prior.Media, media))
            {
                return;
            }

            changes.Add(new StreamCatalogEntry
            {
                Application = application,
                Stream = name,
                IncarnationId = incarnation,
                Generation = generation,
                Publishing = new StreamPublishingInfo { State = state, StartedAt = startedAt, EndedAt = endedAt, EndReason = endReason },
                Hls = new StreamHlsInfo { State = hlsState, PlaylistAvailable = playlistAvailable },
                Media = media,
            });
        }
    }

    /// <summary>Logs a failed reconciliation.</summary>
    /// <param name="exception">The failure.</param>
    [LoggerMessage(EventId = 9200, Level = LogLevel.Warning, Message = "Stream catalog reconciliation failed; the previous catalog is kept")]
    private partial void LogReconciliationFailed(Exception exception);

    /// <summary>Logs the first successful reconciliation.</summary>
    /// <param name="streams">The number of entries.</param>
    [LoggerMessage(EventId = 9201, Level = LogLevel.Information, Message = "Stream catalog initialized with {Streams} stream incarnations")]
    private partial void LogInitialized(int streams);

    /// <summary>Logs detected event loss.</summary>
    [LoggerMessage(EventId = 9202, Level = LogLevel.Information, Message = "Stream lifecycle events were dropped; the stream catalog will reconcile")]
    private partial void LogEventsLost();
}
