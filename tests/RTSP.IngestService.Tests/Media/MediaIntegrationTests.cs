// End-to-end media processing tests through RtmpConnection over in-memory pipes: an FFmpeg-generated H.264/AAC FLV
// fixture replayed by the test client becomes typed samples whose bytes match the source tags exactly, malformed
// publishers are isolated, downstream backpressure stops reading, and every pooled buffer and session state is
// released when publishers disconnect or the downstream handler fails.

using Microsoft.Extensions.Logging;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Media;

/// <summary>Integration tests of RTMP ingest with <see cref="MediaPublishSessionHandler"/>.</summary>
public sealed class MediaIntegrationTests : IDisposable
{
    /// <summary>The downstream recorder.</summary>
    private readonly RecordingMediaHandler _downstream = new();

    /// <summary>The server.</summary>
    private readonly RtmpTestServer _server;

    /// <summary>Initializes a new instance of the <see cref="MediaIntegrationTests"/> class.</summary>
    public MediaIntegrationTests()
    {
        _server = new RtmpTestServer(sessionHandler: (meters, logs) => new MediaPublishSessionHandler(
            Microsoft.Extensions.Options.Options.Create(new MediaOptions()),
            _downstream,
            new MediaMetrics(meters),
            logs.CreateLogger<MediaPublishSessionHandler>()));
    }

    /// <summary>Gets the processor.</summary>
    private MediaPublishSessionHandler Processor => (MediaPublishSessionHandler)_server.SessionHandler;

    /// <inheritdoc />
    public void Dispose() => _server.Dispose();

    /// <summary>
    /// Replays the FFmpeg fixture (H.264 Main with B-frames, AAC-LC stereo 44.1 kHz) and checks that every coded
    /// tag becomes a sample with identical bytes, the tag's timestamp and composition time, and the configuration
    /// and metadata from the stream; then checks cleanup on disconnect.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task FfmpegFixture_ProducesTypedSamples()
    {
        var tags = FlvFile.ReadFixture("h264-aac.flv");
        var videoTags = tags.Where(static t => t.Type == 9 && t.Body[1] == 1).ToList();
        var audioTags = tags.Where(static t => t.Type == 8 && t.Body[1] == 1).ToList();
        Assert.NotEmpty(videoTags);
        Assert.NotEmpty(audioTags);
        var (_, run, client) = _server.Start();
        var (streamId, _) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);

        await ReplayAsync(client, streamId, tags);

        await Eventually.WaitUntilAsync(() => _downstream.Video.Count == videoTags.Count && _downstream.Audio.Count == audioTags.Count, "all samples are delivered");
        Assert.Empty(_downstream.Errors);
        var video = _downstream.Video;
        for (var i = 0; i < videoTags.Count; i++)
        {
            var tag = videoTags[i];
            Assert.Equal(tag.Body[5..], video[i].Data);
            Assert.Equal(tag.Timestamp, video[i].Timestamps.SourceTimestamp);
            Assert.Equal(tag.Body[0] >> 4 == 1, video[i].IsKeyframe);
            Assert.Equal(((tag.Body[2] << 16) | (tag.Body[3] << 8) | tag.Body[4]) << 8 >> 8, video[i].Timestamps.CompositionTimeOffset);
            Assert.True(video[i].Timestamps.PresentationTimestamp >= video[i].Timestamps.DecodeTimestamp);
            Assert.Equal(i, video[i].SequenceNumber);
        }

        Assert.True(video[0].IsKeyframe);
        Assert.True(video[0].ContainsIdr);
        Assert.Contains(video, static v => v.Timestamps.CompositionTimeOffset > 0);
        Assert.Equal(audioTags.Select(static t => t.Body[2..]), _downstream.Audio.Select(static a => a.Data));

        var info = video[^1].Session.StreamInfo;
        Assert.Equal(0x4D, info.Video!.Configuration.Profile);
        Assert.Equal(4, info.Video.NalLengthSize);
        Assert.Equal(160, info.Video.Width);
        Assert.Equal(120, info.Video.Height);
        Assert.Equal(10, info.Video.FrameRate);
        Assert.Equal(2, info.Audio!.AudioObjectType);
        Assert.Equal(44100, info.Audio.SampleRate);
        Assert.Equal(2, info.Audio.ChannelCount);
        Assert.Equal(2, video[^1].Session.ConfigurationCount);

