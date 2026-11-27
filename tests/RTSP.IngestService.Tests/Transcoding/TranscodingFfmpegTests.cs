// End-to-end codec tests with FFmpeg as a third-party RTMP publisher (legacy FLV and Enhanced RTMP) to a service host,
// which transcodes with its managed pipeline only. For each H.264 and AAC variant the pipeline accepts, the HLS
// presentation served over HTTP is verified with ffprobe to be the canonical profile (H.264 High yuv420p 8-bit BT.709
// limited range; AAC-LC 48 kHz stereo), decoded in full with -xerror, and decoded independently with the managed
// H.264 decoder; every codec or format it does not accept (10-bit or 4:2:2 H.264, HEVC, AV1, VP9, MP3, Opus, FLAC,
// AC-3, E-AC-3) ends the publish with a rejection and packages nothing. Fixtures are a few seconds of generated media.
// The tests need FFmpeg and ffprobe as test tools and skip without them unless RTSP_FFMPEG_REQUIRED=true.

using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Testing;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Http;
using RTSP.IngestService.Rtmp;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Hls;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Transcoding;

/// <summary>Real-FFmpeg-publisher tests of source codecs through managed transcoding to HLS.</summary>
public sealed class TranscodingFfmpegTests
{
    /// <summary>The publisher stream key.</summary>
    internal const string StreamKey = "transcoding-key-0001";

    /// <summary>Seconds of media each fixture publishes.</summary>
    private const int Seconds = 3;

    /// <summary>Generated video.</summary>
    private static readonly string[] VideoInput = ["-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25"];

    /// <summary>Generated audio.</summary>
    private static readonly string[] AudioInput = ["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000"];

    /// <summary>Captured host logs.</summary>
    private readonly FakeLogCollector _logs = new();

    /// <summary>Gets the H.264 sources the managed decoder accepts: name and FFmpeg encoder arguments.</summary>
    public static TheoryData<string, string[]> VideoCodecs => new()
    {
        { "h264-high-bframes", ["-c:v", "libx264", "-profile:v", "high", "-bf", "2", "-g", "25", "-pix_fmt", "yuv420p"] },
        { "h264-main", ["-c:v", "libx264", "-profile:v", "main", "-g", "25", "-pix_fmt", "yuv420p"] },
        { "h264-baseline", ["-c:v", "libx264", "-profile:v", "baseline", "-g", "25", "-pix_fmt", "yuv420p"] },
        { "h264-bt601", ["-c:v", "libx264", "-g", "25", "-pix_fmt", "yuv420p", "-color_primaries", "smpte170m", "-color_trc", "smpte170m", "-colorspace", "smpte170m"] },
    };

    /// <summary>Gets the AAC sources the managed decoder accepts: name and FFmpeg encoder arguments.</summary>
    public static TheoryData<string, string[]> AudioCodecs => new()
    {
        { "aac-44k-mono", ["-c:a", "aac", "-ar", "44100", "-ac", "1", "-b:a", "96k"] },
        { "aac-5.1", ["-c:a", "aac", "-ac", "6", "-b:a", "256k"] },
    };

