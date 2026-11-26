// Deterministic tests of the media multiplexer on a fake clock: timestamp merge and tie-break, per-track order, the
// startup hold, idle release and first-keyframe retention, unit, byte, duration and budget bounds with producer
// backpressure, flush barriers, metadata notification, and payload ownership through completion, cancellation,
// downstream and producer failure, rejection and disposal. Absence checks wait briefly for the output task; they can
// only pass wrongly, never fail wrongly.

using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;
using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Transcoding;

namespace RTSP.IngestService.Tests.Transcoding;

/// <summary>Tests of <see cref="MediaMultiplexer"/>.</summary>
public sealed class MediaMultiplexerTests
{
    /// <summary>Gets the test cancellation token.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Gives the output task time to act on what it has; absence checks follow it.</summary>
    /// <returns>A task.</returns>
    private static Task Settle() => Task.Delay(50, Token);

    /// <summary>Units queued on both tracks leave merged by timestamp, whatever order the producers submitted them in.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task BothTracksQueued_AreMergedByTimestamp()
    {
        await using var mux = new MuxHarness();
        for (var i = 0; i < 10; i++)
        {
            await mux.Video(i * 33, keyframe: i == 0);
        }

        for (var i = 0; i < 16; i++)
        {
            await mux.Audio(i * 21);
        }

        await mux.CompleteAsync();
        var media = mux.Media();
        Assert.Equal(26, media.Count);
        Assert.Equal(media.OrderBy(static m => m.Timestamp).ThenBy(static m => m.Kind == PublishedMessageKind.Video ? 0 : 1).Select(static m => m.Id), media.Select(static m => m.Id));
        mux.AssertReleased();
    }

    /// <summary>At equal timestamps sequence starts go before samples, then video before audio.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Ties_PutSequenceStartsFirst_ThenVideo()
    {
        await using var mux = new MuxHarness(start: false);
        await mux.Audio(1000, EncodedUnitKind.SequenceStart);
        await mux.Audio(1000);
        await mux.Video(1000, EncodedUnitKind.SequenceStart);
        await mux.Video(1000, keyframe: true);
        mux.Start();
        await mux.CompleteAsync();
        Assert.Equal(["V1000h", "A1000h", "V1000", "A1000"], mux.Media().Select(static m => m.Label));
    }

    /// <summary>A track's own order is kept even when its timestamps go backwards, as an AAC header before the first frame does.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task TrackOrder_IsKept_WhenItsTimestampsRegress()
    {
        await using var mux = new MuxHarness(start: false);
        await mux.Video(1000, EncodedUnitKind.SequenceStart);
        await mux.Audio(1000, EncodedUnitKind.SequenceStart);
        await mux.Audio(979);
        await mux.Audio(1000);
        await mux.Video(1000, keyframe: true);
        await mux.Audio(990);
        mux.Start();
        await mux.CompleteAsync();
        Assert.Equal(["V1000h", "A1000h", "A979", "V1000", "A1000", "A990"], mux.Media().Select(static m => m.Label));
    }

    /// <summary>An end of sequence follows everything queued on the other track, as the transcoder writes it last.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task SequenceEnd_FollowsTheOtherTracksQueuedUnits()
    {
        await using var mux = new MuxHarness(start: false);
        await mux.Video(1000, keyframe: true);
        await mux.Video(1033, EncodedUnitKind.SequenceEnd);
        await mux.Audio(1010);
        await mux.Audio(1040);
        mux.Start();
        await mux.CompleteAsync();
        Assert.Equal(["V1000", "A1010", "A1040", "V1033e"], mux.Media().Select(static m => m.Label));
    }

    /// <summary>Units in other time bases are ordered exactly and delivered in milliseconds.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task OtherTimeBases_AreOrderedExactly()
    {
        await using var mux = new MuxHarness(start: false);
        var samples = new MediaTimeBase(1, 48000);
        await mux.Video(20, keyframe: true);
        await mux.Audio(959, timeBase: samples);
        await mux.Audio(960, timeBase: samples);
        await mux.Audio(961, timeBase: samples);
        mux.Start();
        await mux.CompleteAsync();
        Assert.Equal(["A20", "V20", "A20", "A20"], mux.Media().Select(static m => m.Label));
        Assert.Equal([1, 0, 2, 3], mux.Media().Select(static m => m.Id % 100));
    }

