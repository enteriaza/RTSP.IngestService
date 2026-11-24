// Hosted services used to observe and perturb the host lifecycle in tests: recording start/stop order and the
// lifecycle state seen at each step, failing on start, blocking start or stop, faulting in the background, and
// ignoring cancellation.

using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using RTSP.IngestService.Lifecycle;

namespace RTSP.IngestService.Tests.Infrastructure;

/// <summary>
/// Shared, thread-safe journal of lifecycle events written by test hosted services.
/// </summary>
internal sealed class LifecycleJournal
{
    /// <summary>Recorded events in the order they occurred.</summary>
    private readonly ConcurrentQueue<string> _events = new();

    /// <summary>Gets a snapshot of the recorded events.</summary>
    public IReadOnlyList<string> Events => [.. _events];

    /// <summary>Gets or sets the lifecycle captured by <see cref="RecordingService"/>, for assertions after a run.</summary>
    public IApplicationLifecycle? Lifecycle { get; set; }

    /// <summary>Records an event.</summary>
    /// <param name="entry">The event description.</param>
    public void Add(string entry) => _events.Enqueue(entry);
}

/// <summary>
/// Records its start and stop calls together with the lifecycle state and readiness observed at that moment.
/// </summary>
/// <param name="name">Name written to the journal.</param>
/// <param name="journal">The shared journal.</param>
/// <param name="lifecycle">The application lifecycle.</param>
internal class RecordingService(string name, LifecycleJournal journal, IApplicationLifecycle lifecycle) : IHostedService
{
    /// <summary>Gets the shared journal.</summary>
    protected LifecycleJournal Journal { get; } = InitializeJournal(journal, lifecycle);

    /// <summary>Records <c>{name}:start:{state}:{ready}</c>.</summary>
    /// <param name="cancellationToken">Not observed.</param>
    /// <returns>A completed task.</returns>
    public virtual Task StartAsync(CancellationToken cancellationToken)
    {
        Journal.Add($"{name}:start:{lifecycle.State}:{lifecycle.IsReady}");
        return Task.CompletedTask;
    }

    /// <summary>Records <c>{name}:stop:{state}:{ready}</c>.</summary>
    /// <param name="cancellationToken">Not observed.</param>
    /// <returns>A completed task.</returns>
    public virtual Task StopAsync(CancellationToken cancellationToken)
    {
        Journal.Add($"{name}:stop:{lifecycle.State}:{lifecycle.IsReady}");
        return Task.CompletedTask;
    }

    /// <summary>Stores the lifecycle on the journal so tests can inspect it after the host is disposed.</summary>
    /// <param name="journal">The shared journal.</param>
    /// <param name="lifecycle">The application lifecycle.</param>
    /// <returns><paramref name="journal"/>.</returns>
    private static LifecycleJournal InitializeJournal(LifecycleJournal journal, IApplicationLifecycle lifecycle)
    {
        journal.Lifecycle = lifecycle;
        return journal;
    }
}

/// <summary>First recording service in a three-service ordering test.</summary>
/// <param name="journal">The shared journal.</param>
/// <param name="lifecycle">The application lifecycle.</param>
internal sealed class RecordingServiceA(LifecycleJournal journal, IApplicationLifecycle lifecycle) : RecordingService("A", journal, lifecycle);

/// <summary>Second recording service in a three-service ordering test.</summary>
/// <param name="journal">The shared journal.</param>
/// <param name="lifecycle">The application lifecycle.</param>
internal sealed class RecordingServiceB(LifecycleJournal journal, IApplicationLifecycle lifecycle) : RecordingService("B", journal, lifecycle);

/// <summary>Third recording service in a three-service ordering test.</summary>
/// <param name="journal">The shared journal.</param>
/// <param name="lifecycle">The application lifecycle.</param>
internal sealed class RecordingServiceC(LifecycleJournal journal, IApplicationLifecycle lifecycle) : RecordingService("C", journal, lifecycle);

/// <summary>Throws <see cref="InvalidOperationException"/> from <see cref="StartAsync"/>.</summary>
/// <param name="journal">The shared journal.</param>
/// <param name="lifecycle">The application lifecycle.</param>
internal sealed class FailingStartService(LifecycleJournal journal, IApplicationLifecycle lifecycle) : RecordingService("Failing", journal, lifecycle)
{
    /// <summary>Message of the thrown exception.</summary>
    public const string FailureMessage = "Simulated startup failure";

    /// <summary>Records the call and throws.</summary>
    /// <param name="cancellationToken">Not observed.</param>
    /// <returns>Never returns normally.</returns>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _ = base.StartAsync(cancellationToken);
        throw new InvalidOperationException(FailureMessage);
    }
}

