// Tests of the transcoding stage with its managed video and audio stages: canonical output decoded by the managed
// decoder on the source presentation timeline; admission (disabled, capacity, release); rejection of codecs and H.264
// formats the managed pipeline does not transcode, before any stage starts; decoder failures that end only the failing
// session; both overload policies against a blocked downstream stage; new canonical streams on a configuration change
// and on a timeline discontinuity; the startup hold for a late or absent second track; draining versus aborting,
// including an idle stage; declared HDR tone mapped to BT.709; the drain timeout; host shutdown; malformed packets
// mid-stream; and concurrent sessions. Every test also checks
// that no pooled buffer, queued byte or session slot is left behind.

using System.Buffers;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;
using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Video.Color;
using RTSP.IngestService.Media.Codecs.Video.H264;
using RTSP.IngestService.Rtmp.Amf;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Tests.Media;
using RTSP.IngestService.Tests.Media.Codecs.H264;
using RTSP.IngestService.Transcoding;

namespace RTSP.IngestService.Tests.Transcoding;

/// <summary>Tests for <see cref="TranscodingPublishSessionHandler"/> and <see cref="TranscodingSession"/>.</summary>
public sealed class TranscodingSessionTests
{
    /// <summary>The canonical 48 kHz stereo AAC-LC sequence header.</summary>
    private static readonly byte[] CanonicalAacHeader = [0xAF, 0x00, 0x11, 0x90];

    /// <summary>
    /// A publish is re-encoded: every source picture reaches the downstream stage as one canonical H.264 High
    /// picture at its presentation time, the audio as canonical AAC-LC, and the session drains.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Publish_DeliversCanonicalMediaOnTheSourceTimeline()
    {
        using var harness = new TranscodingHarness();
        var tags = FlvFile.ReadFixture("h264-aac.flv");
        var session = await harness.StartAsync();
        await harness.SendTagsAsync(session, tags);
        await harness.EndAsync(session);

        var video = ManagedVideoTestSupport.Decode(harness.Delivered(session, PublishedMessageKind.Video));
        ManagedVideoTestSupport.AssertCanonical(video, 160, 120);
        Assert.Equal(ManagedVideoTestSupport.SourcePresentationTimes(tags), video.Pictures.Select(static p => p.Timestamp));
        Assert.Single(video.SequenceHeaders);
        var audio = harness.Delivered(session, PublishedMessageKind.Audio);
        Assert.Equal(CanonicalAacHeader, audio[0].Payload);
        Assert.Contains(audio, static e => e.Payload![1] == 1);
        AssertMonotonic(harness, session);
        var kinds = harness.Downstream.Events.Where(e => ReferenceEquals(e.Session, session)).Select(static e => e.Kind).ToList();
        Assert.Equal("started", kinds[0]);
        Assert.Equal("ended", kinds[^1]);
        Assert.NotEmpty(harness.Delivered(session, PublishedMessageKind.Data));
        Assert.Contains(10000, harness.EventIds());
        Assert.Contains(10005, harness.EventIds());
        Assert.Contains(10032, harness.EventIds());
        Assert.DoesNotContain(10001, harness.EventIds());
        Assert.DoesNotContain(10003, harness.EventIds());
        AssertReleased(harness);
    }

    /// <summary>Disabled transcoding rejects every publish instead of passing media through.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Disabled_RejectsEveryPublish()
    {
        using var harness = new TranscodingHarness(static o => o.Enabled = false);
        var ex = await Assert.ThrowsAsync<TranscodingRejectedException>(harness.StartAsync);
        Assert.Equal(TranscodingRejectionReason.Disabled, ex.Reason);
        Assert.Empty(harness.Downstream.Events);
        Assert.Contains(10010, harness.EventIds());
        AssertReleased(harness);
    }

    /// <summary>Publishes beyond the session limit are rejected and an ended session frees its slot.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Capacity_IsEnforcedAndReleased()
    {
        using var harness = new TranscodingHarness(static o => o.MaxConcurrentSessions = 2);
        var first = await harness.StartAsync();
        var second = await harness.StartAsync();
        var ex = await Assert.ThrowsAsync<TranscodingRejectedException>(harness.StartAsync);
        Assert.Equal(TranscodingRejectionReason.Capacity, ex.Reason);
        Assert.Equal(2, harness.Handler.ActiveSessionCount);

        await harness.EndAsync(first);
        var third = await harness.StartAsync();
        Assert.Equal(2, harness.Handler.ActiveSessionCount);
        await harness.EndAsync(second);
        await harness.EndAsync(third);
        AssertReleased(harness);
    }

    /// <summary>A codec the parser does not know ends the publish with a reason and starts no stage.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task UnknownCodec_EndsThePublish()
    {
        using var harness = new TranscodingHarness();
        var session = await harness.StartAsync();
        await harness.SendAsync(session, PublishedMessageKind.Audio, 0, FlvPackets.AacSequenceHeader());
        var ex = await Assert.ThrowsAsync<TranscodingRejectedException>(() => harness.SendAsync(session, PublishedMessageKind.Video, 0, [0x14, 0, 1, 2, 3]));
        Assert.Equal(TranscodingRejectionReason.UnsupportedCodec, ex.Reason);
        Assert.Contains(10011, harness.EventIds());
        Assert.Null(harness.Handler.FindSession(session.SessionId)!.VideoStage);
        await harness.EndAsync(session, PublishEndReason.HandlerFailed);
        Assert.Empty(harness.Delivered(session, PublishedMessageKind.Video));
        AssertReleased(harness);
    }