    /// <summary>Gets the sources the managed pipeline does not transcode: name and full FFmpeg encoder arguments.</summary>
    public static TheoryData<string, string[]> UnsupportedCodecs => new()
    {
        { "h264-10bit", ["-c:v", "libx264", "-profile:v", "high10", "-g", "25", "-pix_fmt", "yuv420p10le", "-c:a", "aac"] },
        { "h264-422", ["-c:v", "libx264", "-profile:v", "high422", "-g", "25", "-pix_fmt", "yuv422p", "-c:a", "aac"] },
        { "hevc", ["-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error:keyint=25", "-pix_fmt", "yuv420p", "-c:a", "aac"] },
        { "av1", ["-c:v", "libsvtav1", "-preset", "12", "-g", "25", "-pix_fmt", "yuv420p", "-svtav1-params", "lp=2", "-c:a", "aac"] },
        { "vp9", ["-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-g", "25", "-pix_fmt", "yuv420p", "-c:a", "aac"] },
        { "mp3", ["-c:v", "libx264", "-g", "25", "-pix_fmt", "yuv420p", "-c:a", "libmp3lame", "-ar", "44100", "-ac", "2", "-b:a", "128k"] },
        { "opus", ["-c:v", "libx264", "-g", "25", "-pix_fmt", "yuv420p", "-c:a", "libopus", "-ac", "2", "-b:a", "96k"] },
        { "flac", ["-c:v", "libx264", "-g", "25", "-pix_fmt", "yuv420p", "-c:a", "flac", "-ac", "2"] },
        { "ac3-5.1", ["-c:v", "libx264", "-g", "25", "-pix_fmt", "yuv420p", "-c:a", "ac3", "-ac", "6", "-b:a", "384k"] },
        { "eac3-5.1", ["-c:v", "libx264", "-g", "25", "-pix_fmt", "yuv420p", "-c:a", "eac3", "-ac", "6", "-b:a", "384k"] },
    };

    /// <summary>Gets the test cancellation token.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Each accepted H.264 source, with AAC audio, becomes canonical H.264 with every frame preserved.</summary>
    /// <param name="name">The fixture name.</param>
    /// <param name="encoder">The FFmpeg encoder arguments.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [MemberData(nameof(VideoCodecs))]
    public async Task VideoCodec_IsTranscodedToCanonicalHls(string name, string[] encoder)
    {
        var tools = HlsFfmpegInteropTests.Tools();
        await using var run = await PublishAsync(tools.Ffmpeg, [.. VideoInput, .. AudioInput, "-t", Seconds.ToString(CultureInfo.InvariantCulture), .. encoder, "-c:a", "aac", "-ac", "2", "-b:a", "96k"]);
        var probe = await ProbeCanonicalAsync(tools.Probe, run.PlaylistUri);
        AssertCanonicalVideo(probe.Video, name);
        Assert.Equal(Seconds * 25, probe.Video!.Frames);
        AssertCanonicalAudio(probe.Audio, name);
        await HlsFfmpegInteropTests.DecodeAsync(tools.Ffmpeg, run.PlaylistUri.AbsoluteUri);
        Assert.Equal(Seconds * 25, run.DecodeManaged().Sum(static p => p.Pictures.Count));
    }

    /// <summary>Each accepted AAC source, with H.264 video, becomes canonical AAC-LC 48 kHz stereo.</summary>
    /// <param name="name">The fixture name.</param>
    /// <param name="encoder">The FFmpeg encoder arguments.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [MemberData(nameof(AudioCodecs))]
    public async Task AudioCodec_IsTranscodedToCanonicalHls(string name, string[] encoder)
    {
        var tools = HlsFfmpegInteropTests.Tools();
        await using var run = await PublishAsync(tools.Ffmpeg, [.. VideoInput, .. AudioInput, "-t", Seconds.ToString(CultureInfo.InvariantCulture), "-c:v", "libx264", "-g", "25", "-pix_fmt", "yuv420p", .. encoder]);
        var probe = await ProbeCanonicalAsync(tools.Probe, run.PlaylistUri);
        AssertCanonicalVideo(probe.Video, name);
        AssertCanonicalAudio(probe.Audio, name);
        var seconds = probe.Audio!.Frames * 1024 / 48000.0;
        Assert.InRange(seconds, Seconds - 0.25, Seconds + 0.25);
        await HlsFfmpegInteropTests.DecodeAsync(tools.Ffmpeg, run.PlaylistUri.AbsoluteUri);
        Assert.Equal(probe.Audio.Frames, run.DecodeManaged().Sum(static p => p.AudioStarts.Count));
    }