    /// <summary>
    /// Audio that arrives before the first video keyframe is held, however long, until the keyframe orders it, so HLS
    /// does not discard it.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task AudioBeforeTheFirstKeyframe_IsHeldUntilTheKeyframe()
    {
        await using var mux = new MuxHarness();
        await mux.Audio(0, EncodedUnitKind.SequenceStart);
        for (var i = 0; i < 20; i++)
        {
            await mux.Audio(i * 21);
        }

        await Settle();
        mux.Time.Advance(TimeSpan.FromSeconds(3));
        await Settle();
        Assert.Equal(["A0h"], mux.Media().Select(static m => m.Label));

        await mux.Video(200, keyframe: true);
        await mux.WaitForAsync(12);
        await Settle();
        Assert.Equal([.. Enumerable.Range(0, 10).Select(static i => $"A{i * 21}"), "V200"], mux.Media().Skip(1).Select(static m => m.Label));
        await mux.CompleteAsync();
        Assert.Equal(22, mux.Media().Count);
    }

    /// <summary>Retention for the first keyframe is bounded by the queue's media-duration bound.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task FirstKeyframeRetention_IsBoundedByTheRetentionWindow()
    {
        await using var mux = new MuxHarness();
        using var early = mux.Collect("transcoding.mux.early_releases");
        for (var i = 0; i <= 250; i++)
        {
            await mux.Audio(i * 21);
        }

        await Settle();
        var delivered = mux.Media();
        Assert.NotEmpty(delivered);
        Assert.InRange(mux.Multiplexer.QueuedUnits(MediaTrack.Audio), 230, 240);
        Assert.Equal(delivered.Select(static m => m.Id), delivered.OrderBy(static m => m.Id).Select(static m => m.Id));
        Assert.All(early.GetMeasurementSnapshot(), static m => Assert.Equal("duration", m.Tags["reason"]));
    }

    /// <summary>Video waits for audio that is expected but late for at most the startup hold, then flows freely.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task LateAudio_HoldsVideo_ForTheStartupHoldOnly()
    {
        await using var mux = new MuxHarness();
        await mux.Video(0, keyframe: true);
        await mux.Video(33);
        await Settle();
        Assert.Empty(mux.Media());

        mux.Time.Advance(TimeSpan.FromMilliseconds(999));
        await Settle();
        Assert.Empty(mux.Media());

        mux.Time.Advance(TimeSpan.FromMilliseconds(1));
        await mux.WaitForAsync(2);
        await mux.Video(66);
        await mux.WaitForAsync(3);

        await mux.Audio(80);
        await mux.Video(100);
        await mux.CompleteAsync();
        Assert.Equal(["V0", "V33", "V66", "A80", "V100"], mux.Media().Select(static m => m.Label));
    }

    /// <summary>The startup hold also ends once a second of media is queued, without waiting for the clock.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task StartupHold_EndsAtOneSecondOfQueuedMedia()
    {
        await using var mux = new MuxHarness();
        for (var i = 0; i < 31; i++)
        {
            await mux.Video(i * 33, keyframe: i == 0);
        }

        await Settle();
        Assert.Empty(mux.Media());
        await mux.Video(31 * 33);
        await mux.WaitForAsync(32);
        await mux.Video(32 * 33);
        await mux.WaitForAsync(33);
    }

    /// <summary>A started track that goes quiet holds the other for the idle release, then the other flows until it resumes.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task IdleTrack_IsReleasedAfterTheIdleRelease()
    {
        await using var mux = new MuxHarness();
        await mux.Video(0, keyframe: true);
        await mux.Audio(0);
        await mux.WaitForAsync(1);

        mux.Time.Advance(TimeSpan.FromMilliseconds(500));
        await mux.Video(33);
        await mux.Video(66);
        await mux.WaitForAsync(2);
        await Settle();
        Assert.Equal(["V0", "A0"], mux.Media().Select(static m => m.Label));

        mux.Time.Advance(TimeSpan.FromMilliseconds(499));
        await Settle();
        Assert.Equal(2, mux.Media().Count);

        mux.Time.Advance(TimeSpan.FromMilliseconds(1));
        await mux.WaitForAsync(4);
        await mux.Video(100);
        await mux.WaitForAsync(5);

        await mux.Audio(120);
        await mux.Video(133);
        await mux.Video(166);
        await mux.Audio(150);
        await mux.CompleteAsync();
        Assert.Equal(["V0", "A0", "V33", "V66", "V100", "A120", "V133", "A150", "V166"], mux.Media().Select(static m => m.Label));
    }

