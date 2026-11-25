// Acquires the catalog's authoritative inputs as immutable copies: the HLS store's instances first, then the RTMP
// registry's sessions with their current media descriptions. The order matters: a stream is registered before its HLS
// stream opens and unregistered before it closes, so an HLS instance that is still live but no longer registered has
// ended. A registered session absent from the HLS copy either has not opened its HLS stream yet or opened it and the
// store has since withdrawn and forgotten it; the session's HlsStreamOpened flag, read before the HLS copy, tells the
// two apart without any event history.

using RTSP.IngestService.Hls;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtmp.Publishing;

namespace RTSP.IngestService.Streams;

/// <summary>An immutable copy of one registered RTMP session.</summary>
/// <param name="Application">The application.</param>
/// <param name="Stream">The stream identity.</param>
/// <param name="IncarnationId">The incarnation identifier.</param>
/// <param name="Generation">The registration generation.</param>
/// <param name="Publishing">Whether the session was still publishing when copied.</param>
/// <param name="StartedAt">When publishing started.</param>
/// <param name="EndedAt">When it ended, if it had.</param>
/// <param name="EndReason">Why it ended, if it had.</param>
/// <param name="Media">The complete current media description, if established.</param>
/// <param name="HlsOpenedBeforeCopy">
/// Whether the session's HLS stream had been opened before the HLS copy of the same snapshot was taken, so its
/// absence from that copy means it was withdrawn and drained rather than not opened yet.
/// </param>
internal sealed record PublishingStatus(
    string Application,
    string Stream,
    string IncarnationId,
    long Generation,
    bool Publishing,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? EndReason,
    StreamMediaDescription? Media,
    bool HlsOpenedBeforeCopy = false);

/// <summary>Immutable copies of both authoritative sources, acquired one after the other (not globally atomic).</summary>
/// <param name="Hls">The HLS store's instances, acquired first.</param>
/// <param name="Publishing">The registry's sessions, acquired second.</param>
internal sealed record StreamInventorySnapshot(IReadOnlyList<HlsStreamStatus> Hls, IReadOnlyList<PublishingStatus> Publishing);

/// <summary>Acquires authoritative inventory snapshots.</summary>
internal interface IStreamInventorySource
{
    /// <summary>Acquires a snapshot; no source lock is held after it returns.</summary>
    /// <returns>The snapshot.</returns>
    StreamInventorySnapshot Acquire();
}

/// <summary>Acquires snapshots from the running service's registry, media stage and HLS store; any may be absent.</summary>
/// <param name="registry">The RTMP registry.</param>
/// <param name="media">The media stage, for stream descriptions.</param>
/// <param name="hls">The HLS store's inventory.</param>
internal sealed class RuntimeStreamInventorySource(PublishedStreamRegistry? registry, MediaPublishSessionHandler? media, IHlsStreamInventory? hls) : IStreamInventorySource
{
    /// <inheritdoc />
    public StreamInventorySnapshot Acquire()
    {
        var openedBeforeCopy = new HashSet<PublisherSession>(ReferenceEqualityComparer.Instance);
        foreach (var session in registry?.Sessions ?? [])
        {
            if (session.HlsStreamOpened)
            {
                openedBeforeCopy.Add(session);
            }
        }

        var streams = hls?.GetStreams() ?? [];
        var sessions = registry?.Sessions ?? [];
        var publishing = new List<PublishingStatus>(sessions.Count);
        foreach (var session in sessions)
        {
            var endReason = session.EndReason;
            var info = media?.FindStreamInfo(session.SessionId);
            publishing.Add(new PublishingStatus(
                session.Application,
                session.StreamIdentity,
                session.IncarnationId,
                session.Generation,
                endReason is null,
                session.StartedAt,
                session.EndedAt,
                endReason is { } reason ? StreamEventFactory.EndReason(reason) : null,
                info is { } described && (described.Video is not null || described.Audio is not null) ? StreamEventFactory.Describe(described) : null,
                openedBeforeCopy.Contains(session)));
        }

        return new StreamInventorySnapshot(streams, publishing.AsReadOnly());
    }
}
