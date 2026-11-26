// Tests for the Serilog pipeline: file-sink activation per hosting mode, log path resolution, structured JSON file
// output with the identity properties, context properties across the asynchronous writer, and the journald-oriented
// systemd console formatter.

using System.Text.Json;
using Microsoft.Extensions.Hosting;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hosting;
using RTSP.IngestService.Logging;
using Serilog.Events;
using Serilog.Parsing;

namespace RTSP.IngestService.Tests.Logging;

/// <summary>
/// Tests for <see cref="SerilogPipeline"/> and <see cref="SystemdConsoleFormatter"/>.
/// </summary>
public sealed class SerilogPipelineTests : IDisposable
{
    /// <summary>Per-test temporary directory used as content root for file sink tests.</summary>
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rtsp-ingest-service-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Verifies the file-sink activation matrix for every file logging and hosting mode combination.</summary>
    /// <param name="fileMode">The configured file logging mode.</param>
    /// <param name="hostingMode">The hosting mode.</param>
    /// <param name="expected">Whether the file sink is enabled.</param>
    [Theory]
    [InlineData(FileLoggingMode.Auto, HostingMode.WindowsService, true)]
    [InlineData(FileLoggingMode.Auto, HostingMode.Console, false)]
    [InlineData(FileLoggingMode.Auto, HostingMode.Systemd, false)]
    [InlineData(FileLoggingMode.Enabled, HostingMode.Console, true)]
    [InlineData(FileLoggingMode.Enabled, HostingMode.Systemd, true)]
    [InlineData(FileLoggingMode.Disabled, HostingMode.WindowsService, false)]
    public void IsFileLoggingEnabled_FollowsModeMatrix(FileLoggingMode fileMode, HostingMode hostingMode, bool expected) =>
        Assert.Equal(expected, SerilogPipeline.IsFileLoggingEnabled(fileMode, hostingMode));

    /// <summary>Verifies that relative log paths resolve against the content root and absolute paths are kept.</summary>
    [Fact]
    public void ResolveLogFilePath_HandlesRelativeAndAbsolutePaths()
    {
        var contentRoot = Path.Combine(_directory, "app");
        var absolute = Path.Combine(_directory, "elsewhere", "x-.log");

        Assert.Equal(Path.Combine(contentRoot, "logs", "service-.log"), SerilogPipeline.ResolveLogFilePath("logs/service-.log", contentRoot));
        Assert.Equal(absolute, SerilogPipeline.ResolveLogFilePath(absolute, contentRoot));
    }

    /// <summary>
    /// Verifies that with file logging enabled the log directory is created and each event is written as a JSON line
    /// carrying the rendered message, the application and deployment identity, Environment and HostingMode.
    /// </summary>
    [Fact]
    public void CreateLogger_FileEnabled_WritesStructuredJson()
    {
        var environment = new TestEnvironment { EnvironmentName = "Staging", ContentRootPath = _directory };
        var fileOptions = new FileLoggingOptions { Mode = FileLoggingMode.Enabled, Path = "nested/logs/test-.log" };

        using (var logger = SerilogPipeline.CreateLogger(Identity(), fileOptions, environment, HostingMode.WindowsService))
        {
            Assert.True(Directory.Exists(Path.Combine(_directory, "nested", "logs")));
            logger.Information("Processed {Count} items", 3);
        }

        var line = Assert.Single(File.ReadAllLines(Assert.Single(Directory.GetFiles(Path.Combine(_directory, "nested", "logs")))));
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        Assert.Equal("Processed 3 items", root.GetProperty("RenderedMessage").GetString());
        var properties = root.GetProperty("Properties");
        Assert.Equal(3, properties.GetProperty("Count").GetInt32());
        Assert.Equal(ApplicationIdentity.ServiceAssemblyName, properties.GetProperty("ApplicationName").GetString());
        Assert.Equal("i-0123456789abcdef0", properties.GetProperty("ApplicationInstanceId").GetString());
        Assert.Equal("node01.af-south-1", properties.GetProperty("ApplicationNodeId").GetString());
        Assert.Equal("af-south-1", properties.GetProperty("ApplicationRegion").GetString());
        Assert.Equal("af-south-1a", properties.GetProperty("ApplicationAvailabilityZone").GetString());
        Assert.False(properties.TryGetProperty("Application", out _));
        Assert.Equal("Staging", properties.GetProperty("Environment").GetString());
        Assert.Equal("WindowsService", properties.GetProperty("HostingMode").GetString());
    }

