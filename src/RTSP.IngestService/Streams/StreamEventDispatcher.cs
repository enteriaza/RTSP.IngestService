// Delivers stream events from the channel to the registered consumer on a dedicated task, so producers never run
// consumer code. A failing consumer is logged and skipped; a consumer that ignores cancellation is abandoned after a
// fixed stop budget, so neither can affect ingest or delay shutdown indefinitely.

namespace RTSP.IngestService.Streams;

/// <summary>Reads <see cref="StreamEventChannel"/> and hands each event to the <see cref="IStreamEventConsumer"/>, if one is registered.</summary>
/// <remarks>
/// <para>
/// With no consumer the dispatcher reads nothing, so the channel fills and then overwrites its oldest events, which
/// <see cref="StreamEventChannel.DroppedCount"/> counts.
/// </para>
/// <para>
/// Shutdown: <see cref="StopAsync"/> completes the channel (later events are rejected), cancels the consumer and waits
/// at most <see cref="StopTimeout"/> or until the host's shutdown token fires. Undelivered events are discarded.
/// Registered before the HTTP origin and the RTMP listener, so it stops after them and still accepts the events they
/// produce while stopping.
/// </para>
/// </remarks>
public sealed partial class StreamEventDispatcher : BackgroundService
{
    /// <summary>The longest <see cref="StopAsync"/> waits for the consumer to observe cancellation.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The channel.</summary>
    private readonly StreamEventChannel _channel;

    /// <summary>The consumer, if any.</summary>
    private readonly IStreamEventConsumer? _consumer;

    /// <summary>Metrics.</summary>
    private readonly StreamEventMetrics _metrics;

    /// <summary>Clock for the stop budget.</summary>
    private readonly TimeProvider _time;

    /// <summary>Logger.</summary>
    private readonly ILogger<StreamEventDispatcher> _logger;

    /// <summary>1 while the loop is about to call or is inside the consumer; set before the loop's final cancellation check.</summary>
    private int _inConsumer;

    /// <summary>Initializes a new instance of the <see cref="StreamEventDispatcher"/> class.</summary>
    /// <param name="channel">The channel.</param>
    /// <param name="consumer">The consumer, or <see langword="null"/> for none.</param>
    /// <param name="metrics">Metrics.</param>
    /// <param name="time">Clock for the stop budget.</param>
    /// <param name="logger">Logger.</param>
    internal StreamEventDispatcher(StreamEventChannel channel, IStreamEventConsumer? consumer, StreamEventMetrics metrics, TimeProvider time, ILogger<StreamEventDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _channel = channel;
        _consumer = consumer;
        _metrics = metrics;
        _time = time;
        _logger = logger;
    }

    /// <summary>Completes the channel, cancels the consumer and waits for it within the stop budget.</summary>
    /// <param name="cancellationToken">The host's shutdown token.</param>
    /// <returns>A task that completes when the consumer has stopped or the budget is spent.</returns>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Complete();
        using var budget = new CancellationTokenSource(StopTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        await base.StopAsync(linked.Token).ConfigureAwait(false);
        if (ExecuteTask is { IsCompleted: false } loop && Volatile.Read(ref _inConsumer) == 0)
        {
            // Outside the consumer the cancelled loop runs no consumer code and exits promptly, even if the host's
            // shutdown budget is already spent.
            await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        if (ExecuteTask is { IsCompleted: false })
        {
            LogConsumerAbandoned(StopTimeout);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_consumer is null)
        {
            return;
        }

        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var reader = _channel.Reader;
        var discarded = 0;
        try
        {
            while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                while (!stoppingToken.IsCancellationRequested && reader.TryRead(out var streamEvent))
                {
                    Interlocked.Exchange(ref _inConsumer, 1);
                    try
                    {
                        if (stoppingToken.IsCancellationRequested)
                        {
                            discarded = 1;
                            break;
                        }

                        await _consumer.ConsumeAsync(streamEvent, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _metrics.ConsumerFailed(streamEvent.Type);
                        LogConsumerFailed(ex, streamEvent.Type);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _inConsumer, 0);
                    }
                }

                if (discarded > 0)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        LogStopped(reader.Count + discarded);
    }

    /// <summary>Logs a consumer failure.</summary>
    /// <param name="exception">The failure.</param>
    /// <param name="type">The event type.</param>
    [LoggerMessage(EventId = 9100, Level = LogLevel.Warning, Message = "The stream event consumer failed on a {Type} event; the event is skipped")]
    private partial void LogConsumerFailed(Exception exception, StreamEventType type);

    /// <summary>Logs that delivery stopped.</summary>
    /// <param name="discarded">Events left undelivered.</param>
    [LoggerMessage(EventId = 9101, Level = LogLevel.Debug, Message = "Stream event delivery stopped; {Discarded} undelivered events discarded")]
    private partial void LogStopped(int discarded);

    /// <summary>Logs a consumer that did not stop within the budget.</summary>
    /// <param name="budget">The stop budget.</param>
    [LoggerMessage(EventId = 9102, Level = LogLevel.Warning, Message = "The stream event consumer did not stop within {Budget}; it is abandoned")]
    private partial void LogConsumerAbandoned(TimeSpan budget);
}