    /// <summary>
    /// A source the managed pipeline does not transcode ends the publish at its configuration with a rejection, and
    /// nothing is packaged; there is no fallback.
    /// </summary>
    /// <param name="name">The fixture name.</param>
    /// <param name="encoder">The FFmpeg encoder arguments.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [MemberData(nameof(UnsupportedCodecs))]
    public async Task UnsupportedCodec_IsRejected(string name, string[] encoder)
    {
        var tools = HlsFfmpegInteropTests.Tools();
        using var host = TestHosts.CreateBuilder(_logs, overrides: Settings()).Build();
        await host.StartAsync(Token).WithinSafetyTimeout();
        try
        {
            var port = ((IPEndPoint)host.Services.GetRequiredService<RtmpListener>().LocalEndPoint!).Port;
            await HlsFfmpegInteropTests.RunAsync(tools.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", .. VideoInput, .. AudioInput, "-t", Seconds.ToString(CultureInfo.InvariantCulture), .. encoder, "-f", "flv", $"rtmp://127.0.0.1:{port}/live/{StreamKey}"]);
            await Eventually.WaitUntilAsync(() => _logs.GetSnapshot().Any(static r => r.Id.Id == 10011), $"{name} is rejected");
            Assert.True(host.Services.GetRequiredService<IHlsSegmentStore>().GetPlaylist("live", "camera") is null or { Segments.Count: 0 }, $"{name}: nothing is packaged.");
            Assert.DoesNotContain(_logs.GetSnapshot(), static r => r.Id.Id == 10000);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>A stream without audio produces canonical video only; one without video produces canonical audio only.</summary>
    /// <param name="video">Whether the stream has video (otherwise it has audio only).</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingTrack_ProducesTheOtherTrackOnly(bool video)
    {
        var tools = HlsFfmpegInteropTests.Tools();
        string[] arguments = video
            ? [.. VideoInput, "-t", "3", "-c:v", "libx264", "-g", "25", "-pix_fmt", "yuv420p"]
            : [.. AudioInput, "-t", "3", "-c:a", "aac", "-b:a", "96k"];
        await using var run = await PublishAsync(tools.Ffmpeg, arguments);
        var probe = await ProbeCanonicalAsync(tools.Probe, run.PlaylistUri);
        if (video)
        {
            AssertCanonicalVideo(probe.Video, "h264 video-only");
            Assert.Null(probe.Audio);
        }
        else
        {
            AssertCanonicalAudio(probe.Audio, "aac audio-only");
            Assert.Null(probe.Video);
        }

        await HlsFfmpegInteropTests.DecodeAsync(tools.Ffmpeg, run.PlaylistUri.AbsoluteUri);
    }

    /// <summary>Variable frame rate input keeps its frame timing: every frame survives and none is duplicated.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task VariableFrameRate_IsPreserved()
    {
        var tools = HlsFfmpegInteropTests.Tools();
        await using var run = await PublishAsync(tools.Ffmpeg, [.. VideoInput, .. AudioInput, "-t", "3", "-vf", "select='lt(n\\,25)+not(mod(n\\,2))'", "-fps_mode", "vfr", "-c:v", "libx264", "-g", "25", "-pix_fmt", "yuv420p", "-c:a", "aac"]);
        var probe = await ProbeCanonicalAsync(tools.Probe, run.PlaylistUri);
        AssertCanonicalVideo(probe.Video, "vfr");
        var source = run.SourceVideoFrames;
        Assert.Equal(source, probe.Video!.Frames);
        await HlsFfmpegInteropTests.DecodeAsync(tools.Ffmpeg, run.PlaylistUri.AbsoluteUri);
        Assert.Equal(source, run.DecodeManaged().Sum(static p => p.Pictures.Count));
    }
    /// <summary>Asserts the canonical video profile.</summary>
    /// <param name="video">The probed stream.</param>
    /// <param name="name">The fixture, for messages.</param>
    private static void AssertCanonicalVideo(CanonicalStream? video, string name)
    {
        Assert.NotNull(video);
        Assert.True(
            video is { CodecName: "h264", Profile: "High", PixelFormat: "yuv420p", Width: 320, Height: 240, ColorRange: "tv", ColorSpace: "bt709", ColorTransfer: "bt709", ColorPrimaries: "bt709" },
            $"{name}: {video}");
    }

    /// <summary>Asserts the canonical audio profile.</summary>
    /// <param name="audio">The probed stream.</param>
    /// <param name="name">The fixture, for messages.</param>
    private static void AssertCanonicalAudio(CanonicalStream? audio, string name)
    {
        Assert.NotNull(audio);
        Assert.True(audio is { CodecName: "aac", Profile: "LC", SampleRate: "48000", Channels: 2 }, $"{name}: {audio}");
    }

    /// <summary>Starts a host, publishes with FFmpeg until it exits and waits for the ended playlist.</summary>
    /// <param name="ffmpeg">ffmpeg.</param>
    /// <param name="arguments">Input and encoder arguments (the output is appended).</param>
    /// <returns>The run, which owns the host.</returns>
    private async Task<PublishRun> PublishAsync(string ffmpeg, string[] arguments)
    {
        var host = TestHosts.CreateBuilder(_logs, overrides: Settings()).Build();
        await host.StartAsync(Token).WithinSafetyTimeout();
        try
        {
            var port = ((IPEndPoint)host.Services.GetRequiredService<RtmpListener>().LocalEndPoint!).Port;
            var publish = await HlsFfmpegInteropTests.RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", .. arguments, "-f", "flv", $"rtmp://127.0.0.1:{port}/live/{StreamKey}"]);
            Assert.True(publish.Code == 0, $"FFmpeg exited with {publish.Code}: {publish.Errors}");
            var origin = host.Services.GetRequiredService<HlsHttpOrigin>();
            var playlist = new Uri($"http://127.0.0.1:{origin.LocalEndPoint!.Port}/hls/live/camera/index.m3u8");
            using var client = new HttpClient();
            await Eventually.WaitUntilAsync(
                () =>
                {
                    using var response = client.GetAsync(playlist, Token).GetAwaiter().GetResult();
                    return response.IsSuccessStatusCode && response.Content.ReadAsStringAsync(Token).GetAwaiter().GetResult().Contains("#EXT-X-ENDLIST", StringComparison.Ordinal);
                },
                "the transcoded playlist ends");
            var problems = _logs.GetSnapshot().Where(static r => r.Id.Id is 10001 or 10003 or 10011 or 10012).Select(static r => $"{r.Id.Id}: {r.Message}").ToList();
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
            var sourceFrames = arguments.Contains("-vf") ? await CountSourceFramesAsync(ffmpeg, arguments) : 0;
            return new PublishRun(host, playlist, sourceFrames);
        }
        catch
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
            throw;
        }
    }

    /// <summary>Counts the video frames the same arguments produce in a file.</summary>
    /// <param name="ffmpeg">ffmpeg.</param>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The frame count.</returns>
    private static async Task<int> CountSourceFramesAsync(string ffmpeg, string[] arguments)
    {
        var file = Path.Combine(Path.GetTempPath(), $"rtsp-vfr-{Guid.NewGuid():N}.flv");
        try
        {
            var result = await HlsFfmpegInteropTests.RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", .. arguments, "-f", "flv", file]);
            Assert.Equal(0, result.Code);
            var probe = await HlsFfmpegInteropTests.ProbeAsync(Path.Combine(Path.GetDirectoryName(ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe"), file);
            return probe.Streams.Single(static s => s.CodecType == "video").Frames;
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Probes the presentation's canonical stream properties.</summary>
    /// <param name="probe">ffprobe.</param>
    /// <param name="playlist">The playlist URL.</param>
    /// <returns>The video and audio streams.</returns>
    internal static async Task<(CanonicalStream? Video, CanonicalStream? Audio)> ProbeCanonicalAsync(string probe, Uri playlist)
    {
        var result = await HlsFfmpegInteropTests.RunAsync(probe, "-v", "error", "-count_frames", "-show_entries", "stream=codec_type,codec_name,profile,pix_fmt,width,height,color_range,color_space,color_transfer,color_primaries,sample_rate,channels,nb_read_frames", "-of", "json", playlist.AbsoluteUri);
        Assert.True(result.Code == 0 && result.Errors.Length == 0, $"ffprobe exited with {result.Code}: {result.Errors}");
        using var json = JsonDocument.Parse(result.Output);
        CanonicalStream? video = null;
        CanonicalStream? audio = null;
        foreach (var s in json.RootElement.GetProperty("streams").EnumerateArray())
        {
            string? Text(string property) => s.TryGetProperty(property, out var value) ? value.GetString() : null;
            int Number(string property) => s.TryGetProperty(property, out var value) ? value.GetInt32() : 0;
            var stream = new CanonicalStream(
                Text("codec_name")!,
                Text("profile"),
                Text("pix_fmt"),
                Number("width"),
                Number("height"),
                Text("color_range"),
                Text("color_space"),
                Text("color_transfer"),
                Text("color_primaries"),
                Text("sample_rate"),
                Number("channels"),
                int.Parse(Text("nb_read_frames") ?? "0", CultureInfo.InvariantCulture));
            if (Text("codec_type") == "video")
            {
                video = stream;
            }
            else
            {
                audio = stream;
            }
        }

        return (video, audio);
    }

    /// <summary>
    /// Host settings: publisher <c>camera</c>, 1 s segments, a window holding the whole stream, backpressure so a
    /// faster-than-real-time publisher loses nothing, and the longest drain timeout so its queued video is encoded.
    /// </summary>
    /// <returns>The overrides.</returns>
    internal static Dictionary<string, string?> Settings() => new()
    {
        ["Rtmp:Authentication:Publishers:0:Name"] = "camera",
        ["Rtmp:Authentication:Publishers:0:Application"] = "live",
        ["Rtmp:Authentication:Publishers:0:KeyHash"] = PublisherKeyHash.Create(StreamKey),
        ["Hls:TargetSegmentDuration"] = "00:00:01",
        ["Hls:MaxSegmentDuration"] = "00:00:04",
        ["Hls:PlaylistWindowSize"] = "20",
        ["Transcoding:OverloadPolicy"] = "Backpressure",
        ["Transcoding:DrainTimeout"] = "00:01:00",
    };

    /// <summary>Probed properties of a canonical stream.</summary>
    /// <param name="CodecName">The codec.</param>
    /// <param name="Profile">The profile.</param>
    /// <param name="PixelFormat">The pixel format.</param>
    /// <param name="Width">Width.</param>
    /// <param name="Height">Height.</param>
    /// <param name="ColorRange">Colour range.</param>
    /// <param name="ColorSpace">Matrix coefficients.</param>
    /// <param name="ColorTransfer">Transfer characteristics.</param>
    /// <param name="ColorPrimaries">Colour primaries.</param>
    /// <param name="SampleRate">Sample rate.</param>
    /// <param name="Channels">Channels.</param>
    /// <param name="Frames">Frames read.</param>
    internal sealed record CanonicalStream(string CodecName, string? Profile, string? PixelFormat, int Width, int Height, string? ColorRange, string? ColorSpace, string? ColorTransfer, string? ColorPrimaries, string? SampleRate, int Channels, int Frames);

    /// <summary>A finished publish; disposing stops the host.</summary>
    /// <param name="Host">The host.</param>
    /// <param name="PlaylistUri">The playlist URL.</param>
    /// <param name="SourceVideoFrames">Source video frames, when counted.</param>
    private sealed record PublishRun(IHost Host, Uri PlaylistUri, int SourceVideoFrames) : IAsyncDisposable
    {
        /// <summary>Decodes the stored presentation with the managed decoders.</summary>
        /// <returns>Its periods.</returns>
        public List<HlsPeriod> DecodeManaged()
        {
            var store = Host.Services.GetRequiredService<IHlsSegmentStore>();
            var playlist = store.GetPlaylist("live", "camera");
            Assert.NotNull(playlist);
            return HlsOutputDecoder.Decode(playlist, name => store.ReadSegment("live", "camera", name) ?? throw new InvalidOperationException($"{name} is not stored."));
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await Host.StopAsync(CancellationToken.None);
            Host.Dispose();
        }
    }
}