    /// <summary>
    /// Video and audio codecs the parser accepts but the managed pipeline does not transcode end the publish at their
    /// configuration, with a reason and before a stage for that track exists; nothing of the track is delivered.
    /// </summary>
    /// <param name="file">The fixture.</param>
    /// <param name="trackName">The rejected track (a <see cref="MediaTrack"/>).</param>
    /// <returns>A task.</returns>
    [Theory]
    [InlineData("hevc.flv", nameof(MediaTrack.Video))]
    [InlineData("hevc-pq.flv", nameof(MediaTrack.Video))]
    [InlineData("av1.flv", nameof(MediaTrack.Video))]
    [InlineData("vp9.flv", nameof(MediaTrack.Video))]
    [InlineData("mp3.flv", nameof(MediaTrack.Audio))]
    [InlineData("opus.flv", nameof(MediaTrack.Audio))]
    [InlineData("flac.flv", nameof(MediaTrack.Audio))]
    [InlineData("ac3.flv", nameof(MediaTrack.Audio))]
    [InlineData("eac3.flv", nameof(MediaTrack.Audio))]
    public async Task NonCanonicalSourceCodec_EndsThePublishAtItsConfiguration(string file, string trackName)
    {
        var track = Enum.Parse<MediaTrack>(trackName);
        using var harness = new TranscodingHarness();
        var session = await harness.StartAsync();
        var ex = await Assert.ThrowsAsync<TranscodingRejectedException>(() => harness.SendTagsAsync(session, FlvFile.ReadFixture(file)));
        Assert.Equal(TranscodingRejectionReason.UnsupportedCodec, ex.Reason);
        var pipeline = harness.Handler.FindSession(session.SessionId)!;
        Assert.Null(track == MediaTrack.Video ? pipeline.VideoStage : pipeline.AudioStage);
        Assert.Contains(harness.Logs.GetSnapshot(), r => r.Id.Id == 10011 && r.Message.Contains(trackName, StringComparison.Ordinal));
        await harness.EndAsync(session, PublishEndReason.HandlerFailed);
        Assert.Empty(harness.Delivered(session, track == MediaTrack.Video ? PublishedMessageKind.Video : PublishedMessageKind.Audio));
        AssertReleased(harness);
    }

    /// <summary>
    /// H.264 formats the managed decoder does not support (high bit depth, 4:2:2, interlaced, lossless 4:4:4) end the
    /// publish at the sequence header with a reason naming the limitation, and no video stage starts.
    /// </summary>
    /// <param name="fixture">The decoder fixture.</param>
    /// <returns>A task.</returns>
    [Theory]
    [InlineData("reject-high10")]
    [InlineData("reject-high422")]
    [InlineData("reject-interlaced")]
    [InlineData("reject-lossless444")]
    public async Task UnsupportedH264Format_EndsThePublishAtTheSequenceHeader(string fixture)
    {
        using var harness = new TranscodingHarness();
        var session = await harness.StartAsync();
        var tags = ManagedVideoTestSupport.AnnexBFixtureTags(fixture);
        var ex = await Assert.ThrowsAsync<TranscodingRejectedException>(() => harness.SendTagsAsync(session, tags.Take(1)));
        Assert.Equal(TranscodingRejectionReason.UnsupportedCodec, ex.Reason);
        Assert.Contains("not supported", ex.Message, StringComparison.Ordinal);
        Assert.Null(harness.Handler.FindSession(session.SessionId)!.VideoStage);
        await harness.EndAsync(session, PublishEndReason.HandlerFailed);
        Assert.Empty(harness.Delivered(session, PublishedMessageKind.Video));
        AssertReleased(harness);
    }

    /// <summary>
    /// A picture within the configured input limits but over the managed codecs' 36864 macroblocks (4096x2320 is
    /// 37120) ends the publish with a resolution reason, and one at the limit (4096x2304) is accepted.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task PictureBeyondTheManagedLimits_EndsThePublish()
    {
        using var harness = new TranscodingHarness(static o => o.MaxInputWidth = 4096);
        var rejected = await harness.StartAsync();
        var ex = await Assert.ThrowsAsync<TranscodingRejectedException>(() => harness.SendAsync(rejected, PublishedMessageKind.Video, 0, FlvPackets.AvcSequenceHeader(ManagedVideoTestSupport.RecordWithSize(4096, 2320))));
        Assert.Equal(TranscodingRejectionReason.ResolutionLimit, ex.Reason);
        Assert.Contains("36864 macroblocks", ex.Message, StringComparison.Ordinal);
        await harness.EndAsync(rejected, PublishEndReason.HandlerFailed);

        var accepted = await harness.StartAsync();
        await harness.SendAsync(accepted, PublishedMessageKind.Video, 0, FlvPackets.AvcSequenceHeader(ManagedVideoTestSupport.RecordWithSize(4096, 2304)));
        Assert.NotNull(harness.Handler.FindSession(accepted.SessionId)!.VideoStage);
        await harness.EndAsync(accepted);
        AssertReleased(harness);
    }

