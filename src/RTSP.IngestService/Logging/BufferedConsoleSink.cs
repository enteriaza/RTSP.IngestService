// A console sink for redirected standard output (systemd's journal, a pipe or a file): events are formatted into a
// buffer and written to standard output in one write per batch, instead of one write and flush per event. The
// bounded asynchronous sink's writer calls Flush when its queue is empty or after a batch of events, so output is
// written as promptly as before.

using System.Globalization;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;

namespace RTSP.IngestService.Logging;

/// <summary>
/// Formats events into a buffer and writes the buffer to the console output on <see cref="Flush"/>.
/// </summary>
/// <param name="formatter">The formatter.</param>
/// <param name="output">Returns the writer to flush to, read at every flush; defaults to <see cref="Console.Out"/>.</param>
/// <remarks>
/// The buffer holds at most the events of one batch of the writer (<see cref="BoundedAsyncSink.MaxBatch"/>), plus any
/// events logged directly while the asynchronous sink is being disposed. <see cref="Dispose"/> flushes it. A batch that
/// cannot be written (standard output closed) is discarded with the exception, as a failing console sink would lose it.
/// </remarks>
internal sealed class BufferedConsoleSink(ITextFormatter formatter, Func<TextWriter>? output = null) : ILogEventSink, IDisposable
{
    /// <summary>The formatted events not yet written.</summary>
    private readonly StringWriter _buffer = new(CultureInfo.InvariantCulture);

    /// <summary>Serializes the buffer between the writer thread and events logged during disposal.</summary>
    private readonly Lock _gate = new();

    /// <summary>Formats <paramref name="logEvent"/> into the buffer.</summary>
    /// <param name="logEvent">The event.</param>
    public void Emit(LogEvent logEvent)
    {
        lock (_gate)
        {
            formatter.Format(logEvent, _buffer);
        }
    }

    /// <summary>Writes the buffered events to the console output and empties the buffer.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            var text = _buffer.GetStringBuilder();
            if (text.Length == 0)
            {
                return;
            }

            try
            {
                var writer = output?.Invoke() ?? Console.Out;
                writer.Write(text);
                writer.Flush();
            }
            finally
            {
                text.Clear();
            }
        }
    }

    /// <summary>Writes any buffered events.</summary>
    public void Dispose() => Flush();
}
