// Startup verification of the deployed FFmpeg executable. Before the RTMP listener starts, the service runs the
// configured FFmpeg with fixed informational arguments and confirms it has every decoder, encoder, filter and format
// the canonical pipeline uses, so a missing or incomplete build fails the start with the configuration exit code
// instead of failing each publish later. Results are cached per executable, so repeated hosts in one process (tests)
// probe once.

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;

namespace RTSP.IngestService.Transcoding;

/// <summary>Verifies the FFmpeg executable when transcoding is enabled.</summary>
internal sealed partial class FfmpegCapabilityCheck : IHostedService
{
    /// <summary>Decoders the pipeline selects explicitly.</summary>
    internal static readonly string[] RequiredDecoders = ["h264", "hevc", "libdav1d", "vp9", "aac", "mp3float", "opus", "flac", "ac3", "eac3"];

    /// <summary>Encoders of the canonical profile.</summary>
    internal static readonly string[] RequiredEncoders = ["libx264", "aac"];

    /// <summary>Filters of the canonical graph.</summary>
    internal static readonly string[] RequiredFilters = ["crop", "scale", "format", "aresample", "aformat"];

    /// <summary>The time allowed for one probe.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Successful results by executable path and timestamp.</summary>
    private static readonly ConcurrentDictionary<(string Path, DateTime Modified), string> Verified = new();

    /// <summary>The transcoding options.</summary>
    private readonly TranscodingOptions _options;

    /// <summary>Logger.</summary>
    private readonly ILogger<FfmpegCapabilityCheck> _logger;

    /// <summary>Initializes a new instance of the <see cref="FfmpegCapabilityCheck"/> class.</summary>
    /// <param name="options">The transcoding options.</param>
    /// <param name="logger">Logger.</param>
    public FfmpegCapabilityCheck(IOptions<TranscodingOptions> options, ILogger<FfmpegCapabilityCheck> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            LogDisabled();
            return;
        }

        var path = _options.ResolveFfmpegPath(AppContext.BaseDirectory);
        if (!File.Exists(path))
        {
            throw Failure($"The FFmpeg executable was not found at '{path}'. Deploy FFmpeg beside the service or set Transcoding:FfmpegPath.");
        }

        var key = (path, File.GetLastWriteTimeUtc(path));
        if (!Verified.TryGetValue(key, out var version))
        {
            version = await VerifyAsync(path, cancellationToken).ConfigureAwait(false);
            Verified[key] = version;
        }

        LogReady(path, version);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Runs the probes.</summary>
    /// <param name="path">The executable.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The version line.</returns>
    /// <exception cref="OptionsValidationException">A capability is missing.</exception>
    internal static async Task<string> VerifyAsync(string path, CancellationToken cancellationToken)
    {
        var versionOutput = await RunAsync(path, ["-hide_banner", "-version"], cancellationToken).ConfigureAwait(false);
        var versionLine = versionOutput.Split('\n', 2)[0].Trim();
        var match = VersionPattern().Match(versionLine);
        if (match.Success && int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) < 9)
        {
            throw Failure($"FFmpeg 9 or later is required; found '{versionLine}'.");
        }

        var missing = new List<string>();
        var decoders = await RunAsync(path, ["-hide_banner", "-decoders"], cancellationToken).ConfigureAwait(false);
        missing.AddRange(RequiredDecoders.Where(name => !Lists(decoders, name)).Select(name => "decoder " + name));
        var encoders = await RunAsync(path, ["-hide_banner", "-encoders"], cancellationToken).ConfigureAwait(false);
        missing.AddRange(RequiredEncoders.Where(name => !Lists(encoders, name)).Select(name => "encoder " + name));
        var filters = await RunAsync(path, ["-hide_banner", "-filters"], cancellationToken).ConfigureAwait(false);
        missing.AddRange(RequiredFilters.Where(name => !Lists(filters, name)).Select(name => "filter " + name));
        var scale = await RunAsync(path, ["-hide_banner", "-h", "filter=scale"], cancellationToken).ConfigureAwait(false);
        if (!scale.Contains("intent", StringComparison.Ordinal) || !scale.Contains("perceptual", StringComparison.Ordinal))
        {
            missing.Add("scale filter option intent=perceptual");
        }

        var muxer = await RunAsync(path, ["-hide_banner", "-h", "muxer=flv"], cancellationToken).ConfigureAwait(false);
        var demuxer = await RunAsync(path, ["-hide_banner", "-h", "demuxer=flv"], cancellationToken).ConfigureAwait(false);
        if (!muxer.Contains("Muxer flv", StringComparison.Ordinal))
        {
            missing.Add("muxer flv");
        }

        if (!demuxer.Contains("Demuxer flv", StringComparison.Ordinal))
        {
            missing.Add("demuxer flv");
        }

        if (missing.Count > 0)
        {
            throw Failure($"The FFmpeg build at '{path}' lacks required capabilities: {string.Join(", ", missing)}.");
        }

        return versionLine;
    }

    /// <summary>Whether an FFmpeg list output names a component.</summary>
    /// <param name="output">The list output.</param>
    /// <param name="name">The component name.</param>
    /// <returns><see langword="true"/> if listed.</returns>
    private static bool Lists(string output, string name)
    {
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1] == name)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Runs FFmpeg with fixed arguments and returns its standard output.</summary>
    /// <param name="path">The executable.</param>
    /// <param name="arguments">The arguments.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The output.</returns>
    private static async Task<string> RunAsync(string path, string[] arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw Failure($"The FFmpeg executable at '{path}' could not be started: {ex.Message}");
        }

        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false);
            await error.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw Failure($"The FFmpeg executable at '{path}' failed a capability probe (exit code {process.ExitCode}).");
            }

            return text.Replace("\r", string.Empty, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            throw Failure($"The FFmpeg executable at '{path}' did not answer a capability probe within {ProbeTimeout.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>Creates the startup failure.</summary>
    /// <param name="message">The description.</param>
    /// <returns>The exception.</returns>
    private static OptionsValidationException Failure(string message) =>
        new(TranscodingOptions.SectionName, typeof(TranscodingOptions), [message]);

    /// <summary>Matches the major version of a release build.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"^ffmpeg version n?(\d+)\.", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    /// <summary>Logs that transcoding is disabled.</summary>
    [LoggerMessage(EventId = 10016, Level = LogLevel.Warning, Message = "Transcoding is disabled (Transcoding:Enabled is false); every publish is rejected because only canonical media may reach HLS")]
    private partial void LogDisabled();

    /// <summary>Logs a verified executable.</summary>
    /// <param name="path">The executable.</param>
    /// <param name="version">The version line.</param>
    [LoggerMessage(EventId = 10015, Level = LogLevel.Information, Message = "Transcoding uses FFmpeg at {Path}: {Version}")]
    private partial void LogReady(string path, string version);
}

/// <summary>Terminates any transcoder still running when the host stops.</summary>
/// <param name="handler">The transcoding stage.</param>
internal sealed class TranscodingShutdownService(TranscodingPublishSessionHandler handler) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => handler.AbortAllAsync();
}