    /// <summary>
    /// A burst is released as soon as the other track's head orders it, with no per-unit wait; the other track's
    /// later unit then waits for the bursting track's next unit.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Burst_IsReleasedWithoutWaiting()
    {
        await using var mux = new MuxHarness();
        await mux.Configure();
        await mux.Video(0, keyframe: true);
        await mux.Audio(0);
        for (var i = 1; i <= 60; i++)
        {
            await mux.Video(i * 33);
        }

        await mux.Audio(5000);
        await mux.WaitForAsync(64);
        await Settle();
        Assert.Equal(64, mux.Media().Count);
        Assert.Equal("V1980", mux.Media()[^1].Label);
        Assert.Equal(1, mux.Multiplexer.QueuedUnits(MediaTrack.Audio));
    }

    /// <summary>A track declared absent, or completed, is never waited for.</summary>
    /// <param name="completeInstead">Whether to complete the other track instead of declaring it absent.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentOrCompletedTrack_IsNotWaitedFor(bool completeInstead)
    {
        await using var mux = new MuxHarness();
        if (completeInstead)
        {
            mux.Multiplexer.Video.Complete();
        }
        else
        {
            mux.Multiplexer.Video.Declare(expected: false);
        }

        await mux.Audio(0);
        await mux.Audio(21);
        await mux.WaitForAsync(2);
    }

    /// <summary>Completing both tracks delivers everything still queued and ends the output task.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task CompletingBothTracks_DeliversEverything()
    {
        await using var mux = new MuxHarness();
        await mux.Audio(0);
        await mux.Audio(21);
        mux.Multiplexer.Audio.Complete();
        await Settle();
        Assert.Empty(mux.Media());

        mux.Multiplexer.Video.Complete();
        await mux.Multiplexer.Completion.WithinSafetyTimeout();
        Assert.Equal(2, mux.Media().Count);
        mux.AssertReleased();
    }

    /// <summary>A flush releases everything queued at once in merge order; later units wait for the normal rules.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Flush_ReleasesEverythingQueuedBeforeIt()
    {
        await using var mux = new MuxHarness();
        await mux.Audio(0);
        await mux.Audio(21);
        await mux.Audio(42);
        await mux.Multiplexer.FlushAsync(Token).AsTask().WithinSafetyTimeout();
        Assert.Equal(3, mux.Media().Count);

        await mux.Audio(63);
        await Settle();
        Assert.Equal(3, mux.Media().Count);
        await mux.Multiplexer.FlushAsync(Token).AsTask().WithinSafetyTimeout();
        Assert.Equal(4, mux.Media().Count);
    }

    /// <summary>
    /// Across a restart the session flushes, declares a new epoch and the timeline may restart lower: the earlier
    /// epoch's units are delivered first and the new one is merged on its own.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Discontinuity_IsSeparatedByAFlush()
    {
        await using var mux = new MuxHarness();
        await mux.Video(9000, keyframe: true);
        await mux.Audio(9010);
        await mux.Audio(9030);
        await mux.Multiplexer.FlushAsync(Token).AsTask().WithinSafetyTimeout();
        mux.Multiplexer.Video.Declare(expected: true);
        mux.Multiplexer.Audio.Declare(expected: true);
        await mux.Audio(100);
        await Settle();
        Assert.Equal(3, mux.Media().Count);

        await mux.Video(120, keyframe: true);
        await mux.CompleteAsync();
        Assert.Equal(["V9000", "A9010", "A9030", "A100", "V120"], mux.Media().Select(static m => m.Label));
    }

    /// <summary>
    /// A full queue makes its producer wait without dropping anything; the queue is then released early, so the wait
    /// ends as soon as the downstream stage accepts units.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task FullQueue_MakesTheProducerWait_WithoutDropping()
    {
        await using var mux = new MuxHarness();
        mux.Downstream.Block();
        await mux.Video(0, keyframe: true);
        await mux.Audio(0);
        await mux.Downstream.WaitForBlockedAsync();
        for (var i = 1; i < MultiplexerLimits.AudioUnits; i++)
        {
            await mux.Audio(i);
        }

        var waiting = mux.Audio(MultiplexerLimits.AudioUnits);
        await Settle();
        Assert.False(waiting.IsCompleted);
        Assert.Equal(MultiplexerLimits.AudioUnits, mux.Multiplexer.QueuedUnits(MediaTrack.Audio));

        mux.Downstream.Unblock();
        await waiting.AsTask().WithinSafetyTimeout();
        await mux.CompleteAsync();
        Assert.Equal(MultiplexerLimits.AudioUnits + 2, mux.Media().Count);
        mux.AssertReleased();
    }

