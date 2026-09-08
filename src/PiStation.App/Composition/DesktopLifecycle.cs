namespace PiStation.App.Composition;

/// <summary>Small, UI-independent shutdown policy used by desktop runtimes.</summary>
internal static class DesktopLifecycle
{
    public static async Task ShutdownAsync(
        Func<CancellationToken, Task>? finalFlush,
        IReadOnlyList<Func<Task>> cleanup,
        TimeSpan flushTimeout,
        bool flush)
    {
        if (flush && finalFlush is not null)
        {
            using var timeout = new CancellationTokenSource(flushTimeout);
            try { await finalFlush(timeout.Token).ConfigureAwait(false); }
            catch (Exception) { }
        }

        foreach (var release in cleanup)
        {
            try { await release().ConfigureAwait(false); }
            catch (Exception) { }
        }
    }

    public static bool ProfileChanged<T>(T? previous, T current) =>
        previous is not null && !EqualityComparer<T>.Default.Equals(previous, current);

    public static async Task StopSharingAsync(Func<Task>? disposeListener, Action persistDisabled)
    {
        Exception? failure = null;
        if (disposeListener is not null)
        {
            try { await disposeListener().ConfigureAwait(false); }
            catch (Exception exception) { failure = exception; }
        }
        try { persistDisabled(); }
        catch (Exception exception)
        {
            var persistenceFailure = new IOException(
                "The disabled sharing setting could not be saved. Sharing may resume on the next app launch.", exception);
            failure = failure is null
                ? new IOException("Sharing stopped. " + persistenceFailure.Message, exception)
                : new AggregateException("Remote sharing shutdown and settings persistence failed.", failure, persistenceFailure);
        }
        if (failure is not null) throw failure;
    }
}
