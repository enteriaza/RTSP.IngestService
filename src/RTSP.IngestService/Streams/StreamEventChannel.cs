// The single owner of the bounded stream event channel. Producers write without waiting; when the channel is full the
// oldest undelivered event is overwritten and counted through the channel's item-dropped callback, because with
// drop-oldest TryWrite still succeeds and its result cannot reveal the loss.

using System.Threading.Channels;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;

namespace RTSP.IngestService.Streams;

/// <summary>
/// Owns the bounded, drop-oldest channel that carries stream lifecycle events from producers to the single consumer.
/// </summary>
/// <remarks>
/// <para>
/// Capacity: <see cref="StreamEventOptions.QueueCapacity"/> events. Writing to a full channel succeeds and discards
/// the oldest undelivered event (<see cref="BoundedChannelFullMode.DropOldest"/>); writers never wait.
/// </para>
/// <para>
/// Accounting: <see cref="DroppedCount"/> counts exactly the events the channel overwrote, as reported by its
/// item-dropped callback; it does not count events discarded at shutdown or offered after <see cref="Complete"/>
/// (<see cref="RejectedCount"/>). Counters are monotonic and updated with interlocked operations.
/// </para>
/// <para>
/// Lifetime: <see cref="Complete"/> closes the channel to further events; it is called by
/// <see cref="StreamEventDispatcher"/> when the service stops and by <see cref="Dispose"/>. Thread-safe.
/// </para>
/// </remarks>
public sealed class StreamEventChannel : IStreamEventPublisher, IDisposable
{
    /// <summary>The channel.</summary>
    private readonly Channel<StreamEvent> _channel;

    /// <summary>Metrics.</summary>
    private readonly StreamEventMetrics _metrics;

    /// <summary>Events accepted.</summary>
    private long _published;

    /// <summary>Accepted events overwritten undelivered.</summary>
    private long _dropped;

    /// <summary>Events offered after completion.</summary>
    private long _rejected;

    /// <summary>Initializes a new instance of the <see cref="StreamEventChannel"/> class.</summary>
    /// <param name="options">The capacity.</param>
    /// <param name="metrics">Metrics.</param>
    internal StreamEventChannel(IOptions<StreamEventOptions> options, StreamEventMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);
        _metrics = metrics;
        Capacity = options.Value.QueueCapacity;
        _channel = Channel.CreateBounded<StreamEvent>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            },
            OnDropped);
    }

    /// <summary>Gets the channel capacity in events.</summary>
    public int Capacity { get; }

    /// <summary>Gets the number of events accepted into the channel, including any later overwritten.</summary>
    public long PublishedCount => Interlocked.Read(ref _published);

    /// <summary>Gets the number of accepted events overwritten undelivered because the channel was full.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Gets the number of events offered after the channel was completed.</summary>
    public long RejectedCount => Interlocked.Read(ref _rejected);

    /// <summary>Gets the number of events waiting for the consumer.</summary>
    public int Count => _channel.Reader.Count;

    /// <summary>Gets the reader; only <see cref="StreamEventDispatcher"/> reads.</summary>
    internal ChannelReader<StreamEvent> Reader => _channel.Reader;

    /// <inheritdoc />
    public void Publish(StreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);
        if (_channel.Writer.TryWrite(streamEvent))
        {
            Interlocked.Increment(ref _published);
            _metrics.Published(streamEvent.Type);
        }
        else
        {
            Interlocked.Increment(ref _rejected);
            _metrics.Rejected(streamEvent.Type);
        }
    }

    /// <summary>Closes the channel: later events are rejected; buffered events stay readable until consumed or discarded.</summary>
    /// <returns><see langword="true"/> if this call closed it.</returns>
    public bool Complete() => _channel.Writer.TryComplete();

    /// <inheritdoc />
    public void Dispose() => Complete();

    /// <summary>Counts an event the channel overwrote.</summary>
    /// <param name="dropped">The overwritten event.</param>
    private void OnDropped(StreamEvent dropped)
    {
        Interlocked.Increment(ref _dropped);
        _metrics.Dropped(dropped.Type);
    }
}
