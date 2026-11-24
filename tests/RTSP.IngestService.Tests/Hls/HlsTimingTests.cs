// Media-timeline integrity tests for HLS packaging: AAC frame timing at several sample rates and frame lengths, A/V
// synchronisation with B-frames, video stalls under continuous audio, and the playlist entry cap through the
// packager. They check exact integer timelines: contiguous tracks, EXTINF equal to the packaged span, no drift.

using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Media;

namespace RTSP.IngestService.Tests.Hls;

/// <summary>
/// Verifies packaged sample timing stays coherent with the source timestamps and the playlist.
/// </summary>
public sealed class HlsTimingTests
{
    /// <summary>
    /// Verifies audio-only AAC segments: every sample lasts exactly one frame, the track is contiguous across segments,
    /// each EXTINF is the distance between segment starts (so the sum is the audio duration, with no drift), every
    /// segment fits TARGETDURATION, and the final frame near a boundary is kept whole.
    /// </summary>
    /// <param name="sampleRate">The sample rate.</param>
    /// <param name="config">The AudioSpecificConfig.</param>
    /// <param name="frameLength">Samples per frame.</param>
    /// <param name="targetSeconds">TargetSegmentDuration (MaxSegmentDuration is 2 s).</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData(44_100, new byte[] { 0x12, 0x10 }, 1024, 1.0)]
    [InlineData(48_000, new byte[] { 0x11, 0x90 }, 1024, 1.0)]
    [InlineData(44_100, new byte[] { 0x12, 0x14 }, 960, 1.0)]
    [InlineData(48_000, new byte[] { 0x11, 0x94 }, 960, 1.0)]
    [InlineData(44_100, new byte[] { 0x12, 0x10 }, 1024, 2.0)]
    public async Task AudioOnly_FramesStayWholeAndContiguous(int sampleRate, byte[] config, int frameLength, double targetSeconds)
    {
        using var harness = new HlsHarness(new HlsOptions { TargetSegmentDuration = TimeSpan.FromSeconds(targetSeconds), MaxSegmentDuration = TimeSpan.FromSeconds(2) });
        var session = await harness.StartAsync();
        await harness.SendAsync(session, PublishedMessageKind.Audio, 0, FlvPackets.AacSequenceHeader(config));
        var frames = (int)(5.3 * sampleRate / frameLength);
        for (var i = 0; i < frames; i++)
        {
            await harness.SendAsync(session, PublishedMessageKind.Audio, Milliseconds(i, frameLength, sampleRate), FlvPackets.AacRaw(0x21, (byte)i));
        }

        await harness.EndAsync(session);
        var playlist = harness.Playlist()!;
        var runs = playlist.Segments.Select(s => Mp4Fragments.Read(harness.Segment(s.Name)).Runs.Single()).ToList();
        Assert.All(runs, static r => Assert.Equal((uint)Mp4AudioTrack.Id, r.TrackId));
        Assert.All(runs.SelectMany(static r => r.Samples), s => Assert.Equal((uint)frameLength, s.Duration));
        Assert.Equal(frames, runs.Sum(static r => r.Samples.Count));
        Assert.Equal(0ul, runs[0].BaseDecodeTime);
        for (var i = 1; i < runs.Count; i++)
        {
            Assert.Equal(runs[i - 1].BaseDecodeTime + (ulong)(runs[i - 1].Samples.Count * frameLength), runs[i].BaseDecodeTime);
        }

        var starts = runs.Select(static r => (long)r.BaseDecodeTime).Append((long)frames * frameLength).Select(t => t * 90_000 / sampleRate).ToList();
        Assert.Equal(runs.Select((_, i) => TimeSpan.FromTicks((starts[i + 1] * 1000 / 9) - (starts[i] * 1000 / 9))), playlist.Segments.Select(static s => s.Duration));
        Assert.Equal(TimeSpan.FromTicks(starts[^1] * 1000 / 9), playlist.Segments.Aggregate(TimeSpan.Zero, static (t, s) => t + s.Duration));
        Assert.All(playlist.Segments.SkipLast(1), s => Assert.InRange(s.Duration, TimeSpan.FromSeconds(targetSeconds), TimeSpan.FromSeconds(targetSeconds) + TimeSpan.FromSeconds((double)frameLength / sampleRate)));
        Assert.All(playlist.Segments, static s => Assert.True(HlsPlaylist.RenderedMilliseconds(s.Duration) <= 2499, s.Name));
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>
    /// Verifies an AAC frame timestamped more than a quarter frame early, which would overlap the previous frame's
    /// decoded samples, is dropped rather than shortening the previous frame; a later frame beyond tolerance starts at
    /// its own timestamp (a source gap, kept exactly), and following frames continue contiguously.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Audio_EarlyFrameIsDroppedAndGapIsKept()
    {
        using var harness = new HlsHarness(new HlsOptions { TargetSegmentDuration = TimeSpan.FromSeconds(1), MaxSegmentDuration = TimeSpan.FromSeconds(2) });
        using var dropped = new MetricCollector<long>(harness.MeterFactory, HlsMetrics.MeterName, "hls.samples.dropped");
        var session = await harness.StartAsync();
        await harness.SendAsync(session, PublishedMessageKind.Audio, 0, FlvPackets.AacSequenceHeader([0x12, 0x10]));
        for (var i = 0; i < 10; i++)
        {
            await harness.SendAsync(session, PublishedMessageKind.Audio, Milliseconds(i, 1024, 44_100), FlvPackets.AacRaw(0x21, (byte)i));
        }

        await harness.SendAsync(session, PublishedMessageKind.Audio, Milliseconds(10, 1024, 44_100) - 10, FlvPackets.AacRaw(0x21, 0xEE));
        for (var i = 10; i < 20; i++)
        {
            await harness.SendAsync(session, PublishedMessageKind.Audio, Milliseconds(i, 1024, 44_100), FlvPackets.AacRaw(0x21, (byte)i));
        }

        await harness.SendAsync(session, PublishedMessageKind.Audio, 600, FlvPackets.AacRaw(0x21, 0xF0));
        await harness.SendAsync(session, PublishedMessageKind.Audio, 600 + 23, FlvPackets.AacRaw(0x21, 0xF1));
        await harness.EndAsync(session);

        var run = Mp4Fragments.Read(harness.Segment("segment-0.m4s")).Runs.Single();
        var time = run.BaseDecodeTime;
        var starts = run.Samples.Select(s => { var start = time; time += s.Duration; return start; }).ToList();
        Assert.Equal(Enumerable.Range(0, 20).Select(static i => (ulong)i * 1024).Append(26_460ul).Append(26_460ul + 1024), starts);
        Assert.All(run.Samples.Where(static (_, i) => i != 19), static s => Assert.Equal(1024u, s.Duration));
        Assert.Equal(26_460u - (19 * 1024), run.Samples[19].Duration);
        var drop = Assert.Single(dropped.GetMeasurementSnapshot());
        Assert.Equal((1L, "timeline_overlap"), (drop.Value, (string)drop.Tags["reason"]!));
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>
    /// Verifies H.264 with B-frames and AAC at 48 kHz over ten one-second segments: segment boundaries fall on video
    /// IDRs while audio frames straddle them; both tracks stay contiguous across every boundary, composition offsets
    /// are preserved, each EXTINF is the segment's video span, audio never drifts from video by more than one frame,
    /// and the final segment ends where the last audio frame ends.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task AudioVideo_WithBFrames_StayInSyncWithoutDrift()
    {
        using var harness = new HlsHarness(new HlsOptions { TargetSegmentDuration = TimeSpan.FromSeconds(1), MaxSegmentDuration = TimeSpan.FromSeconds(2), PlaylistWindowSize = 20 });
        var session = await harness.StartAsync();
        await harness.SendAsync(session, PublishedMessageKind.Video, 0, HlsFixture.AvcSequenceHeader);
        await harness.SendAsync(session, PublishedMessageKind.Audio, 0, FlvPackets.AacSequenceHeader([0x11, 0x90]));
        var videoFrames = 250;
        var audioFrames = (int)Math.Ceiling(10.03 * 48_000 / 1024);
        var events = Enumerable.Range(0, videoFrames).Select(static n => (Time: (uint)(n * 40), Video: true, Index: n))
            .Concat(Enumerable.Range(0, audioFrames).Select(static n => (Time: Milliseconds(n, 1024, 48_000), Video: false, Index: n)))
            .OrderBy(static e => e.Time).ThenBy(static e => e.Video ? 0 : 1);
        foreach (var (time, isVideo, index) in events)
        {
            if (isVideo)
            {
                await harness.VideoAsync(session, time, idr: index % 25 == 0, compositionTime: CompositionTime(index));
            }
            else
            {
                await harness.SendAsync(session, PublishedMessageKind.Audio, time, FlvPackets.AacRaw(0x21, (byte)index));
            }
        }

        await harness.EndAsync(session);
        var playlist = harness.Playlist()!;
        Assert.Equal(10, playlist.Segments.Count);
        Assert.All(playlist.Segments, static s => Assert.False(s.Discontinuity));
        Assert.All(playlist.Segments.SkipLast(1), static s => Assert.Equal(TimeSpan.FromSeconds(1), s.Duration));

        var segments = playlist.Segments.Select(s => Mp4Fragments.Read(harness.Segment(s.Name)).Runs).ToList();
        var video = segments.Select(static r => r.Single(static t => t.TrackId == Mp4VideoTrack.Id)).ToList();
        var audio = segments.Select(static r => r.Single(static t => t.TrackId == Mp4AudioTrack.Id)).ToList();
        Assert.All(video, static r => Assert.Equal(Mp4Sample.SyncFlags, r.Samples[0].Flags));
        Assert.Equal(Enumerable.Range(0, videoFrames).Select(static n => CompositionTime(n) * 90), video.SelectMany(static r => r.Samples).Select(static s => s.CompositionOffset!.Value));
        Assert.All(audio.SelectMany(static r => r.Samples), static s => Assert.Equal(1024u, s.Duration));
        Assert.Equal(audioFrames, audio.Sum(static r => r.Samples.Count));
        for (var i = 0; i < segments.Count; i++)
        {
            Assert.Equal((ulong)i * 90_000, video[i].BaseDecodeTime);
            if (i > 0)
            {
                Assert.Equal(audio[i - 1].BaseDecodeTime + (ulong)(audio[i - 1].Samples.Count * 1024), audio[i].BaseDecodeTime);
            }

            var audioSeconds = audio[i].BaseDecodeTime / 48_000.0;
            Assert.InRange(audioSeconds - i, -0.001, (1024 / 48_000.0) + 0.001);
        }

        var audioEnd = (long)audio[^1].BaseDecodeTime + (audio[^1].Samples.Count * 1024);
        Assert.Equal(Milliseconds(audioFrames - 1, 1024, 48_000), (uint)Math.Round((audioEnd - 1024) / 48.0));
        Assert.Equal(TimeSpan.FromTicks(Math.Max(250L * 3600, audioEnd * 90_000 / 48_000) * 1000 / 9), playlist.Segments.Aggregate(TimeSpan.Zero, static (t, s) => t + s.Duration));
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>
    /// Verifies a video stall while audio continues: audio closes segments before they exceed TARGETDURATION, the
    /// last video frame keeps its observed duration (it is not stretched or shortened), audio stays contiguous with no
    /// discontinuity, and every segment's media starts within one audio frame of where the playlist says it starts.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task VideoStall_AudioKeepsSegmentsWithinTarget()
    {
        using var harness = new HlsHarness();
        var session = await harness.StartAsync();
        await harness.SendAsync(session, PublishedMessageKind.Video, 0, HlsFixture.AvcSequenceHeader);
        await harness.SendAsync(session, PublishedMessageKind.Audio, 0, FlvPackets.AacSequenceHeader([0x11, 0x90]));
        var videoTimes = Enumerable.Range(0, 25).Select(static n => (uint)(n * 40)).Concat(Enumerable.Range(0, 25).Select(static n => (uint)(4000 + (n * 40)))).ToList();
        var audioFrames = (int)(5.0 * 48_000 / 1024);
        var events = videoTimes.Select(static t => (Time: t, Video: true))
            .Concat(Enumerable.Range(0, audioFrames).Select(static n => (Time: Milliseconds(n, 1024, 48_000), Video: false)))
            .OrderBy(static e => e.Time).ThenBy(static e => e.Video ? 0 : 1);
        foreach (var (time, isVideo) in events)
        {
            if (isVideo)
            {
                await harness.VideoAsync(session, time, idr: time % 1000 == 0);
            }
            else
            {
                await harness.SendAsync(session, PublishedMessageKind.Audio, time, FlvPackets.AacRaw(0x21, 0x10));
            }
        }

        await harness.EndAsync(session);
        var playlist = harness.Playlist()!;
        Assert.All(playlist.Segments, static s => Assert.False(s.Discontinuity));
        Assert.All(playlist.Segments, static s => Assert.True(HlsPlaylist.RenderedMilliseconds(s.Duration) <= 2499, s.Name));

        var runs = playlist.Segments.Select(s => Mp4Fragments.Read(harness.Segment(s.Name)).Runs).ToList();
        var videoSamples = runs.SelectMany(static r => r.Where(static t => t.TrackId == Mp4VideoTrack.Id)).SelectMany(t =>
        {
            var time = t.BaseDecodeTime;
            return t.Samples.Select(s => { var start = time; time += s.Duration; return (Start: start, s.Duration); }).ToList();
        }).ToList();
        Assert.Equal(videoTimes.Select(static t => (ulong)t * 90), videoSamples.Select(static s => s.Start));
        Assert.All(videoSamples, static s => Assert.Equal(3600u, s.Duration));

        var audio = runs.SelectMany(static r => r.Where(static t => t.TrackId == Mp4AudioTrack.Id)).ToList();
        Assert.All(audio.SelectMany(static t => t.Samples), static s => Assert.Equal(1024u, s.Duration));
        for (var i = 1; i < audio.Count; i++)
        {
            Assert.Equal(audio[i - 1].BaseDecodeTime + (ulong)(audio[i - 1].Samples.Count * 1024), audio[i].BaseDecodeTime);
        }

        var playlistStart = TimeSpan.Zero;
        foreach (var segment in runs)
        {
            var mediaStart = segment.Min(static t => t.TrackId == Mp4VideoTrack.Id ? TimeSpan.FromTicks((long)t.BaseDecodeTime * 1000 / 9) : TimeSpan.FromTicks((long)t.BaseDecodeTime * TimeSpan.TicksPerSecond / 48_000));
            Assert.InRange(mediaStart - playlistStart, -TimeSpan.FromMilliseconds(1), TimeSpan.FromTicks(1024 * TimeSpan.TicksPerSecond / 48_000));
            playlistStart += playlist.Segments[runs.IndexOf(segment)].Duration;
        }

        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>
    /// Verifies the packager ends the publish explicitly when its live playlist reaches the entry cap with less than
    /// three target durations listed: the refused segment changes nothing, no segment is released early, event 7013
    /// is logged, and the withdrawn playlist's segments stay available.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task EntryCap_EndsThePublishWithoutShorteningThePlaylist()
    {
        using var harness = new HlsHarness(new HlsOptions { TargetSegmentDuration = TimeSpan.FromMilliseconds(1), MaxSegmentDuration = TimeSpan.FromSeconds(120) });
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session, audio: false);
        Exception? failure = null;
        uint t = 0;
        for (; failure is null && t <= 20_000; t += 10)
        {
            failure = await Record.ExceptionAsync(() => harness.VideoAsync(session, t, idr: true));
        }

        Assert.Equal(10_010u, t - 10);
        Assert.Equal(HlsAppendResult.PlaylistFull, Assert.IsType<HlsPackagingStoppedException>(failure).Reason);
        Assert.Single(harness.Logs.GetSnapshot(), static r => r.Id.Id == 7013);
        var playlist = harness.Playlist()!;
        Assert.Equal((HlsPlaylist.MaxEntries, 0L), (playlist.Segments.Count, playlist.MediaSequence));
        Assert.Equal(TimeSpan.FromSeconds(10), playlist.Segments.Aggregate(TimeSpan.Zero, static (s, e) => s + e.Duration));
        Assert.Null(harness.Store.GetSegment("live", "camera1", "segment-1000.m4s"));

        await harness.EndAsync(session, PublishEndReason.HandlerFailed);
        Assert.Null(harness.Playlist());
        Assert.All(playlist.Segments, s => Assert.NotNull(harness.Store.GetSegment("live", "camera1", s.Name)));
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>The RTMP millisecond timestamp of an AAC frame (rounded to nearest, as encoders write it).</summary>
    /// <param name="frame">The frame index.</param>
    /// <param name="frameLength">Samples per frame.</param>
    /// <param name="sampleRate">The sample rate.</param>
    /// <returns>The timestamp.</returns>
    private static uint Milliseconds(int frame, int frameLength, int sampleRate) => (uint)(((long)frame * frameLength * 1000 + (sampleRate / 2)) / sampleRate);

    /// <summary>A B-frame-like composition time pattern (ms): reference frames lead by two frames, B-frames by none.</summary>
    /// <param name="frame">The frame index.</param>
    /// <returns>The composition time.</returns>
    private static int CompositionTime(int frame) => (frame % 25) % 3 == 0 ? 80 : 0;
}
