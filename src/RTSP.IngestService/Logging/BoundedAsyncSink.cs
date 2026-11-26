// A bounded asynchronous Serilog sink: log calls enqueue the event and return, and one dedicated thread writes the
// events to the wrapped sinks in order, in batches, so callers never wait for console or file I/O or contend for the
// sinks' locks, and a buffered sink can write a whole batch at once.
// Overflow policy: when the queue is full the caller waits for space, so no event is ever dropped. Disposal writes
// every queued event, waiting at most DrainTimeout, and then disposes the wrapped sinks.

using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;

namespace RTSP.IngestService.Logging;

/// <summary>
/// Writes log events to a wrapped logger on a dedicated thread through a queue of at most <see cref="Capacity"/> events.
/// </summary>
/// <remarks>
/// <para>
/// Events are written in the order they were queued. Enrichment happens before queueing, on the logging thread, so
/// properties that depend on the calling context are preserved.
/// </para>
/// <para>
/// While the queue is full, <see cref="Emit"/> blocks until the writer has made room: a burst larger than the queue
/// slows the callers to the rate of the sinks, as synchronous logging would, instead of dropping events.
/// </para>
/// <para>
/// <see cref="Dispose"/> stops accepting events, waits up to <see cref="DrainTimeout"/> for the writer to write the
/// queued ones and then disposes the wrapped logger, which flushes the file sink. Events logged during disposal are
/// written directly. If the sinks do not drain in time (for example, standard output is blocked), the events still
/// queued are lost and the loss is reported to <see cref="SelfLog"/>.
/// </para>
/// </remarks>
internal sealed class BoundedAsyncSink : ILogEventSink, IDisposable
{
    /// <summary>The maximum number of queued events.</summary>
    public const int Capacity = 10_000;

    /// <summary>The most events written between two calls of the batch callback.</summary>
    public const int MaxBatch = 1000;

    /// <summary>The longest <see cref="Dispose"/> waits for the queued events to be written.</summary>
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Called on the writer thread after each batch of events, or <see langword="null"/>.</summary>
    private readonly Action? _batchWritten;

    /// <summary>The queued events.</summary>
    private readonly BlockingCollection<LogEvent> _queue;

    /// <summary>The wrapped logger, written only by the writer thread until disposal.</summary>
    private readonly Logger _inner;

    /// <summary>The writer thread.</summary>
    private readonly Thread _writer;

    /// <summary>The longest disposal waits for the queue to drain.</summary>
    private readonly TimeSpan _drainTimeout;

    /// <summary>1 once disposal has begun.</summary>
    private int _disposing;

    /// <summary>Whether <see cref="_inner"/> has been disposed.</summary>
    private volatile bool _innerDisposed;

    /// <summary>Initializes a new instance of the <see cref="BoundedAsyncSink"/> class and starts the writer thread.</summary>
    /// <param name="inner">The logger whose sinks receive the events; ownership passes to this instance.</param>
    /// <param name="capacity">The maximum number of queued events.</param>
    /// <param name="drainTimeout">The longest disposal waits for the queue to drain.</param>
    /// <param name="batchWritten">
    /// Called on the writer thread after each batch: when the queue is empty or after <see cref="MaxBatch"/> events.
    /// </param>
    public BoundedAsyncSink(Logger inner, int capacity = Capacity, TimeSpan? drainTimeout = null, Action? batchWritten = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _inner = inner;
        _batchWritten = batchWritten;
        _queue = new BlockingCollection<LogEvent>(capacity);
        _drainTimeout = drainTimeout ?? DrainTimeout;
        _writer = new Thread(Write) { IsBackground = true, Name = "Log writer" };
        _writer.Start();
    }

    /// <summary>Gets the number of queued events.</summary>
    public int Count => _queue.Count;

    /// <summary>Queues <paramref name="logEvent"/>, waiting while the queue is full.</summary>
    /// <param name="logEvent">The event.</param>
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (Volatile.Read(ref _disposing) == 0)
        {
            try
            {
                _queue.Add(logEvent);
                return;
            }
            catch (InvalidOperationException) when (_queue.IsAddingCompleted)
            {
            }
        }

        if (!_innerDisposed)
        {
            _inner.Write(logEvent);
        }
    }

    /// <summary>Writes the queued events, then disposes the wrapped logger.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0)
        {
            return;
        }

        _queue.CompleteAdding();
        if (!_writer.Join(_drainTimeout))
        {
            SelfLog.WriteLine("The log writer did not drain within {0}; {1} queued event(s) were not written", _drainTimeout, _queue.Count);
        }

        _innerDisposed = true;
        _inner.Dispose();
    }

    /// <summary>
    /// Writes queued events in batches of at most <see cref="MaxBatch"/>, calling the batch callback after each, until
    /// the queue is completed and empty.
    /// </summary>
    private void Write()
    {
        while (_queue.TryTake(out var logEvent, Timeout.Infinite))
        {
            var written = 0;
            do
            {
                try
                {
                    _inner.Write(logEvent);
                }
                catch (Exception ex)
                {
                    SelfLog.WriteLine("The log writer failed to write an event: {0}", ex);
                }
            }
            while (++written < MaxBatch && _queue.TryTake(out logEvent));

            try
            {
                _batchWritten?.Invoke();
            }
            catch (Exception ex)
            {
                SelfLog.WriteLine("The log writer failed to complete a batch: {0}", ex);
            }
        }
    }
}
