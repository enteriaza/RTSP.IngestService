// Integration of the managed audio and video stages with the real media and HLS stages, with no FFmpeg anywhere:
// fixture FLV files are published through the transcoding stage and the stored HLS presentation is decoded
// independently of the packager (Phase 3 managed H.264 decoder, managed AAC decoder). It must be canonical H.264 and
// AAC-LC 48 kHz stereo on a contiguous audio timeline that starts at the first picture; a killed publish must not
// flush; a replacement must start new stages; audio-only and video-only publishes must package their one track; an
// unsupported source codec must end the publish without output; and audio far ahead of the first video picture must
// start an audio-only period before the audio and video one. FFmpeg, when present, adds an ffprobe and decode oracle.

using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Hls;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Tests.Media;
using RTSP.IngestService.Tests.Media.Codecs.H264;
using RTSP.IngestService.Transcoding;
using FlvTag = RTSP.IngestService.Tests.Media.FlvTag;

namespace RTSP.IngestService.Tests.Transcoding;

/// <summary>HLS integration of the managed audio and video stages.</summary>
public sealed class ManagedAudioHlsTests
{
    /// <summary>One AAC frame at 48 kHz in milliseconds.</summary>
    private const double AacFrameMilliseconds = 1024 * 1000.0 / 48_000;

    /// <summary>
    /// Managed audio and video package into one valid period: every source picture, AAC-LC audio on a contiguous
    /// timeline starting within one frame at or after the first picture, and a clean decode.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task ManagedAudioAndVideo_PackageIntoValidHls()
    {
        using var run = new ManagedHlsRun();
        var session = await run.StartAsync();
        var tags = FlvFile.ReadFixture("h264-aac-4s.flv");
        await run.PublishAsync(session, tags);
        await run.EndAsync(session);

        run.AssertReleased();
        var (playlist, periods) = run.DecodeEnded();
        var period = Assert.Single(periods);
        Assert.Equal(ManagedVideoTestSupport.SourcePresentationTimes(tags).Count, period.Pictures.Count);
        AssertAligned(period);
        AssertContiguousAudio(period);
        Assert.Contains(10000, run.EventIds());
        Assert.Contains(10031, run.EventIds());
        await run.OracleAsync(playlist, video: true);
    }

    /// <summary>Fixtures published back to back on one timeline stay one valid presentation.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task LongPublish_StaysValid()
    {
        using var run = new ManagedHlsRun();
        var session = await run.StartAsync();
        var tags = FlvFile.ReadFixture("h264-aac-4s.flv");
        await run.PublishAsync(session, tags, tags);
        await run.EndAsync(session);

        run.AssertReleased();
        var (playlist, periods) = run.DecodeEnded();
        Assert.All(periods, static p => Assert.True(p.Pictures[0].IsKeyframe));
        AssertAligned(periods[0]);
        Assert.All(periods, AssertContiguousAudio);
        await run.OracleAsync(playlist, video: true);
    }

    /// <summary>
    /// A killed publish ends without flushing: the live presentation packaged before the kill decodes cleanly, the HLS
    /// stage withdraws the stream as it does for any abnormal end, and every buffer is returned.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Kill_EndsWithoutFlushing()
    {
        using var run = new ManagedHlsRun();
        var session = await run.StartAsync();
        var tags = FlvFile.ReadFixture("h264-aac-4s.flv");
        await run.PublishAsync(session, tags, tags);
        await Eventually.WaitUntilAsync(() => run.Hls.Playlist() is { Segments.Count: > 0 }, "a segment is packaged");
        var live = run.Hls.Playlist()!;
        Assert.False(live.Ended);
        var periods = HlsOutputDecoder.Decode(live, name => run.Hls.Segment(name));
        Assert.NotEmpty(periods[0].Pictures);
        Assert.True(periods[0].Pictures[0].IsKeyframe);
        if (FfmpegTool.Path is { } ffmpeg)
        {
            using var directory = new HlsFfmpegInteropTests.TemporaryDirectory();
            var path = directory.Write(run.Hls.Store, "live", "camera1", live);
            File.AppendAllText(path, "#EXT-X-ENDLIST\n");
            await HlsFfmpegInteropTests.DecodeAsync(ffmpeg, path);
        }

        await run.EndAsync(session, PublishEndReason.ConnectionError);

        Assert.Null(run.Hls.Playlist());
        Assert.Contains(10031, run.EventIds());
        run.AssertReleased();
    }

