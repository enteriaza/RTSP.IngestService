// Read-only inventory of the HLS store's stream instances, for the stream catalog. Each call returns a new immutable
// list copied under the store's lock, so it can be enumerated freely afterwards and never changes underneath.

namespace RTSP.IngestService.Hls;

/// <summary>The HLS state of one stream instance.</summary>
public enum HlsStreamState
{
    /// <summary>
    /// The stream is open and being packaged. Its playlist is served only once the first segment has been published;
    /// see <see cref="HlsStreamStatus.PlaylistAvailable"/>.
    /// </summary>
    Live,

    /// <summary>The stream ended normally; its ended playlist (<c>#EXT-X-ENDLIST</c>) is still served.</summary>
    Ended,

    /// <summary>The playlist is no longer served; retained segments stay available until their deadlines (draining).</summary>
    Withdrawn,
}

/// <summary>An immutable status of one stream instance in the HLS store.</summary>
/// <param name="Application">The application.</param>
/// <param name="Stream">The stream identity.</param>
/// <param name="IncarnationId">The publication's incarnation identifier.</param>
/// <param name="Generation">The publication's registration generation, or 0 if none was supplied.</param>
/// <param name="State">The instance's state.</param>
/// <param name="PlaylistAvailable">
/// Whether <see cref="IHlsSegmentStore.GetPlaylist"/> currently returns this instance's playlist, so the playlist
/// route serves it: the instance is the key's current one and has published a playlist. Never true for
/// <see cref="HlsStreamState.Withdrawn"/>, and false for a <see cref="HlsStreamState.Live"/> instance until its first
/// segment is published.
/// </param>
public sealed record HlsStreamStatus(string Application, string Stream, string IncarnationId, long Generation, HlsStreamState State, bool PlaylistAvailable);

/// <summary>Lists the stream instances an HLS store holds.</summary>
public interface IHlsStreamInventory
{
    /// <summary>
    /// Returns every instance that has a playlist or still-available segments and was opened with an incarnation
    /// identifier. Instances whose segments have all been released (drained) are not listed.
    /// </summary>
    /// <returns>A new immutable list.</returns>
    IReadOnlyList<HlsStreamStatus> GetStreams();
}
