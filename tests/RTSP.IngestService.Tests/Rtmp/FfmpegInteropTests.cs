// Optional interoperability test: publishes a short synthetic stream with a real FFmpeg client to the RTMP listener.
// FFmpeg is located through the RTSP_FFMPEG_PATH environment variable or the PATH; when it is absent the test is
// skipped. FFmpeg is a test-time tool only and is never used by the service.

using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Tests.Media;

namespace RTSP.IngestService.Tests.Rtmp;

/// <summary>
/// Interoperability tests with FFmpeg.
/// </summary>
public sealed class FfmpegInteropTests
{
    /// <summary>
    /// Verifies that FFmpeg can connect, publish metadata, audio and video, and unpublish, and that the session ends
    /// cleanly when FFmpeg finishes.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Ffmpeg_PublishesSyntheticStream()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            Assert.Skip("FFmpeg was not found; set RTSP_FFMPEG_PATH or add ffmpeg to PATH to run this test.");
        }

        using var server = new RtmpTestServer(new RtmpOptions { ListenAddress = "127.0.0.1", Port = 0 });
        using var listener = server.CreateListener();
        await listener.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var port = ((IPEndPoint)listener.LocalEndPoint!).Port;
            var start = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[]
            {
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100",
                "-t", "2", "-c:v", "flv", "-c:a", "adpcm_swf",
                "-f", "flv", $"rtmp://127.0.0.1:{port}/live/{RtmpTestServer.StreamKey}",
            })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WithinSafetyTimeout();
            Assert.True(process.ExitCode == 0, $"FFmpeg exited with {process.ExitCode}: {await errors}");

            var ended = await server.Handler.NextAsync("ended");
            var session = ended.Session;
            Assert.Equal(RtmpTestServer.StreamIdentity, session.StreamIdentity);
            Assert.Contains(session.EndReason, new PublishEndReason?[] { PublishEndReason.Unpublished, PublishEndReason.StreamDeleted, PublishEndReason.ClientDisconnected });
            Assert.True(session.VideoMessageCount > 10, $"{session.VideoMessageCount} video messages");
            Assert.True(session.AudioMessageCount > 10, $"{session.AudioMessageCount} audio messages");
            Assert.Contains(session.Metadata, static p => p.Key == "width" && p.Value.NumberValue == 320);
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Verifies that an H.264 (with B-frames) and AAC stream published by FFmpeg is turned into typed samples by
    /// media processing: configurations are parsed, samples carry valid NAL units, and nothing is dropped or leaked.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Ffmpeg_H264Aac_ProducesTypedSamples()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            Assert.Skip("FFmpeg was not found; set RTSP_FFMPEG_PATH or add ffmpeg to PATH to run this test.");
        }

        var downstream = new RecordingMediaHandler();
        using var server = new RtmpTestServer(
            new RtmpOptions { ListenAddress = "127.0.0.1", Port = 0 },
            sessionHandler: (meters, logs) => new MediaPublishSessionHandler(
                Microsoft.Extensions.Options.Options.Create(new MediaOptions()),
                downstream,
                new MediaMetrics(meters),
                logs.CreateLogger<MediaPublishSessionHandler>()));
        using var listener = server.CreateListener();
        await listener.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var port = ((IPEndPoint)listener.LocalEndPoint!).Port;
            var exit = await RunFfmpegAsync(
                ffmpeg,
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                "-t", "2", "-c:v", "libx264", "-profile:v", "high", "-bf", "2", "-g", "25", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-ac", "2", "-b:a", "96k",
                "-f", "flv", $"rtmp://127.0.0.1:{port}/live/{RtmpTestServer.StreamKey}");
            Assert.True(exit.Code == 0, $"FFmpeg exited with {exit.Code}: {exit.Errors}");

            await Eventually.WaitUntilAsync(() => downstream.Events.Any(static e => e.Kind == "ended"), "the media session ends");
            var video = downstream.Video;
            var audio = downstream.Audio;
            Assert.Empty(downstream.Errors);
            Assert.True(video.Count >= 40, $"{video.Count} video samples");
            Assert.True(audio.Count >= 80, $"{audio.Count} audio samples");
            Assert.True(video[0].IsKeyframe && video[0].ContainsIdr);
            Assert.Equal(2, video.Count(static v => v.IsKeyframe));
            Assert.Contains(video, static v => v.Timestamps.CompositionTimeOffset > 0);
            Assert.All(video, static v => Assert.All(v.NalUnits, static n => Assert.Equal(0, n[0] & 0x80)));
            var info = video[^1].Session.StreamInfo;
            Assert.Equal(100, info.Video!.Configuration.Profile);
            Assert.Equal(320, info.Video.Width);
            Assert.Equal(48000, info.Audio!.SampleRate);
            Assert.Equal(2, info.Audio.ChannelCount);
            Assert.Equal(0, server.Pool.Outstanding);
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Runs FFmpeg to completion.</summary>
    /// <param name="ffmpeg">The executable.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <returns>The exit code and standard error.</returns>
    private static async Task<(int Code, string Errors)> RunFfmpegAsync(string ffmpeg, params string[] arguments)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).WithinSafetyTimeout();
        return (process.ExitCode, await errors);
    }

    /// <summary>Finds FFmpeg from <c>RTSP_FFMPEG_PATH</c> or the PATH.</summary>
    /// <returns>The executable path, or <see langword="null"/>.</returns>
    internal static string? FindFfmpeg()
    {
        if (Environment.GetEnvironmentVariable("RTSP_FFMPEG_PATH") is { Length: > 0 } configured && File.Exists(configured))
        {
            return configured;
        }

        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }
}
