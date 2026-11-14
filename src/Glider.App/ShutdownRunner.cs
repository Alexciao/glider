namespace Glider.App;

/// <summary>
/// Blocking, time-boxed disposal for process exit. The host quits on its STA thread, whose <see cref="SynchronizationContext"/> is
/// still the WPF dispatcher's after <c>Application.Run</c> returned, and that dispatcher no longer pumps. Anything awaited
/// on that context never resumes, so the disposal runs on a pool thread (no context) instead.
/// </summary>
internal static class ShutdownRunner
{
    /// <returns>True when <paramref name="dispose"/> finished within <paramref name="timeout"/>; false when it timed out or threw (reported to <paramref name="onError"/>).</returns>
    public static bool DisposeBlocking(Func<ValueTask> dispose, TimeSpan timeout, Action<Exception>? onError = null)
    {
        try
        {
            return Task.Run(async () => await dispose().ConfigureAwait(false)).Wait(timeout);
        }
        catch (AggregateException ex)
        {
            onError?.Invoke(ex.InnerException ?? ex);
            return false;
        }
    }
}
