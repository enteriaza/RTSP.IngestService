// End-to-end HLS packaging through RtmpConnection over in-memory pipes: the checked-in fixture is published by the
// test client, processed by the Phase 3 media pipeline and packaged into the in-memory store; the live playlist,
// initialization segment and media segments are validated while publishing and after the publisher disconnects,
// and every pooled buffer and session is released. Needs no FFmpeg.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Media;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Tests.Media;

namespace RTSP.IngestService.Tests.Hls;

/// <summary>Integration tests of RTMP ingest with HLS packaging.</summary>
public sealed class HlsIntegrationTests : IDisposable
{
    /// <summary>The clock of the store.</summary>
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

    /// <summary>The server.</summary>
    private readonly RtmpTestServer _server;

    /// <summary>The store.</summary>
    private InMemoryHlsSegmentStore? _store;

    /// <summary>The packaging handler.</summary>
    private HlsPackagingHandler? _packaging;

    /// <summary>Initializes a new instance of the <see cref="HlsIntegrationTests"/> class.</summary>
    public HlsIntegrationTests()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new HlsOptions { TargetSegmentDuration = TimeSpan.FromSeconds(1), MaxSegmentDuration = TimeSpan.FromSeconds(2) });
        _server = new RtmpTestServer(sessionHandler: (meters, logs) =>
        {
            var metrics = new HlsMetrics(meters);
            _store = new InMemoryHlsSegmentStore(options, _time, metrics);
            _packaging = new HlsPackagingHandler(options, _store, metrics, logs.CreateLogger<HlsPackagingHandler>());
            return new MediaPublishSessionHandler(Microsoft.Extensions.Options.Options.Create(new MediaOptions()), _packaging, new MediaMetrics(meters), logs.CreateLogger<MediaPublishSessionHandler>());
        });
    }

    /// <inheritdoc />
    public void Dispose() => _server.Dispose();

    /// <summary>
    /// Publishes the fixture: completed segments appear in the live playlist without ENDLIST while publishing; after
    /// disconnect the final segment is added with ENDLIST, the presentation is complete and nothing leaks.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Fixture_IsPackagedLive()
    {
        var (_, run, client) = _server.Start();
        var (streamId, _) = await client.PublishFlowAsync(RtmpTestServer.StreamKey);
        await MediaIntegrationTests.ReplayAsync(client, streamId, HlsFixture.Tags);

        await Eventually.WaitUntilAsync(() => _store!.GetPlaylist("live", RtmpTestServer.StreamIdentity)?.Segments.Count == 3, "three segments are live");
        var live = _store!.GetPlaylist("live", RtmpTestServer.StreamIdentity)!;
        Assert.False(live.Ended);
        Assert.DoesNotContain("#EXT-X-ENDLIST", live.Text, StringComparison.Ordinal);
        Assert.StartsWith($"#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-MAP:URI=\"{_store.Generation}/init-0.mp4\"\n", live.Text, StringComparison.Ordinal);
        Assert.Equal(1, _packaging!.ActiveSessionCount);

        await client.DisposeAsync();
        await run.WithinSafetyTimeout();
        await Eventually.WaitUntilAsync(() => _packaging.ActiveSessionCount == 0, "the packaging session ends");

        var playlist = _store.GetPlaylist("live", RtmpTestServer.StreamIdentity)!;
        Assert.True(playlist.Ended);
        Assert.Equal(4, playlist.Segments.Count);
        var init = Mp4Parser.Parse(_store.ReadSegment("live", RtmpTestServer.StreamIdentity, "init-0.mp4")!);
        Assert.Equal(HlsFixture.AvcRecord, init.One("moov/trak/mdia/minf/stbl/stsd/avc1/avcC").Body);
        var videoBytes = HlsFixture.Tags.Where(static t => t.Type == 9 && t.Body[1] == 1).Select(static t => t.Body[5..]).ToList();
        var audioBytes = HlsFixture.Tags.Where(static t => t.Type == 8 && t.Body[1] == 1).Select(static t => t.Body[2..]).ToList();
        var packagedVideo = new List<byte[]>();
        var packagedAudio = new List<byte[]>();
        foreach (var entry in playlist.Segments)
        {
            var bytes = _store.ReadSegment("live", RtmpTestServer.StreamIdentity, entry.Name)!;
            foreach (var trackRun in Mp4Fragments.Read(bytes).Runs)
            {
                var offset = trackRun.DataOffset;
                foreach (var sample in trackRun.Samples)
                {
                    (trackRun.TrackId == 1 ? packagedVideo : packagedAudio).Add(bytes[offset..(offset + (int)sample.Size)]);
                    offset += (int)sample.Size;
                }
            }
        }

        Assert.Equal(videoBytes, packagedVideo);
        Assert.Equal(audioBytes, packagedAudio);
        Assert.Equal(0, _server.Pool.Outstanding);

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(_store.GetPlaylist("live", RtmpTestServer.StreamIdentity));
        Assert.NotNull(_store.GetSegment("live", RtmpTestServer.StreamIdentity, playlist.Segments[^1].Name));
        var deadline = playlist.Segments.Aggregate(TimeSpan.Zero, static (t, s) => t + s.Duration) + playlist.Segments[^1].Duration;
        _time.Advance(deadline - TimeSpan.FromTicks(1));
        Assert.NotNull(_store.GetSegment("live", RtmpTestServer.StreamIdentity, playlist.Segments[^1].Name));
        _time.Advance(TimeSpan.FromTicks(1));
        Assert.Null(_store.GetSegment("live", RtmpTestServer.StreamIdentity, playlist.Segments[^1].Name));
        Assert.Equal(0L, _store.TotalBytes);
    }
}
