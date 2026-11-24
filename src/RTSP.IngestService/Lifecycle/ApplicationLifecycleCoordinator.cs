// Bridges Generic Host lifecycle events onto ApplicationLifecycle. It is registered as the first hosted service, so
// its StartingAsync runs before any other service starts and its StoppedAsync runs after every other service has
// stopped; at that point it inspects background services for faults and for work abandoned at the shutdown timeout.


namespace RTSP.IngestService.Lifecycle;

/// <summary>
/// Maps host lifecycle events to <see cref="ApplicationLifecycle"/> transitions:
/// <list type="bullet">
///   <item><description><see cref="StartingAsync"/> → <see cref="ApplicationState.Starting"/>.</description></item>
///   <item><description><see cref="IHostApplicationLifetime.ApplicationStarted"/> → <see cref="ApplicationState.Running"/>.</description></item>
///   <item><description><see cref="IHostApplicationLifetime.ApplicationStopping"/> or <see cref="StoppingAsync"/> → <see cref="ApplicationState.Stopping"/>.</description></item>
///   <item><description><see cref="StoppedAsync"/> → <see cref="ApplicationState.Stopped"/>, or <see cref="ApplicationState.Failed"/> if any <see cref="BackgroundService"/> faulted.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// The coordinator never initiates start or stop; it only observes the host. It relies on sequential hosted-service
/// start and stop (the host default, enforced by the hosting registration) and on being registered first.
/// </remarks>
public sealed partial class ApplicationLifecycleCoordinator : IHostedLifecycleService, IDisposable
{
    /// <summary>The lifecycle state machine being driven.</summary>
    private readonly ApplicationLifecycle _lifecycle;

    /// <summary>Source of the host's started and stopping notifications.</summary>
    private readonly IHostApplicationLifetime _applicationLifetime;

    /// <summary>Used to enumerate hosted services after shutdown; resolving them in the constructor would be circular.</summary>
    private readonly IServiceProvider _services;

    /// <summary>Logger for abandoned and faulted background services.</summary>
    private readonly ILogger<ApplicationLifecycleCoordinator> _logger;

    /// <summary>Registration on <see cref="IHostApplicationLifetime.ApplicationStarted"/>.</summary>
    private CancellationTokenRegistration _startedRegistration;

    /// <summary>Registration on <see cref="IHostApplicationLifetime.ApplicationStopping"/>.</summary>
    private CancellationTokenRegistration _stoppingRegistration;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApplicationLifecycleCoordinator"/> class.
    /// </summary>
    /// <param name="lifecycle">The lifecycle state machine to drive.</param>
    /// <param name="applicationLifetime">The host application lifetime.</param>
    /// <param name="services">The root service provider, used to enumerate hosted services during shutdown.</param>
    /// <param name="logger">Logger for background service diagnostics.</param>
    public ApplicationLifecycleCoordinator(
        ApplicationLifecycle lifecycle,
        IHostApplicationLifetime applicationLifetime,
        IServiceProvider services,
        ILogger<ApplicationLifecycleCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        _lifecycle = lifecycle;
        _applicationLifetime = applicationLifetime;
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Enters <see cref="ApplicationState.Starting"/> and subscribes to the host's started and stopping
    /// notifications. If shutdown was already requested, the stopping callback runs immediately.
    /// </summary>
    /// <param name="cancellationToken">Not observed; the method completes synchronously.</param>
    /// <returns>A completed task.</returns>
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        _lifecycle.TryTransitionTo(ApplicationState.Starting);

        _startedRegistration = _applicationLifetime.ApplicationStarted.Register(
            static state => ((ApplicationLifecycle)state!).TryTransitionTo(ApplicationState.Running), _lifecycle);
        _stoppingRegistration = _applicationLifetime.ApplicationStopping.Register(
            static state => ((ApplicationLifecycle)state!).TryTransitionTo(ApplicationState.Stopping), _lifecycle);

        return Task.CompletedTask;
    }

    /// <summary>Performs no work; readiness is signalled by <see cref="IHostApplicationLifetime.ApplicationStarted"/>.</summary>
    /// <param name="cancellationToken">Not observed.</param>
    /// <returns>A completed task.</returns>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Performs no work. Other services' <c>StartedAsync</c> methods run after this one, so readiness is deferred to
    /// <see cref="IHostApplicationLifetime.ApplicationStarted"/>.
    /// </summary>
    /// <param name="cancellationToken">Not observed.</param>
    /// <returns>A completed task.</returns>
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Enters <see cref="ApplicationState.Stopping"/> if the <see cref="IHostApplicationLifetime.ApplicationStopping"/>
    /// callback has not already done so (for example when <c>IHost.StopAsync</c> is called directly).
    /// </summary>
    /// <param name="cancellationToken">Not observed.</param>
    /// <returns>A completed task.</returns>
    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        _lifecycle.TryTransitionTo(ApplicationState.Stopping);
        return Task.CompletedTask;
    }

    /// <summary>Performs no work.</summary>
    /// <param name="cancellationToken">Not observed.</param>
    /// <returns>A completed task.</returns>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Runs after every other hosted service has stopped. Logs a warning for each <see cref="BackgroundService"/>
    /// whose execution had not completed (abandoned at the shutdown timeout), then enters
    /// <see cref="ApplicationState.Failed"/> if any background service faulted, otherwise
    /// <see cref="ApplicationState.Stopped"/>.
    /// </summary>
    /// <param name="cancellationToken">Not observed.</param>
    /// <returns>A completed task.</returns>
    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        List<Exception>? faults = null;

        foreach (var service in _services.GetServices<IHostedService>())
        {
            if (service is not BackgroundService { ExecuteTask: { } executeTask })
            {
                continue;
            }

            if (executeTask.IsFaulted)
            {
                faults ??= [];
                faults.AddRange(executeTask.Exception.InnerExceptions);
            }
            else if (!executeTask.IsCompleted)
            {
                LogBackgroundServiceAbandoned(service.GetType().FullName);
            }
        }

        if (faults is null)
        {
            _lifecycle.TryTransitionTo(ApplicationState.Stopped);
        }
        else
        {
            _lifecycle.TryFail(faults.Count == 1 ? faults[0] : new AggregateException("One or more background services faulted.", faults));
        }

        return Task.CompletedTask;
    }

    /// <summary>Removes the host lifetime subscriptions.</summary>
    public void Dispose()
    {
        _startedRegistration.Dispose();
        _stoppingRegistration.Dispose();
    }

    /// <summary>Logs a background service that did not finish before the shutdown timeout expired.</summary>
    /// <param name="serviceType">Full type name of the background service.</param>
    [LoggerMessage(EventId = 1100, Level = LogLevel.Warning, Message = "Background service {ServiceType} did not complete before the shutdown timeout expired; its remaining work was abandoned")]
    private partial void LogBackgroundServiceAbandoned(string? serviceType);
}