    /// <summary>A replacement publish of the same stream gets new managed stages and stays valid.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Replacement_StartsANewStream()
    {
        using var run = new ManagedHlsRun();
        var first = await run.StartAsync();
        await run.PublishAsync(first, FlvFile.ReadFixture("h264-aac.flv"));
        await run.EndAsync(first);
        var second = await run.StartAsync();
        var tags = FlvFile.ReadFixture("h264-aac-4s.flv");
        await run.PublishAsync(second, tags);
        await run.EndAsync(second);

        run.AssertReleased();
        Assert.Equal(2, run.EventIds().Count(static id => id == 10031));
        Assert.Equal(2, run.EventIds().Count(static id => id == 10000));
        var (playlist, periods) = run.DecodeEnded();
        Assert.Equal(ManagedVideoTestSupport.SourcePresentationTimes(tags).Count, periods.Sum(static p => p.Pictures.Count));
        AssertAligned(periods[0]);
        await run.OracleAsync(playlist, video: true);
    }

    /// <summary>An audio-only publish starts no video stage and drains into a valid audio-only presentation.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task AudioOnly_StartsNoVideoStage()
    {
        using var run = new ManagedHlsRun();
        var session = await run.StartAsync();
        await run.PublishAsync(session, [.. FlvFile.ReadFixture("h264-aac-4s.flv").Where(static t => t.Type == 8)]);
        await run.EndAsync(session);

        run.AssertReleased();
        Assert.DoesNotContain(10000, run.EventIds());
        var (playlist, periods) = run.DecodeEnded();
        var period = Assert.Single(periods);
        Assert.Null(period.AvcRecord);
        Assert.Empty(period.Pictures);
        AssertContiguousAudio(period);
        await run.OracleAsync(playlist, video: false);
    }

    /// <summary>
    /// A video-only publish of a synthetic 320x240 source packages every picture into a video-only presentation once
    /// the startup hold declares audio absent.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task VideoOnly_PackagesEveryPicture()
    {
        using var run = new ManagedHlsRun();
        var session = await run.StartAsync();
        var tags = ManagedVideoTestSupport.SyntheticTags(SyntheticPattern.Motion, 320, 240, 75);
        await run.PublishAsync(session, tags);
        await run.EndAsync(session);

        run.AssertReleased();
        var (playlist, periods) = run.DecodeEnded();
        var period = Assert.Single(periods);
        Assert.Empty(period.AudioStarts);
        var source = ManagedVideoTestSupport.SourcePresentationTimes(tags);
        Assert.Equal(source.Select(t => t - source[0]), period.Pictures.Select(p => p.Timestamp - period.Pictures[0].Timestamp));
        Assert.All(period.Pictures, static p => Assert.Equal((320, 240), (p.Width, p.Height)));
        await run.OracleAsync(playlist, video: true, audio: false);
    }

    /// <summary>
    /// A source codec the managed pipeline does not transcode (MP3 audio, HEVC video) ends the publish at its
    /// configuration with a rejection: nothing is packaged and every buffer is returned.
    /// </summary>
    /// <param name="fixture">The fixture.</param>
    /// <returns>A task.</returns>
    [Theory]
    [InlineData("mp3.flv")]
    [InlineData("hevc.flv")]
    public async Task UnsupportedCodec_EndsThePublishWithoutOutput(string fixture)
    {
        using var run = new ManagedHlsRun();
        var session = await run.StartAsync();
        var ex = await Assert.ThrowsAsync<TranscodingRejectedException>(() => run.PublishAsync(session, FlvFile.ReadFixture(fixture)));
        Assert.Equal(TranscodingRejectionReason.UnsupportedCodec, ex.Reason);
        await run.EndAsync(session, PublishEndReason.HandlerFailed);

        Assert.Contains(10011, run.EventIds());
        Assert.True(run.Hls.Playlist() is null or { Segments.Count: 0 }, "Nothing is packaged from a rejected publish.");
        run.AssertReleased();
    }

