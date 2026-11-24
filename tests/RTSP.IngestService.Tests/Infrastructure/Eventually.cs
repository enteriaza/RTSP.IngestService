// Waits for an asynchronous invariant to become true. The timeout is a safety net that turns a hang into a test
// failure; it is not part of any assertion and is far longer than the expected wait.

namespace RTSP.IngestService.Tests.Infrastructure;

/// <summary>
/// Polls a condition until it holds.
/// </summary>
internal static class Eventually
{
    /// <summary>Upper bound on any wait, so a broken invariant fails the test instead of hanging the run.</summary>
    public static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Waits until <paramref name="condition"/> returns <see langword="true"/>.
    /// </summary>
    /// <param name="condition">The invariant to wait for.</param>
    /// <param name="description">Describes the invariant in the failure message.</param>
    /// <returns>A task that completes when the condition holds.</returns>
    /// <exception cref="TimeoutException">The condition did not hold within <see cref="SafetyTimeout"/>.</exception>
    public static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow + SafetyTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting until {description}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Awaits <paramref name="task"/>, failing if it does not complete within <see cref="SafetyTimeout"/>.
    /// </summary>
    /// <param name="task">The task to await.</param>
    /// <returns>A task that completes when <paramref name="task"/> completes.</returns>
    public static Task WithinSafetyTimeout(this Task task) => task.WaitAsync(SafetyTimeout, TestContext.Current.CancellationToken);

    /// <summary>
    /// Awaits <paramref name="task"/>, failing if it does not complete within <see cref="SafetyTimeout"/>.
    /// </summary>
    /// <typeparam name="T">The task result type.</typeparam>
    /// <param name="task">The task to await.</param>
    /// <returns>The result of <paramref name="task"/>.</returns>
    public static Task<T> WithinSafetyTimeout<T>(this Task<T> task) => task.WaitAsync(SafetyTimeout, TestContext.Current.CancellationToken);
}
