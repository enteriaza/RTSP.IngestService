// Tests for live HLS playlist state and rendering: exact text of an initial playlist, EXT-X-MAP placement, media
// sequence progression through the sliding window, the three-target-duration rule, discontinuity sequence numbers,
// capacity removal, the entry cap and EXT-X-ENDLIST.

using RTSP.IngestService.Hls;

namespace RTSP.IngestService.Tests.Hls;

/// <summary>
/// Tests for <see cref="HlsPlaylist"/>.
/// </summary>
public sealed class HlsPlaylistTests
{
    /// <summary>Verifies the exact text of a playlist with two segments.</summary>
    [Fact]
    public void Render_IsExact()
    {
        var playlist = new HlsPlaylist(10, 6);
        Assert.Empty(playlist.Append(Entry(0, 4.0)));
        Assert.Empty(playlist.Append(Entry(1, 3.96)));

        Assert.Equal(
            "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:10\n#EXT-X-MEDIA-SEQUENCE:0\n" +
            "#EXT-X-MAP:URI=\"init-0.mp4\"\n#EXTINF:4.000,\nsegment-0.m4s\n#EXTINF:3.960,\nsegment-1.m4s\n",
            playlist.Render());
        Assert.Equal(TimeSpan.FromSeconds(7.96), playlist.Duration);
        Assert.False(playlist.Ended);
    }

    /// <summary>Verifies the window keeps at least three target durations and the media sequence advances.</summary>
    [Fact]
    public void Window_RetiresOldestWhileThreeTargetDurationsRemain()
    {
        var playlist = new HlsPlaylist(2, 3);
        var retired = new List<HlsSegmentEntry>();
        for (var i = 0; i < 6; i++)
        {
            retired.AddRange(playlist.Append(Entry(i, 1.0)));
        }

        // Three one-second segments are only three seconds, less than 3 x 2 s, so six are needed.
        Assert.Empty(retired);
        retired.AddRange(playlist.Append(Entry(6, 1.0)));
        Assert.Equal([0L], retired.Select(static e => e.Sequence));
        Assert.Equal(1, playlist.MediaSequence);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:1\n", playlist.Render(), StringComparison.Ordinal);
        Assert.StartsWith("segment-1.m4s", playlist.Render().Split("#EXTINF:1.000,\n")[1], StringComparison.Ordinal);

        var longer = new HlsPlaylist(4, 3);
        for (var i = 0; i < 10; i++)
        {
            longer.Append(Entry(i, 4.0));
        }

        Assert.Equal(3, longer.Entries.Count);
        Assert.Equal(7, longer.MediaSequence);
    }

    /// <summary>Verifies discontinuities, MAP changes and the discontinuity sequence after the first one rolls out.</summary>
    [Fact]
    public void Discontinuities_AreTaggedAndCounted()
    {
        var playlist = new HlsPlaylist(1, 3);
        playlist.Append(Entry(0, 1.0));
        playlist.Append(Entry(1, 1.0, "init-1.mp4", discontinuity: true));
        playlist.Append(Entry(2, 1.0, "init-1.mp4", discontinuity: true));
        var text = playlist.Render();

        Assert.Equal(
            "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:0\n" +
            "#EXT-X-MAP:URI=\"init-0.mp4\"\n#EXTINF:1.000,\nsegment-0.m4s\n" +
            "#EXT-X-DISCONTINUITY\n#EXT-X-MAP:URI=\"init-1.mp4\"\n#EXTINF:1.000,\nsegment-1.m4s\n" +
            "#EXT-X-DISCONTINUITY\n#EXTINF:1.000,\nsegment-2.m4s\n",
            text);

        playlist.Append(Entry(3, 1.0, "init-1.mp4"));
        Assert.Equal((1L, 1L), (playlist.MediaSequence, playlist.DiscontinuitySequence));
        text = playlist.Render();
        Assert.Contains("#EXT-X-DISCONTINUITY-SEQUENCE:1\n#EXT-X-MAP:URI=\"init-1.mp4\"\n#EXTINF:1.000,\nsegment-1.m4s\n#EXT-X-DISCONTINUITY\n", text, StringComparison.Ordinal);
        playlist.Append(Entry(4, 1.0, "init-1.mp4"));
        Assert.Equal(2, playlist.DiscontinuitySequence);
        Assert.DoesNotContain("#EXT-X-DISCONTINUITY\n", playlist.Render(), StringComparison.Ordinal);
    }