    /// <summary>The byte and duration bounds also make the producer wait; an empty queue always takes one unit.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ByteAndDurationBounds_AreEnforced()
    {
        var limits = new MultiplexerLimits(
            new MultiplexerTrackLimits(100, 1000, TimeSpan.FromSeconds(5)),
            new MultiplexerTrackLimits(100, 1000, TimeSpan.FromMilliseconds(100)),
            MultiplexerLimits.DefaultStartupHold,
            MultiplexerLimits.DefaultIdleRelease);
        await using var mux = new MuxHarness(limits: limits);
        mux.Downstream.Block();
        await mux.Video(0, keyframe: true);
        await mux.Audio(0);
        await mux.Downstream.WaitForBlockedAsync();
        await mux.Video(1, size: 2000);
        var bytes = mux.Video(2, size: 10);
        await mux.Audio(100);
        var duration = mux.Audio(101);
        await Settle();
        Assert.False(bytes.IsCompleted);
        Assert.False(duration.IsCompleted);

        mux.Downstream.Unblock();
        await bytes.AsTask().WithinSafetyTimeout();
        await duration.AsTask().WithinSafetyTimeout();
        await mux.CompleteAsync();
        Assert.Equal(["V0", "A0", "V1", "V2", "A100", "A101"], mux.Media().Select(static m => m.Label));
        mux.AssertReleased();
    }

    /// <summary>
    /// Queued bytes are reserved from the budget and never exceed it: an empty queue holds its head on a floor, a
    /// non-empty queue reserves shared bytes, and an exhausted shared pool makes a producer with queued units wait.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task SharedBudget_IsReservedAndReleased()
    {
        await using var mux = new MuxHarness(sharedBudget: new TranscodingBufferBudget(1100, 500, 2));
        mux.Downstream.Block();
        await mux.Video(0, keyframe: true);
        await mux.Audio(0, size: 60);
        await mux.Downstream.WaitForBlockedAsync();
        await mux.Audio(21, size: 100);
        Assert.Equal(160, mux.Budget.Used);
        var waiting = mux.Audio(42, size: 10);
        await Settle();
        Assert.False(waiting.IsCompleted);

        await mux.Video(33, size: 500);
        Assert.Equal(660, mux.Budget.Used);

        mux.Downstream.Unblock();
        await waiting.AsTask().WithinSafetyTimeout();
        await mux.CompleteAsync();
        Assert.Equal(5, mux.Media().Count);
        Assert.True(mux.Budget.Peak <= mux.Budget.Limit);
        mux.AssertReleased();
    }

    /// <summary>Without a floor, a unit larger than the free shared pool waits even on an empty queue, and is admitted once bytes are released.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task StrictBudget_NeverOverdraws()
    {
        var budget = new TranscodingBufferBudget(100);
        Assert.True(budget.TryReserve(60));
        await using var mux = new MuxHarness(sharedBudget: budget);
        var waiting = mux.Video(0, keyframe: true, size: 50);
        await Settle();
        Assert.False(waiting.IsCompleted);
        Assert.Equal(60, budget.Used);

        budget.Release(60);
        await waiting.AsTask().WithinSafetyTimeout();
        await mux.CompleteAsync();
        Assert.Single(mux.Media());
        Assert.Equal(60, budget.Peak);
        mux.AssertReleased();
    }

    /// <summary>Many multiplexers sharing a budget with floors, under concurrent producers and a full shared pool, never exceed the limit and all progress.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ConcurrentProducers_StayWithinTheBudget()
    {
        const int Sessions = 4;
        var budget = new TranscodingBufferBudget(8000, 600, 2 * Sessions);
        Assert.True(budget.TryReserve(budget.SharedLimit - 300));
        var muxes = Enumerable.Range(0, Sessions).Select(_ => new MuxHarness(sharedBudget: budget)).ToArray();
        try
        {
            var producers = muxes.SelectMany(static mux => new[]
            {
                Task.Run(async () =>
                {
                    for (var i = 0; i < 200; i++)
                    {
                        await mux.Video(i * 33, keyframe: i % 30 == 0, size: 100 + (i % 5 * 100));
                    }
                }),
                Task.Run(async () =>
                {
                    for (var i = 0; i < 300; i++)
                    {
                        await mux.Audio(i * 21, size: 50 + (i % 7 * 60));
                    }
                }),
            }).ToArray();
            await Task.WhenAll(producers).WithinSafetyTimeout();
            budget.Release(budget.SharedLimit - 300);
            foreach (var mux in muxes)
            {
                await mux.CompleteAsync();
                Assert.Equal(500, mux.Media().Count);
                mux.AssertPayloadsReleased();
            }

            Assert.Equal(0, budget.Used);
            Assert.True(budget.Peak <= budget.Limit, $"peak {budget.Peak} exceeds {budget.Limit}");
        }
        finally
        {
            foreach (var mux in muxes)
            {
                await mux.DisposeAsync();
            }
        }
    }