        await client.DisposeAsync();
        await run.WithinSafetyTimeout();
        await Eventually.WaitUntilAsync(() => _downstream.Events.Any(static e => e.Kind == "ended"), "the media session ends");
        Assert.Equal(0, Processor.ActiveSessionCount);
        Assert.Equal(0, _server.Pool.Outstanding);
        Assert.DoesNotContain(_server.Logs.GetSnapshot(), static r => r.Message.Contains(RtmpTestServer.StreamKey, StringComparison.Ordinal));
    }

    /// <summary>A publisher sending malformed media keeps publishing and does not affect another publisher.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task MalformedPublisher_IsIsolated()
    {
        var (_, badRun, bad) = _server.Start();
        var (_, goodRun, good) = _server.Start();
        var (badStream, _) = await bad.PublishFlowAsync(RtmpTestServer.StreamKey);
        var (goodStream, _) = await good.PublishFlowAsync("secret-key-0002");

        await bad.SendMessageAsync(6, badStream, RtmpMessageType.Video, 0, new byte[] { 0x17, 0x00, 0, 0, 0, 0x07 });
        await bad.SendMessageAsync(6, badStream, RtmpMessageType.Video, 0, new byte[] { 0x17, 0x01, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF });
        await bad.SendMessageAsync(4, badStream, RtmpMessageType.Audio, 0, new byte[] { 0x2F, 0x01 });
        await good.SendMessageAsync(6, goodStream, RtmpMessageType.Video, 0, FlvPackets.AvcSequenceHeader());
        await good.SendMessageAsync(6, goodStream, RtmpMessageType.Video, 0, FlvPackets.AvcNalu(true, 0, 4, [0x65, 0x88]));
        await bad.SendMessageAsync(6, badStream, RtmpMessageType.Video, 40, FlvPackets.AvcSequenceHeader());
        await bad.SendMessageAsync(6, badStream, RtmpMessageType.Video, 40, FlvPackets.AvcNalu(true, 0, 4, [0x65, 0x88]));

        await Eventually.WaitUntilAsync(() => _downstream.Video.Count == 2, "both publishers deliver a sample");
        var errors = _downstream.Events.OfType<ErrorEvent>().ToList();
        Assert.Equal([MediaErrorKind.InvalidConfiguration, MediaErrorKind.MissingConfiguration, MediaErrorKind.UnsupportedCodec], errors.Select(static e => e.Error.Kind));
        Assert.All(errors, static e => Assert.Equal("camera1", e.Session.Publisher.StreamIdentity));
        Assert.Equal(["camera1", "camera2"], _downstream.Video.Select(static v => v.Session.Publisher.StreamIdentity).Order(StringComparer.Ordinal));
        Assert.Equal(0, _downstream.Video.Single(static v => v.Session.Publisher.StreamIdentity == "camera2").Session.ErrorCount);
        Assert.Contains(_server.Logs.GetSnapshot(), static r => r.Id.Id == 6004 && r.Level == LogLevel.Warning);

        await bad.DisposeAsync();
        await good.DisposeAsync();
        await badRun.WithinSafetyTimeout();
        await goodRun.WithinSafetyTimeout();
        await Eventually.WaitUntilAsync(() => Processor.ActiveSessionCount == 0, "both media sessions are released");
        Assert.Equal(0, _server.Pool.Outstanding);
    }

    /// <summary>A blocked downstream handler stops further messages from being processed until it completes.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task BlockedDownstream_AppliesBackpressure()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _downstream.SampleGate = gate;
        var (_, run, client) = _server.Start();
        var (streamId, _) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);
        await client.SendMessageAsync(6, streamId, RtmpMessageType.Video, 0, FlvPackets.AvcSequenceHeader());
        await client.SendMessageAsync(4, streamId, RtmpMessageType.Audio, 0, FlvPackets.AacSequenceHeader());

        await client.SendMessageAsync(6, streamId, RtmpMessageType.Video, 0, FlvPackets.AvcNalu(true, 0, 4, [0x65, 0x88]));
        await client.SendMessageAsync(4, streamId, RtmpMessageType.Audio, 10, FlvPackets.AacRaw(0x21, 0x10));
        await client.SendMessageAsync(4, streamId, RtmpMessageType.Audio, 33, new byte[] { 0x2F, 0x01 });
        await _downstream.SampleBlocked.Task.WithinSafetyTimeout();

        var session = Assert.Single(_downstream.Events, static e => e.Kind == "started").Session;
        Assert.Equal(1, session.VideoSampleCount);
        Assert.Equal(0, session.AudioSampleCount);
        Assert.Empty(_downstream.Errors);
        Assert.Empty(_downstream.Video);

        gate.SetResult();
        await Eventually.WaitUntilAsync(() => _downstream.Errors.Count == 1, "processing resumes");
        Assert.Single(_downstream.Video);
        Assert.Single(_downstream.Audio);

        await client.DisposeAsync();
        await run.WithinSafetyTimeout();
        Assert.Equal(0, _server.Pool.Outstanding);
    }

    /// <summary>A downstream failure ends the session and closes the connection; nothing is leaked.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task DownstreamFailure_ClosesConnection()
    {
        _downstream.FailSamples = true;
        var (_, run, client) = _server.Start();
        var (streamId, _) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);
        await client.SendMessageAsync(4, streamId, RtmpMessageType.Audio, 0, FlvPackets.AacSequenceHeader());

        await client.SendMessageAsync(4, streamId, RtmpMessageType.Audio, 0, FlvPackets.AacRaw(0x21));

        await client.WaitForCloseAsync();
        await run.WithinSafetyTimeout();
        var ended = Assert.Single(_downstream.Events, static e => e.Kind == "ended");
        Assert.Equal(PublishEndReason.HandlerFailed, ended.Session.Publisher.EndReason);
        Assert.Equal(0, Processor.ActiveSessionCount);
        Assert.Equal(0, _server.Pool.Outstanding);
        await client.DisposeAsync();
    }

    /// <summary>Sends FLV tags as RTMP messages: script on chunk stream 4 as AMF0 data, audio on 4, video on 6.</summary>
    /// <param name="client">The client.</param>
    /// <param name="streamId">The message stream.</param>
    /// <param name="tags">The tags.</param>
    /// <returns>A task.</returns>
    internal static async Task ReplayAsync(RtmpTestClient client, uint streamId, IReadOnlyList<FlvTag> tags)
    {
        foreach (var tag in tags)
        {
            var (chunkStream, type) = tag.Type switch
            {
                8 => (4, RtmpMessageType.Audio),
                9 => (6, RtmpMessageType.Video),
                _ => (5, RtmpMessageType.DataAmf0),
            };
            await client.SendMessageAsync(chunkStream, streamId, type, tag.Timestamp, tag.Body);
        }
    }
}
