// Lifecycle tests of the media multiplexer inside a transcoding session, with the echoing fake transcoder: a
// downstream failure faults only that session, and a downstream stage that stops accepting media cannot hold a kill
// or an expired drain, with every buffer and budget byte released either way.

using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Tests.Media;
using RTSP.IngestService.Transcoding;

namespace RTSP.IngestService.Tests.Transcoding;

/// <summary>Tests of the multiplexer's place in the transcoding session lifecycle.</summary>
public sealed class TranscodingMultiplexerLifecycleTests
{
    /// <summary>A downstream failure faults the session, refuses further media and releases everything.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task DownstreamFailure_FaultsTheSession()
    {
        using var harness = new TranscodingHarness();
        harness.Downstream.FailMessages = true;
        var session = await harness.StartAsync();
        var tags = FlvFile.ReadFixture("h264-aac.flv");
        await harness.SendTagsAsync(session, tags.Take(tags.Count / 2));
        await Eventually.WaitUntilAsync(() => harness.Handler.FindSession(session.SessionId)?.Fault is not null, "the session has faulted");

        await Assert.ThrowsAsync<TranscodingFailedException>(() => harness.SendTagsAsync(session, tags.Skip(tags.Count / 2)));
        await harness.EndAsync(session, PublishEndReason.HandlerFailed);

        Assert.True(Assert.Single(harness.Factory.Started).Killed);
        Assert.Equal("ended", harness.Downstream.Events.Last().Kind);
        AssertReleased(harness);
    }

    /// <summary>A kill ends the session while the downstream stage is blocked, discarding what the multiplexer holds.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Kill_WithABlockedDownstream_Completes()
    {
        using var harness = new TranscodingHarness();
        harness.Downstream.MessageGate = new TaskCompletionSource();
        var session = await harness.StartAsync();
        await harness.SendTagsAsync(session, FlvFile.ReadFixture("h264-aac.flv"));
        await harness.Downstream.MessageBlocked.Task.WithinSafetyTimeout();

        await harness.EndAsync(session, PublishEndReason.ConnectionError);

        Assert.Empty(harness.Delivered(session, PublishedMessageKind.Video));
        Assert.Equal("ended", harness.Downstream.Events.Last().Kind);
        AssertReleased(harness);
    }

    /// <summary>A drain whose downstream stage stops accepting media is ended by the drain timeout.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Drain_WithABlockedDownstream_EndsAtTheDrainTimeout()
    {
        using var harness = new TranscodingHarness(static o => o.DrainTimeout = TimeSpan.FromMilliseconds(500));
        harness.Downstream.MessageGate = new TaskCompletionSource();
        var session = await harness.StartAsync();
        await harness.SendTagsAsync(session, FlvFile.ReadFixture("h264-aac.flv"));
        await harness.Downstream.MessageBlocked.Task.WithinSafetyTimeout();

        await harness.EndAsync(session);

        Assert.True(Assert.Single(harness.Factory.Started).Disposed);
        Assert.Equal("ended", harness.Downstream.Events.Last().Kind);
        AssertReleased(harness);
    }

    /// <summary>Asserts that the session's slot, budget and source buffers were released.</summary>
    /// <param name="harness">The harness.</param>
    private static void AssertReleased(TranscodingHarness harness)
    {
        Assert.Equal(0, harness.Handler.ActiveSessionCount);
        Assert.Equal(0, harness.Handler.BufferedBytes);
        Assert.Equal(0, harness.Pool.Outstanding);
    }
}