    /// <summary>Cancelling a waiting producer ends its wait and releases its payload.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task CancellingAWaitingProducer_ReleasesItsPayload()
    {
        var limits = new MultiplexerLimits(
            new MultiplexerTrackLimits(1, 1000, TimeSpan.FromSeconds(5)),
            new MultiplexerTrackLimits(1, 1000, TimeSpan.FromSeconds(5)),
            MultiplexerLimits.DefaultStartupHold,
            MultiplexerLimits.DefaultIdleRelease);
        await using var mux = new MuxHarness(limits: limits);
        mux.Downstream.Block();
        await mux.Video(0, keyframe: true);
        await mux.Audio(0);
        await mux.Downstream.WaitForBlockedAsync();
        await mux.Video(33);
        using var cancel = new CancellationTokenSource();
        var waiting = mux.Video(66, cancellationToken: cancel.Token);
        await Settle();
        Assert.False(waiting.IsCompleted);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.AsTask().WithinSafetyTimeout());
        mux.Downstream.Unblock();
        await mux.CompleteAsync();
        Assert.Equal(["V0", "A0", "V33"], mux.Media().Select(static m => m.Label));
        mux.AssertReleased();
    }

    /// <summary>Submissions after completion, after disposal or to a failed multiplexer are refused and their payloads released.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task RejectedSubmissions_ReleaseTheirPayloads()
    {
        await using var mux = new MuxHarness();
        using var units = mux.Collect("transcoding.mux.units");
        mux.Multiplexer.Audio.Complete();
        await Assert.ThrowsAsync<InvalidOperationException>(() => mux.Audio(0).AsTask());

        mux.Multiplexer.Video.Fail(new InvalidOperationException("producer"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mux.Video(0, keyframe: true).AsTask());

        await mux.Multiplexer.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => mux.Video(0, keyframe: true).AsTask());
        mux.AssertReleased();
        Assert.Equal(3, units.GetMeasurementSnapshot().Count(static m => (string?)m.Tags["outcome"] == "rejected"));
    }

    /// <summary>Disposal, or the owner's cancellation, discards everything queued and releases it.</summary>
    /// <param name="cancel">Whether to cancel the owner's token rather than dispose.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppingWithQueuedUnits_ReleasesThem(bool cancel)
    {
        await using var mux = new MuxHarness();
        mux.Downstream.Block();
        await mux.Video(0, keyframe: true);
        await mux.Audio(0);
        await mux.Downstream.WaitForBlockedAsync();
        for (var i = 1; i < 20; i++)
        {
            await mux.Video(i * 33);
            await mux.Audio(i * 21);
        }

        if (cancel)
        {
            await mux.Owner.CancelAsync();
            await mux.Multiplexer.Completion.WithinSafetyTimeout();
        }
        else
        {
            await mux.Multiplexer.DisposeAsync().AsTask().WithinSafetyTimeout();
        }

        Assert.Empty(mux.Media());
        mux.AssertReleased();
    }

    /// <summary>
    /// A downstream failure stops the multiplexer: the owner is told, the failed unit and everything queued are
    /// released, and a waiting producer is woken, either admitted by the release before the failure or refused.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task DownstreamFailure_StopsAndReleasesEverything()
    {
        var limits = new MultiplexerLimits(
            new MultiplexerTrackLimits(2, 1000, TimeSpan.FromSeconds(5)),
            new MultiplexerTrackLimits(2, 1000, TimeSpan.FromSeconds(5)),
            MultiplexerLimits.DefaultStartupHold,
            MultiplexerLimits.DefaultIdleRelease);
        await using var mux = new MuxHarness(limits: limits);
        mux.Downstream.Block();
        mux.Downstream.FailAt = 1;
        await mux.Video(0, keyframe: true);
        await mux.Audio(0);
        await mux.Downstream.WaitForBlockedAsync();
        await mux.Video(33);
        await mux.Video(66);
        await mux.Audio(21);
        var waiting = mux.Audio(42);
        await Settle();
        Assert.False(waiting.IsCompleted);

        mux.Downstream.Unblock();
        try
        {
            await waiting.AsTask().WithinSafetyTimeout();
        }
        catch (InvalidOperationException)
        {
            // Refused after the failure, rather than admitted by the release before it.
        }

        await mux.Multiplexer.Completion.WithinSafetyTimeout();
        Assert.IsType<IOException>(Assert.Single(mux.Faults));
        Assert.IsType<IOException>(mux.Multiplexer.Fault);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mux.Audio(63).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => mux.Multiplexer.FlushAsync(Token).AsTask());
        Assert.Equal(["V0"], mux.Media().Select(static m => m.Label));
        mux.AssertReleased();
    }

    /// <summary>A producer failure stops the multiplexer without blocking the other producer.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ProducerFailure_DoesNotBlockTheOtherProducer()
    {
        await using var mux = new MuxHarness();
        mux.Downstream.Block();
        await mux.Video(0, keyframe: true);
        await mux.Audio(0);
        await mux.Downstream.WaitForBlockedAsync();
        for (var i = 1; i < MultiplexerLimits.AudioUnits; i++)
        {
            await mux.Audio(i);
        }

        var waiting = mux.Audio(1000);
        await Settle();
        Assert.False(waiting.IsCompleted);

        var failure = new InvalidDataException("video producer");
        mux.Multiplexer.Video.Fail(failure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => waiting.AsTask().WithinSafetyTimeout());
        Assert.Same(failure, Assert.Single(mux.Faults));
        mux.Downstream.Unblock();
        await mux.Multiplexer.Completion.WithinSafetyTimeout();
        mux.AssertReleased();
    }

    /// <summary>A pending flush ends when the multiplexer is stopped.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task PendingFlush_EndsWhenStopped()
    {
        await using var mux = new MuxHarness();
        mux.Downstream.Block();
        await mux.Video(0, keyframe: true);
        await mux.Audio(0);
        await mux.Downstream.WaitForBlockedAsync();
        await mux.Video(33);
        var flush = mux.Multiplexer.FlushAsync(Token).AsTask();
        await Settle();
        Assert.False(flush.IsCompleted);

        await mux.Owner.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush.WithinSafetyTimeout());
        await mux.Multiplexer.Completion.WithinSafetyTimeout();
        mux.AssertReleased();
    }

    /// <summary>Changed metadata is announced with a data message before the next submitted unit, at its timestamp.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Metadata_PrecedesTheNextSubmittedUnit()
    {
        await using var mux = new MuxHarness(start: false);
        await mux.Video(0, keyframe: true);
        mux.Multiplexer.NotifyMetadata();
        await mux.Audio(40);
        await mux.Video(33);
        mux.Start();
        await mux.CompleteAsync();
        Assert.Equal(["V0", "V33", "D40", "A40"], mux.Downstream.Delivered.Select(static m => m.Label));
    }

    /// <summary>A unit that the downstream stage has not accepted when the owner cancels is released.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task CancellationDuringDelivery_ReleasesTheUnit()
    {
        await using var mux = new MuxHarness();
        mux.Downstream.Block();
        await mux.Video(0, keyframe: true);
        await mux.Audio(0);
        await mux.Downstream.WaitForBlockedAsync();
        await mux.Owner.CancelAsync();
        await mux.Multiplexer.Completion.WithinSafetyTimeout();
        Assert.Null(mux.Multiplexer.Fault);
        Assert.Empty(mux.Faults);
        mux.AssertReleased();
    }

    /// <summary>The default bounds follow the transcoding limits.</summary>
    [Fact]
    public void DefaultLimits_FollowTheTranscodingOptions()
    {
        var limits = MultiplexerLimits.FromOptions(new TranscodingOptions());
        Assert.Equal(new MultiplexerTrackLimits(300, 7_000_000, TimeSpan.FromSeconds(5)), limits.Video);
        Assert.Equal(new MultiplexerTrackLimits(256, 256 * 1538, TimeSpan.FromSeconds(5)), limits.Audio);
        Assert.Equal(TimeSpan.FromSeconds(1), limits.StartupHold);
        Assert.Equal(TimeSpan.FromSeconds(1), limits.IdleRelease);
    }

    /// <summary>A multiplexer under test with a controllable downstream stage, a counting pool and a fake clock.</summary>
    private sealed class MuxHarness : IAsyncDisposable
    {
        /// <summary>The next payload ID.</summary>
        private int _nextId;

        /// <summary>Initializes a new instance of the <see cref="MuxHarness"/> class.</summary>
        /// <param name="start">Whether to start the output task now.</param>
        /// <param name="limits">The limits; defaults when <see langword="null"/>.</param>
        /// <param name="budget">The budget in bytes.</param>
        /// <param name="sharedBudget">A budget to use instead of a new one of <paramref name="budget"/> bytes.</param>
        public MuxHarness(bool start = true, MultiplexerLimits? limits = null, long budget = 1 << 30, TranscodingBufferBudget? sharedBudget = null)
        {
            Services = new ServiceCollection().AddMetrics().BuildServiceProvider();
            Budget = sharedBudget ?? new TranscodingBufferBudget(budget);
            var session = new PublisherSession(1, null, "live", "camera", "live", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            Multiplexer = new MediaMultiplexer(
                session,
                Downstream,
                limits ?? MultiplexerLimits.FromOptions(new TranscodingOptions()),
                Budget,
                new TranscodingMetrics(Services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()),
                LoggerFactory.CreateLogger<MediaMultiplexer>(),
                Time,
                Faults.Add,
                Owner.Token);
            if (start)
            {
                Start();
            }
        }

        /// <summary>Gets the services owning the meter factory.</summary>
        public ServiceProvider Services { get; }

        /// <summary>Gets the logger factory.</summary>
        public LoggerFactory LoggerFactory { get; } = new([new FakeLoggerProvider(new FakeLogCollector())]);

        /// <summary>Gets the clock.</summary>
        public FakeTimeProvider Time { get; } = new(DateTimeOffset.UnixEpoch);

        /// <summary>Gets the payload pool.</summary>
        public CountingArrayPool Pool { get; } = new();

        /// <summary>Gets the budget.</summary>
        public TranscodingBufferBudget Budget { get; }

        /// <summary>Gets the owner's cancellation.</summary>
        public CancellationTokenSource Owner { get; } = new();

        /// <summary>Gets the downstream stage.</summary>
        public ControlledDownstream Downstream { get; } = new();

        /// <summary>Gets the faults reported to the owner.</summary>
        public List<Exception> Faults { get; } = [];

        /// <summary>Gets the multiplexer.</summary>
        public MediaMultiplexer Multiplexer { get; }

        /// <summary>Starts the output task.</summary>
        public void Start() => Multiplexer.Start();

        /// <summary>Collects a long-valued instrument.</summary>
        /// <param name="name">The instrument.</param>
        /// <returns>The collector.</returns>
        public MetricCollector<long> Collect(string name) =>
            new(Services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(), TranscodingMetrics.MeterName, name);

        /// <summary>Submits sequence starts for both tracks.</summary>
        /// <returns>A task.</returns>
        public async Task Configure()
        {
            await Video(0, EncodedUnitKind.SequenceStart);
            await Audio(0, EncodedUnitKind.SequenceStart);
        }

        /// <summary>Submits a video unit.</summary>
        /// <param name="timestamp">Its timestamp in milliseconds.</param>
        /// <param name="kind">Its kind.</param>
        /// <param name="keyframe">Whether it is a keyframe.</param>
        /// <param name="size">Its payload size.</param>
        /// <param name="cancellationToken">Cancellation; the test's when omitted.</param>
        /// <returns>The submission.</returns>
        public ValueTask Video(long timestamp, EncodedUnitKind kind = EncodedUnitKind.Sample, bool keyframe = false, int size = 4, CancellationToken? cancellationToken = null) =>
            Multiplexer.Video.SubmitAsync(Unit(kind, timestamp, MediaTimeBase.Milliseconds, keyframe, size), cancellationToken ?? Token);

        /// <summary>Submits an audio unit.</summary>
        /// <param name="timestamp">Its timestamp.</param>
        /// <param name="kind">Its kind.</param>
        /// <param name="size">Its payload size.</param>
        /// <param name="timeBase">Its time base; milliseconds when omitted.</param>
        /// <returns>The submission.</returns>
        public ValueTask Audio(long timestamp, EncodedUnitKind kind = EncodedUnitKind.Sample, int size = 4, MediaTimeBase? timeBase = null) =>
            Multiplexer.Audio.SubmitAsync(Unit(kind, timestamp, timeBase ?? MediaTimeBase.Milliseconds, true, size), Token);

        /// <summary>Waits until a number of media messages has been delivered.</summary>
        /// <param name="count">The count.</param>
        /// <returns>A task.</returns>
        public Task WaitForAsync(int count) => Eventually.WaitUntilAsync(() => Media().Count >= count, $"{count} units are delivered");

        /// <summary>Completes both inputs and waits for the output task.</summary>
        /// <returns>A task.</returns>
        public Task CompleteAsync() => Multiplexer.CompleteAsync().WithinSafetyTimeout();

        /// <summary>Gets the delivered audio and video messages.</summary>
        /// <returns>The messages.</returns>
        public List<Delivered> Media() => [.. Downstream.Delivered.Where(static m => m.Kind != PublishedMessageKind.Data)];

        /// <summary>Asserts that every payload was returned and every budget byte released.</summary>
        public void AssertReleased()
        {
            AssertPayloadsReleased();
            Assert.Equal(0, Budget.Used);
        }

        /// <summary>Asserts that every payload was returned and nothing is queued, for a budget shared with other multiplexers.</summary>
        public void AssertPayloadsReleased()
        {
            Assert.Equal(0, Pool.Outstanding);
            Assert.Equal(0, Multiplexer.QueuedBytes(MediaTrack.Video) + Multiplexer.QueuedBytes(MediaTrack.Audio));
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            Downstream.Unblock();
            await Multiplexer.DisposeAsync();
            Owner.Dispose();
            LoggerFactory.Dispose();
            await Services.DisposeAsync();
        }

        /// <summary>Creates a unit whose first payload byte identifies it.</summary>
        /// <param name="kind">Its kind.</param>
        /// <param name="timestamp">Its timestamp.</param>
        /// <param name="timeBase">Its time base.</param>
        /// <param name="sync">Whether it is a sync point.</param>
        /// <param name="size">Its payload size.</param>
        /// <returns>The unit.</returns>
        private EncodedAccessUnit Unit(EncodedUnitKind kind, long timestamp, MediaTimeBase timeBase, bool sync, int size)
        {
            var payload = new byte[Math.Max(size, 1)];
            payload[0] = (byte)(Interlocked.Increment(ref _nextId) - 1);
            payload[^1] = (byte)kind;
            var buffer = new PooledBuffer(Pool);
            buffer.EnsureCapacity(payload.Length);
            buffer.Append(new ReadOnlySequence<byte>(payload));
            return new EncodedAccessUnit(kind, timestamp, timeBase, sync, buffer);
        }
    }

    /// <summary>A delivered message.</summary>
    /// <param name="Kind">Its kind.</param>
    /// <param name="Timestamp">Its timestamp.</param>
    /// <param name="Id">Its payload ID.</param>
    /// <param name="UnitKind">Its unit kind, from the payload.</param>
    private sealed record Delivered(PublishedMessageKind Kind, uint Timestamp, int Id, EncodedUnitKind UnitKind)
    {
        /// <summary>Gets a label: track letter, timestamp, and <c>h</c> for a sequence start or <c>e</c> for a sequence end.</summary>
        public string Label => Kind switch
        {
            PublishedMessageKind.Data => $"D{Timestamp}",
            _ => $"{(Kind == PublishedMessageKind.Video ? 'V' : 'A')}{Timestamp}{UnitKind switch { EncodedUnitKind.SequenceStart => "h", EncodedUnitKind.SequenceEnd => "e", _ => string.Empty }}",
        };
    }

    /// <summary>Records messages and disposes them; can block or fail.</summary>
    private sealed class ControlledDownstream : IPublishSessionHandler
    {
        /// <summary>Completed to let blocked calls continue.</summary>
        private TaskCompletionSource _gate = CompletedGate();

        /// <summary>Completed when a call is blocked.</summary>
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Calls made.</summary>
        private int _calls;

        /// <summary>Gets the delivered messages.</summary>
        public List<Delivered> Delivered
        {
            get
            {
                lock (Messages)
                {
                    return [.. Messages];
                }
            }
        }

        /// <summary>Gets or sets the zero-based call that throws, or -1.</summary>
        public int FailAt { get; set; } = -1;

        /// <summary>Gets the recorded messages.</summary>
        private List<Delivered> Messages { get; } = [];

        /// <summary>Makes the next calls wait until <see cref="Unblock"/>.</summary>
        public void Block() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lets blocked calls continue.</summary>
        public void Unblock() => _gate.TrySetResult();

        /// <summary>Waits until a call is blocked.</summary>
        /// <returns>A task.</returns>
        public Task WaitForBlockedAsync() => _blocked.Task.WithinSafetyTimeout();

        /// <inheritdoc />
        public ValueTask OnSessionStartedAsync(PublisherSession session, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        /// <inheritdoc />
        public async ValueTask OnMessageAsync(PublisherSession session, PublishedMessage message, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls) - 1;
            if (!_gate.Task.IsCompleted)
            {
                _blocked.TrySetResult();
                await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (call == FailAt)
            {
                throw new IOException("The downstream stage failed.");
            }

            var payload = message.Payload.Span;
            var id = payload.Length > 0 ? payload[0] : -1;
            var kind = payload.Length > 0 ? (EncodedUnitKind)payload[^1] : EncodedUnitKind.Sample;
            lock (Messages)
            {
                Messages.Add(new Delivered(message.Kind, message.Timestamp, id, kind));
            }

            message.Dispose();
        }

        /// <inheritdoc />
        public ValueTask OnSessionEndedAsync(PublisherSession session, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        /// <summary>Returns a completed gate.</summary>
        /// <returns>The gate.</returns>
        private static TaskCompletionSource CompletedGate()
        {
            var gate = new TaskCompletionSource();
            gate.SetResult();
            return gate;
        }
    }
}
