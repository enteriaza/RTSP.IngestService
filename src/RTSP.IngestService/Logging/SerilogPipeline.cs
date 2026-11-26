// Builds the Serilog loggers used by the service: the main pipeline behind Microsoft.Extensions.Logging, and a
// minimal bootstrap logger for failures that occur before the host's logging is available. Sinks depend on the
// hosting mode: coloured console text interactively, priority-prefixed single-line output under systemd, and a
// rolling JSON file for Windows Service execution (or wherever file logging is enabled). The main logger writes its
// sinks asynchronously through a bounded queue that never drops events.

using System.Globalization;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using Serilog.Formatting.Json;

namespace RTSP.IngestService.Logging;

/// <summary>
/// Factory for the service's Serilog loggers.
/// </summary>
/// <remarks>
/// The main logger accepts every level; level filtering is performed by Microsoft.Extensions.Logging using the
/// standard <c>Logging:LogLevel</c> configuration, which reloads with the configuration file. Only the application
/// identity (<see cref="ApplicationIdentityEnricher"/>), environment, hosting mode, machine name and process ID are
/// attached as properties; other configuration values are never logged by the pipeline. The identity properties are
/// rendered by the JSON file sink; the console and systemd templates show only the message, and the identity appears
/// there once, in startup event 4008. The main logger's sinks are written by one background thread through a
/// <see cref="BoundedAsyncSink"/>; console output that is redirected (always under systemd) is written once per batch
/// through a <see cref="BufferedConsoleSink"/> with the same template or formatter, and an interactive console keeps
/// the themed console sink. The bootstrap logger writes synchronously.
/// </remarks>
public static class SerilogPipeline
{
    /// <summary>Template used for interactive console output.</summary>
    public const string ConsoleOutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// <see cref="ConsoleOutputTemplate"/> for <see cref="BufferedConsoleSink"/>. The console sink renders non-string
    /// scalars in <c>:lj</c> unquoted, which the core template formatter does only for <c>:l</c>; with <c>j</c> it
    /// would quote enums, identifiers and time spans as JSON.
    /// </summary>
    internal const string BufferedConsoleOutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {SourceContext}: {Message:l}{NewLine}{Exception}";

    /// <summary>
    /// Creates the main application logger.
    /// </summary>
    /// <param name="identity">The application and deployment identity.</param>
    /// <param name="fileLogging">Validated file logging settings.</param>
    /// <param name="environment">The host environment, supplying the environment name and content root.</param>
    /// <param name="mode">The hosting mode, which selects the sinks.</param>
    /// <returns>A logger the caller owns and must dispose to flush the file sink.</returns>
    /// <exception cref="IOException">The log directory could not be created.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not create the log directory.</exception>
    public static Logger CreateLogger(ApplicationIdentity identity, FileLoggingOptions fileLogging, IHostEnvironment environment, HostingMode mode)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(fileLogging);
        ArgumentNullException.ThrowIfNull(environment);

        var configuration = new LoggerConfiguration().MinimumLevel.Verbose();
        BufferedConsoleSink? console = null;
        switch (mode)
        {
            case HostingMode.Console when Console.IsOutputRedirected:
                console = new BufferedConsoleSink(new MessageTemplateTextFormatter(BufferedConsoleOutputTemplate, CultureInfo.InvariantCulture));
                configuration.WriteTo.Sink(console);
                break;
            case HostingMode.Console:
                configuration.WriteTo.Console(outputTemplate: ConsoleOutputTemplate, formatProvider: CultureInfo.InvariantCulture);
                break;
            case HostingMode.Systemd:
                console = new BufferedConsoleSink(new SystemdConsoleFormatter());
                configuration.WriteTo.Sink(console);
                break;
            case HostingMode.WindowsService:
                // No console is attached to a Windows Service; the file sink below is the log destination.
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported hosting mode.");
        }

        if (IsFileLoggingEnabled(fileLogging.Mode, mode))
        {
            var path = ResolveLogFilePath(fileLogging.Path, environment.ContentRootPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            configuration.WriteTo.File(
                new JsonFormatter(renderMessage: true, formatProvider: CultureInfo.InvariantCulture),
                path,
                fileSizeLimitBytes: fileLogging.FileSizeLimitMegabytes * 1024L * 1024L,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: fileLogging.RetainedFileCountLimit);
        }

        return new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .Enrich.With(new ApplicationIdentityEnricher(identity))
            .Enrich.WithProperty("Environment", environment.EnvironmentName)
            .Enrich.WithProperty("HostingMode", mode.ToString())
            .Enrich.WithProperty("MachineName", Environment.MachineName)
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .WriteTo.Sink(new BoundedAsyncSink(configuration.CreateLogger(), batchWritten: console is null ? null : console.Flush))
            .CreateLogger();
    }

    /// <summary>
    /// Creates the bootstrap logger used before the host is built and if building it fails. It writes to standard
    /// error for console and systemd execution, and to <c>logs/bootstrap-.log</c> under the application directory for
    /// Windows Service execution. If that directory is not writable the Windows Service bootstrap output is lost.
    /// </summary>
    /// <param name="mode">The hosting mode.</param>
    /// <returns>A logger the caller owns and must dispose.</returns>
    public static Logger CreateBootstrapLogger(HostingMode mode)
    {
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("SourceContext", ApplicationIdentity.ServiceAssemblyName + ".Bootstrap")
            .Enrich.WithProperty("HostingMode", mode.ToString());

        // Configuration is not loaded yet, so only the name is known; without an entry assembly the bootstrap logger
        // still has to report the failure that the host build will then raise.
        if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name is { Length: > 0 } name)
        {
            configuration.Enrich.WithProperty(ApplicationIdentityEnricher.ApplicationName, name);
        }

        _ = mode switch
        {
            HostingMode.Systemd => configuration.WriteTo.Console(new SystemdConsoleFormatter(), standardErrorFromLevel: LogEventLevel.Verbose),
            HostingMode.WindowsService => configuration.WriteTo.File(
                new JsonFormatter(renderMessage: true, formatProvider: CultureInfo.InvariantCulture),
                Path.Combine(AppContext.BaseDirectory, "logs", "bootstrap-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7),
            _ => configuration.WriteTo.Console(
                outputTemplate: ConsoleOutputTemplate,
                formatProvider: CultureInfo.InvariantCulture,
                standardErrorFromLevel: LogEventLevel.Verbose),
        };

        return configuration.CreateLogger();
    }

    /// <summary>
    /// Determines whether the rolling file sink is enabled.
    /// </summary>
    /// <param name="fileLoggingMode">The configured file logging mode.</param>
    /// <param name="hostingMode">The hosting mode.</param>
    /// <returns>
    /// <see langword="true"/> for <see cref="FileLoggingMode.Enabled"/>, and for <see cref="FileLoggingMode.Auto"/>
    /// when running as a Windows Service.
    /// </returns>
    public static bool IsFileLoggingEnabled(FileLoggingMode fileLoggingMode, HostingMode hostingMode) => fileLoggingMode switch
    {
        FileLoggingMode.Enabled => true,
        FileLoggingMode.Auto => hostingMode == HostingMode.WindowsService,
        _ => false,
    };

    /// <summary>
    /// Resolves the configured log file path to an absolute path.
    /// </summary>
    /// <param name="configuredPath">The configured path, absolute or relative.</param>
    /// <param name="contentRootPath">The host content root used as the base for relative paths.</param>
    /// <returns>The absolute, normalised path.</returns>
    public static string ResolveLogFilePath(string configuredPath, string contentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        return Path.GetFullPath(configuredPath, contentRootPath);
    }
}