/// <summary>
/// Blocks in <see cref="StartAsync"/> until its token is cancelled, optionally requesting application shutdown first.
/// </summary>
/// <param name="journal">The shared journal.</param>
/// <param name="lifecycle">The application lifecycle.</param>
/// <param name="applicationLifetime">Used to request shutdown when <see cref="RequestStopDuringStart"/> is set.</param>
/// <param name="settings">Controls whether shutdown is requested.</param>
internal sealed class BlockingStartService(
    LifecycleJournal journal,
    IApplicationLifecycle lifecycle,
    IHostApplicationLifetime applicationLifetime,
    BlockingStartSettings settings) : RecordingService("BlockingStart", journal, lifecycle)
{
    /// <summary>Gets a value indicating whether shutdown is requested from within startup.</summary>
    private bool RequestStopDuringStart => settings.RequestStopDuringStart;

    /// <summary>Records the call, optionally requests shutdown, then waits for cancellation.</summary>
    /// <param name="cancellationToken">The startup token, cancelled by shutdown requests and the startup timeout.</param>
    /// <returns>A task that is cancelled when <paramref name="cancellationToken"/> is cancelled.</returns>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);
        if (RequestStopDuringStart)
        {
            applicationLifetime.StopApplication();
        }

        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}

/// <summary>Settings for <see cref="BlockingStartService"/>.</summary>
/// <param name="RequestStopDuringStart">Whether the service requests shutdown while starting.</param>
internal sealed record BlockingStartSettings(bool RequestStopDuringStart);

/// <summary>
/// Blocks in <see cref="StopAsync"/> until its token is cancelled by the shutdown timeout, recording that it was.
/// </summary>
/// <param name="journal">The shared journal.</param>
/// <param name="lifecycle">The application lifecycle.</param>
internal sealed class BlockingStopService(LifecycleJournal journal, IApplicationLifecycle lifecycle) : RecordingService("BlockingStop", journal, lifecycle)
{
    /// <summary>Records the call, waits for the shutdown token to be cancelled, then records the cancellation.</summary>
    /// <param name="cancellationToken">The shutdown token, cancelled when the shutdown timeout expires.</param>
    /// <returns>A task that completes after cancellation has been observed.</returns>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Journal.Add("BlockingStop:stop-token-cancelled");
        }
    }
}

/// <summary>Background service that waits on its stopping token and records when cancellation arrives.</summary>
/// <param name="journal">The shared journal.</param>
internal sealed class CancellationObservingService(LifecycleJournal journal) : BackgroundService
{
    /// <summary>Records start, waits for cancellation, records it and rethrows.</summary>
    /// <param name="stoppingToken">Cancelled when the host stops this service.</param>
    /// <returns>A task that is cancelled when the service stops.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        journal.Add("Observer:executing");
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            journal.Add("Observer:cancelled");
            throw;
        }
    }
}

/// <summary>Background service that faults once the application is running.</summary>
/// <param name="applicationLifetime">Used to wait until the application has started.</param>
internal sealed class FaultingBackgroundService(IHostApplicationLifetime applicationLifetime) : BackgroundService
{
    /// <summary>Message of the thrown exception.</summary>
    public const string FailureMessage = "Simulated background failure";

    /// <summary>Waits for the application to start, then throws.</summary>
    /// <param name="stoppingToken">Cancelled when the host stops this service.</param>
    /// <returns>A faulted task.</returns>
    /// <exception cref="InvalidOperationException">Always, once the application has started.</exception>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (applicationLifetime.ApplicationStarted.Register(() => started.TrySetResult()))
        {
            await started.Task.WaitAsync(stoppingToken);
        }

        throw new InvalidOperationException(FailureMessage);
    }
}

/// <summary>Background service that ignores its stopping token until <see cref="Release"/> is called.</summary>
internal sealed class UncooperativeBackgroundService : BackgroundService
{
    /// <summary>Completes the execution when signalled.</summary>
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when <see cref="ExecuteAsync"/> has been entered.</summary>
    private readonly TaskCompletionSource _executionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets a task that completes once <see cref="ExecuteAsync"/> has been entered. The host may cancel a background
    /// service before its execution begins, so tests must await this before stopping the host.
    /// </summary>
    public Task ExecutionStarted => _executionStarted.Task;

    /// <summary>Allows the execution to finish, so tests can clean up.</summary>
    public void Release() => _release.TrySetResult();

    /// <summary>Signals <see cref="ExecutionStarted"/>, then waits for <see cref="Release"/> regardless of <paramref name="stoppingToken"/>.</summary>
    /// <param name="stoppingToken">Deliberately ignored.</param>
    /// <returns>A task that completes when <see cref="Release"/> is called.</returns>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _executionStarted.TrySetResult();
        return _release.Task;
    }
}