    /// <summary>
    /// Persistent undecodable video ends only its own session once more than
    /// <see cref="ManagedVideoStage.MaxDecodeFailures"/> frames fail within the window, as a decoder failure; another
    /// session is unaffected.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task PersistentDecodeFailures_EndOnlyThatSession()
    {
        using var harness = new TranscodingHarness();
        var tags = FlvFile.ReadFixture("h264-aac.flv");
        var header = tags.First(static t => t.Type == 9 && t.Body[1] == 0);
        var failing = await harness.StartAsync();
        await harness.SendTagsAsync(failing, [header]);
        for (var i = 0; i < ManagedVideoStage.MaxDecodeFailures; i++)
        {
            await harness.SendAsync(failing, PublishedMessageKind.Video, (uint)(i * 100), CorruptIdr());
        }

        var pipeline = harness.Handler.FindSession(failing.SessionId)!;
        await Eventually.WaitUntilAsync(() => pipeline.VideoStage!.Recreations == ManagedVideoStage.MaxDecodeFailures, "every corrupt frame replaced the decoder");
        Assert.Null(pipeline.Fault);
        await harness.SendAsync(failing, PublishedMessageKind.Video, (uint)(ManagedVideoStage.MaxDecodeFailures * 100), CorruptIdr());
        await Eventually.WaitUntilAsync(() => pipeline.Fault is not null, "one failure beyond the threshold faults the session");

        var healthy = await harness.StartAsync();
        await harness.SendTagsAsync(healthy, tags);
        var failure = await Assert.ThrowsAsync<TranscodingFailedException>(() => harness.SendTagsAsync(failing, tags));
        Assert.Equal(TranscodingStage.Decoder, failure.Stage);
        Assert.True(failure.Initialization);
        Assert.Contains(10001, harness.EventIds());
        Assert.Contains(10033, harness.EventIds());

        await harness.EndAsync(failing, PublishEndReason.HandlerFailed);
        await harness.EndAsync(healthy);
        ManagedVideoTestSupport.AssertCanonical(ManagedVideoTestSupport.Decode(harness.Delivered(healthy, PublishedMessageKind.Video)), 160, 120);
        Assert.DoesNotContain(harness.Delivered(failing, PublishedMessageKind.Video), static e => e.Payload![1] == 1);
        AssertReleased(harness);
    }

    /// <summary>An isolated corrupt frame replaces the decoder; video resumes at the next IDR picture and the publish continues.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task IsolatedCorruptFrame_RecreatesTheDecoderAndResumesAtTheNextIdr()
    {
        using var harness = new TranscodingHarness();
        var tags = ManagedVideoTestSupport.SyntheticTags(SyntheticPattern.Motion, 96, 64, 75);
        var corrupt = tags.FindIndex(static t => t.Timestamp == 400);
        var session = await harness.StartAsync();
        await harness.SendTagsAsync(session, tags.Take(corrupt));
        await harness.SendAsync(session, PublishedMessageKind.Video, tags[corrupt].Timestamp, [0x27, 1, 0, 0, 0, 0, 0, 0, 5, 0x41, 0x00, 0x00, 0x00, 0x00]);
        await harness.SendTagsAsync(session, tags.Skip(corrupt + 1));
        await harness.EndAsync(session);

        var stage = harness.Handler.FindSession(session.SessionId);
        Assert.Null(stage);
        var video = ManagedVideoTestSupport.Decode(harness.Delivered(session, PublishedMessageKind.Video));
        ManagedVideoTestSupport.AssertCanonical(video, 96, 64);
        var secondIdr = tags.Skip(1).Where(static t => t.Body[0] == 0x17).Select(static t => (long)t.Timestamp).ElementAt(1);
        Assert.Equal(
            [.. tags.Skip(1).Take(corrupt - 1).Select(static t => (long)t.Timestamp), .. tags.Skip(1).Where(t => t.Timestamp >= secondIdr).Select(static t => (long)t.Timestamp)],
            video.Pictures.Select(static p => p.Timestamp));
        Assert.Contains(10033, harness.EventIds());
        Assert.DoesNotContain(10001, harness.EventIds());
        AssertReleased(harness);
    }

    /// <summary>
    /// With the drop policy a blocked downstream stage never blocks the publisher: queues stay bounded, video is
    /// dropped up to the next keyframe and audio frame by frame, and an aborting end releases everything.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task BlockedDownstream_DropsVideoUntilKeyframe()
    {
        using var harness = new TranscodingHarness(static o =>
        {
            o.MaxBufferedVideoFrames = 8;
            o.MaxBufferedAudioFrames = 16;
            o.MaxInputFrameRate = 15;
        });
        harness.Downstream.MessageGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = await harness.StartAsync();
        var tags = FlvFile.ReadFixture("h264-aac-4s.flv");
        for (var loop = 0u; loop < 4; loop++)
        {
            await harness.SendTagsAsync(session, loop == 0 ? tags : tags.Where(static t => t.Body.Length > 1 && t.Body[1] == 1), loop * 4000).WithinSafetyTimeout();
        }

        Assert.True(harness.Handler.FindSession(session.SessionId)!.QueuedBytes <= harness.Options.MaxSessionBufferBytes);
        var drops = DropLogs(harness);
        Assert.Contains(drops, static m => m.Contains("Video", StringComparison.Ordinal) && m.Contains("QueueFull", StringComparison.Ordinal));
        Assert.Contains(drops, static m => m.Contains("Video", StringComparison.Ordinal) && m.Contains("AwaitingKeyframe", StringComparison.Ordinal));
        Assert.Contains(drops, static m => m.Contains("Audio", StringComparison.Ordinal) && m.Contains("QueueFull", StringComparison.Ordinal));
        Assert.DoesNotContain(drops, static m => m.Contains("Audio", StringComparison.Ordinal) && m.Contains("AwaitingKeyframe", StringComparison.Ordinal));

        await harness.EndAsync(session, PublishEndReason.ConnectionError);
        Assert.Equal("ended", harness.Downstream.Events.Last().Kind);
        AssertReleased(harness);
    }