    /// <summary>
    /// Verifies that properties pushed on the logging thread's context reach the file although the sinks are written
    /// on the background writer, and that disposal writes every event.
    /// </summary>
    [Fact]
    public void CreateLogger_KeepsContextPropertiesAndWritesEveryEvent()
    {
        var environment = new TestEnvironment { ContentRootPath = _directory };
        var fileOptions = new FileLoggingOptions { Mode = FileLoggingMode.Enabled, Path = "logs/test-.log" };

        using (var logger = SerilogPipeline.CreateLogger(Identity(), fileOptions, environment, HostingMode.WindowsService))
        {
            for (var i = 0; i < 500; i++)
            {
                using (Serilog.Context.LogContext.PushProperty("Scope", i))
                {
                    logger.Information("Event {Number}", i);
                }
            }
        }

        var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(Path.Combine(_directory, "logs"))));
        Assert.Equal(500, lines.Length);
        Assert.All(lines.Select(static (line, i) => (line, i)), static entry =>
        {
            using var json = JsonDocument.Parse(entry.line);
            var properties = json.RootElement.GetProperty("Properties");
            Assert.Equal(entry.i, properties.GetProperty("Number").GetInt32());
            Assert.Equal(entry.i, properties.GetProperty("Scope").GetInt32());
        });
    }

    /// <summary>
    /// Verifies that the buffered console template renders a line as the console sink does: strings, identifiers,
    /// enums, Booleans and time spans unquoted.
    /// </summary>
    [Fact]
    public void BufferedConsoleTemplate_RendersScalarsAsTheConsoleSinkDoes()
    {
        var logEvent = new LogEvent(
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, 678, TimeSpan.FromHours(2)),
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("Session {SessionId} on {RtmpApplication} ended ({Completed}): {Reason} after {Duration}"),
            [
                new LogEventProperty("SessionId", new ScalarValue(Guid.Parse("01a214a9-d3ad-7c7c-b808-311b8e0eee4e"))),
                new LogEventProperty("RtmpApplication", new ScalarValue("live")),
                new LogEventProperty("Completed", new ScalarValue(true)),
                new LogEventProperty("Reason", new ScalarValue(HostingMode.Console)),
                new LogEventProperty("Duration", new ScalarValue(TimeSpan.FromSeconds(30))),
                new LogEventProperty("SourceContext", new ScalarValue("RTSP.IngestService.Test")),
            ]);
        using var output = new StringWriter();

        new Serilog.Formatting.Display.MessageTemplateTextFormatter(SerilogPipeline.BufferedConsoleOutputTemplate, System.Globalization.CultureInfo.InvariantCulture).Format(logEvent, output);

        Assert.Equal(
            "[2026-01-02 03:04:05.678 +02:00 INF] RTSP.IngestService.Test: Session 01a214a9-d3ad-7c7c-b808-311b8e0eee4e on live ended (True): Console after 00:00:30" + Environment.NewLine,
            output.ToString());
    }

    /// <summary>
    /// Verifies that the identity cannot be overwritten: message-template and log-context properties with the identity
    /// names (as an RTMP application name once could) are replaced by the service's values on every event, and that
    /// RTMP's own <c>RtmpApplication</c> property is kept.
    /// </summary>
    [Fact]
    public void CreateLogger_IdentityOverridesEventAndContextProperties()
    {
        var environment = new TestEnvironment { ContentRootPath = _directory };
        var fileOptions = new FileLoggingOptions { Mode = FileLoggingMode.Enabled, Path = "logs/test-.log" };

        using (var logger = SerilogPipeline.CreateLogger(Identity(), fileOptions, environment, HostingMode.Console))
        {
            logger.Information("Connected to {RtmpApplication} as {ApplicationName} on {ApplicationNodeId}", "live", "live", "spoofed");
            using (Serilog.Context.LogContext.PushProperty("ApplicationRegion", "spoofed"))
            {
                logger.Information("In context");
            }
        }

        var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(Path.Combine(_directory, "logs"))));
        Assert.Equal(2, lines.Length);
        Assert.All(lines, static line =>
        {
            using var json = JsonDocument.Parse(line);
            var properties = json.RootElement.GetProperty("Properties");
            Assert.Equal(ApplicationIdentity.ServiceAssemblyName, properties.GetProperty("ApplicationName").GetString());
            Assert.Equal("node01.af-south-1", properties.GetProperty("ApplicationNodeId").GetString());
            Assert.Equal("af-south-1", properties.GetProperty("ApplicationRegion").GetString());
        });
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("live", first.RootElement.GetProperty("Properties").GetProperty("RtmpApplication").GetString());
    }

    /// <summary>
    /// Verifies that unconfigured deployment values are absent, not empty, while the application name is always present.
    /// </summary>
    [Fact]
    public void CreateLogger_OmitsUnconfiguredDeploymentIdentity()
    {
        var environment = new TestEnvironment { ContentRootPath = _directory };
        var fileOptions = new FileLoggingOptions { Mode = FileLoggingMode.Enabled, Path = "logs/test-.log" };

        using (var logger = SerilogPipeline.CreateLogger(Identity(new ApplicationOptions { NodeId = "node07" }), fileOptions, environment, HostingMode.Console))
        {
            logger.Information("Event");
        }

        using var json = JsonDocument.Parse(Assert.Single(File.ReadAllLines(Assert.Single(Directory.GetFiles(Path.Combine(_directory, "logs"))))));
        var properties = json.RootElement.GetProperty("Properties");
        Assert.Equal(ApplicationIdentity.ServiceAssemblyName, properties.GetProperty("ApplicationName").GetString());
        Assert.Equal("node07", properties.GetProperty("ApplicationNodeId").GetString());
        foreach (var absent in new[] { "ApplicationInstanceId", "ApplicationRegion", "ApplicationAvailabilityZone", "Application" })
        {
            Assert.False(properties.TryGetProperty(absent, out _), absent);
        }
    }

    /// <summary>Verifies that no file is written when file logging is disabled.</summary>
    [Fact]
    public void CreateLogger_FileDisabled_WritesNoFile()
    {
        var environment = new TestEnvironment { ContentRootPath = _directory };
        var fileOptions = new FileLoggingOptions { Mode = FileLoggingMode.Disabled, Path = "logs/test-.log" };

        using (var logger = SerilogPipeline.CreateLogger(Identity(), fileOptions, environment, HostingMode.WindowsService))
        {
            logger.Information("Not written to a file");
        }

        Assert.False(Directory.Exists(Path.Combine(_directory, "logs")));
    }

    /// <summary>Verifies the syslog priority mapping used for journald.</summary>
    /// <param name="level">The Serilog level.</param>
    /// <param name="priority">The expected syslog priority.</param>
    [Theory]
    [InlineData(LogEventLevel.Verbose, 7)]
    [InlineData(LogEventLevel.Debug, 7)]
    [InlineData(LogEventLevel.Information, 6)]
    [InlineData(LogEventLevel.Warning, 4)]
    [InlineData(LogEventLevel.Error, 3)]
    [InlineData(LogEventLevel.Fatal, 2)]
    public void SystemdFormatter_MapsSyslogPriority(LogEventLevel level, int priority) =>
        Assert.Equal(priority, SystemdConsoleFormatter.GetSyslogPriority(level));

    /// <summary>
    /// Verifies that the systemd formatter writes one priority-prefixed line containing the source context, message
    /// and the folded exception, terminated by a single newline.
    /// </summary>
    [Fact]
    public void SystemdFormatter_WritesSingleLineWithPriorityPrefix()
    {
        Exception exception;
        try
        {
            throw new InvalidOperationException("line one" + Environment.NewLine + "line two");
        }
        catch (InvalidOperationException ex)
        {
            exception = ex;
        }

        var logEvent = new LogEvent(
            DateTimeOffset.UnixEpoch,
            LogEventLevel.Error,
            exception,
            new MessageTemplateParser().Parse("Failed after {Attempts} attempts"),
            [
                new LogEventProperty("Attempts", new ScalarValue(3)),
                new LogEventProperty("SourceContext", new ScalarValue("RTSP.IngestService.Test")),
            ]);
        using var output = new StringWriter();

        new SystemdConsoleFormatter().Format(logEvent, output);

        var text = output.ToString();
        Assert.StartsWith("<3>RTSP.IngestService.Test: Failed after 3 attempts System.InvalidOperationException: line one line two", text, StringComparison.Ordinal);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.Equal(1, text.Count(static c => c == '\n'));
        Assert.DoesNotContain('\r', text);
    }

    /// <summary>
    /// Verifies that the systemd formatter renders string, enum and TimeSpan property values without quotes, matching
    /// the console output.
    /// </summary>
    [Fact]
    public void SystemdFormatter_RendersScalarValuesUnquoted()
    {
        var logEvent = new LogEvent(
            DateTimeOffset.UnixEpoch,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("{Name} in {Mode} mode after {Elapsed}"),
            [
                new LogEventProperty("Name", new ScalarValue("svc")),
                new LogEventProperty("Mode", new ScalarValue(HostingMode.Systemd)),
                new LogEventProperty("Elapsed", new ScalarValue(TimeSpan.FromSeconds(90))),
                new LogEventProperty("SourceContext", new ScalarValue("RTSP.IngestService.Test")),
            ]);
        using var output = new StringWriter();

        new SystemdConsoleFormatter().Format(logEvent, output);

        Assert.Equal("<6>RTSP.IngestService.Test: svc in Systemd mode after 00:01:30\n", output.ToString());
    }

    /// <summary>
    /// Creates the identity of the service assembly (as when it is the entry assembly) with a deployment identity.
    /// </summary>
    /// <param name="deployment">The deployment identity; a full example identity if omitted.</param>
    /// <returns>The identity.</returns>
    private static ApplicationIdentity Identity(ApplicationOptions? deployment = null) => ApplicationIdentity.Create(
        typeof(SerilogPipeline).Assembly,
        deployment ?? new ApplicationOptions { InstanceId = "i-0123456789abcdef0", NodeId = "node01.af-south-1", Region = "af-south-1", AvailabilityZone = "af-south-1a" });

    /// <summary>Deletes the temporary directory.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>Minimal host environment for pipeline tests.</summary>
    private sealed class TestEnvironment : IHostEnvironment
    {
        /// <inheritdoc />
        public string EnvironmentName { get; set; } = "Testing";

        /// <inheritdoc />
        public string ApplicationName { get; set; } = "RTSP.IngestService.Tests";

        /// <inheritdoc />
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        /// <inheritdoc />
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
