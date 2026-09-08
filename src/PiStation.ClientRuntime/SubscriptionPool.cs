namespace PiStation.ClientRuntime;

// A consumer owns its lease; the pool owns the single underlying stream for that key.
internal sealed class SubscriptionPool<TKey, T>(Func<TKey, T> create, Func<T, Func<ValueTask>, T> wrap,
    Action<TKey, T>? retired = null) : IAsyncDisposable where TKey : notnull where T : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, Entry> _entries = [];
    private bool _disposed;
    public int Count { get { lock (_gate) return _entries.Count; } }

    public T Acquire(TKey key)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(key, out var entry)) _entries.Add(key, entry = new(create(key)));
            entry.References++;
            return wrap(entry.Stream, () => ReleaseAsync(key, entry));
        }
    }

    private async ValueTask ReleaseAsync(TKey key, Entry entry)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var current) || !ReferenceEquals(current, entry)) return;
            if (--entry.References > 0) return;
            _entries.Remove(key);
        }
        await entry.Stream.DisposeAsync().ConfigureAwait(false);
        lock (_gate) { if (!_disposed && !_entries.ContainsKey(key)) retired?.Invoke(key, entry.Stream); }
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        lock (_gate) { if (_disposed) return; _disposed = true; entries = [.. _entries.Values]; _entries.Clear(); }
        foreach (var entry in entries) await entry.Stream.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class Entry(T stream) { public T Stream { get; } = stream; public int References { get; set; } }
}
