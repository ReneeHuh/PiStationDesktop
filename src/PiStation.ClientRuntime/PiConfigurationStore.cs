using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

public enum PiConfigurationApplyResult
{
    Applied,
    Ignored,
}

public sealed class PiConfigurationChangedEventArgs(
    ThreadId threadId,
    ThreadPiConfigurationSnapshot? snapshot) : EventArgs
{
    public ThreadId ThreadId { get; } = threadId;

    public ThreadPiConfigurationSnapshot? Snapshot { get; } = snapshot;
}

public sealed class PiConfigurationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<ThreadId, ThreadPiConfigurationSnapshot> _snapshots = [];

    public event EventHandler<PiConfigurationChangedEventArgs>? Changed;

    public ThreadPiConfigurationSnapshot? GetCurrent(ThreadId threadId)
    {
        lock (_gate)
        {
            return _snapshots.GetValueOrDefault(threadId);
        }
    }

    public PiConfigurationApplyResult Apply(ThreadPiConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var threadId = snapshot.Configuration.ThreadId;
        lock (_gate)
        {
            if (_snapshots.TryGetValue(threadId, out var current) &&
                current.Configuration.Revision > snapshot.Configuration.Revision)
            {
                return PiConfigurationApplyResult.Ignored;
            }

            _snapshots[threadId] = snapshot;
        }

        Changed?.Invoke(this, new PiConfigurationChangedEventArgs(threadId, snapshot));
        return PiConfigurationApplyResult.Applied;
    }

    internal IReadOnlyList<ThreadId> TrackedThreadIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _snapshots.Keys];
            }
        }
    }

    internal void Remove(ThreadId threadId)
    {
        lock (_gate)
        {
            if (!_snapshots.Remove(threadId))
            {
                return;
            }
        }

        Changed?.Invoke(this, new PiConfigurationChangedEventArgs(threadId, null));
    }

    internal void Clear()
    {
        ThreadId[] threadIds;
        lock (_gate)
        {
            threadIds = [.. _snapshots.Keys];
            _snapshots.Clear();
        }

        foreach (var threadId in threadIds)
        {
            Changed?.Invoke(this, new PiConfigurationChangedEventArgs(threadId, null));
        }
    }
}