    /// <summary>
    /// When the source's audio runs more than the multiplexer's retention window ahead of its first video picture, the
    /// multiplexer releases the audio rather than block the publisher: HLS starts an audio-only period and, at the
    /// first IDR, a new audio and video period with its own initialization segment after a discontinuity. Nothing is
    /// lost or leaked, and the stream ends normally.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task AudioFarAheadOfVideo_StartsAudioOnly_ThenAddsVideo()
    {
        using var run = new ManagedHlsRun();
        var session = await run.StartAsync();
        var fixture = FlvFile.ReadFixture("h264-aac-4s.flv");
        var shift = fixture.Max(static t => t.Timestamp) + 40;
        List<FlvTag> audio = [.. fixture.Where(static t => t.Body.Length > 1 && t.Body[1] == 0)];
        audio.AddRange(fixture.Where(static t => t.Type == 8 && t.Body[1] == 1));
        audio.AddRange(fixture.Where(static t => t.Type == 8 && t.Body[1] == 1).Select(t => t with { Timestamp = t.Timestamp + shift }));
        List<FlvTag> both = [.. fixture.Where(static t => t.Type is 8 or 9 && t.Body[1] == 1).Select(t => t with { Timestamp = t.Timestamp + (2 * shift) })];
        await run.PublishAsync(session, paced: false, audio);
        await Eventually.WaitUntilAsync(() => run.Downstream.Count(PublishedMessageKind.Audio) > 2, "the multiplexer releases audio ahead of the first video");
        await run.PublishAsync(session, paced: false, both);
        await run.EndAsync(session);

        run.AssertReleased();
        var (playlist, periods) = run.DecodeEnded();
        var initializations = playlist.Segments.Select(static s => s.InitializationName).Distinct(StringComparer.Ordinal).ToList();
        Assert.Equal(2, initializations.Count);
        Assert.True(playlist.Segments.First(s => s.InitializationName == initializations[1]).Discontinuity);
        Assert.Equal((1, 2), (Mp4Parser.Parse(run.Hls.Segment(initializations[0])).All("moov/trak").Count(), Mp4Parser.Parse(run.Hls.Segment(initializations[1])).All("moov/trak").Count()));
        Assert.Null(periods[0].AvcRecord);
        Assert.Equal(ManagedVideoTestSupport.SourcePresentationTimes(fixture).Count, periods.Sum(static p => p.Pictures.Count));
        AssertAligned(periods[1]);
    }

    /// <summary>Asserts the period's first audio sample starts within one AAC frame at or after its first picture.</summary>
    /// <param name="period">The period.</param>
    private static void AssertAligned(HlsPeriod period)
    {
        Assert.NotEmpty(period.Pictures);
        Assert.NotEmpty(period.AudioStarts);
        Assert.InRange(period.AudioStarts[0] - period.Pictures.Min(static p => p.Timestamp), 0, AacFrameMilliseconds + 1);
    }

    /// <summary>Asserts the period's audio samples follow one another without gaps or overlaps.</summary>
    /// <param name="period">The period.</param>
    private static void AssertContiguousAudio(HlsPeriod period)
    {
        Assert.NotEmpty(period.AudioStarts);
        for (var i = 1; i < period.AudioStarts.Count; i++)
        {
            Assert.Equal(AacFrameMilliseconds, period.AudioStarts[i] - period.AudioStarts[i - 1], 6);
        }
    }
}
