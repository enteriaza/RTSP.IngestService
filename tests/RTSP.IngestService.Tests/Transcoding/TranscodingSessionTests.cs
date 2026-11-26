// Tests of the transcoding stage with a scripted in-memory transcoder: delivery of the transcoder's output on the
// source timeline; admission (disabled, capacity, release); codec rejection; decoder, process-start and crash failures
// that end only the failing session; both overload policies and the service-wide buffer budget; restarts on a
// configuration change and on a timeline discontinuity; the startup hold for a late second track; draining versus
// killing; host shutdown; malformed packets mid-stream; and concurrent sessions of different codecs. Every test also
// checks that no pooled buffer, queued byte or session slot is left behind.

using RTSP.IngestService.Configuration;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Tests.Media;
using RTSP.IngestService.Transcoding;

namespace RTSP.IngestService.Tests.Transcoding;

/// <summary>Tests for <see cref="TranscodingPublishSessionHandler"/> and <see cref="TranscodingSession"/>.</summary>
public sealed class TranscodingSessionTests
{
    /// <summary>The transcoder's output reaches the downstream stage on the source timeline and the session drains.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Publish_DeliversTranscoderOutputOnTheSourceTimeline()
    {
        using var harness = new TranscodingHarness();
        var tags = FlvFile.ReadFixture("h264-aac.flv");
        var session = await harness.StartAsync();
        await harness.SendTagsAsync(session, tags);
        await harness.EndAsync(session);

        var process = Assert.Single(harness.Factory.Started);
        Assert.True(process.InputCompleted, "A normally ended publish closes the transcoder input to flush it.");
        Assert.False(process.Killed);
        Assert.Contains("h264", process.Arguments);
        Assert.Contains("libx264", process.Arguments);
        Assert.Contains("aac", process.Arguments);
        AssertCodedFrames(harness, session, tags);
        var kinds = harness.Downstream.Events.Where(e => ReferenceEquals(e.Session, session)).Select(static e => e.Kind).ToList();
        Assert.Equal("started", kinds[0]);
        Assert.Equal("ended", kinds[^1]);
        Assert.NotEmpty(harness.Delivered(session, PublishedMessageKind.Data));
        Assert.Contains(10000, harness.EventIds());
        Assert.Contains(10005, harness.EventIds());
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
        Assert.Equal(0, harness.Factory.Starts);
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

    /// <summary>An unsupported codec ends the publish with a reason and starts no transcoder.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task UnsupportedCodec_EndsThePublish()
    {
        using var harness = new TranscodingHarness();
        var session = await harness.StartAsync();
        await harness.SendAsync(session, PublishedMessageKind.Audio, 0, FlvPackets.AacSequenceHeader());
        var ex = await Assert.ThrowsAsync<TranscodingRejectedException>(() => harness.SendAsync(session, PublishedMessageKind.Video, 0, [0x14, 0, 1, 2, 3]));
        Assert.Equal(TranscodingRejectionReason.UnsupportedCodec, ex.Reason);
        Assert.Contains(10011, harness.EventIds());
        await harness.EndAsync(session, PublishEndReason.HandlerFailed);
        Assert.Equal(0, harness.Factory.Starts);
        Assert.Empty(harness.Delivered(session, PublishedMessageKind.Video));
        AssertReleased(harness);
    }

    /// <summary>A transcoder failure ends only its own session; another session is unaffected.</summary>
    /// <param name="behaviorName">How the first transcoder fails (a <see cref="FakeTranscoderBehavior"/>).</param>
    /// <param name="stageName">The expected failing stage (a <see cref="TranscodingStage"/>).</param>
    /// <returns>A task.</returns>
    [Theory]
    [InlineData(nameof(FakeTranscoderBehavior.Crash), nameof(TranscodingStage.Decoder))]
    [InlineData(nameof(FakeTranscoderBehavior.ExitAtStart), nameof(TranscodingStage.Decoder))]
    [InlineData(nameof(FakeTranscoderBehavior.FailToStart), nameof(TranscodingStage.Process))]
    public async Task TranscoderFailure_EndsOnlyThatSession(string behaviorName, string stageName)
    {
        var behavior = Enum.Parse<FakeTranscoderBehavior>(behaviorName);
        var stage = Enum.Parse<TranscodingStage>(stageName);
        using var harness = new TranscodingHarness();
        harness.Factory.BehaviorForStart = index => index == 0 ? behavior : FakeTranscoderBehavior.Echo;
        harness.Factory.CrashAfterBytes = 1;
        var tags = FlvFile.ReadFixture("h264-aac.flv");
        var failing = await harness.StartAsync();
        await harness.SendTagsAsync(failing, tags.Take(8));
        await Eventually.WaitUntilAsync(() => harness.Handler.FindSession(failing.SessionId)?.Fault is not null, "the failed transcoder faults its session");

        var healthy = await harness.StartAsync();
        await harness.SendTagsAsync(healthy, tags);
        var failure = await Assert.ThrowsAsync<TranscodingFailedException>(() => harness.SendTagsAsync(failing, tags.Skip(8)));
        Assert.Equal(stage, failure.Stage);
        Assert.True(failure.Initialization);
        Assert.Contains(10001, harness.EventIds());

        await harness.EndAsync(failing, PublishEndReason.HandlerFailed);
        await harness.EndAsync(healthy);
        AssertCodedFrames(harness, healthy, tags);
        Assert.DoesNotContain(harness.Delivered(failing, PublishedMessageKind.Video), static e => e.Payload![1] == 1);
        AssertReleased(harness);
    }

    /// <summary>
    /// With the drop policy a stalled transcoder never blocks the publisher: queues stay bounded, video is dropped
    /// up to the next keyframe and audio frame by frame, and ending the session kills the transcoder.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task StalledTranscoder_DropsVideoUntilKeyframe()
    {
        using var harness = new TranscodingHarness(static o =>
        {
            o.MaxBufferedVideoFrames = 8;
            o.MaxBufferedAudioFrames = 16;
        });
        harness.Factory.Behavior = FakeTranscoderBehavior.Stall;
        var session = await harness.StartAsync();
        await harness.SendTagsAsync(session, FlvFile.ReadFixture("h264-aac-4s.flv")).WithinSafetyTimeout();

        Assert.True(harness.Handler.FindSession(session.SessionId)!.QueuedBytes <= harness.Options.MaxSessionBufferBytes);
        var drops = DropLogs(harness);
        Assert.Contains(drops, static m => m.Contains("Video", StringComparison.Ordinal) && m.Contains("QueueFull", StringComparison.Ordinal));
        Assert.Contains(drops, static m => m.Contains("Video", StringComparison.Ordinal) && m.Contains("AwaitingKeyframe", StringComparison.Ordinal));
        Assert.Contains(drops, static m => m.Contains("Audio", StringComparison.Ordinal) && m.Contains("QueueFull", StringComparison.Ordinal));
        Assert.DoesNotContain(drops, static m => m.Contains("Audio", StringComparison.Ordinal) && m.Contains("AwaitingKeyframe", StringComparison.Ordinal));

        await harness.EndAsync(session, PublishEndReason.ConnectionError);
        var process = Assert.Single(harness.Factory.Started);
        Assert.True(process.Killed);
        Assert.False(process.InputCompleted);
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
        });
        harness.Factory.Behavior = FakeTranscoderBehavior.Stall;
        var session = await harness.StartAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task? blocked = null;
        foreach (var tag in FlvFile.ReadFixture("h264-aac-4s.flv"))
        {
            var send = harness.SendAsync(session, TranscodingHarness.Kind(tag), tag.Timestamp, tag.Body, cancellation.Token);
            if (await Task.WhenAny(send, Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken)) != send)
            {
                blocked = send;
                break;
            }

