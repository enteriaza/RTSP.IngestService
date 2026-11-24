// An IStreamEventPublisher that records every event in publication order, so lifecycle tests can assert exactly
// which events were emitted, when and in what order, without a channel or consumer in between.

using System.Collections.Concurrent;
using RTSP.IngestService.Streams;

namespace RTSP.IngestService.Tests.Infrastructure;

/// <summary>Records published stream events.</summary>
internal sealed class RecordingStreamEventPublisher : IStreamEventPublisher
{
    /// <summary>The events in publication order.</summary>
    private readonly ConcurrentQueue<StreamEvent> _events = new();

    /// <summary>Gets a snapshot of the events published so far.</summary>
    public IReadOnlyList<StreamEvent> Events => [.. _events];

    /// <summary>Gets the types of the events published so far.</summary>
    public IReadOnlyList<StreamEventType> Types => [.. _events.Select(static e => e.Type)];

    /// <summary>Records an event.</summary>
    /// <param name="streamEvent">The event.</param>
    public void Publish(StreamEvent streamEvent) => _events.Enqueue(streamEvent);
}
