// Integration tests of the HTTP HLS origin inside the real service host: the listener is bound before readiness,
// an HTTP bind failure fails startup, and live FFmpeg publishes over RTMP play back over HTTP with ffprobe and
// ffmpeg (playlist refresh while publishing, ENDLIST after the publish ends, stream parameters, frame counts,
// duration and an -xerror decode), with and without video. The FFmpeg tests are skipped without FFmpeg.

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Testing;
using RTSP.IngestService.Hosting;
using RTSP.IngestService.Http;
using RTSP.IngestService.Lifecycle;
using RTSP.IngestService.Rtmp;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Hls;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Http;

/// <summary>Integration tests for <see cref="HlsHttpServiceCollectionExtensions.AddHlsHttpOrigin"/> in the service host.</summary>
public sealed class HlsHttpPlaybackTests
{
    /// <summary>The configured test stream key.</summary>
    private const string StreamKey = "playback-key-0001";

    /// <summary>Captured host logs.</summary>
    private readonly FakeLogCollector _logs = new();

    /// <summary>
    /// Verifies the origin is bound before the host reports Running, serves 404 for unknown streams, never exposes the
    /// stream key, and stops with the host.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Host_BindsOriginBeforeReady_AndStopsIt()
    {
        using var host = TestHosts.CreateBuilder(_logs, overrides: Settings()).Build();
        var origin = host.Services.GetRequiredService<HlsHttpOrigin>();
        Assert.Null(origin.LocalEndPoint);

        await host.StartAsync(Token).WithinSafetyTimeout();

        Assert.True(host.Services.GetRequiredService<IApplicationLifecycle>().IsReady);
        Assert.NotEqual(0, origin.LocalEndPoint!.Port);
        Assert.Equal(IPAddress.Loopback, origin.LocalEndPoint.Address);
        var records = _logs.GetSnapshot().ToList();
        var listening = records.FindIndex(static r => r.Id.Id == 8000);
        var running = records.FindIndex(static r => r.Id.Id == 1000 && r.Message.EndsWith("to Running", StringComparison.Ordinal));
        Assert.InRange(listening, 0, running - 1);

        using var client = new HttpClient();
        using var missing = await client.GetAsync(BaseUri(origin, "index.m3u8"), Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var byKey = await client.GetAsync(new Uri($"http://127.0.0.1:{origin.LocalEndPoint.Port}/hls/live/{StreamKey}/index.m3u8"), Token);
        Assert.Equal(HttpStatusCode.NotFound, byKey.StatusCode);

        await host.StopAsync(Token).WithinSafetyTimeout();
        Assert.Contains(_logs.GetSnapshot(), static r => r.Id.Id == 8002);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(BaseUri(origin, "index.m3u8"), Token));
    }

