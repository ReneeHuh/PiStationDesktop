using PiStation.Host.Errors;
using PiStation.PiRpc.Sessions;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Projections;

namespace PiStation.Host.Threads;

public sealed partial class PiThreadController
{
    public async Task<T> WithSessionAsync<T>(Func<PiSessionDocument, string, Task<T>> action, CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is null || Journal.Projection.RuntimeState != ThreadRuntimeState.Ready ||
                Journal.Projection.Timeline.Any(item => item is ApprovalTimelineItem { State: InteractionState.Pending } or QuestionTimelineItem { State: InteractionState.Pending }))
                throw new HostOperationException(ProtocolErrorCodes.ThreadBusy, "Finish the active turn and interactions before inspecting, forking or exporting its session.");
            var state = await _process.Connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
            if (state.SessionFile is not { } path || !File.Exists(path))
                throw new InvalidDataException("This thread has no saved Pi session yet. Complete a turn first.");
            ValidateSessionFile(path);
            var document = await PiSessionDocument.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (document.SessionId != _thread.PiSessionId) throw new InvalidDataException("The session file no longer belongs to this thread.");
            TouchRuntime();
            return await action(document, path).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }
}