    /// <summary>Verifies a playlist may start at any sequence number and ENDLIST ends it.</summary>
    [Fact]
    public void StartSequenceAndEnd()
    {
        var playlist = new HlsPlaylist(10, 6);
        playlist.Append(Entry(5, 2.0));
        playlist.Append(Entry(6, 2.0));
        Assert.Equal((5L, TimeSpan.FromSeconds(4)), (playlist.MediaSequence, playlist.Duration));

        playlist.End();
        Assert.True(playlist.Ended);
        Assert.EndsWith("segment-6.m4s\n#EXT-X-ENDLIST\n", playlist.Render(), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => playlist.Append(Entry(7, 1.0)));
    }

    /// <summary>
    /// Verifies the target-duration invariant at the rounding threshold: an EXTINF that rounds to the target is
    /// accepted (up to TD.499 s), one that rounds above it is rejected without changing the playlist.
    /// </summary>
    /// <param name="ticks">The segment duration in 100 ns ticks.</param>
    /// <param name="extinf">The rendered EXTINF value, or null when rejected.</param>
    [Theory]
    [InlineData(40_000_000L, "4.000")]
    [InlineData(44_989_999L, "4.499")]
    [InlineData(44_994_999L, "4.499")]
    [InlineData(44_995_000L, null)]
    [InlineData(45_000_000L, null)]
    [InlineData(49_000_000L, null)]
    [InlineData(4_999L, "0.000")]
    [InlineData(5_000L, "0.001")]
    public void TargetDuration_RoundingThreshold(long ticks, string? extinf)
    {
        var playlist = new HlsPlaylist(4, 3);
        playlist.Append(Entry(0, 1.0));
        var entry = new HlsSegmentEntry(1, "segment-1.m4s", TimeSpan.FromTicks(ticks), "init-0.mp4", false);
        if (extinf is null)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => playlist.Append(entry));
            Assert.Single(playlist.Entries);
            Assert.Equal(TimeSpan.FromSeconds(1), playlist.Duration);
        }
        else
        {
            playlist.Append(entry);
            Assert.Contains($"#EXTINF:{extinf},\nsegment-1.m4s\n", playlist.Render(), StringComparison.Ordinal);
            var advertised = Math.Round(decimal.Parse(extinf, System.Globalization.CultureInfo.InvariantCulture), MidpointRounding.AwayFromZero);
            Assert.True(advertised <= 4);
        }

        Assert.Equal(TimeSpan.FromMilliseconds(4499), playlist.MaxSegmentDuration);
        Assert.Contains("#EXT-X-TARGETDURATION:4\n", playlist.Render(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies TARGETDURATION is stable for the playlist's lifetime: a long (maximum) segment rolling out of the window
    /// never lowers it, and it always covers every listed segment.
    /// </summary>
    [Fact]
    public void TargetDuration_IsStableAcrossRollover()
    {
        var playlist = new HlsPlaylist(4, 3);
        playlist.Append(Entry(0, 4.499));
        var texts = new List<string> { playlist.Render() };
        for (var i = 1; i < 40; i++)
        {
            playlist.Append(Entry(i, 0.5));
            texts.Add(playlist.Render());
        }

        Assert.DoesNotContain(playlist.Entries, static e => e.Sequence == 0);
        Assert.All(texts, static t => Assert.Contains("#EXT-X-TARGETDURATION:4\n", t, StringComparison.Ordinal));
        Assert.All(texts, static t => Assert.All(
            t.Split('\n').Where(static l => l.StartsWith("#EXTINF:", StringComparison.Ordinal)),
            static l => Assert.True(Math.Round(decimal.Parse(l[8..^1], System.Globalization.CultureInfo.InvariantCulture), MidpointRounding.AwayFromZero) <= 4, l)));
    }

    /// <summary>Verifies out-of-order sequence numbers, invalid arguments and the entry cap.</summary>
    [Fact]
    public void Guards()
    {
        var playlist = new HlsPlaylist(100, 2000);
        playlist.Append(Entry(0, 0.001));
        Assert.Throws<InvalidOperationException>(() => playlist.Append(Entry(2, 1.0)));
        Assert.Throws<ArgumentOutOfRangeException>(static () => new HlsPlaylist(0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(static () => new HlsPlaylist(1, 0));
        for (var i = 1; i < HlsPlaylist.MaxEntries; i++)
        {
            playlist.Append(Entry(i, 0.001));
        }

        Assert.Equal((HlsPlaylist.MaxEntries, 0L), (playlist.Entries.Count, playlist.MediaSequence));
        Assert.Throws<InvalidOperationException>(() => playlist.Append(Entry(HlsPlaylist.MaxEntries, 0.001)));
        Assert.Equal((HlsPlaylist.MaxEntries, 0L), (playlist.Entries.Count, playlist.MediaSequence));
    }

    /// <summary>
    /// Verifies the entry cap never shortens a live playlist below three target durations: at the cap, a segment is
    /// accepted only if removing the oldest still leaves 3 × TD; otherwise it is refused with nothing changed.
    /// </summary>
    /// <param name="first">The oldest entry's duration in seconds.</param>
    /// <param name="each">Each of the other 999 entries' duration in seconds.</param>
    /// <param name="shortest">The shortest next segment the playlist accepts, in seconds.</param>
    [Theory]
    [InlineData(4.0, 0.009, 3.009)]
    [InlineData(0.012, 0.012, 0.012)]
    [InlineData(0.008, 0.008, 4.008)]
    public void EntryCap_PreservesMinimumDuration(double first, double each, double shortest)
    {
        var playlist = new HlsPlaylist(4, 3);
        playlist.Append(Entry(0, first));
        for (var i = 1; i < HlsPlaylist.MaxEntries; i++)
        {
            Assert.Empty(playlist.Append(Entry(i, each)));
        }

        var minimum = TimeSpan.FromSeconds(12);
        Assert.Equal(HlsPlaylist.MaxEntries, playlist.Entries.Count);
        Assert.Equal(TimeSpan.FromSeconds(first) + (TimeSpan.FromSeconds(each) * (HlsPlaylist.MaxEntries - 1)), playlist.Duration);
        Assert.Equal(minimum, playlist.MinimumDuration);
        var before = playlist.Render();

        var tooShort = TimeSpan.FromSeconds(shortest) - TimeSpan.FromTicks(1);
        Assert.False(playlist.HasEntryRoom(tooShort));
        Assert.Throws<InvalidOperationException>(() => playlist.Append(new HlsSegmentEntry(HlsPlaylist.MaxEntries, "segment-1000.m4s", tooShort, "init-0.mp4", false)));
        Assert.Equal(before, playlist.Render());

        Assert.True(playlist.HasEntryRoom(TimeSpan.FromSeconds(shortest)));
        var retired = playlist.Append(Entry(HlsPlaylist.MaxEntries, shortest));
        Assert.Equal([0L], retired.Select(static e => e.Sequence));
        Assert.Equal((HlsPlaylist.MaxEntries, 1L), (playlist.Entries.Count, playlist.MediaSequence));
        Assert.True(playlist.Duration >= minimum);
    }

    /// <summary>
    /// Verifies an ended playlist at the cap keeps every segment (an ENDLIST playlist is never shortened and accepts no
    /// further segments, so the live minimum does not constrain it).
    /// </summary>
    [Fact]
    public void EntryCap_EndedPlaylistKeepsEverySegment()
    {
        var playlist = new HlsPlaylist(4, 3);
        for (var i = 0; i < HlsPlaylist.MaxEntries; i++)
        {
            playlist.Append(Entry(i, 0.002));
        }

        playlist.End();
        var text = playlist.Render();
        Assert.EndsWith("segment-999.m4s\n#EXT-X-ENDLIST\n", text, StringComparison.Ordinal);
        Assert.Equal(HlsPlaylist.MaxEntries, text.Split('\n').Count(static l => l.StartsWith("#EXTINF:", StringComparison.Ordinal)));
        Assert.Throws<InvalidOperationException>(() => playlist.Append(Entry(HlsPlaylist.MaxEntries, 10)));
    }

    /// <summary>Creates an entry.</summary>
    /// <param name="sequence">The sequence number.</param>
    /// <param name="seconds">The duration.</param>
    /// <param name="initialization">The initialization segment.</param>
    /// <param name="discontinuity">Whether it starts a discontinuity.</param>
    /// <returns>The entry.</returns>
    internal static HlsSegmentEntry Entry(long sequence, double seconds, string initialization = "init-0.mp4", bool discontinuity = false) =>
        new(sequence, $"segment-{sequence}.m4s", TimeSpan.FromSeconds(seconds), initialization, discontinuity);
}