            await send;
        }

        Assert.NotNull(blocked);
        Assert.Empty(DropLogs(harness));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);

        await harness.EndAsync(session, PublishEndReason.ConnectionError);
        Assert.True(Assert.Single(harness.Factory.Started).Killed);
        AssertReleased(harness);
    }

    /// <summary>The service-wide budget drops a session's media when other sessions hold it, even under backpressure.</summary>
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
        harness.Factory.Behavior = FakeTranscoderBehavior.Stall;
        var header = FlvPackets.AvcSequenceHeader(H264Record());
        var frame = BigKeyframe(250_000);

        var first = await harness.StartAsync();
        await harness.SendAsync(first, PublishedMessageKind.Video, 0, header);
        for (var i = 0; i < 4; i++)
        {
            await harness.SendAsync(first, PublishedMessageKind.Video, (uint)(i * 33), frame).WithinSafetyTimeout();
        }

        var second = await harness.StartAsync();
        await harness.SendAsync(second, PublishedMessageKind.Video, 0, header);
        await harness.SendAsync(second, PublishedMessageKind.Video, 0, frame).WithinSafetyTimeout();

        Assert.Contains(DropLogs(harness), static m => m.Contains("BudgetExhausted", StringComparison.Ordinal));
        Assert.True(harness.Handler.BufferedBytes <= harness.Options.MaxTotalBufferBytes);
        Assert.Equal(0, harness.Handler.FindSession(second.SessionId)!.QueuedBytes);

        await harness.EndAsync(first, PublishEndReason.ConnectionError);
        await harness.EndAsync(second, PublishEndReason.ConnectionError);
        AssertReleased(harness);
    }

    /// <summary>A changed source configuration drains the transcoder and starts a new one; output stays monotonic.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task ConfigurationChange_RestartsTheTranscoder()
    {
        using var harness = new TranscodingHarness();
        var tags = FlvFile.ReadFixture("h264-aac-4s.flv");
        var half = tags.Count / 2;
        var session = await harness.StartAsync();
        await harness.SendTagsAsync(session, tags.Take(half));
        await harness.SendAsync(session, PublishedMessageKind.Audio, tags[half - 1].Timestamp, FlvPackets.AacSequenceHeader([0x11, 0x90]));
        await harness.SendTagsAsync(session, tags.Skip(half));
        await harness.EndAsync(session);

        var processes = harness.Factory.Started.ToArray();
        Assert.Equal(2, processes.Length);
        Assert.True(processes[0].InputCompleted, "The replaced transcoder is drained.");
        Assert.Contains(harness.Logs.GetSnapshot(), static r => r.Id.Id == 10000 && r.Message.Contains("(Configuration)", StringComparison.Ordinal));
        AssertMonotonic(harness, session);
        AssertReleased(harness);
    }

    /// <summary>A timeline discontinuity restarts the transcoder and the output follows the new timeline.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Discontinuity_RestartsTheTranscoder()
    {
        using var harness = new TranscodingHarness();
        var tags = FlvFile.ReadFixture("h264-aac.flv");
        var session = await harness.StartAsync();
        await harness.SendTagsAsync(session, tags);
        await harness.SendTagsAsync(session, tags, timestampShift: 60_000);
        await harness.EndAsync(session);

        Assert.Equal(2, harness.Factory.Started.Count);
        Assert.Contains(harness.Logs.GetSnapshot(), static r => r.Id.Id == 10000 && r.Message.Contains("(Discontinuity)", StringComparison.Ordinal));
        var delivered = harness.Delivered(session, PublishedMessageKind.Video).Where(static e => e.Payload![1] == 1).Select(static e => e.Timestamp).ToList();
        var source = tags.Where(static t => t.Type == 9 && t.Body[1] == 1).Select(static t => t.Timestamp).ToList();
        Assert.Equal([.. source, .. source.Select(static t => t + 60_000)], delivered);
        AssertReleased(harness);
    }

    /// <summary>When the second track's configuration arrives shortly after the first media, one transcoder takes both.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task LateSecondTrack_StartsOneTranscoder()
    {
        using var harness = new TranscodingHarness();
        var tags = FlvFile.ReadFixture("h264-aac.flv");
        var audioHeader = tags.First(static t => t.Type == 8 && t.Body[1] == 0);
        List<Media.FlvTag> ordered =
        [
            .. tags.Where(static t => t.Type == 9 && t.Timestamp < 300),
            audioHeader,
            .. tags.Where(t => t.Type is 8 or 9 && t.Timestamp >= 300 && !ReferenceEquals(t, audioHeader)),
        ];
        var session = await harness.StartAsync();
        await harness.SendTagsAsync(session, ordered);
        await harness.EndAsync(session);

        var process = Assert.Single(harness.Factory.Started);
        Assert.Contains("-c:a", process.Arguments);
        Assert.Contains("-c:v", process.Arguments);
        var sourceVideo = ordered.Count(static t => t.Type == 9 && t.Body[1] == 1);
        Assert.Equal(sourceVideo, harness.Delivered(session, PublishedMessageKind.Video).Count(static e => e.Payload![1] == 1));
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
        var delivered = harness.Delivered(session, PublishedMessageKind.Video).Where(static e => e.Payload![1] == 1).ToList();
        var lastKeyframe = tags.Last(static t => t.Type == 9 && t.Body[0] == 0x17 && t.Body[1] == 1);
        Assert.Contains(delivered, e => e.Timestamp == lastKeyframe.Timestamp);
        Assert.True(delivered.Count < tags.Count(static t => t.Type == 9 && t.Body[1] == 1));
        AssertMonotonic(harness, session);
        AssertReleased(harness);
    }

    /// <summary>
    /// A publish that ends normally or by disconnect drains its transcoder; one ended by an error, a timeout or
    /// shutdown kills it. Either way the session ends downstream.
    /// </summary>
    /// <param name="reasonName">The end reason (a <see cref="PublishEndReason"/>).</param>
    /// <param name="drains">Whether the transcoder is drained.</param>
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
    public async Task EndReason_DrainsOrKillsTheTranscoder(string reasonName, bool drains)
    {
        using var harness = new TranscodingHarness();
        var tags = FlvFile.ReadFixture("h264-aac.flv");
        var session = await harness.StartAsync();
        await harness.SendTagsAsync(session, tags);
        await harness.EndAsync(session, Enum.Parse<PublishEndReason>(reasonName));

        var process = Assert.Single(harness.Factory.Started);
        Assert.Equal(drains, process.InputCompleted);
        if (drains)
        {
            AssertCodedFrames(harness, session, tags);
        }

        Assert.Equal("ended", harness.Downstream.Events.Last().Kind);
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
        AssertCodedFrames(harness, session, tags);
        AssertReleased(harness);
    }

    /// <summary>Transcoder failures are classified by stage from its diagnostics.</summary>
    /// <param name="diagnostics">The diagnostics.</param>
    /// <param name="stageName">The expected stage (a <see cref="TranscodingStage"/>).</param>
    [Theory]
    [InlineData("Error while decoding stream #0:0: Invalid data found when processing input", nameof(TranscodingStage.Decoder))]
    [InlineData("[flv @ 0x1] Invalid data found when processing input", nameof(TranscodingStage.Decoder))]
    [InlineData("[libx264 @ 0x1] Error setting profile high.", nameof(TranscodingStage.Encoder))]
    [InlineData("Error while encoding audio", nameof(TranscodingStage.Encoder))]
    [InlineData("Error reinitializing filters!", nameof(TranscodingStage.Encoder))]
    [InlineData("", nameof(TranscodingStage.Process))]
    [InlineData("Killed", nameof(TranscodingStage.Process))]
    public void Failure_IsClassifiedByStage(string diagnostics, string stageName)
    {
        Assert.Equal(Enum.Parse<TranscodingStage>(stageName), TranscodingSession.Classify(diagnostics));
    }

    /// <summary>A transcoder that does not finish within the drain timeout is killed.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task DrainTimeout_KillsTheTranscoder()
    {
        using var harness = new TranscodingHarness(static o => o.DrainTimeout = TimeSpan.FromSeconds(1));
        harness.Factory.Behavior = FakeTranscoderBehavior.Stall;
        var session = await harness.StartAsync();
        await harness.SendTagsAsync(session, FlvFile.ReadFixture("h264-aac.flv"));
        await harness.EndAsync(session);

        Assert.True(Assert.Single(harness.Factory.Started).Killed);
        Assert.Contains(10002, harness.EventIds());
        AssertReleased(harness);
    }

    /// <summary>Host shutdown kills every transcoder; the sessions still end downstream afterwards.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Shutdown_KillsEveryTranscoder()
    {
        using var harness = new TranscodingHarness();
        harness.Factory.Behavior = FakeTranscoderBehavior.Stall;
        var tags = FlvFile.ReadFixture("h264-aac.flv");
        var first = await harness.StartAsync();
        var second = await harness.StartAsync();
        await harness.SendTagsAsync(first, tags);
        await harness.SendTagsAsync(second, tags);

        await harness.Handler.AbortAllAsync().WithinSafetyTimeout();
        Assert.All(harness.Factory.Started, static p => Assert.True(p.Killed));
        await harness.SendTagsAsync(first, tags.Take(4));

        await harness.EndAsync(first, PublishEndReason.ServerShutdown);
        await harness.EndAsync(second, PublishEndReason.ServerShutdown);
        Assert.Equal(2, harness.Downstream.Events.Count(static e => e.Kind == "ended"));
        AssertReleased(harness);
    }

    /// <summary>Concurrent sessions of different codecs each get their own decoder and complete output.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task ConcurrentSessions_OfDifferentCodecs_AreIsolated()
    {
        using var harness = new TranscodingHarness();
        string[] files = ["h264-aac.flv", "hevc.flv", "av1.flv", "vp9.flv", "opus.flv", "flac.flv"];
        var runs = await Task.WhenAll(files.Select(async file =>
        {
            var tags = FlvFile.ReadFixture(file);
            var session = await harness.StartAsync();
            await harness.SendTagsAsync(session, tags);
            await harness.EndAsync(session);
            return (Tags: tags, Session: session);
        }));

        Assert.Equal(files.Length, harness.Factory.Started.Count);
        var decoders = harness.Factory.Started.SelectMany(static p => Decoders(p.Arguments)).ToHashSet();
        Assert.Equal(new HashSet<string> { "h264", "hevc", "libdav1d", "vp9", "opus", "flac", "aac" }, decoders);
        foreach (var (tags, session) in runs)
        {
            var expected = tags.Count(IsCodedFrame);
            var delivered = harness.Downstream.Events.Count(e => e.Kind == "message" && ReferenceEquals(e.Session, session) && e.MessageKind != PublishedMessageKind.Data && IsCodedFrame(new Media.FlvTag(e.MessageKind == PublishedMessageKind.Audio ? (byte)8 : (byte)9, e.Timestamp, e.Payload!)));
            Assert.True(expected > 0);
            Assert.Equal(expected, delivered);
        }

        AssertReleased(harness);
    }

    /// <summary>Asserts that every coded frame was delivered with its timestamp and bytes.</summary>
    /// <param name="harness">The harness.</param>
    /// <param name="session">The session.</param>
    /// <param name="tags">The source tags.</param>
    private static void AssertCodedFrames(TranscodingHarness harness, PublisherSession session, IReadOnlyList<Media.FlvTag> tags)
    {
        foreach (var (type, kind) in new[] { ((byte)9, PublishedMessageKind.Video), ((byte)8, PublishedMessageKind.Audio) })
        {
            var source = tags.Where(t => t.Type == type && t.Body.Length > 1 && t.Body[1] == 1).ToList();
            var delivered = harness.Delivered(session, kind).Where(static e => e.Payload!.Length > 1 && e.Payload[1] == 1).ToList();
            Assert.Equal(source.Select(static t => t.Timestamp), delivered.Select(static e => e.Timestamp));
            Assert.All(source.Zip(delivered), static pair => Assert.Equal(pair.First.Body.AsSpan(1).ToArray(), pair.Second.Payload!.AsSpan(1).ToArray()));
        }
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

    /// <summary>Whether a tag is a coded frame (not a sequence start or end, command or metadata).</summary>
    /// <param name="tag">The tag.</param>
    /// <returns><see langword="true"/> for a coded frame.</returns>
    private static bool IsCodedFrame(Media.FlvTag tag)
    {
        var body = tag.Body;
        if (body.Length < 2 || tag.Type is not (8 or 9))
        {
            return false;
        }

        if (tag.Type == 9)
        {
            return (body[0] & 0x80) == 0
                ? body[1] == 1 && body.Length > 5
                : (body[0] & 0x0F) is 1 or 3 && ((body[0] >> 4) & 0x07) != 5 && body.Length > 5;
        }

        return (body[0] >> 4) switch
        {
            10 => body[1] == 1 && body.Length > 2,
            9 => (body[0] & 0x0F) == 1 && body.Length > 5,
            _ => body.Length > 1,
        };
    }

    /// <summary>Gets the decoders an argument list selects (the codec options before the input).</summary>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The decoder names.</returns>
    private static IEnumerable<string> Decoders(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i + 1 < arguments.Count && arguments[i] != "-i"; i++)
        {
            if (arguments[i] is "-c:v" or "-c:a")
            {
                yield return arguments[i + 1];
            }
        }
    }

    /// <summary>Gets the H.264 configuration record of the H.264 fixture.</summary>
    /// <returns>The AVCDecoderConfigurationRecord.</returns>
    private static byte[] H264Record() => FlvFile.ReadFixture("h264-aac.flv").First(static t => t.Type == 9 && t.Body[1] == 0).Body[5..];

    /// <summary>Builds a large legacy AVC keyframe with one IDR NAL unit.</summary>
    /// <param name="size">The NAL unit size.</param>
    /// <returns>The packet.</returns>
    private static byte[] BigKeyframe(int size)
    {
        var nal = new byte[size];
        nal[0] = 0x65;
        return FlvPackets.AvcNalu(true, 0, 4, nal);
    }
}