    /// <summary>With the backpressure policy a full queue makes the publisher wait, without drops, until cancelled.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Backpressure_MakesThePublisherWait()
    {
        using var harness = new TranscodingHarness(static o =>
        {
            o.OverloadPolicy = TranscodingOverloadPolicy.Backpressure;
            o.MaxBufferedVideoFrames = 8;
            o.MaxBufferedAudioFrames = 16;
            o.MaxInputFrameRate = 15;
        });
        harness.Downstream.MessageGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = await harness.StartAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var tags = FlvFile.ReadFixture("h264-aac-4s.flv");
        Task? blocked = null;
        for (var loop = 0u; loop < 4 && blocked is null; loop++)
        {
            foreach (var tag in loop == 0 ? tags : tags.Where(static t => t.Body.Length > 1 && t.Body[1] == 1))
            {
                var send = harness.SendAsync(session, TranscodingHarness.Kind(tag), tag.Timestamp + (loop * 4000), tag.Body, cancellation.Token);
                if (await Task.WhenAny(send, Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken)) != send)
                {
                    blocked = send;
                    break;
                }

                await send;
            }
        }

        Assert.NotNull(blocked);
        Assert.Empty(DropLogs(harness));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);

        await harness.EndAsync(session, PublishEndReason.ConnectionError);
        AssertReleased(harness);
    }

    /// <summary>The service-wide budget drops a session's media when another session holds it, even under backpressure.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task BufferBudget_IsSharedAcrossSessions()
    {
        using var harness = new TranscodingHarness(static o =>
        {
            o.OverloadPolicy = TranscodingOverloadPolicy.Backpressure;
            o.MaxSessionBufferMegabytes = 1;
            o.MaxTotalBufferMegabytes = 1;
            o.MaxBufferedVideoFrames = 10000;
        });
        harness.Downstream.MessageGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = ManagedVideoTestSupport.SyntheticTags(SyntheticPattern.Noise, 320, 240, 8, qp: 0);
        Assert.All(source.Skip(1), static t => Assert.True(t.Body.Length > 100_000, "Each noise picture is large."));

        var first = await harness.StartAsync();
        var firstSends = Task.Run(() => harness.SendTagsAsync(first, source), TestContext.Current.CancellationToken);
        await Eventually.WaitUntilAsync(() => harness.Handler.BufferedBytes > 600_000 || firstSends.IsCompleted, "the first session holds most of the budget");

        var second = await harness.StartAsync();
        await harness.SendTagsAsync(second, source).WithinSafetyTimeout();
        var secondId = second.SessionId.ToString();
        Assert.Contains(DropLogs(harness), m => m.Contains("BudgetExhausted", StringComparison.Ordinal) && m.Contains(secondId, StringComparison.Ordinal));
        Assert.True(harness.Handler.BufferedBytes <= harness.Options.MaxTotalBufferBytes);
        Assert.True(harness.Handler.Budget.Peak <= harness.Handler.Budget.Limit);

        await harness.EndAsync(first, PublishEndReason.ConnectionError);
        await firstSends.WithinSafetyTimeout();
        await harness.EndAsync(second, PublishEndReason.ConnectionError);
        AssertReleased(harness);
    }

    /// <summary>
    /// A changed video configuration with a new picture size drains the old stream and starts a new canonical stream
    /// of the new size, announced by a new sequence header, with output still increasing.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task VideoConfigurationChange_StartsANewCanonicalStream()
    {
        using var harness = new TranscodingHarness();
        var first = ManagedVideoTestSupport.SyntheticTags(SyntheticPattern.Motion, 96, 64, 25);
        var second = ManagedVideoTestSupport.SyntheticTags(SyntheticPattern.Bars, 128, 96, 25, start: 1000);
        var session = await harness.StartAsync();
        await harness.SendTagsAsync(session, first);
        await harness.SendTagsAsync(session, second);
        await harness.EndAsync(session);

        var delivered = harness.Delivered(session, PublishedMessageKind.Video);
        var video = ManagedVideoTestSupport.Decode(delivered);
        Assert.Equal(2, video.SequenceHeaders.Count);
        Assert.Equal(50, video.Pictures.Count);
        Assert.All(video.Pictures.Take(25), static p => Assert.Equal((96, 64), (p.Width, p.Height)));
        Assert.All(video.Pictures.Skip(25), static p => Assert.Equal((128, 96), (p.Width, p.Height)));
        Assert.True(video.Pictures[25].IsKeyframe, "The new stream starts with an IDR picture.");
        Assert.Contains(harness.Logs.GetSnapshot(), static r => r.Id.Id == 10000 && r.Message.Contains("(Configuration)", StringComparison.Ordinal) && r.Message.Contains("128x96", StringComparison.Ordinal));
        AssertMonotonic(harness, session);
        AssertReleased(harness);
    }

    /// <summary>A changed audio configuration leaves video in its stream: one sequence header and every picture delivered.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task AudioConfigurationChange_DoesNotRestartVideo()
    {
        using var harness = new TranscodingHarness();
        var tags = FlvFile.ReadFixture("h264-aac-4s.flv");
        var half = tags.Count / 2;
        var session = await harness.StartAsync();
        await harness.SendTagsAsync(session, tags.Take(half));
        await harness.SendAsync(session, PublishedMessageKind.Audio, tags[half - 1].Timestamp, FlvPackets.AacSequenceHeader([0x11, 0x90]));
        await harness.SendTagsAsync(session, tags.Skip(half));
        await harness.EndAsync(session);

        var video = ManagedVideoTestSupport.Decode(harness.Delivered(session, PublishedMessageKind.Video));
        Assert.Single(video.SequenceHeaders);
        Assert.Equal(ManagedVideoTestSupport.SourcePresentationTimes(tags), video.Pictures.Select(static p => p.Timestamp));
        Assert.Equal(1, harness.Handler.FindSession(session.SessionId)?.VideoStage?.Streams ?? 1);
        Assert.Single(harness.Logs.GetSnapshot(), static r => r.Id.Id == 10000);
        AssertMonotonic(harness, session);
        AssertReleased(harness);
    }

    /// <summary>A timeline discontinuity ends the canonical stream and a new one follows the new timeline.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Discontinuity_StartsANewCanonicalStream()
    {
        using var harness = new TranscodingHarness();
        var tags = FlvFile.ReadFixture("h264-aac.flv");
        var session = await harness.StartAsync();
        await harness.SendTagsAsync(session, tags);
        await harness.SendTagsAsync(session, tags, timestampShift: 60_000);
        await harness.EndAsync(session);

        Assert.Contains(harness.Logs.GetSnapshot(), static r => r.Id.Id == 10000 && r.Message.Contains("(Discontinuity)", StringComparison.Ordinal));
        var video = ManagedVideoTestSupport.Decode(harness.Delivered(session, PublishedMessageKind.Video));
        ManagedVideoTestSupport.AssertCanonical(video, 160, 120);
        var source = ManagedVideoTestSupport.SourcePresentationTimes(tags);
        Assert.Equal([.. source, .. source.Select(static t => t + 60_000)], video.Pictures.Select(static p => p.Timestamp));
        Assert.True(video.Pictures[source.Count].IsKeyframe, "The stream after the discontinuity starts with an IDR picture.");
        AssertReleased(harness);
    }

    /// <summary>When the audio configuration arrives shortly after the first video, both tracks are delivered completely.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task LateAudioTrack_IsDeliveredWithAllVideo()
    {
        using var harness = new TranscodingHarness();
        var tags = FlvFile.ReadFixture("h264-aac.flv");
        var audioHeader = tags.First(static t => t.Type == 8 && t.Body[1] == 0);
        List<FlvTag> ordered =
        [
            .. tags.Where(static t => t.Type == 9 && t.Timestamp < 300),
            audioHeader,
            .. tags.Where(t => t.Type is 8 or 9 && t.Timestamp >= 300 && !ReferenceEquals(t, audioHeader)),
        ];
        var session = await harness.StartAsync();
        await harness.SendTagsAsync(session, ordered);
        await harness.EndAsync(session);

        var video = ManagedVideoTestSupport.Decode(harness.Delivered(session, PublishedMessageKind.Video));
        Assert.Equal(ManagedVideoTestSupport.SourcePresentationTimes(ordered), video.Pictures.Select(static p => p.Timestamp));
        Assert.Contains(harness.Delivered(session, PublishedMessageKind.Audio), static e => e.Payload![1] == 1);
        AssertMonotonic(harness, session);
        AssertReleased(harness);
    }

    /// <summary>A video-only publish is delivered completely: audio is declared absent after the startup hold.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task VideoOnlyPublish_IsDeliveredCompletely()
    {
        using var harness = new TranscodingHarness();
        var tags = FlvFile.ReadFixture("h264-aac-4s.flv").Where(static t => t.Type == 9).ToList();
        var session = await harness.StartAsync();
        await harness.SendTagsAsync(session, tags);
        var pipeline = harness.Handler.FindSession(session.SessionId)!;
        await Eventually.WaitUntilAsync(() => harness.Delivered(session, PublishedMessageKind.Video).Count > 10, "video is delivered before the publish ends");
        Assert.Null(pipeline.AudioStage);
        await harness.EndAsync(session);

        var video = ManagedVideoTestSupport.Decode(harness.Delivered(session, PublishedMessageKind.Video));
        ManagedVideoTestSupport.AssertCanonical(video, 160, 120);
        Assert.Equal(ManagedVideoTestSupport.SourcePresentationTimes(tags), video.Pictures.Select(static p => p.Timestamp));
        Assert.Empty(harness.Delivered(session, PublishedMessageKind.Audio));
        AssertReleased(harness);
    }

    /// <summary>A malformed packet mid-stream is dropped, video resumes at the next keyframe and the session continues.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task MalformedPacket_IsDroppedAndVideoResumesAtTheNextKeyframe()
    {
        using var harness = new TranscodingHarness();
        var tags = FlvFile.ReadFixture("h264-aac-4s.flv").ToList();
        var cut = tags.FindIndex(static t => t.Type == 9 && t.Body[1] == 1 && t.Timestamp > 500);
        var session = await harness.StartAsync();
        await harness.SendTagsAsync(session, tags.Take(cut));
        await harness.SendAsync(session, PublishedMessageKind.Video, tags[cut].Timestamp, [0x27, 0x01]);
        await harness.SendTagsAsync(session, tags.Skip(cut + 1));
        await harness.EndAsync(session);

        Assert.Contains(10012, harness.EventIds());
        Assert.Contains(DropLogs(harness), static m => m.Contains("AwaitingKeyframe", StringComparison.Ordinal));
        var video = ManagedVideoTestSupport.Decode(harness.Delivered(session, PublishedMessageKind.Video));
        ManagedVideoTestSupport.AssertCanonical(video, 160, 120);
        var lastKeyframe = tags.Last(static t => t.Type == 9 && t.Body[0] == 0x17 && t.Body[1] == 1);
        Assert.Contains(video.Pictures, p => p.Timestamp == lastKeyframe.Timestamp + (((lastKeyframe.Body[2] << 24) | (lastKeyframe.Body[3] << 16) | (lastKeyframe.Body[4] << 8)) >> 8));
        Assert.True(video.Pictures.Count < ManagedVideoTestSupport.SourcePresentationTimes(tags).Count);
        AssertMonotonic(harness, session);
        AssertReleased(harness);
    }

    /// <summary>
    /// A publish that ends normally or by disconnect drains its stages and every picture is delivered; one ended by an
    /// error, a timeout or shutdown aborts them. Either way the session ends downstream and everything is released.
    /// </summary>
    /// <param name="reasonName">The end reason (a <see cref="PublishEndReason"/>).</param>
    /// <param name="drains">Whether the pipeline drains.</param>
    /// <returns>A task.</returns>
    [Theory]
    [InlineData(nameof(PublishEndReason.Unpublished), true)]
    [InlineData(nameof(PublishEndReason.StreamDeleted), true)]
    [InlineData(nameof(PublishEndReason.ClientDisconnected), true)]
    [InlineData(nameof(PublishEndReason.ConnectionTimeout), false)]
    [InlineData(nameof(PublishEndReason.ProtocolError), false)]
    [InlineData(nameof(PublishEndReason.ConnectionError), false)]
    [InlineData(nameof(PublishEndReason.ServerShutdown), false)]
    [InlineData(nameof(PublishEndReason.HandlerFailed), false)]
    public async Task EndReason_DrainsOrAbortsThePipeline(string reasonName, bool drains)
    {
        using var harness = new TranscodingHarness();
        var tags = FlvFile.ReadFixture("h264-aac.flv");
        var session = await harness.StartAsync();
        await harness.SendTagsAsync(session, tags);
        var stage = harness.Handler.FindSession(session.SessionId)!.VideoStage!;
        await harness.EndAsync(session, Enum.Parse<PublishEndReason>(reasonName));

        var pictures = ManagedVideoTestSupport.SourcePresentationTimes(tags);
        Assert.True(drains == (stage.EncodedUnits == pictures.Count), $"Only a drain flushes the pictures the decoder holds for reordering; {stage.EncodedUnits} of {pictures.Count} were encoded.");
        if (drains)
        {
            var video = ManagedVideoTestSupport.Decode(harness.Delivered(session, PublishedMessageKind.Video));
            Assert.Equal(pictures, video.Pictures.Select(static p => p.Timestamp));
        }

        Assert.DoesNotContain(10002, harness.EventIds());
        Assert.Equal("ended", harness.Downstream.Events.Last().Kind);
        AssertReleased(harness);
    }

    /// <summary>
    /// A kill reason aborts the video stage even when it has decoded every queued picture and is idle, so the end of its
    /// queue cannot be mistaken for a drain: the pictures the decoder holds for reordering are never flushed or encoded,
    /// and neither a failure nor a drain timeout is logged.
    /// </summary>
    /// <param name="reasonName">A kill end reason (a <see cref="PublishEndReason"/>).</param>
    /// <returns>A task.</returns>
    [Theory]
    [InlineData(nameof(PublishEndReason.ConnectionTimeout))]
    [InlineData(nameof(PublishEndReason.ProtocolError))]
    [InlineData(nameof(PublishEndReason.ConnectionError))]
    [InlineData(nameof(PublishEndReason.ServerShutdown))]
    [InlineData(nameof(PublishEndReason.HandlerFailed))]
    public async Task KillReason_WithAnIdleVideoStage_AbortsWithoutDraining(string reasonName)
    {
        using var harness = new TranscodingHarness();
        var tags = FlvFile.ReadFixture("h264-aac.flv");
        var session = await harness.StartAsync();
        await harness.SendTagsAsync(session, tags);
        var pipeline = harness.Handler.FindSession(session.SessionId)!;
        var stage = pipeline.VideoStage!;
        var pictures = ManagedVideoTestSupport.SourcePresentationTimes(tags).Count;
        await Eventually.WaitUntilAsync(
            () => pipeline.QueuedBytes == 0 && stage.DecodedUnits == pictures,
            "the video stage has decoded every source picture and waits for more input");

        await harness.EndAsync(session, Enum.Parse<PublishEndReason>(reasonName));

        Assert.True(stage.Completion.IsCompleted);
        Assert.InRange(stage.EncodedUnits, 0, pictures - 1);
        Assert.DoesNotContain(10001, harness.EventIds());
        Assert.DoesNotContain(10002, harness.EventIds());
        Assert.Equal("ended", harness.Downstream.Events.Last().Kind);
        AssertReleased(harness);
    }

    /// <summary>
    /// Enhanced RTMP colorInfo metadata declaring BT.2100 PQ or HLG makes the video stage treat an H.264 source as HDR
    /// in place of its BT.709 bitstream tags and tone map it: every canonical picture matches the stage's colour
    /// conversion of the declared source, which differs measurably from the SDR interpretation.
    /// </summary>
    /// <param name="transfer">The declared transfer characteristics.</param>
    /// <returns>A task.</returns>
    [Theory]
    [InlineData(16)]
    [InlineData(18)]
    public async Task DeclaredHdr_IsToneMappedToBt709(int transfer)
    {
        const double Tolerance = 2.0;
        const double MinimumEffect = 4.0;
        var tags = ManagedVideoTestSupport.SyntheticTags(SyntheticPattern.Ramp, 64, 64, 10);
        var expected = SourceLuma(tags, new VideoColorInfo(9, (byte)transfer, 9, false));
        var sdr = SourceLuma(tags, null);
        Assert.True(expected.Zip(sdr, static (h, s) => Math.Abs(h - s)).Average() > MinimumEffect, "The declared transfer must change the converted pictures for this test to discriminate.");

        using var harness = new TranscodingHarness();
        var session = await harness.StartAsync();
        await harness.SendAsync(session, PublishedMessageKind.Video, 0, HdrColorInfo(transfer));
        await harness.SendTagsAsync(session, tags);
        await harness.EndAsync(session);

        var video = ManagedVideoTestSupport.Decode(harness.Delivered(session, PublishedMessageKind.Video));
        ManagedVideoTestSupport.AssertCanonical(video, 64, 64);
        Assert.Equal(expected.Count, video.Pictures.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.InRange(video.Pictures[i].MeanLuma, expected[i] - Tolerance, expected[i] + Tolerance);
        }

        AssertReleased(harness);
    }

    /// <summary>A publisher that sends slowly is not timed out by the pipeline and all its media is delivered.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task SlowPublisher_IsDeliveredCompletely()
    {
        using var harness = new TranscodingHarness();
        var tags = FlvFile.ReadFixture("h264-aac.flv").Take(24).ToList();
        var session = await harness.StartAsync();
        foreach (var tag in tags)
        {
            await harness.SendTagsAsync(session, [tag]);
            await Task.Delay(TimeSpan.FromMilliseconds(60), TestContext.Current.CancellationToken);
        }

        await harness.EndAsync(session);
        Assert.Null(harness.Handler.FindSession(session.SessionId));
        var video = ManagedVideoTestSupport.Decode(harness.Delivered(session, PublishedMessageKind.Video));
        Assert.Equal(ManagedVideoTestSupport.SourcePresentationTimes(tags), video.Pictures.Select(static p => p.Timestamp));
        AssertReleased(harness);
    }

    /// <summary>A drain that cannot deliver within the drain timeout is cut off and logged; the session still ends.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task DrainTimeout_DiscardsUndeliveredOutput()
    {
        using var harness = new TranscodingHarness(static o => o.DrainTimeout = TimeSpan.FromSeconds(1));
        harness.Downstream.MessageGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = await harness.StartAsync();
        await harness.SendTagsAsync(session, FlvFile.ReadFixture("h264-aac.flv"));
        await harness.EndAsync(session);

        Assert.Contains(10002, harness.EventIds());
        Assert.Equal("ended", harness.Downstream.Events.Last().Kind);
        AssertReleased(harness);
    }

    /// <summary>Host shutdown aborts every pipeline; later media is ignored and the sessions still end downstream.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Shutdown_AbortsEveryPipeline()
    {
        using var harness = new TranscodingHarness();
        harness.Downstream.MessageGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tags = FlvFile.ReadFixture("h264-aac.flv");
        var first = await harness.StartAsync();
        var second = await harness.StartAsync();
        await harness.SendTagsAsync(first, tags);
        await harness.SendTagsAsync(second, tags);

        await harness.Handler.AbortAllAsync().WithinSafetyTimeout();
        Assert.All([first, second], s => Assert.True(harness.Handler.FindSession(s.SessionId)!.VideoStage!.Completion.IsCompleted));
        await harness.SendTagsAsync(first, tags.Skip(4).Take(4), 10_000);

        await harness.EndAsync(first, PublishEndReason.ServerShutdown);
        await harness.EndAsync(second, PublishEndReason.ServerShutdown);
        Assert.Equal(2, harness.Downstream.Events.Count(static e => e.Kind == "ended"));
        AssertReleased(harness);
    }

    /// <summary>Concurrent sessions each get their own codecs and complete, uncorrupted canonical output.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task ConcurrentSessions_AreIsolated()
    {
        using var harness = new TranscodingHarness();
        List<FlvTag>[] sources =
        [
            [.. FlvFile.ReadFixture("h264-aac.flv")],
            ManagedVideoTestSupport.SyntheticTags(SyntheticPattern.Motion, 96, 64, 30),
            ManagedVideoTestSupport.SyntheticTags(SyntheticPattern.Bars, 128, 96, 30),
            ManagedVideoTestSupport.AnnexBFixtureTags("main-cabac-bpyramid-weightp-320x240"),
        ];
        var runs = await Task.WhenAll(sources.Select(async tags =>
        {
            var session = await harness.StartAsync();
            await harness.SendTagsAsync(session, tags);
            await harness.EndAsync(session);
            return (Tags: tags, Session: session);
        }));

        (int, int)[] sizes = [(160, 120), (96, 64), (128, 96), (320, 240)];
        for (var i = 0; i < runs.Length; i++)
        {
            var video = ManagedVideoTestSupport.Decode(harness.Delivered(runs[i].Session, PublishedMessageKind.Video));
            ManagedVideoTestSupport.AssertCanonical(video, sizes[i].Item1, sizes[i].Item2);
            Assert.Equal(ManagedVideoTestSupport.SourcePresentationTimes(runs[i].Tags), video.Pictures.Select(static p => p.Timestamp));
        }

        AssertReleased(harness);
    }

    /// <summary>Asserts that delivered decode timestamps never go backwards (video strictly increasing).</summary>
    /// <param name="harness">The harness.</param>
    /// <param name="session">The session.</param>
    private static void AssertMonotonic(TranscodingHarness harness, PublisherSession session)
    {
        var video = harness.Delivered(session, PublishedMessageKind.Video).Where(static e => e.Payload![1] == 1).Select(static e => (long)e.Timestamp).ToList();
        Assert.All(video.Zip(video.Skip(1)), static p => Assert.True(p.Second > p.First, $"video {p.First} then {p.Second}"));
        var audio = harness.Delivered(session, PublishedMessageKind.Audio).Where(static e => e.Payload![1] == 1).Select(static e => (long)e.Timestamp).ToList();
        Assert.All(audio.Zip(audio.Skip(1)), static p => Assert.True(p.Second >= p.First, $"audio {p.First} then {p.Second}"));
    }

    /// <summary>Asserts that no slot, queued byte or pooled buffer is held.</summary>
    /// <param name="harness">The harness.</param>
    private static void AssertReleased(TranscodingHarness harness)
    {
        Assert.Equal(0, harness.Handler.ActiveSessionCount);
        Assert.Equal(0, harness.Handler.BufferedBytes);
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>Gets the drop log messages.</summary>
    /// <param name="harness">The harness.</param>
    /// <returns>The messages.</returns>
    private static List<string> DropLogs(TranscodingHarness harness) =>
        [.. harness.Logs.GetSnapshot().Where(static r => r.Id.Id is 10003 or 10004).Select(static r => r.Message)];

    /// <summary>Builds an IDR packet whose slice header cannot be parsed (an Exp-Golomb code longer than 32 bits).</summary>
    /// <returns>The packet.</returns>
    private static byte[] CorruptIdr() => FlvPackets.AvcNalu(true, 0, 4, [0x65, 0x00, 0x00, 0x03, 0x00, 0x00, 0x03, 0x00, 0x00, 0x03, 0x00, 0x00, 0x01]);

    /// <summary>Builds an Enhanced RTMP H.264 colorInfo metadata packet declaring BT.2100.</summary>
    /// <param name="transfer">The transfer characteristics: 16 (PQ) or 18 (HLG).</param>
    /// <returns>The payload.</returns>
    private static byte[] HdrColorInfo(int transfer)
    {
        var output = new ArrayBufferWriter<byte>();
        Amf0Writer.WriteValue(output, AmfValue.FromString("colorInfo"));
        Amf0Writer.WriteValue(output, AmfValue.CreateObject(
        [
            new("colorConfig", AmfValue.CreateObject(
            [
                new("bitDepth", AmfValue.FromNumber(8)),
                new("colorPrimaries", AmfValue.FromNumber(9)),
                new("transferCharacteristics", AmfValue.FromNumber(transfer)),
                new("matrixCoefficients", AmfValue.FromNumber(9)),
            ])),
        ]));
        return [0x94, .. "avc1"u8, .. output.WrittenSpan];
    }

    /// <summary>
    /// Decodes a source with the managed decoder and gives each picture's mean luma after the video stage's colour
    /// handling: unchanged, or relabelled with a declared colour and converted with <see cref="ManagedVideoStage.ColorPolicy"/>.
    /// </summary>
    /// <param name="tags">The source tags: an AVC sequence header, then coded pictures without B-frames.</param>
    /// <param name="declared">The declared colour that replaces the bitstream's tags, or <see langword="null"/>.</param>
    /// <returns>The mean luma of each picture, in presentation order.</returns>
    private static List<double> SourceLuma(List<FlvTag> tags, VideoColorInfo? declared)
    {
        var frames = new List<VideoFrame>();
        var converted = new List<VideoFrame>();
        var pictures = new List<CanonicalPicture>();
        try
        {
            using var decoder = new H264Decoder();
            decoder.Configure(H264DecoderConfiguration.FromAvcC(tags[0].Body.AsMemory(5)));
            foreach (var tag in tags.Skip(1))
            {
                decoder.Decode(new EncodedPacket(tag.Body.AsMemory(5), tag.Timestamp, MediaTimeBase.Milliseconds), frames, TestContext.Current.CancellationToken);
            }

            decoder.Drain(frames);
            if (declared is not { } color)
            {
                ManagedVideoTestSupport.Collect(frames, pictures);
                return [.. pictures.Select(static p => p.MeanLuma)];
            }

            foreach (var frame in frames)
            {
                using var relabelled = new VideoFrame(frame.Width, frame.Height, frame.Timestamp, frame.TimeBase, color with { FullRange = frame.Color.FullRange });
                for (var plane = 0; plane < 3; plane++)
                {
                    frame.GetPlane(plane).CopyTo(relabelled.GetPlane(plane));
                }

                var output = new VideoFrame(frame.Width, frame.Height, frame.Timestamp, frame.TimeBase, VideoColorInfo.Bt709);
                converted.Add(output);
                VideoColorConverter.Convert(VideoImage.FromFrame(relabelled), output, ManagedVideoStage.ColorPolicy, TestContext.Current.CancellationToken);
            }

            ManagedVideoTestSupport.Collect(converted, pictures);
            return [.. pictures.Select(static p => p.MeanLuma)];
        }
        finally
        {
            frames.ForEach(static f => f.Dispose());
            converted.ForEach(static f => f.Dispose());
        }
    }
}
