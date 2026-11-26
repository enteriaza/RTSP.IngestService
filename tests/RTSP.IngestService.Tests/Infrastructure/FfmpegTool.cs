// Locates the FFmpeg and ffprobe executables used by tests: RTSP_FFMPEG_PATH, else the PATH. Tests that need them
// call Require, which skips the test when FFmpeg is absent, or fails it when RTSP_FFMPEG_REQUIRED=true (as in CI).

namespace RTSP.IngestService.Tests.Infrastructure;

/// <summary>Finds FFmpeg for tests.</summary>
internal static class FfmpegTool
{
    /// <summary>The located executable, resolved once.</summary>
    private static readonly Lazy<string?> Located = new(Locate);

    /// <summary>Gets the FFmpeg executable, or <see langword="null"/> when absent.</summary>
    public static string? Path => Located.Value;

    /// <summary>Gets the ffprobe executable beside FFmpeg, or <see langword="null"/>.</summary>
    public static string? ProbePath => Path is { } ffmpeg && System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe") is var probe && File.Exists(probe) ? probe : null;

    /// <summary>Gets a value indicating whether FFmpeg is required (<c>RTSP_FFMPEG_REQUIRED=true</c>).</summary>
    public static bool Required =>
        string.Equals(Environment.GetEnvironmentVariable("RTSP_FFMPEG_REQUIRED"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns FFmpeg, skipping the test when it is absent and not required.</summary>
    /// <param name="probe">Whether ffprobe is needed too.</param>
    /// <returns>The FFmpeg executable.</returns>
    public static string Require(bool probe = false)
    {
        if (Path is { } ffmpeg && (!probe || ProbePath is not null))
        {
            return ffmpeg;
        }

        Assert.False(Required, "FFmpeg (and ffprobe) were not found but RTSP_FFMPEG_REQUIRED is set; tests that need them must not be skipped.");
        Assert.Skip("FFmpeg was not found; set RTSP_FFMPEG_PATH or add ffmpeg to PATH. Publishing through the service requires it because every stream is transcoded.");
        return null!;
    }

    /// <summary>Locates FFmpeg.</summary>
    /// <returns>The path, or <see langword="null"/>.</returns>
    private static string? Locate()
    {
        if (Environment.GetEnvironmentVariable("RTSP_FFMPEG_PATH") is { Length: > 0 } configured && File.Exists(configured))
        {
            return System.IO.Path.GetFullPath(configured);
        }

        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => System.IO.Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }
}
