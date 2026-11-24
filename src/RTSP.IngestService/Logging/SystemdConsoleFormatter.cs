// Serilog text formatter for standard output under systemd. Each event is written as a single line prefixed with
// its syslog priority ("<N>"), which journald parses (SyslogLevelPrefix=yes, the default) to set the entry priority.
// Multi-line content such as exception stack traces is folded onto that line so that it stays in one journal entry.

using System.Globalization;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace RTSP.IngestService.Logging;

/// <summary>
/// Formats log events as <c>&lt;priority&gt;SourceContext: message exception</c> on a single line. Timestamps are
/// omitted because journald records its own. The priority mapping matches the framework's systemd console formatter.
/// </summary>
/// <remarks>Instances are stateless apart from the immutable inner template formatter and are thread-safe.</remarks>
public sealed class SystemdConsoleFormatter : ITextFormatter
{
    /// <summary>
    /// Template rendered after the priority prefix. <c>:l</c> without <c>j</c> keeps non-string scalars such as enums
    /// and time spans unquoted; <c>j</c> would render them as quoted JSON strings.
    /// </summary>
    public const string OutputTemplate = "{SourceContext}: {Message:l} {Exception}";

    /// <summary>Renders <see cref="OutputTemplate"/> with invariant culture.</summary>
    private readonly MessageTemplateTextFormatter _inner = new(OutputTemplate, CultureInfo.InvariantCulture);

    /// <summary>
    /// Maps a Serilog level to a syslog priority: Fatal 2 (crit), Error 3 (err), Warning 4 (warning),
    /// Information 6 (info), Debug and Verbose 7 (debug).
    /// </summary>
    /// <param name="level">The event level.</param>
    /// <returns>The syslog priority number.</returns>
    public static int GetSyslogPriority(LogEventLevel level) => level switch
    {
        LogEventLevel.Fatal => 2,
        LogEventLevel.Error => 3,
        LogEventLevel.Warning => 4,
        LogEventLevel.Information => 6,
        _ => 7,
    };

    /// <summary>
    /// Writes <paramref name="logEvent"/> to <paramref name="output"/> as one line terminated by <c>\n</c>.
    /// </summary>
    /// <param name="logEvent">The event to format.</param>
    /// <param name="output">The destination writer.</param>
    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        _inner.Format(logEvent, buffer);

        output.Write('<');
        output.Write(GetSyslogPriority(logEvent.Level).ToString(CultureInfo.InvariantCulture));
        output.Write('>');
        output.Write(buffer.ToString().TrimEnd().ReplaceLineEndings(" "));
        output.Write('\n');
    }
}
