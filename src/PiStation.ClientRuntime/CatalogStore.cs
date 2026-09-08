using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Streaming;

namespace PiStation.ClientRuntime;

public sealed class CatalogStore
{
    private readonly object _gate = new();
    private Dictionary<ProjectId, ProjectDescriptor> _projects = [];
    private Dictionary<ThreadId, ThreadDescriptor> _threads = [];
    private Dictionary<ProjectId, ProjectDescriptor>? _pendingProjects;
    private Dictionary<ThreadId, ThreadDescriptor>? _pendingThreads;
    private readonly HashSet<ProjectId> _pendingRemovedProjects = [];
    private readonly HashSet<ThreadId> _pendingRemovedThreads = [];
    private CatalogCursor? _pendingCursor;
    private CatalogCursor? _cursor;
    private bool _isSynchronized;
    public bool IsSynchronized { get { lock (_gate) return _isSynchronized; } }
    internal void SetDisconnected() { lock (_gate) _isSynchronized = false; }
    public event EventHandler? Changed;
    public CatalogCursor? Cursor { get { lock (_gate) return _cursor; } }
    public IReadOnlyList<ProjectDescriptor> Projects { get { lock (_gate) return [.. _projects.Values.OrderBy(p => p.DisplayName)]; } }
    public IReadOnlyList<ThreadDescriptor> Threads { get { lock (_gate) return [.. _threads.Values]; } }

    internal void AbandonTransfer()
    {
        lock (_gate)
        {
            _pendingProjects = null; _pendingThreads = null; _pendingCursor = null;
            _pendingRemovedProjects.Clear(); _pendingRemovedThreads.Clear();
        }
    }

    internal void Apply(CatalogBatch batch, EnvironmentId expected, ThreadMetadataStore metadata)
    {
        if (batch.EnvironmentId != expected || batch.Sequence < 0)
            throw new InvalidDataException("The catalog belongs to a different environment or has an invalid cursor.");
        var complete = false;
        lock (_gate)
        {
            var cursor = new CatalogCursor(batch.Epoch, batch.Sequence);
            if (batch.Reset || _pendingProjects is null)
            {
                if (!batch.Reset && _cursor is not null && (_cursor.Epoch != batch.Epoch || batch.Sequence < _cursor.Sequence))
                    throw new InvalidDataException("The catalog requires a new snapshot.");
                _pendingProjects = batch.Reset ? [] : new(_projects);
                _pendingThreads = batch.Reset ? [] : new(_threads);
                _pendingCursor = cursor;
                _pendingRemovedProjects.Clear();
                _pendingRemovedThreads.Clear();
            }
            if (_pendingCursor != cursor) throw new InvalidDataException("Catalog pages have inconsistent watermarks.");
            foreach (var project in batch.Projects)
            {
                if (project.EnvironmentId != expected) throw new InvalidDataException("Invalid project identity.");
                _pendingProjects[project.ProjectId] = project;
            }
            foreach (var thread in batch.Threads)
            {
                if (thread.EnvironmentId != expected) throw new InvalidDataException("Invalid thread identity.");
                _pendingThreads![thread.ThreadId] = thread;
            }
            foreach (var id in batch.RemovedProjects)
            {
                _pendingRemovedProjects.Add(id);
                _pendingProjects.Remove(id);
                foreach (var thread in _pendingThreads!.Values.Where(t => t.ProjectId == id).ToArray()) _pendingThreads.Remove(thread.ThreadId);
            }
            foreach (var id in batch.RemovedThreads)
            {
                _pendingRemovedThreads.Add(id);
                _pendingThreads!.Remove(id);
            }
            if (batch.Complete)
            {
                // A command reply may populate metadata before its creation event reaches
                // this catalog. Explicit tombstones must remove those entries as well.
                foreach (var id in _projects.Keys.Except(_pendingProjects.Keys).Union(_pendingRemovedProjects)) metadata.RemoveProject(id);
                foreach (var id in _threads.Keys.Except(_pendingThreads!.Keys).Union(_pendingRemovedThreads)) metadata.Remove(id);
                foreach (var thread in _pendingThreads.Values.ToArray())
                {
                    metadata.Apply(thread);
                    _pendingThreads[thread.ThreadId] = metadata.GetCurrent(thread.ThreadId)!;
                }
                _projects = _pendingProjects;
                _threads = _pendingThreads;
                _cursor = cursor;
                _isSynchronized = true;
                _pendingProjects = null;
                _pendingThreads = null;
                _pendingCursor = null;
                complete = true;
            }
        }
        if (complete) Changed?.Invoke(this, EventArgs.Empty);
    }
}
