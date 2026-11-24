// Live HLS media playlist state and rendering (RFC 8216). It keeps the sliding window of completed segments with
// their exact durations, media and discontinuity sequence numbers and initialization-segment references, and renders
// deterministic playlist text. It has no I/O and no clock; the segment store decides when segments are retired.

using System.Globalization;
using System.Text;

namespace RTSP.IngestService.Hls;

/// <summary>A completed media segment listed in a playlist.</summary>
/// <param name="Sequence">The media sequence number.</param>
/// <param name="Name">The segment URI, relative to the playlist.</param>
/// <param name="Duration">The exact segment duration.</param>
/// <param name="InitializationName">The URI of the initialization segment it requires.</param>
/// <param name="Discontinuity">Whether <c>#EXT-X-DISCONTINUITY</c> precedes it.</param>
public sealed record HlsSegmentEntry(long Sequence, string Name, TimeSpan Duration, string InitializationName, bool Discontinuity);

/// <summary>
/// A live media playlist over a sliding window of segments.
/// </summary>
/// <remarks>
/// <para>
/// Version 7 is declared because media segments are fragmented MP4 referenced through <c>#EXT-X-MAP</c>.
/// <c>#EXT-X-TARGETDURATION</c> never changes during the playlist's lifetime (RFC 8216, section 6.2.1), so instead of
/// deriving it from the listed segments, every appended segment is checked against it: its <c>#EXTINF</c> value,
/// rendered in whole milliseconds, must round to at most the target duration (section 4.3.3.1). <c>#EXT-X-MAP</c> is
/// written before the first listed segment and wherever the initialization segment changes; a change is always also
/// a discontinuity.
/// </para>
/// <para>
/// Retirement: after each append, the oldest segments are removed while more than the window size remain and the
/// remaining duration would still be at least three target durations (RFC 8216, section 6.2.2). Not thread-safe.
/// </para>
/// </remarks>
public sealed class HlsPlaylist
{
    /// <summary>
    /// Cap on listed segments, bounding rendering cost. It never removes a segment early: a segment that would need
    /// the playlist to exceed it, because removing the oldest would leave less than three target durations, is
    /// refused instead (see <see cref="HasEntryRoom"/>).
    /// </summary>
    public const int MaxEntries = 1000;

    /// <summary>The listed segments, oldest first.</summary>
    private readonly List<HlsSegmentEntry> _entries = [];

