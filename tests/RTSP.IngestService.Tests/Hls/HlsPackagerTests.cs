// Behavioural tests for HLS packaging through the real media processor: keyframe-aligned segmentation of the
// fixture with continuous tfdt and exact durations, forced boundaries without keyframes, sync-sample validation,
// audio/video alignment, delayed tracks, timestamp jumps and rollover, final and aborted sessions, unsupported or
// invalid tracks, oversized samples, and that every pooled payload is returned.

using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Media;

namespace RTSP.IngestService.Tests.Hls;

/// <summary>
/// Tests for <see cref="HlsPackager"/> and <see cref="HlsPackagingHandler"/>.
/// </summary>
public sealed class HlsPackagerTests
{
    /// <summary>Verifies the fixture is cut at each one-second IDR with continuous, exact timing on both tracks.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Fixture_IsCutAtKeyframesWithContinuousTiming()
    {
        using var harness = new HlsHarness();
        var session = await harness.StartAsync();
        await harness.ReplayAsync(session, HlsFixture.Tags);
        Assert.Equal(3, harness.Playlist()!.Segments.Count);
        await harness.EndAsync(session);

        var playlist = harness.Playlist()!;
        Assert.True(playlist.Ended);
        Assert.All(playlist.Segments, static s => Assert.Equal(("init-0.mp4", false), (s.InitializationName, s.Discontinuity)));
        Assert.Equal([1.0, 1.0, 1.0], playlist.Segments.Take(3).Select(static s => s.Duration.TotalSeconds));
        var segments = Read(harness, playlist);
        ulong videoNext = 0, audioNext = segments[0].Runs[1].BaseDecodeTime;
        // FFmpeg's first AAC packet is at 177 ms on the same timeline as the first IDR at 0 ms.
        var firstAudio = HlsFixture.Tags.First(static t => t.Type == 8 && t.Body[1] == 1).Timestamp;
        Assert.Equal(177u, firstAudio);
        Assert.Equal((ulong)Math.Round(firstAudio * 44.1), audioNext);
        for (var i = 0; i < segments.Count; i++)
        {
            var (sequence, runs) = segments[i];
            Assert.Equal((uint)i + 1, sequence);
            var (video, audio) = (runs[0], runs[1]);
            Assert.Equal((1u, 2u), (video.TrackId, audio.TrackId));
            Assert.Equal(videoNext, video.BaseDecodeTime);
            Assert.Equal(audioNext, audio.BaseDecodeTime);
            Assert.Equal(10, video.Samples.Count);
            Assert.Equal(Mp4Sample.SyncFlags, video.Samples[0].Flags);
            Assert.All(video.Samples.Skip(1), static s => Assert.Equal(Mp4Sample.NonSyncFlags, s.Flags));
            Assert.All(video.Samples, static s => Assert.Equal(9000u, s.Duration));
            Assert.All(audio.Samples, static s => Assert.Equal(1024u, s.Duration));
            videoNext += (ulong)video.Samples.Sum(static s => s.Duration);
            audioNext += (ulong)audio.Samples.Sum(static s => s.Duration);
        }

        Assert.Contains(segments.SelectMany(static s => s.Runs[0].Samples), static s => s.CompositionOffset > 0);
        Assert.Equal(360_000ul, videoNext);

        // The final segment ends where its media ends: the last AAC frame finishes after the last video frame.
        var audioEnd = (long)audioNext * 90_000 / 44_100;
        Assert.True(audioEnd > 360_000);
        Assert.Equal(TimeSpan.FromTicks((audioEnd - 270_000) * 1000 / 9), playlist.Segments[^1].Duration);
        Assert.Equal(TimeSpan.FromTicks(audioEnd * 1000 / 9), playlist.Segments.Aggregate(TimeSpan.Zero, static (t, s) => t + s.Duration));
        var videoTags = HlsFixture.Tags.Where(static t => t.Type == 9 && t.Body[1] == 1).ToList();
        var firstVideo = Mp4Fragments.Read(harness.Segment("segment-0.m4s"));
        Assert.Equal((uint)(videoTags[0].Body.Length - 5), firstVideo.Runs[0].Samples[0].Size);
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>Verifies an IDR before the target does not cut a segment; the first IDR at or after it does.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Keyframes_BeforeTarget_DoNotCut()
    {
        using var harness = new HlsHarness();
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session, audio: false);
        for (uint t = 0; t <= 3000; t += 100)
        {
            await harness.VideoAsync(session, t, idr: t % 500 == 0);
        }

        await harness.EndAsync(session);
        var playlist = harness.Playlist()!;
        Assert.Equal([1.0, 1.0, 1.0, 0.1], playlist.Segments.Select(static s => s.Duration.TotalSeconds));
        Assert.Single(Mp4Parser.Parse(harness.Segment("init-0.mp4")).All("moov/trak"));
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>Verifies the maximum duration forces a cut without a keyframe and the next segment does not start with sync.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task MissingKeyframes_ForceCutAtMaximum()
    {
        using var harness = new HlsHarness();
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session, audio: false);
        for (uint t = 0; t < 5000; t += 100)
        {
            await harness.VideoAsync(session, t, idr: t == 0);
        }

        await harness.EndAsync(session);
        var playlist = harness.Playlist()!;
        Assert.Equal([2.0, 2.0, 1.0], playlist.Segments.Select(static s => s.Duration.TotalSeconds));
        var second = Mp4Fragments.Read(harness.Segment("segment-1.m4s")).Runs[0];
        Assert.Equal((180_000ul, Mp4Sample.NonSyncFlags), (second.BaseDecodeTime, second.Samples[0].Flags!.Value));
        Assert.Single(harness.Logs.GetSnapshot(), static r => r.Id.Id == 7008);
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>Verifies samples before the first IDR are dropped, and a keyframe flag without an IDR is not a sync sample.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task SyncSamples_AreValidatedByNalType()
    {
        using var harness = new HlsHarness();
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session, audio: false);
        await harness.VideoAsync(session, 0, idr: false);
        await harness.SendAsync(session, PublishedMessageKind.Video, 100, FlvPackets.AvcNalu(true, 0, 4, HlsHarness.Slice));
        Assert.Null(harness.Playlist());
        await harness.VideoAsync(session, 200, idr: true);
        await harness.SendAsync(session, PublishedMessageKind.Video, 1200, FlvPackets.AvcNalu(true, 0, 4, HlsHarness.Slice));
        await harness.VideoAsync(session, 1300, idr: true);
        await harness.EndAsync(session);

        var playlist = harness.Playlist()!;
        Assert.Equal([1.1, 0.1], playlist.Segments.Select(static s => s.Duration.TotalSeconds));
        var first = Mp4Fragments.Read(harness.Segment("segment-0.m4s")).Runs[0];
        Assert.Equal(0ul, first.BaseDecodeTime);
        Assert.Equal([Mp4Sample.SyncFlags, Mp4Sample.NonSyncFlags], first.Samples.Select(static s => s.Flags!.Value));
        Assert.Single(harness.Logs.GetSnapshot(), static r => r.Id.Id == 7009);
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>Verifies audio waits for the first IDR and shares its time base, with exact continuation durations.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Audio_IsAlignedToTheVideoTimeline()
    {
        using var harness = new HlsHarness();
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session);
        for (var i = 0; i < 130; i++)
        {
            var audioTime = (uint)Math.Round(i * 1024 * 1000 / 44100.0);
            await harness.AudioAsync(session, audioTime);
            if (i % 4 == 0)
            {
                var videoTime = (uint)(i / 4 * 100);
                if (videoTime >= 100)
                {
                    await harness.VideoAsync(session, videoTime, idr: videoTime % 1000 == 100);
                }
            }
        }

        await harness.EndAsync(session);
        var segments = Read(harness, harness.Playlist()!);
        var (video, audio) = (segments[0].Runs[0], segments[0].Runs[1]);
        Assert.Equal(0ul, video.BaseDecodeTime);
        // The first audio frame at or after 100 ms is frame 5 at 116 ms: 16 ms after the base, 706 ticks at 44.1 kHz.
        Assert.Equal(706ul, audio.BaseDecodeTime);
        var all = segments.SelectMany(static s => s.Runs.Where(static r => r.TrackId == 2)).ToList();
        Assert.All(all.SelectMany(static r => r.Samples), static s => Assert.Equal(1024u, s.Duration));
        for (var i = 1; i < all.Count; i++)
        {
            Assert.Equal(all[i - 1].BaseDecodeTime + (ulong)(all[i - 1].Samples.Count * 1024), all[i].BaseDecodeTime);
        }

        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>Verifies an audio track appearing mid-stream starts a new period with a new initialization segment.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task DelayedAudio_StartsANewPeriod()
    {
        using var harness = new HlsHarness();
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session, audio: false);
        for (uint t = 0; t < 1500; t += 100)
        {
            await harness.VideoAsync(session, t, idr: t % 1000 == 0);
        }

        await harness.SendAsync(session, PublishedMessageKind.Audio, 1500, HlsFixture.AacSequenceHeader);
        for (uint t = 1500; t < 3500; t += 100)
        {
            await harness.AudioAsync(session, t);
            await harness.VideoAsync(session, t, idr: t % 1000 == 0);
        }

        await harness.EndAsync(session);
        var playlist = harness.Playlist()!;
        Assert.Equal(
            [("init-0.mp4", false), ("init-0.mp4", false), ("init-1.mp4", true), ("init-1.mp4", false)],
            playlist.Segments.Select(static s => (s.InitializationName, s.Discontinuity)));
        Assert.Contains($"#EXT-X-DISCONTINUITY\n#EXT-X-MAP:URI=\"{harness.Store.Generation}/init-1.mp4\"\n", playlist.Text, StringComparison.Ordinal);
        Assert.Equal(2, Mp4Parser.Parse(harness.Segment("init-1.mp4")).All("moov/trak").Count());
        var third = Mp4Fragments.Read(harness.Segment("segment-2.m4s")).Runs;
        Assert.Equal((180_000ul, 2), (third[0].BaseDecodeTime, third.Count));
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>Verifies an audio-only stream is cut at the target duration.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task AudioOnly_IsCutAtTarget()
    {
        using var harness = new HlsHarness();
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session, video: false);
        for (var i = 0; i < 100; i++)
        {
            await harness.AudioAsync(session, (uint)Math.Round(i * 1024 * 1000 / 44100.0));
        }

        await harness.EndAsync(session);
        var playlist = harness.Playlist()!;
        Assert.Equal([44, 44, 12], playlist.Segments.Select(s => Mp4Fragments.Read(harness.Segment(s.Name)).Runs.Single().Samples.Count));
        Assert.Equal(44 * 1024 / 44100.0, playlist.Segments[0].Duration.TotalSeconds, 6);
        Assert.Equal(2u, Mp4Parser.Parse(harness.Segment("init-0.mp4")).One("moov/trak/tkhd").U32(12));
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>Verifies a timestamp jump starts a discontinuity that keeps the initialization segment and the timeline.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task TimestampJump_IsADiscontinuity()
    {
        using var harness = new HlsHarness();
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session, audio: false);
        for (uint t = 0; t < 1000; t += 100)
        {
            await harness.VideoAsync(session, t, idr: t == 0);
        }

        for (uint t = 60_000; t < 61_000; t += 100)
        {
            await harness.VideoAsync(session, t, idr: t == 60_000);
        }

        await harness.EndAsync(session);
        var playlist = harness.Playlist()!;
        Assert.Equal([("init-0.mp4", false), ("init-0.mp4", true)], playlist.Segments.Select(static s => (s.InitializationName, s.Discontinuity)));
        Assert.Equal(60_000ul * 90, Mp4Fragments.Read(harness.Segment("segment-1.m4s")).Runs[0].BaseDecodeTime);
        Assert.Single(harness.Logs.GetSnapshot(), static r => r.Id.Id == 7006);
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>Verifies 32-bit RTMP timestamp rollover is continuous: no discontinuity and monotonic tfdt.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task TimestampRollover_IsContinuous()
    {
        using var harness = new HlsHarness();
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session, audio: false);
        var start = uint.MaxValue - 1499;
        for (var i = 0; i < 40; i++)
        {
            await harness.VideoAsync(session, unchecked(start + (uint)(i * 100)), idr: i % 10 == 0);
        }

        await harness.EndAsync(session);
        var playlist = harness.Playlist()!;
        Assert.Equal(4, playlist.Segments.Count);
        Assert.DoesNotContain(playlist.Segments, static s => s.Discontinuity);
        Assert.Equal(
            [0ul, 90_000ul, 180_000ul, 270_000ul],
            playlist.Segments.Select(s => Mp4Fragments.Read(harness.Segment(s.Name)).Runs[0].BaseDecodeTime));
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>Verifies an abnormal end discards pending samples and removes the stream; a normal one writes the final fragment.</summary>
    /// <param name="reason">The end reason.</param>
    /// <param name="completed">Whether it counts as a normal end.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData(PublishEndReason.Unpublished, true)]
    [InlineData(PublishEndReason.StreamDeleted, true)]
    [InlineData(PublishEndReason.ClientDisconnected, true)]
    [InlineData(PublishEndReason.ConnectionTimeout, false)]
    [InlineData(PublishEndReason.ProtocolError, false)]
    [InlineData(PublishEndReason.ServerShutdown, false)]
    [InlineData(PublishEndReason.HandlerFailed, false)]
    public async Task SessionEnd_FinalFragmentOrCleanup(PublishEndReason reason, bool completed)
    {
        using var harness = new HlsHarness();
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session);
        for (uint t = 0; t <= 1500; t += 100)
        {
            await harness.VideoAsync(session, t, idr: t % 1000 == 0);
            await harness.AudioAsync(session, t);
        }

        Assert.Equal(1, harness.Packaging.ActiveSessionCount);
        await harness.EndAsync(session, reason);

        Assert.Equal(0, harness.Packaging.ActiveSessionCount);
        if (completed)
        {
            var playlist = harness.Playlist()!;
            Assert.True(playlist.Ended);
            Assert.Equal([1.0, 0.6], playlist.Segments.Select(static s => s.Duration.TotalSeconds));
        }
        else
        {
            Assert.Null(harness.Playlist());
            Assert.Equal((0, 1), (harness.Store.StreamCount, harness.Store.WithdrawnStreamCount));
            Assert.NotNull(harness.Store.GetSegment("live", "camera1", "segment-0.m4s"));
            harness.Time.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal((0, 0L), (harness.Store.WithdrawnStreamCount, harness.Store.TotalBytes));
        }

        Assert.Single(harness.Logs.GetSnapshot(), static r => r.Id.Id == 7011);
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>Verifies unsupported AAC configurations are rejected while video is still packaged.</summary>
    /// <param name="config">The AudioSpecificConfig.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData(new byte[] { 0x1A, 0x10 })]
    [InlineData(new byte[] { 0x12, 0x00 })]
    public async Task UnsupportedAac_IsDroppedVideoContinues(byte[] config)
    {
        using var harness = new HlsHarness();
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session, audio: false);
        await harness.SendAsync(session, PublishedMessageKind.Audio, 0, FlvPackets.AacSequenceHeader(config));
        for (uint t = 0; t < 1000; t += 100)
        {
            await harness.VideoAsync(session, t, idr: t == 0);
            await harness.AudioAsync(session, t);
        }

        await harness.EndAsync(session);
        Assert.Single(harness.Playlist()!.Segments);
        Assert.Equal(1u, Mp4Parser.Parse(harness.Segment("init-0.mp4")).One("moov/trak/tkhd").U32(12));
        Assert.Single(Mp4Fragments.Read(harness.Segment("segment-0.m4s")).Runs);
        Assert.Single(harness.Logs.GetSnapshot(), static r => r.Id.Id == 7003);
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>Verifies an SPS without valid dimensions rejects video and leaves an audio-only presentation.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task InvalidSps_IsDroppedAudioContinues()
    {
        using var harness = new HlsHarness();
        var session = await harness.StartAsync();
        await harness.SendAsync(session, PublishedMessageKind.Video, 0, FlvPackets.AvcSequenceHeader());
        await harness.ConfigureAsync(session, video: false);
        for (uint t = 0; t < 500; t += 100)
        {
            await harness.VideoAsync(session, t, idr: t == 0);
            await harness.AudioAsync(session, t);
        }

        await harness.EndAsync(session);
        Assert.Equal(2u, Mp4Parser.Parse(harness.Segment("init-0.mp4")).One("moov/trak/tkhd").U32(12));
        Assert.Single(harness.Logs.GetSnapshot(), static r => r.Id.Id == 7003 && r.Message.Contains("SPS", StringComparison.Ordinal));
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>Verifies the size limit closes a segment early, and a single oversized sample is dropped with a discontinuity.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task SegmentSize_IsBounded()
    {
        using var harness = new HlsHarness(new HlsOptions { TargetSegmentDuration = TimeSpan.FromSeconds(1), MaxSegmentDuration = TimeSpan.FromSeconds(2), MaxSegmentSize = 64 * 1024 });
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session, audio: false);
        byte[] big = [0x41, .. new byte[30_000]];
        await harness.VideoAsync(session, 0, idr: true);
        for (uint t = 100; t <= 300; t += 100)
        {
            await harness.SendAsync(session, PublishedMessageKind.Video, t, FlvPackets.AvcNalu(false, 0, 4, big));
        }

        await harness.SendAsync(session, PublishedMessageKind.Video, 400, FlvPackets.AvcNalu(false, 0, 4, [0x41, .. new byte[70_000]]));
        await harness.VideoAsync(session, 500, idr: true);
        await harness.VideoAsync(session, 600, idr: false);
        await harness.EndAsync(session);

        var playlist = harness.Playlist()!;
        Assert.Equal([false, false, true], playlist.Segments.Select(static s => s.Discontinuity));
        Assert.All(playlist.Segments, s => Assert.True(harness.Segment(s.Name).Length <= 64 * 1024));
        Assert.Equal(500ul * 90, Mp4Fragments.Read(harness.Segment("segment-2.m4s")).Runs[0].BaseDecodeTime);
        Assert.Contains(harness.Logs.GetSnapshot(), static r => r.Id.Id == 7004);
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>Verifies a second publisher on a busy stream key is rejected without affecting the first.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task BusyStream_IsRejected()
    {
        using var harness = new HlsHarness();
        var first = await harness.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await harness.StartAsync());
        Assert.Single(harness.Logs.GetSnapshot(), static r => r.Id.Id == 7010);
        Assert.Equal(1, harness.Packaging.ActiveSessionCount);
        await harness.EndAsync(first);
        Assert.Equal(0, harness.Packaging.ActiveSessionCount);
    }

    /// <summary>
    /// Verifies TARGETDURATION covers every segment however sample timing falls: slightly over the target, a sparse
    /// gap after the last frame, a long final step, a non-integer maximum, and a step that would carry the segment past
    /// TD + 0.499 s (cut before it). No sample duration is shortened: each is the step to the next sample, or the last
    /// observed step for a final sample; a gap no segment can span ends the period with a discontinuity. Every sample's
    /// decode time is unchanged, consecutive segments join exactly, and each EXTINF is its segment's media span.
    /// </summary>
    /// <param name="maxSeconds">MaxSegmentDuration.</param>
    /// <param name="times">Video decode times in ms; IDRs are marked negative.</param>
    /// <param name="expected">Expected EXTINF values.</param>
    /// <param name="discontinuities">Expected discontinuity flags.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData(2.0, new[] { -0, 100, 200, 300, 400, 500, 600, 700, 800, 900, 1000, -1050, 1150 }, new[] { "1.050", "0.200" }, new[] { false, false })]
    [InlineData(2.0, new[] { -0, 100, 1900, -4500, 4600 }, new[] { "1.900", "1.800", "0.200" }, new[] { false, false, true })]
    [InlineData(2.0, new[] { -0, 1900 }, new[] { "1.900", "1.900" }, new[] { false, false })]
    [InlineData(1.5, new[] { -0, 1400, 3000, 3100 }, new[] { "1.400", "1.600", "0.200" }, new[] { false, false, false })]
    [InlineData(2.0, new[] { -0, 1000, 1999, 2000, -2100 }, new[] { "1.999", "0.201" }, new[] { false, false })]
    [InlineData(2.0, new[] { -0, 1000, 2000, -2400, 2500 }, new[] { "2.000", "0.600" }, new[] { false, false })]
    public async Task TargetDuration_CoversEverySegment(double maxSeconds, int[] times, string[] expected, bool[] discontinuities)
    {
        using var harness = new HlsHarness(new HlsOptions { TargetSegmentDuration = TimeSpan.FromSeconds(1), MaxSegmentDuration = TimeSpan.FromSeconds(maxSeconds) });
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session, audio: false);
        for (var i = 0; i < times.Length; i++)
        {
            await harness.VideoAsync(session, (uint)Math.Abs(times[i]), idr: i == 0 || times[i] < 0);
        }

        await harness.EndAsync(session);
        var playlist = harness.Playlist()!;
        Assert.Contains("#EXT-X-TARGETDURATION:2\n", playlist.Text, StringComparison.Ordinal);
        var extinf = playlist.Text.Split('\n').Where(static l => l.StartsWith("#EXTINF:", StringComparison.Ordinal)).Select(static l => l[8..^1]).ToList();
        Assert.Equal(expected, extinf);
        Assert.All(extinf, static e => Assert.True(Math.Round(decimal.Parse(e, System.Globalization.CultureInfo.InvariantCulture), MidpointRounding.AwayFromZero) <= 2, e));

        var decodeTimes = playlist.Segments.SelectMany(s =>
        {
            var run = Mp4Fragments.Read(harness.Segment(s.Name)).Runs[0];
            var time = run.BaseDecodeTime;
            return run.Samples.Select(sample => { var start = time; time += sample.Duration; return start; }).ToList();
        });
        Assert.Equal(times.Select(static t => (ulong)Math.Abs(t) * 90), decodeTimes);
        Assert.Equal(discontinuities, playlist.Segments.Select(static s => s.Discontinuity));
        AssertVideoTimeline(harness, playlist);
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>
    /// Asserts each segment's EXTINF equals its video span (tfdt to the end of its last sample) and each segment
    /// starts exactly where the previous one ended unless it starts a discontinuity.
    /// </summary>
    /// <param name="harness">The harness.</param>
    /// <param name="playlist">The playlist.</param>
    private static void AssertVideoTimeline(HlsHarness harness, HlsPlaylistSnapshot playlist)
    {
        ulong? previousEnd = null;
        foreach (var segment in playlist.Segments)
        {
            var run = Mp4Fragments.Read(harness.Segment(segment.Name)).Runs[0];
            var end = run.BaseDecodeTime + (ulong)run.Samples.Sum(static s => s.Duration);
            Assert.Equal(TimeSpan.FromTicks((long)(end - run.BaseDecodeTime) * 1000 / 9), segment.Duration);
            if (previousEnd is { } expected && !segment.Discontinuity)
            {
                Assert.Equal(expected, run.BaseDecodeTime);
            }

            previousEnd = end;
        }
    }

    /// <summary>
    /// Verifies a transient global StorageFull drops only the segment being closed, keeps the published playlist, and
    /// resumes at the next IDR with a discontinuity and no sequence gap once space returns.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task StorageFull_ResumesAtNextSyncSample()
    {
        using var harness = new HlsHarness(new HlsOptions { TargetSegmentDuration = TimeSpan.FromSeconds(1), MaxSegmentDuration = TimeSpan.FromSeconds(2), MaxSessionStorageMegabytes = 1, MaxTotalStorageMegabytes = 1 });
        var other = await harness.StartAsync("other");
        await harness.ConfigureAsync(other, audio: false);
        await LargeFramesAsync(harness, other, 0, 900);
        await harness.EndAsync(other);
        Assert.Single(harness.Playlist("other")!.Segments);

        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session, audio: false);
        await LargeFramesAsync(harness, session, 0, 3000);
        var published = harness.Playlist()!;
        Assert.Equal(2, published.Segments.Count);
        Assert.Null(harness.Playlist("other"));
        Assert.Contains(harness.Logs.GetSnapshot(), static r => r.Id.Id == 7004);

        harness.Time.Advance(TimeSpan.FromSeconds(2));
        await LargeFramesAsync(harness, session, 3100, 4000);
        var playlist = harness.Playlist()!;
        Assert.Equal([(0L, false), (1L, false), (2L, true)], playlist.Segments.Select(static s => (s.Sequence, s.Discontinuity)));
        var resumed = Mp4Fragments.Read(harness.Segment("segment-2.m4s")).Runs[0];
        Assert.Equal((3000ul * 90, Mp4Sample.SyncFlags), (resumed.BaseDecodeTime, resumed.Samples[0].Flags!.Value));
        Assert.Equal(10, resumed.Samples.Count);
        await harness.EndAsync(session);
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>
    /// Verifies a stream whose listed segments fill its own limit ends the publish explicitly instead of releasing
    /// segments early or stalling, and that another publisher is unaffected.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task StreamLimitExceeded_EndsThePublishOnly()
    {
        using var harness = new HlsHarness(new HlsOptions { TargetSegmentDuration = TimeSpan.FromSeconds(1), MaxSegmentDuration = TimeSpan.FromSeconds(2), MaxSessionStorageMegabytes = 1 });
        var other = await harness.StartAsync("other");
        await harness.ConfigureAsync(other, audio: false);
        await harness.VideoAsync(other, 0, idr: true);

        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session, audio: false);
        await LargeFramesAsync(harness, session, 0, 3900);
        var published = harness.Playlist()!;
        Assert.Equal(3, published.Segments.Count);
        var failure = await Record.ExceptionAsync(() => harness.SendAsync(session, PublishedMessageKind.Video, 4000, FlvPackets.AvcNalu(true, 0, 4, LargeFrame(idr: true))));
        Assert.Equal(HlsAppendResult.StreamLimitExceeded, Assert.IsType<HlsPackagingStoppedException>(failure).Reason);
        Assert.Single(harness.Logs.GetSnapshot(), static r => r.Id.Id == 7012 && r.Level == Microsoft.Extensions.Logging.LogLevel.Error);
        Assert.Same(published, harness.Playlist());

        await harness.EndAsync(session, PublishEndReason.HandlerFailed);
        Assert.Null(harness.Playlist());
        Assert.All(published.Segments, s => Assert.NotNull(harness.Store.GetSegment("live", "camera1", s.Name)));

        for (uint t = 100; t <= 1000; t += 100)
        {
            await harness.VideoAsync(other, t, idr: t == 1000);
        }

        Assert.Single(harness.Playlist("other")!.Segments);
        await harness.EndAsync(other);
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>Sends 30 KB frames every 100 ms in [<paramref name="from"/>, <paramref name="to"/>], IDR every second.</summary>
    /// <param name="harness">The harness.</param>
    /// <param name="session">The session.</param>
    /// <param name="from">First time.</param>
    /// <param name="to">Last time.</param>
    /// <returns>A task.</returns>
    private static async Task LargeFramesAsync(HlsHarness harness, PublisherSession session, uint from, uint to)
    {
        for (var t = from; t <= to; t += 100)
        {
            await harness.SendAsync(session, PublishedMessageKind.Video, t, FlvPackets.AvcNalu(t % 1000 == 0, 0, 4, LargeFrame(t % 1000 == 0)));
        }
    }

    /// <summary>Returns a 30 KB slice NAL unit.</summary>
    /// <param name="idr">Whether it is an IDR slice.</param>
    /// <returns>The NAL unit.</returns>
    private static byte[] LargeFrame(bool idr) => [idr ? (byte)0x65 : (byte)0x41, .. new byte[30_000]];

    /// <summary>Reads every listed segment.</summary>
    /// <param name="harness">The harness.</param>
    /// <param name="playlist">The playlist.</param>
    /// <returns>The parsed fragments.</returns>
    private static List<(uint Sequence, IReadOnlyList<TrackRun> Runs)> Read(HlsHarness harness, HlsPlaylistSnapshot playlist) =>
        [.. playlist.Segments.Select(s => Mp4Fragments.Read(harness.Segment(s.Name)))];
}
