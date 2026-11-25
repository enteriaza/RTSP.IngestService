// Building blocks for the end-to-end tests: a stream event consumer that records every event and forwards it to the
// real stream catalog (optionally holding delivery to create channel pressure), a host built through the production
// composition with that consumer and an in-memory metrics reader added, a cancellable real-time FFmpeg publisher, and
// an optional evidence log written only when RTSP_E2E_EVIDENCE_DIR is set.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Testing;
using OpenTelemetry.Metrics;
using RTSP.IngestService.Rtmp;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Streams;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.EndToEnd;

/// <summary>Records every delivered stream event and forwards it to the real catalog; can hold delivery.</summary>
/// <param name="catalog">The production catalog.</param>
internal sealed class RecordingEventConsumer(StreamCatalog catalog) : IStreamEventConsumer
{
    /// <summary>Delivered events, in delivery order.</summary>
    private readonly ConcurrentQueue<StreamEvent> _events = new();

    /// <summary>The gate delivery waits at, or <see langword="null"/> while not held.</summary>
    private volatile TaskCompletionSource? _gate;

    /// <summary>Gets the events delivered so far.</summary>
    public IReadOnlyList<StreamEvent> Events => [.. _events];

    /// <summary>Gets a signal completed when a delivery is waiting at the gate.</summary>
    public TaskCompletionSource Held { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Holds the next and later deliveries until <see cref="Release"/>.</summary>
    public void Hold()
    {
        Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Releases held deliveries.</summary>
    public void Release()
    {
        var gate = _gate;
        _gate = null;
        gate?.TrySetResult();
    }

    /// <summary>Returns the events of one incarnation.</summary>
    /// <param name="incarnationId">The incarnation.</param>
    /// <returns>Its events in delivery order.</returns>
    public List<StreamEvent> Of(string incarnationId) => [.. _events.Where(e => e.IncarnationId == incarnationId)];

    /// <inheritdoc />
    public async ValueTask ConsumeAsync(StreamEvent streamEvent, CancellationToken cancellationToken)
    {
        if (_gate is { } gate)
        {
            Held.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        _events.Enqueue(streamEvent);
        await catalog.ConsumeAsync(streamEvent, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Builds end-to-end hosts.</summary>
internal static class EndToEndHost
{
    /// <summary>The stream key of publisher <c>camera{n}</c>.</summary>
    /// <param name="n">The publisher number, 1 to 4.</param>
    /// <returns>The key.</returns>
    public static string Key(int n) => $"e2e-secret-key-000{n}";

    /// <summary>Settings: publishers <c>camera1</c> to <c>camera4</c>, 1 s segments and a 10-segment window.</summary>
    /// <returns>The overrides; callers add or replace entries.</returns>
    public static Dictionary<string, string?> Settings()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Hls:TargetSegmentDuration"] = "00:00:01",
            ["Hls:MaxSegmentDuration"] = "00:00:04",
            ["Hls:PlaylistWindowSize"] = "10",
        };
        for (var n = 1; n <= 4; n++)
        {
            settings[$"Rtmp:Authentication:Publishers:{n - 1}:Name"] = $"camera{n}";
            settings[$"Rtmp:Authentication:Publishers:{n - 1}:Application"] = "live";
            settings[$"Rtmp:Authentication:Publishers:{n - 1}:KeyHash"] = PublisherKeyHash.Create(Key(n));
        }

        return settings;
    }

    /// <summary>
    /// Builds a host through the production composition, replacing only the event consumer registration with a
    /// <see cref="RecordingEventConsumer"/> over the real catalog and, if requested, adding an in-memory metrics reader.
    /// </summary>
    /// <param name="logs">Receives the host's logs.</param>
    /// <param name="overrides">Configuration overrides.</param>
    /// <param name="metrics">Receives in-memory metric exports, or <see langword="null"/> for none.</param>
    /// <param name="configure">Further test registrations, or <see langword="null"/>.</param>
    /// <returns>The host, not started.</returns>
    public static IHost Build(FakeLogCollector logs, Dictionary<string, string?> overrides, List<MetricSnapshot>? metrics = null, Action<IServiceCollection>? configure = null)
    {
        var builder = TestHosts.CreateBuilder(logs, overrides: overrides);
        builder.Services.RemoveAll<IStreamEventConsumer>();
        builder.Services.AddSingleton(static sp => new RecordingEventConsumer(sp.GetRequiredService<StreamCatalog>()));
        builder.Services.AddSingleton<IStreamEventConsumer>(static sp => sp.GetRequiredService<RecordingEventConsumer>());
        if (metrics is not null)
        {
            builder.Services.ConfigureOpenTelemetryMeterProvider(m => m.AddInMemoryExporter(metrics));
        }

        configure?.Invoke(builder.Services);
        return builder.Build();
    }

    /// <summary>Returns the RTMP listener's port.</summary>
    /// <param name="host">The started host.</param>
    /// <returns>The port.</returns>
    public static int RtmpPort(IHost host) => ((IPEndPoint)host.Services.GetRequiredService<RtmpListener>().LocalEndPoint!).Port;
}

/// <summary>A real-time FFmpeg publish of an H.264 test pattern and an AAC tone over RTMP.</summary>
internal sealed class FfmpegPublisher : IAsyncDisposable
{
    /// <summary>The process.</summary>
    private readonly Process _process;

    /// <summary>Reads standard error.</summary>
    private readonly Task<string> _errors;

    /// <summary>Initializes a new instance of the <see cref="FfmpegPublisher"/> class and starts the process.</summary>
    /// <param name="ffmpeg">ffmpeg.</param>
    /// <param name="port">The RTMP port.</param>
    /// <param name="key">The stream key.</param>
    /// <param name="seconds">The publish duration.</param>
    /// <param name="pattern">The lavfi video source, for example <c>testsrc</c> or <c>smptebars</c>.</param>
    /// <param name="frequency">The tone frequency in hertz.</param>
    public FfmpegPublisher(string ffmpeg, int port, string key, int seconds, string pattern = "testsrc", int frequency = 440)
    {
        var start = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false };
        string[] arguments =
        [
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-re", "-f", "lavfi", "-i", $"{pattern}=size=320x240:rate=25",
            "-re", "-f", "lavfi", "-i", $"sine=frequency={frequency.ToString(CultureInfo.InvariantCulture)}:sample_rate=48000",
            "-t", seconds.ToString(CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-profile:v", "high", "-bf", "2", "-g", "25", "-keyint_min", "25", "-sc_threshold", "0", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-ac", "2", "-b:a", "96k",
            "-f", "flv", $"rtmp://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/live/{key}",
        ];
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Seconds = seconds;
        _process = Process.Start(start)!;
        _process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        _errors = _process.StandardError.ReadToEndAsync(CancellationToken.None);
    }

    /// <summary>Gets the publish duration.</summary>
    public int Seconds { get; }

    /// <summary>Gets a value indicating whether the process has exited.</summary>
    public bool HasExited => _process.HasExited;

    /// <summary>Waits for the publish to finish, within its duration plus a margin.</summary>
    /// <returns>The exit code and standard error.</returns>
    public async Task<(int Code, string Errors)> WaitAsync()
    {
        await _process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(Seconds + 20), TestContext.Current.CancellationToken);
        return (_process.ExitCode, await _errors);
    }

    /// <summary>Kills the process, so the server sees the connection drop without an unpublish.</summary>
    public void Kill() => _process.Kill(entireProcessTree: true);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _process.Dispose();
    }
}

/// <summary>Writes verification evidence to <c>$RTSP_E2E_EVIDENCE_DIR/{name}.log</c> when that variable is set; otherwise discards it.</summary>
/// <param name="name">The log name.</param>
internal sealed class Evidence(string name)
{
    /// <summary>The lines.</summary>
    private readonly ConcurrentQueue<string> _lines = new();

    /// <summary>The start time.</summary>
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Records a line, prefixed with the elapsed time.</summary>
    /// <param name="line">The line.</param>
    public void Add(string line)
    {
        _lines.Enqueue(string.Create(CultureInfo.InvariantCulture, $"[{_clock.Elapsed.TotalSeconds,7:F3}s] {line}"));
        Save();
    }

    /// <summary>Writes the lines so far, if an evidence directory is configured.</summary>
    public void Save()
    {
        if (Environment.GetEnvironmentVariable("RTSP_E2E_EVIDENCE_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            lock (_lines)
            {
                File.WriteAllLines(Path.Combine(directory, name + ".log"), _lines);
            }
        }
    }
}
