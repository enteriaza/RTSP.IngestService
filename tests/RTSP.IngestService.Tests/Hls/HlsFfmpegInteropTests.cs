// Optional interoperability tests: packaged HLS output is written from the segment store to a temporary directory and
// validated by ffprobe/ffmpeg (stream parameters, frame counts, timing and a full decode). One test replays the
// checked-in fixture; the other publishes live with a real FFmpeg client over RTMP. Both are skipped without FFmpeg.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Media;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Tests.Rtmp;

namespace RTSP.IngestService.Tests.Hls;

/// <summary>
/// Validates packaged HLS with ffprobe and ffmpeg.
/// </summary>
public sealed class HlsFfmpegInteropTests
{
    /// <summary>Verifies that the replayed fixture packages into an HLS presentation ffprobe reads and ffmpeg decodes.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Fixture_PackagesIntoValidHls()
    {
        var tools = Tools();
        using var harness = new HlsHarness();
        var session = await harness.StartAsync();
        await harness.ReplayAsync(session, HlsFixture.Tags);
        await harness.EndAsync(session);

        var playlist = harness.Playlist()!;
        Assert.True(playlist.Ended);
        Assert.Equal(4, playlist.Segments.Count);
        using var directory = new TemporaryDirectory();
        var path = directory.Write(harness.Store, "live", "camera1", playlist);

        var probe = await ProbeAsync(tools.Probe, path);
        var video = probe.Streams.Single(static s => s.CodecType == "video");
        var audio = probe.Streams.Single(static s => s.CodecType == "audio");
        Assert.Equal(("h264", 160, 120), (video.CodecName, video.Width, video.Height));
        Assert.Equal(("aac", "44100", 2), (audio.CodecName, audio.SampleRate, audio.Channels));
        Assert.Equal(40, video.Frames);
        Assert.InRange(audio.Frames, 170, 175);
        Assert.Equal(playlist.Segments.Sum(static s => s.Duration.TotalSeconds), probe.Duration, 0.0015);
        await AssertTimingAsync(tools.Probe, path, harness.Store, "camera1", playlist, 44_100);
        await DecodeAsync(tools.Ffmpeg, path);
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>
    /// Verifies a live FFmpeg publish (H.264 high profile with B-frames, AAC 48 kHz) is packaged by the RTMP host into
    /// keyframe-aligned segments that reconstruct into a valid presentation.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Ffmpeg_LivePublish_PackagesIntoValidHls()
    {
        var tools = Tools();
        var options = new HlsOptions { TargetSegmentDuration = TimeSpan.FromSeconds(1), MaxSegmentDuration = TimeSpan.FromSeconds(2) };
        InMemoryHlsSegmentStore? store = null;
        HlsPackagingHandler? packaging = null;
        using var server = new RtmpTestServer(
            new RtmpOptions { ListenAddress = "127.0.0.1", Port = 0 },
            sessionHandler: (meters, logs) =>
            {
                var metrics = new HlsMetrics(meters);
                store = new InMemoryHlsSegmentStore(Microsoft.Extensions.Options.Options.Create(options), TimeProvider.System, metrics);
                packaging = new HlsPackagingHandler(Microsoft.Extensions.Options.Options.Create(options), store, metrics, logs.CreateLogger<HlsPackagingHandler>());
                return new MediaPublishSessionHandler(
                    Microsoft.Extensions.Options.Options.Create(new MediaOptions()),
                    packaging,
                    new MediaMetrics(meters),
                    logs.CreateLogger<MediaPublishSessionHandler>());
            });
        using var listener = server.CreateListener();
        await listener.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var port = ((IPEndPoint)listener.LocalEndPoint!).Port;
            var exit = await RunAsync(
                tools.Ffmpeg,
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                "-t", "4", "-c:v", "libx264", "-profile:v", "high", "-bf", "2", "-g", "25", "-keyint_min", "25", "-sc_threshold", "0", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-ac", "2", "-b:a", "96k",
                "-f", "flv", $"rtmp://127.0.0.1:{port}/live/{RtmpTestServer.StreamKey}");
            Assert.True(exit.Code == 0, $"FFmpeg exited with {exit.Code}: {exit.Errors}");

            await Eventually.WaitUntilAsync(() => packaging!.ActiveSessionCount == 0 && store!.GetPlaylist("live", RtmpTestServer.StreamIdentity) is { Ended: true }, "the HLS stream ends");
            var playlist = store!.GetPlaylist("live", RtmpTestServer.StreamIdentity)!;
            Assert.Equal(4, playlist.Segments.Count);
            Assert.All(playlist.Segments.Take(3), static s => Assert.Equal(1.0, s.Duration.TotalSeconds));
            using var directory = new TemporaryDirectory();
            var path = directory.Write(store, "live", RtmpTestServer.StreamIdentity, playlist);

            var probe = await ProbeAsync(tools.Probe, path);
            var video = probe.Streams.Single(static s => s.CodecType == "video");
            var audio = probe.Streams.Single(static s => s.CodecType == "audio");
            Assert.Equal(("h264", 320, 240), (video.CodecName, video.Width, video.Height));
            Assert.Equal(("aac", "48000", 2), (audio.CodecName, audio.SampleRate, audio.Channels));
            Assert.Equal(100, video.Frames);
            Assert.InRange(audio.Frames, 185, 190);
            Assert.Equal(playlist.Segments.Sum(static s => s.Duration.TotalSeconds), probe.Duration, 0.0015);
            await AssertTimingAsync(tools.Probe, path, store, RtmpTestServer.StreamIdentity, playlist, 48_000);
            await DecodeAsync(tools.Ffmpeg, path);
            Assert.Equal(0, server.Pool.Outstanding);
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Finds ffmpeg and ffprobe or skips the test.</summary>
    /// <returns>The executables.</returns>
    internal static (string Ffmpeg, string Probe) Tools()
    {
        var ffmpeg = FfmpegInteropTests.FindFfmpeg();
        var probe = ffmpeg is null ? null : Path.Combine(Path.GetDirectoryName(ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        if (ffmpeg is null || !File.Exists(probe))
        {
            Assert.Skip("FFmpeg and ffprobe were not found; set RTSP_FFMPEG_PATH or add ffmpeg to PATH to run this test.");
        }

        return (ffmpeg, probe);
    }

    /// <summary>Runs ffprobe over the playlist, counting frames.</summary>
    /// <param name="probe">ffprobe.</param>
    /// <param name="path">The playlist path.</param>
    /// <returns>The result.</returns>
    internal static async Task<ProbeResult> ProbeAsync(string probe, string path)
    {
        var result = await RunAsync(probe, "-v", "error", "-count_frames", "-show_entries", "stream=codec_type,codec_name,width,height,sample_rate,channels,nb_read_frames:format=duration", "-of", "json", path);
        Assert.True(result.Code == 0 && result.Errors.Length == 0, $"ffprobe exited with {result.Code}: {result.Errors}");
        using var json = JsonDocument.Parse(result.Output);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().Select(static s => new ProbeStream(
            s.GetProperty("codec_type").GetString()!,
            s.GetProperty("codec_name").GetString()!,
            s.TryGetProperty("width", out var w) ? w.GetInt32() : 0,
            s.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
            s.TryGetProperty("sample_rate", out var r) ? r.GetString() : null,
            s.TryGetProperty("channels", out var c) ? c.GetInt32() : 0,
            int.Parse(s.GetProperty("nb_read_frames").GetString()!, CultureInfo.InvariantCulture))).ToList();
        var duration = double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        return new ProbeResult(streams, duration);
    }

    /// <summary>
    /// Verifies the presentation's timing, not just that it decodes. The packaged MP4 timeline (tfdt plus trun
    /// durations and composition offsets) must be contiguous on both tracks, with one AAC frame per audio sample; the
    /// EXTINF values must add up to it exactly; ffprobe's demuxed packets must carry exactly the packaged timestamps and
    /// durations; and the decoded frames must have the packaged presentation times, with every packaged AAC frame
    /// decoding to its full frame of samples, so decoded audio duration and the A/V offset match the packaging.
    /// </summary>
    /// <param name="probe">ffprobe.</param>
    /// <param name="path">The playlist path.</param>
    /// <param name="store">The store.</param>
    /// <param name="identity">The stream identity.</param>
    /// <param name="playlist">The playlist.</param>
    /// <param name="sampleRate">The AAC sample rate.</param>
    /// <returns>A task.</returns>
    private static async Task AssertTimingAsync(string probe, string path, InMemoryHlsSegmentStore store, string identity, HlsPlaylistSnapshot playlist, int sampleRate)
    {
        var video = new List<(long Dts, long Pts, long Duration)>();
        var audio = new List<(long Start, long Duration)>();
        foreach (var segment in playlist.Segments)
        {
            foreach (var run in Mp4Fragments.Read(store.GetSegment("live", identity, segment.Name)!.Data.ToArray()).Runs)
            {
                var time = (long)run.BaseDecodeTime;
                foreach (var sample in run.Samples)
                {
                    if (run.TrackId == 1)
                    {
                        video.Add((time, time + (sample.CompositionOffset ?? 0), sample.Duration));
                    }
                    else
                    {
                        audio.Add((time, sample.Duration));
                    }

                    time += sample.Duration;
                }
            }
        }

        for (var i = 1; i < video.Count; i++)
        {
            Assert.Equal(video[i - 1].Dts + video[i - 1].Duration, video[i].Dts);
        }

        Assert.All(audio, static a => Assert.Equal(1024, a.Duration));
        for (var i = 1; i < audio.Count; i++)
        {
            Assert.Equal(audio[i - 1].Start + 1024, audio[i].Start);
        }

        var timelineEnd = Math.Max(video[^1].Dts + video[^1].Duration, (audio[^1].Start + 1024) * 90_000 / sampleRate);
        Assert.Equal(TimeSpan.FromTicks((timelineEnd - video[0].Dts) * 1000 / 9), playlist.Segments.Aggregate(TimeSpan.Zero, static (t, s) => t + s.Duration));
        Assert.All(playlist.Segments, s => Assert.True(Math.Round(s.Duration.TotalSeconds, MidpointRounding.AwayFromZero) <= 2, s.Name));

        var videoPackets = await PacketsAsync(probe, path, "v:0");
        var audioPackets = await PacketsAsync(probe, path, "a:0");
        Assert.Equal(video.Select(static v => Ticks(v.Pts, 90_000)), videoPackets.Packets.Select(p => Ticks(p.Pts, videoPackets.TimeBase)));
        Assert.Equal(video.Select(static v => Ticks(v.Duration, 90_000)), videoPackets.Packets.Select(p => Ticks(p.Duration, videoPackets.TimeBase)));
        Assert.Equal(audio.Select(a => Ticks(a.Start, sampleRate)), audioPackets.Packets.Select(p => Ticks(p.Pts, audioPackets.TimeBase)));
        Assert.Equal(audio.Select(a => Ticks(a.Duration, sampleRate)), audioPackets.Packets.Select(p => Ticks(p.Duration, audioPackets.TimeBase)));

        var videoFrames = await FramesAsync(probe, path, "v:0");
        var audioFrames = await FramesAsync(probe, path, "a:0");
        Assert.Equal(video.Select(static v => Ticks(v.Pts, 90_000)).Order(), videoFrames.Frames.Select(f => Ticks(f.Pts, videoFrames.TimeBase)).Order());
        Assert.Equal(audio.Select(a => Ticks(a.Start, sampleRate)), audioFrames.Frames.Select(f => Ticks(f.Pts, audioFrames.TimeBase)));
        Assert.All(audioFrames.Frames, static f => Assert.Equal(1024, f.Samples));
        Assert.Equal(Ticks(audio.Count * 1024L, sampleRate), Ticks(audioFrames.Frames.Sum(static f => (long)f.Samples), sampleRate));
        Assert.Equal(
            Ticks(audio[0].Start, sampleRate) - Ticks(video.Min(static v => v.Pts), 90_000),
            Ticks(audioFrames.Frames[0].Pts, audioFrames.TimeBase) - videoFrames.Frames.Min(f => Ticks(f.Pts, videoFrames.TimeBase)));
    }

    /// <summary>Converts a time in units of <paramref name="timeBase"/> to 100 ns ticks (rounded down).</summary>
    /// <param name="value">The time.</param>
    /// <param name="timeBase">Units per second.</param>
    /// <returns>The ticks.</returns>
    private static long Ticks(long value, long timeBase) => (long)(new System.Numerics.BigInteger(value) * TimeSpan.TicksPerSecond / timeBase);

    /// <summary>Converts a time in an ffprobe time base to 100 ns ticks (rounded down).</summary>
    /// <param name="value">The time.</param>
    /// <param name="timeBase">The time base.</param>
    /// <returns>The ticks.</returns>
    private static long Ticks(long value, (long Numerator, long Denominator) timeBase) =>
        (long)(new System.Numerics.BigInteger(value) * timeBase.Numerator * TimeSpan.TicksPerSecond / timeBase.Denominator);

    /// <summary>Reads one stream's demuxed packets with ffprobe.</summary>
    /// <param name="probe">ffprobe.</param>
    /// <param name="path">The playlist.</param>
    /// <param name="stream">The stream specifier.</param>
    /// <returns>The stream time base and packets in decode order.</returns>
    private static async Task<((long Numerator, long Denominator) TimeBase, IReadOnlyList<(long Pts, long Duration)> Packets)> PacketsAsync(string probe, string path, string stream)
    {
        using var json = await ProbeJsonAsync(probe, path, stream, "stream=time_base:packet=pts,duration");
        var packets = json.RootElement.GetProperty("packets").EnumerateArray().Select(static p => (p.GetProperty("pts").GetInt64(), p.GetProperty("duration").GetInt64())).ToList();
        return (TimeBase(json), packets);
    }

    /// <summary>Decodes one stream with ffprobe and reads its frames.</summary>
    /// <param name="probe">ffprobe.</param>
    /// <param name="path">The playlist.</param>
    /// <param name="stream">The stream specifier.</param>
    /// <returns>The stream time base and decoded frames (presentation time and audio sample count) in output order.</returns>
    private static async Task<((long Numerator, long Denominator) TimeBase, IReadOnlyList<(long Pts, int Samples)> Frames)> FramesAsync(string probe, string path, string stream)
    {
        using var json = await ProbeJsonAsync(probe, path, stream, "stream=time_base:frame=pts,nb_samples");
        var frames = json.RootElement.GetProperty("frames").EnumerateArray().Select(static f => (f.GetProperty("pts").GetInt64(), f.TryGetProperty("nb_samples", out var n) ? n.GetInt32() : 0)).ToList();
        return (TimeBase(json), frames);
    }

    /// <summary>Runs ffprobe for one stream.</summary>
    /// <param name="probe">ffprobe.</param>
    /// <param name="path">The playlist.</param>
    /// <param name="stream">The stream specifier.</param>
    /// <param name="entries">The entries to show.</param>
    /// <returns>The JSON output.</returns>
    private static async Task<JsonDocument> ProbeJsonAsync(string probe, string path, string stream, string entries)
    {
        var arguments = new List<string> { "-v", "error", "-select_streams", stream, "-show_entries", entries };
        if (entries.Contains("frame=", StringComparison.Ordinal))
        {
            arguments.Add("-show_frames");
        }
        else
        {
            arguments.Add("-show_packets");
        }

        arguments.AddRange(["-of", "json", path]);
        var result = await RunAsync(probe, [.. arguments]);
        Assert.True(result.Code == 0 && result.Errors.Length == 0, $"ffprobe exited with {result.Code}: {result.Errors}");
        return JsonDocument.Parse(result.Output);
    }

    /// <summary>Reads the probed stream's time base.</summary>
    /// <param name="json">The ffprobe output.</param>
    /// <returns>The time base.</returns>
    private static (long Numerator, long Denominator) TimeBase(JsonDocument json)
    {
        var parts = json.RootElement.GetProperty("streams")[0].GetProperty("time_base").GetString()!.Split('/');
        return (long.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    /// <summary>Decodes the whole presentation, failing on any decoder error.</summary>
    /// <param name="ffmpeg">ffmpeg.</param>
    /// <param name="path">The playlist path.</param>
    /// <returns>A task.</returns>
    internal static async Task DecodeAsync(string ffmpeg, string path)
    {
        var result = await RunAsync(ffmpeg, "-hide_banner", "-v", "error", "-nostdin", "-xerror", "-i", path, "-f", "null", "-");
        Assert.True(result.Code == 0 && result.Errors.Length == 0, $"ffmpeg decode exited with {result.Code}: {result.Errors}");
    }

    /// <summary>Runs a process to completion.</summary>
    /// <param name="executable">The executable.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <returns>The exit code, standard output and standard error.</returns>
    internal static async Task<(int Code, string Output, string Errors)> RunAsync(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).WithinSafetyTimeout();
        return (process.ExitCode, await output, (await errors).Trim());
    }

    /// <summary>An ffprobe stream.</summary>
    /// <param name="CodecType">video or audio.</param>
    /// <param name="CodecName">The codec.</param>
    /// <param name="Width">Width.</param>
    /// <param name="Height">Height.</param>
    /// <param name="SampleRate">Sample rate.</param>
    /// <param name="Channels">Channels.</param>
    /// <param name="Frames">Frames read.</param>
    internal sealed record ProbeStream(string CodecType, string CodecName, int Width, int Height, string? SampleRate, int Channels, int Frames);

    /// <summary>An ffprobe result.</summary>
    /// <param name="Streams">The streams.</param>
    /// <param name="Duration">The format duration in seconds.</param>
    internal sealed record ProbeResult(IReadOnlyList<ProbeStream> Streams, double Duration);

    /// <summary>A temporary directory holding a reconstructed presentation.</summary>
    private sealed class TemporaryDirectory : IDisposable
    {
        /// <summary>The path.</summary>
        private readonly string _path = Directory.CreateTempSubdirectory("rtsp-hls-").FullName;

        /// <summary>Writes the playlist and every segment it references from the store, under the store's generation directory.</summary>
        /// <param name="store">The store.</param>
        /// <param name="application">The application.</param>
        /// <param name="identity">The stream identity.</param>
        /// <param name="playlist">The playlist.</param>
        /// <returns>The playlist path.</returns>
        public string Write(InMemoryHlsSegmentStore store, string application, string identity, HlsPlaylistSnapshot playlist)
        {
            var generation = Directory.CreateDirectory(Path.Combine(_path, store.Generation)).FullName;
            foreach (var name in playlist.Segments.SelectMany(static s => new[] { s.InitializationName, s.Name }).Distinct())
            {
                File.WriteAllBytes(Path.Combine(generation, name), store.GetSegment(application, identity, name)!.Data.ToArray());
            }

            var path = Path.Combine(_path, "index.m3u8");
            File.WriteAllText(path, playlist.Text);
            return path;
        }

        /// <summary>Deletes the directory.</summary>
        public void Dispose() => Directory.Delete(_path, recursive: true);
    }
}