    /// <summary>Initializes a new instance of the <see cref="HlsPlaylist"/> class.</summary>
    /// <param name="targetDurationSeconds">The <c>#EXT-X-TARGETDURATION</c> value, at least 1.</param>
    /// <param name="windowSize">The minimum number of listed segments, at least 1.</param>
    public HlsPlaylist(int targetDurationSeconds, int windowSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetDurationSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 1);
        TargetDurationSeconds = targetDurationSeconds;
        WindowSize = windowSize;
    }

    /// <summary>Gets the target duration in seconds.</summary>
    public int TargetDurationSeconds { get; }

    /// <summary>Gets the minimum number of listed segments.</summary>
    public int WindowSize { get; }

    /// <summary>Gets the listed segments, oldest first.</summary>
    public IReadOnlyList<HlsSegmentEntry> Entries => _entries;

    /// <summary>Gets the sequence number of the first listed segment (or of the next segment when none is listed).</summary>
    public long MediaSequence { get; private set; }

    /// <summary>Gets the number of discontinuities removed from the start of the playlist.</summary>
    public long DiscontinuitySequence { get; private set; }

    /// <summary>Gets a value indicating whether <c>#EXT-X-ENDLIST</c> has been added.</summary>
    public bool Ended { get; private set; }

    /// <summary>Gets the total duration of the listed segments.</summary>
    public TimeSpan Duration { get; private set; }

    /// <summary>
    /// Gets the longest segment duration the playlist accepts: one whose <c>#EXTINF</c> value, as rendered with
    /// millisecond precision, rounds to at most the target duration (RFC 8216, section 4.3.3.1).
    /// </summary>
    public TimeSpan MaxSegmentDuration => LongestSegment(TargetDurationSeconds);

    /// <summary>Gets the shortest duration a live playlist may be reduced to by removing segments: three target durations.</summary>
    public TimeSpan MinimumDuration => TimeSpan.FromSeconds(3L * TargetDurationSeconds);

    /// <summary>Returns the longest segment duration allowed under a target duration.</summary>
    /// <param name="targetDurationSeconds">The target duration.</param>
    /// <returns>The target duration plus 499 milliseconds.</returns>
    public static TimeSpan LongestSegment(int targetDurationSeconds) =>
        TimeSpan.FromSeconds(targetDurationSeconds) + TimeSpan.FromMilliseconds(499);

    /// <summary>Returns a duration in whole milliseconds as rendered in <c>#EXTINF</c> (rounded half up).</summary>
    /// <param name="duration">The duration.</param>
    /// <returns>The milliseconds.</returns>
    public static long RenderedMilliseconds(TimeSpan duration) =>
        (duration.Ticks + (TimeSpan.TicksPerMillisecond / 2)) / TimeSpan.TicksPerMillisecond;

    /// <summary>Throws if <paramref name="entry"/> cannot be appended; never changes the playlist.</summary>
    /// <param name="entry">The segment.</param>
    /// <exception cref="InvalidOperationException">The playlist has ended or the sequence number is out of order.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The duration is negative or rounds above the target duration.</exception>
    public void EnsureCanAppend(HlsSegmentEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (Ended)
        {
            throw new InvalidOperationException("The playlist has ended.");
        }

        var expected = _entries.Count == 0 ? (long?)null : _entries[^1].Sequence + 1;
        if (expected is { } next && entry.Sequence != next)
        {
            throw new InvalidOperationException($"Segment {entry.Sequence} does not follow segment {next - 1}.");
        }

        if (entry.Duration < TimeSpan.Zero || RenderedMilliseconds(entry.Duration) > RenderedMilliseconds(MaxSegmentDuration))
        {
            throw new ArgumentOutOfRangeException(nameof(entry), entry.Duration, $"A segment must last at most {MaxSegmentDuration} under target duration {TargetDurationSeconds} s.");
        }
    }

    /// <summary>
    /// Returns whether a segment of <paramref name="duration"/> can be appended within <see cref="MaxEntries"/>: the
    /// playlist is below the cap, or removing its oldest segment would still leave three target durations listed.
    /// </summary>
    /// <param name="duration">The new segment's duration.</param>
    /// <returns><see langword="true"/> if it fits.</returns>
    public bool HasEntryRoom(TimeSpan duration) =>
        _entries.Count < MaxEntries || Duration + duration - _entries[0].Duration >= MinimumDuration;

    /// <summary>Appends a segment and retires the oldest segments the window no longer needs.</summary>
    /// <param name="entry">The segment; see <see cref="EnsureCanAppend"/> and <see cref="HasEntryRoom"/>.</param>
    /// <returns>The retired segments, oldest first.</returns>
    /// <exception cref="InvalidOperationException">The playlist has ended, the sequence number is out of order, or the playlist is full.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The duration is negative or rounds above the target duration.</exception>
    public IReadOnlyList<HlsSegmentEntry> Append(HlsSegmentEntry entry)
    {
        EnsureCanAppend(entry);
        if (!HasEntryRoom(entry.Duration))
        {
            throw new InvalidOperationException($"The playlist lists {MaxEntries} segments spanning less than three target durations.");
        }

        if (_entries.Count == 0)
        {
            MediaSequence = entry.Sequence;
        }

        _entries.Add(entry);
        Duration += entry.Duration;

        var retired = new List<HlsSegmentEntry>();
        while (_entries.Count > WindowSize && Duration - _entries[0].Duration >= MinimumDuration)
        {
            retired.Add(RemoveOldest());
        }

        return retired;
    }

    /// <summary>Marks the playlist complete; <see cref="Render(string)"/> then ends with <c>#EXT-X-ENDLIST</c>.</summary>
    public void End() => Ended = true;

    /// <summary>Renders the playlist text.</summary>
    /// <returns>The playlist, with LF line endings.</returns>
    public string Render() => Render(string.Empty);

    /// <summary>Renders the playlist text, with <paramref name="uriPrefix"/> before every segment and map URI.</summary>
    /// <param name="uriPrefix">Prefix of each relative URI, for example <c>{generation}/</c>; may be empty.</param>
    /// <returns>The playlist, with LF line endings.</returns>
    public string Render(string uriPrefix)
    {
        ArgumentNullException.ThrowIfNull(uriPrefix);
        var text = new StringBuilder(256 + (_entries.Count * 96));
        text.Append("#EXTM3U\n#EXT-X-VERSION:7\n");
        text.Append(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{TargetDurationSeconds}\n");
        text.Append(CultureInfo.InvariantCulture, $"#EXT-X-MEDIA-SEQUENCE:{MediaSequence}\n");
        if (DiscontinuitySequence > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"#EXT-X-DISCONTINUITY-SEQUENCE:{DiscontinuitySequence}\n");
        }

        string? map = null;
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (i > 0 && entry.Discontinuity)
            {
                text.Append("#EXT-X-DISCONTINUITY\n");
            }

            if (!string.Equals(map, entry.InitializationName, StringComparison.Ordinal))
            {
                map = entry.InitializationName;
                text.Append(CultureInfo.InvariantCulture, $"#EXT-X-MAP:URI=\"{uriPrefix}{map}\"\n");
            }

            var milliseconds = RenderedMilliseconds(entry.Duration);
            text.Append(CultureInfo.InvariantCulture, $"#EXTINF:{milliseconds / 1000}.{milliseconds % 1000:D3},\n{uriPrefix}{entry.Name}\n");
        }

        if (Ended)
        {
            text.Append("#EXT-X-ENDLIST\n");
        }

        return text.ToString();
    }

    /// <summary>Removes the oldest segment and advances the sequence numbers.</summary>
    /// <returns>The removed segment.</returns>
    private HlsSegmentEntry RemoveOldest()
    {
        var removed = _entries[0];
        _entries.RemoveAt(0);
        Duration -= removed.Duration;
        MediaSequence = removed.Sequence + 1;
        if (_entries.Count > 0 && _entries[0].Discontinuity)
        {
            DiscontinuitySequence++;
        }

        return removed;
    }
}