    /// <summary>Verifies an HTTP port conflict fails startup with exit code 1, before readiness.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task HttpPortConflict_FailsStartup()
    {
        using var occupier = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        occupier.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        occupier.Listen();
        var overrides = Settings();
        overrides["Http:Port"] = ((IPEndPoint)occupier.LocalEndPoint!).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var exitCode = await ServiceRunner.RunAsync(TestHosts.Arguments, HostingMode.Console, TestHosts.Configure(_logs, overrides), Token).WithinSafetyTimeout();

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.DoesNotContain(_logs.GetSnapshot(), static r => r.Id.Id == 8000);
        Assert.DoesNotContain(_logs.GetSnapshot(), static r => r.Id.Id == 1000 && r.Message.EndsWith("to Running", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies a real-time FFmpeg publish (H.264 with B-frames, AAC) plays over HTTP: the playlist is live and grows
    /// while publishing, ends with ENDLIST afterwards, and ffprobe and ffmpeg read the HTTP presentation completely.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Ffmpeg_LivePublish_PlaysOverHttp()
    {
        var tools = HlsFfmpegInteropTests.Tools();
        using var host = TestHosts.CreateBuilder(_logs, overrides: Settings()).Build();
        await host.StartAsync(Token).WithinSafetyTimeout();
        try
        {
            var origin = host.Services.GetRequiredService<HlsHttpOrigin>();
            var publish = Publish(tools.Ffmpeg, host, video: true);
            using var client = new HttpClient();
            var playlistUri = BaseUri(origin, "index.m3u8");

            var live = await WaitForPlaylistAsync(client, playlistUri, static text => Segments(text) >= 2);
            Assert.DoesNotContain("#EXT-X-ENDLIST", live, StringComparison.Ordinal);
            var refreshed = await WaitForPlaylistAsync(client, playlistUri, text => Segments(text) > Segments(live));
            Assert.DoesNotContain("#EXT-X-ENDLIST", refreshed, StringComparison.Ordinal);
            var generation = host.Services.GetRequiredService<RTSP.IngestService.Hls.IHlsSegmentStore>().Generation;
            Assert.Contains($"#EXT-X-MAP:URI=\"{generation}/init-0.mp4\"", live, StringComparison.Ordinal);
            Assert.Contains($"\n{generation}/segment-0.m4s\n", live, StringComparison.Ordinal);
            using (var segment = await client.GetAsync(BaseUri(origin, $"{generation}/segment-0.m4s"), Token))
            {
                Assert.Equal(HttpStatusCode.OK, segment.StatusCode);
            }

            var exit = await publish;
            Assert.True(exit.Code == 0, $"FFmpeg exited with {exit.Code}: {exit.Errors}");
            var ended = await WaitForPlaylistAsync(client, playlistUri, static text => text.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal));
            Assert.Equal(6, Segments(ended));

            var probe = await HlsFfmpegInteropTests.ProbeAsync(tools.Probe, playlistUri.AbsoluteUri);
            var videoStream = probe.Streams.Single(static s => s.CodecType == "video");
            var audioStream = probe.Streams.Single(static s => s.CodecType == "audio");
            Assert.Equal(("h264", 320, 240), (videoStream.CodecName, videoStream.Width, videoStream.Height));
            Assert.Equal(("aac", "48000", 2), (audioStream.CodecName, audioStream.SampleRate, audioStream.Channels));
            Assert.Equal(150, videoStream.Frames);
            Assert.InRange(audioStream.Frames, 279, 283);
            Assert.Equal(Duration(ended), probe.Duration, 0.0015);
            await HlsFfmpegInteropTests.DecodeAsync(tools.Ffmpeg, playlistUri.AbsoluteUri);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Verifies an audio-only FFmpeg publish plays over HTTP.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Ffmpeg_AudioOnlyPublish_PlaysOverHttp()
    {
        var tools = HlsFfmpegInteropTests.Tools();
        using var host = TestHosts.CreateBuilder(_logs, overrides: Settings()).Build();
        await host.StartAsync(Token).WithinSafetyTimeout();
        try
        {
            var origin = host.Services.GetRequiredService<HlsHttpOrigin>();
            var exit = await Publish(tools.Ffmpeg, host, video: false);
            Assert.True(exit.Code == 0, $"FFmpeg exited with {exit.Code}: {exit.Errors}");
            using var client = new HttpClient();
            var playlistUri = BaseUri(origin, "index.m3u8");
            var ended = await WaitForPlaylistAsync(client, playlistUri, static text => text.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal));

            var probe = await HlsFfmpegInteropTests.ProbeAsync(tools.Probe, playlistUri.AbsoluteUri);
            var audioStream = Assert.Single(probe.Streams);
            Assert.Equal(("audio", "aac", "48000", 2), (audioStream.CodecType, audioStream.CodecName, audioStream.SampleRate, audioStream.Channels));
            Assert.InRange(audioStream.Frames, 279, 283);
            Assert.Equal(Duration(ended), probe.Duration, 0.0015);
            await HlsFfmpegInteropTests.DecodeAsync(tools.Ffmpeg, playlistUri.AbsoluteUri);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Gets the test cancellation token.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Host settings: one publisher <c>camera</c>, 1 s segments and a window holding the whole test stream.</summary>
    /// <returns>The overrides.</returns>
    private static Dictionary<string, string?> Settings() => new()
    {
        ["Rtmp:Authentication:Publishers:0:Name"] = "camera",
        ["Rtmp:Authentication:Publishers:0:Application"] = "live",
        ["Rtmp:Authentication:Publishers:0:KeyHash"] = PublisherKeyHash.Create(StreamKey),
        ["Hls:TargetSegmentDuration"] = "00:00:01",
        ["Hls:MaxSegmentDuration"] = "00:00:04",
        ["Hls:PlaylistWindowSize"] = "10",
    };

    /// <summary>Returns the URL of a resource of <c>live/camera</c>.</summary>
    /// <param name="origin">The started origin.</param>
    /// <param name="file">The file name.</param>
    /// <returns>The URL.</returns>
    private static Uri BaseUri(HlsHttpOrigin origin, string file) => new($"http://127.0.0.1:{origin.LocalEndPoint!.Port}/hls/live/camera/{file}");

    /// <summary>Counts the media segments a playlist lists.</summary>
    /// <param name="playlist">The playlist text.</param>
    /// <returns>The number of <c>#EXTINF</c> tags.</returns>
    private static int Segments(string playlist) => playlist.Split('\n').Count(static line => line.StartsWith("#EXTINF:", StringComparison.Ordinal));

    /// <summary>Sums the <c>#EXTINF</c> durations of a playlist.</summary>
    /// <param name="playlist">The playlist text.</param>
    /// <returns>The duration in seconds.</returns>
    private static double Duration(string playlist) => playlist.Split('\n')
        .Where(static line => line.StartsWith("#EXTINF:", StringComparison.Ordinal))
        .Sum(static line => double.Parse(line["#EXTINF:".Length..].TrimEnd(',', '\r'), System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Publishes 6 s in real time (25 fps video with a keyframe per second, and 48 kHz AAC) to <c>live</c>.</summary>
    /// <param name="ffmpeg">ffmpeg.</param>
    /// <param name="host">The started host.</param>
    /// <param name="video">Whether to include video.</param>
    /// <returns>The FFmpeg result.</returns>
    private static Task<(int Code, string Output, string Errors)> Publish(string ffmpeg, IHost host, bool video)
    {
        var port = ((IPEndPoint)host.Services.GetRequiredService<RtmpListener>().LocalEndPoint!).Port;
        List<string> arguments = ["-hide_banner", "-loglevel", "error", "-nostdin"];
        if (video)
        {
            arguments.AddRange(["-re", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25"]);
        }

        arguments.AddRange(["-re", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "6"]);
        if (video)
        {
            arguments.AddRange(["-c:v", "libx264", "-profile:v", "high", "-bf", "2", "-g", "25", "-keyint_min", "25", "-sc_threshold", "0", "-pix_fmt", "yuv420p"]);
        }

        arguments.AddRange(["-c:a", "aac", "-ac", "2", "-b:a", "96k", "-f", "flv", $"rtmp://127.0.0.1:{port}/live/{StreamKey}"]);
        return HlsFfmpegInteropTests.RunAsync(ffmpeg, [.. arguments]);
    }

    /// <summary>Polls the playlist over HTTP until it satisfies <paramref name="condition"/>.</summary>
    /// <param name="client">The client.</param>
    /// <param name="uri">The playlist URL.</param>
    /// <param name="condition">The condition on the playlist text.</param>
    /// <returns>The first playlist text that satisfies it.</returns>
    private static async Task<string> WaitForPlaylistAsync(HttpClient client, Uri uri, Func<string, bool> condition)
    {
        string? text = null;
        await Eventually.WaitUntilAsync(
            () =>
            {
                using var response = client.GetAsync(uri, Token).GetAwaiter().GetResult();
                text = response.StatusCode == HttpStatusCode.OK ? response.Content.ReadAsStringAsync(Token).GetAwaiter().GetResult() : null;
                return text is not null && condition(text);
            },
            "the HTTP playlist reaches the expected state");
        return text!;
    }
}
