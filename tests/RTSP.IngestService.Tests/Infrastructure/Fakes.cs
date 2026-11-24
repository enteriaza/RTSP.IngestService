// Test doubles for collaborators that cannot be exercised directly in unit tests: the systemd notification socket,
// the application lifecycle and an options monitor whose value or validation outcome can be changed on demand.

using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Lifecycle;

namespace RTSP.IngestService.Tests.Infrastructure;

/// <summary>
/// Records notifications instead of writing to a systemd socket.
/// </summary>
internal sealed class FakeSystemdNotifier : ISystemdNotifier
{
    /// <summary>Notifications in the order they were sent.</summary>
    private readonly Channel<ServiceState> _notifications = Channel.CreateUnbounded<ServiceState>();

    /// <summary>Gets or sets a value indicating whether notifications are enabled.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the next call to <see cref="Notify"/> throws a <see cref="SocketException"/>.</summary>
    public bool FailNextNotification { get; set; }

    /// <summary>Gets the number of notifications recorded so far.</summary>
    public int Count => _notifications.Reader.Count;

    /// <summary>Records <paramref name="state"/>, or throws once if <see cref="FailNextNotification"/> is set.</summary>
    /// <param name="state">The notification.</param>
    /// <exception cref="SocketException">When <see cref="FailNextNotification"/> was set.</exception>
    public void Notify(ServiceState state)
    {
        if (FailNextNotification)
        {
            FailNextNotification = false;
            throw new SocketException((int)SocketError.ConnectionRefused);
        }

        _notifications.Writer.TryWrite(state);
    }

    /// <summary>Waits for and returns the next recorded notification.</summary>
    /// <returns>The notification.</returns>
    public async Task<ServiceState> NextAsync() =>
        await _notifications.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WithinSafetyTimeout();
}

/// <summary>
/// Lifecycle whose state is set directly by the test.
/// </summary>
internal sealed class StubApplicationLifecycle : IApplicationLifecycle
{
    /// <summary>Gets or sets the reported state.</summary>
    public ApplicationState State { get; set; } = ApplicationState.Running;

    /// <inheritdoc />
    public bool IsReady => State == ApplicationState.Running;

    /// <inheritdoc />
    public DateTimeOffset StateChangedAt { get; set; }

    /// <inheritdoc />
    public Exception? Failure { get; set; }
}

/// <summary>
/// Options monitor whose current value, or validation failure, is controlled by the test.
/// </summary>
/// <typeparam name="TOptions">The options type.</typeparam>
/// <param name="value">The initial value.</param>
internal sealed class TestOptionsMonitor<TOptions>(TOptions value) : IOptionsMonitor<TOptions>
    where TOptions : class
{
    /// <summary>Gets or sets the value returned by <see cref="CurrentValue"/>.</summary>
    public TOptions Value { get; set; } = value;

    /// <summary>Gets or sets a failure that <see cref="CurrentValue"/> throws instead of returning a value.</summary>
    public OptionsValidationException? ValidationFailure { get; set; }

    /// <summary>Gets <see cref="Value"/>, or throws <see cref="ValidationFailure"/> when set.</summary>
    public TOptions CurrentValue => ValidationFailure is { } failure ? throw failure : Value;

    /// <summary>Returns <see cref="CurrentValue"/>, ignoring the name.</summary>
    /// <param name="name">Ignored.</param>
    /// <returns>The current value.</returns>
    public TOptions Get(string? name) => CurrentValue;

    /// <summary>Change notifications are not supported; returns <see langword="null"/>.</summary>
    /// <param name="listener">Ignored.</param>
    /// <returns><see langword="null"/>.</returns>
    public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
}
