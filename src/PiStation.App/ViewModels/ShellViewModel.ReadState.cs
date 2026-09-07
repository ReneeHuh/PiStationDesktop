using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Receipts;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    private readonly SemaphoreSlim _readStateGate = new(1, 1);
    private long _readVisitVersion;
    private long _lastReadRequested;
    private long _manualUnreadThrough;
    private bool _readWindowActive;

    public void SetReadWindowActive(bool active)
    {
        _readWindowActive = active;
        if (active)
        {
            _lastReadRequested = 0;
            _ = ReadDisplayedCompletionAsync(Thread.Projection);
        }
    }

    private void BeginReadVisit()
    {
        _readVisitVersion++;
        _lastReadRequested = 0;
        _manualUnreadThrough = 0;
    }

    private async Task ReadDisplayedCompletionAsync(ThreadProjection? projection)
    {
        if (!_readWindowActive || projection is null || projection.RuntimeState != ThreadRuntimeState.Ready ||
            projection.ThreadId != SelectedThread?.ThreadId || projection.CompletionSequence <= _lastReadRequested ||
            projection.CompletionSequence <= _manualUnreadThrough) return;
        var sequence = projection.CompletionSequence;
        var visit = _readVisitVersion;
        var client = _client;
        if (client is null) return;
        _lastReadRequested = sequence;
        await _readStateGate.WaitAsync();
        try
        {
            if (!_readWindowActive || visit != _readVisitVersion || !ReferenceEquals(client, _client) ||
                SelectedThread?.ThreadId != projection.ThreadId || sequence <= _manualUnreadThrough) return;
            var result = await client.SetThreadReadStateAsync(projection.ThreadId, sequence);
            if (result.Receipt.State != CommandReceiptState.Completed)
                throw new InvalidOperationException("The host could not confirm the read state. Reopen the thread to retry.");
            if (result.Thread is { } thread) ApplyReadMetadata(thread);
        }
        catch (Exception exception)
        {
            if (visit == _readVisitVersion && _lastReadRequested == sequence) _lastReadRequested = 0;
            ReportRuntimeError($"Could not save thread read state: {exception.Message}");
        }
        finally { _readStateGate.Release(); }
    }

    public async Task MarkThreadsUnreadAsync(IReadOnlyList<ThreadDescriptor> threads)
    {
        // Suppress queued automatic reads immediately. An already-dispatched read
        // finishes first, then this explicit action wins through the same gate.
        foreach (var thread in threads)
            if (SelectedThread?.ThreadId == thread.ThreadId)
                _manualUnreadThrough = Math.Max(_manualUnreadThrough, thread.CompletionSequence);
        await _readStateGate.WaitAsync();
        try
        {
            var client = RequireClient();
            foreach (var thread in threads.DistinctBy(thread => thread.ThreadId))
            {
                if (thread.CompletionSequence == 0) continue;
                var result = await client.SetThreadReadStateAsync(thread.ThreadId, thread.CompletionSequence, true);
                if (result.Receipt.State != CommandReceiptState.Completed)
                    throw new InvalidOperationException($"Could not confirm Mark unread for {thread.Title}.");
                if (result.Thread is { } updated) ApplyReadMetadata(updated);
            }
            ThreadLifecycleStatus = "Marked selected completed threads unread";
        }
        catch (Exception exception) { ReportRuntimeError(exception); }
        finally { _readStateGate.Release(); }
    }

    private void ApplyReadMetadata(ThreadDescriptor thread) => RunOnUiThread(() =>
    {
        var index = Threads.ToList().FindIndex(item => item.ThreadId == thread.ThreadId);
        if (index >= 0 && Threads[index] != thread && Threads[index].Revision <= thread.Revision) Threads[index] = thread;
        if (SelectedThread?.ThreadId == thread.ThreadId && SelectedThread.Revision <= thread.Revision) SelectedThread = thread;
    });
}
