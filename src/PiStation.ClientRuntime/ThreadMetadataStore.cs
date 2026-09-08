using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

public enum ThreadMetadataApplyResult
{
    Applied,
    Ignored,
}

public sealed class ThreadMetadataChangedEventArgs(
    ProjectId projectId,
    ThreadId threadId,
    ThreadDescriptor? thread) : EventArgs
{
    public ProjectId ProjectId { get; } = projectId;

    public ThreadId ThreadId { get; } = threadId;

    public ThreadDescriptor? Thread { get; } = thread;
}

public sealed class ThreadMetadataStore
{
    private readonly object _gate = new();
    private readonly Dictionary<ThreadId, ThreadDescriptor> _threads = [];
    private readonly HashSet<ProjectId> _trackedProjectIds = [];

    public event EventHandler<ThreadMetadataChangedEventArgs>? Changed;

    public ThreadDescriptor? GetCurrent(ThreadId threadId)
    {
        lock (_gate)
        {
            return _threads.GetValueOrDefault(threadId);
        }
    }

    public IReadOnlyList<ThreadDescriptor> GetProjectThreads(
        ProjectId projectId,
        bool includeArchived = false)
    {
        lock (_gate)
        {
            return OrderThreads(_threads.Values.Where(thread =>
                thread.ProjectId == projectId && (includeArchived || !thread.IsArchived)));
        }
    }

    public ThreadMetadataApplyResult Apply(ThreadDescriptor thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        lock (_gate)
        {
            _trackedProjectIds.Add(thread.ProjectId);
            if (_threads.TryGetValue(thread.ThreadId, out var current) &&
                IsNewer(current, thread))
            {
                return ThreadMetadataApplyResult.Ignored;
            }

            if (current == thread)
            {
                return ThreadMetadataApplyResult.Ignored;
            }

            _threads[thread.ThreadId] = thread;
        }

        Changed?.Invoke(
            this,
            new ThreadMetadataChangedEventArgs(thread.ProjectId, thread.ThreadId, thread));
        return ThreadMetadataApplyResult.Applied;
    }

    internal IReadOnlyList<ProjectId> TrackedProjectIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _trackedProjectIds];
            }
        }
    }

    internal void ApplyProjectSnapshot(
        ProjectId projectId,
        IReadOnlyList<ThreadDescriptor> threads,
        bool includeArchived,
        bool isComplete)
    {
        ArgumentNullException.ThrowIfNull(threads);
        lock (_gate)
        {
            _trackedProjectIds.Add(projectId);
        }

        foreach (var thread in threads)
        {
            if (thread.ProjectId != projectId)
            {
                throw new InvalidDataException("The thread metadata belongs to another project.");
            }

            Apply(thread);
        }

        if (!isComplete)
        {
            return;
        }

        var returnedIds = threads.Select(static thread => thread.ThreadId).ToHashSet();
        ThreadDescriptor[] removed;
        lock (_gate)
        {
            removed = [.. _threads.Values.Where(thread =>
                thread.ProjectId == projectId &&
                (includeArchived || !thread.IsArchived) &&
                !returnedIds.Contains(thread.ThreadId))];
            foreach (var thread in removed)
            {
                _threads.Remove(thread.ThreadId);
            }
        }

        foreach (var thread in removed)
        {
            Changed?.Invoke(
                this,
                new ThreadMetadataChangedEventArgs(projectId, thread.ThreadId, null));
        }
    }

    internal void RemoveProject(ProjectId projectId)
    {
        ThreadDescriptor[] removed;
        lock (_gate)
        {
            _trackedProjectIds.Remove(projectId);
            removed = [.. _threads.Values.Where(thread => thread.ProjectId == projectId)];
            foreach (var thread in removed)
            {
                _threads.Remove(thread.ThreadId);
            }
        }

        foreach (var thread in removed)
        {
            Changed?.Invoke(
                this,
                new ThreadMetadataChangedEventArgs(projectId, thread.ThreadId, null));
        }
    }

    internal void Remove(ThreadId threadId)
    {
        ThreadDescriptor? removed;
        lock (_gate) _threads.Remove(threadId, out removed);
        if (removed is not null) Changed?.Invoke(this, new(removed.ProjectId, threadId, null));
    }

    internal void Clear()
    {
        ThreadDescriptor[] removed;
        lock (_gate)
        {
            removed = [.. _threads.Values];
            _threads.Clear();
            _trackedProjectIds.Clear();
        }

        foreach (var thread in removed)
        {
            Changed?.Invoke(
                this,
                new ThreadMetadataChangedEventArgs(thread.ProjectId, thread.ThreadId, null));
        }
    }

    private static bool IsNewer(ThreadDescriptor current, ThreadDescriptor candidate) =>
        current.Revision > candidate.Revision ||
        current.Revision == candidate.Revision && current.UpdatedUtc > candidate.UpdatedUtc;

    private static ThreadDescriptor[] OrderThreads(IEnumerable<ThreadDescriptor> threads) =>
        [.. threads
            .OrderBy(static thread => thread.IsArchived)
            .ThenByDescending(static thread => thread.IsPinned)
            .ThenByDescending(static thread => thread.UpdatedUtc)
            .ThenBy(static thread => thread.ThreadId.Value, StringComparer.Ordinal)];
}
