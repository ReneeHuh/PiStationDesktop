using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiStation.Host.Errors;
using PiStation.PiRpc.Process;
using PiStation.PiRpc.Sessions;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Serialization;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Threads;

public sealed partial class PiThreadController
{
    private readonly SemaphoreSlim _navigationControl = new(1, 1);
    private (Guid Id, PiProcess Process)? _navigation;
    private long _navigationGeneration;
    internal long NavigationGeneration => Interlocked.Read(ref _navigationGeneration);

    public async Task<NavigatePiSessionResult> NavigateSessionAsync(NavigatePiSessionRequest request, CancellationToken cancellationToken)
    {
        if (request.ThreadId != _thread.ThreadId || request.OperationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.EntryId) || request.EntryId.Length > 256 ||
            string.IsNullOrWhiteSpace(request.ExpectedRevision) || request.ExpectedRevision.Length != 64 ||
            request.CustomInstructions?.Length > 16384)
            throw new InvalidDataException("Refresh the session tree and select a conversation point.");
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        var started = false;
        try
        {
            if (_process is null || Journal.Projection.RuntimeState != ThreadRuntimeState.Ready ||
                Journal.Projection.ShellExecution?.IsActive == true ||
                Journal.Projection.Timeline.Any(item => item is ApprovalTimelineItem { State: InteractionState.Pending } or QuestionTimelineItem { State: InteractionState.Pending }))
                throw new HostOperationException(ProtocolErrorCodes.ThreadBusy, "Finish active work and pending interactions before switching branches.");
            var state = await _process.Connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
            if (state.IsStreaming || state.IsCompacting || state.PendingMessageCount != 0)
                throw new HostOperationException(ProtocolErrorCodes.ThreadBusy, "Finish the active turn, compaction and queued prompts before switching branches.");
            ValidateSessionFile(state.SessionFile);
            var path = state.SessionFile ?? throw new InvalidDataException("Complete a turn before switching branches.");
            var document = await PiSessionDocument.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (document.SessionId != _thread.PiSessionId) throw new InvalidDataException("The active session no longer belongs to this thread.");
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, ProtocolJsonContext.Default.NavigatePiSessionRequest)));
            var operationId = request.OperationId.ToString("N");
            var previous = document.Entries.FirstOrDefault(entry => entry["customType"]?.ToString() == "pistation.branch-navigation" &&
                entry["data"]?["operationId"]?.ToString() == operationId);
            if (previous is not null)
            {
                if (previous["data"]?["requestHash"]?.ToString() != hash) throw new InvalidDataException("This navigation identity was used for another selection.");
                return new(EnvironmentService.CreateSessionSnapshot(_thread.ThreadId, path, document), false,
                    await ResolveNavigationPromptAsync(previous["data"]?["result"]?["editorText"]?.GetValue<string>(), cancellationToken).ConfigureAwait(false));
            }
            if (request.ExpectedRevision != document.Revision || !document.Entries.Any(entry => entry["id"]?.ToString() == request.EntryId))
                throw new InvalidDataException("The session changed. Refresh its tree before switching branches.");
            cancellationToken.ThrowIfCancellationRequested();
            // Once accepted, transport disconnection cannot abandon the operation halfway through.
            // The durable Pi marker makes a retry with this identity safe after a lost response.
            await _navigationControl.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { _navigation = (request.OperationId, _process); }
            finally { _navigationControl.Release(); }
            started = true;
            Interlocked.Increment(ref _navigationGeneration);
            Journal.Commit(new RuntimeStateChangedEvent(ThreadRuntimeState.Hydrating));
            var result = await _process.Connection.NavigateSessionAsync(new JsonObject
            {
                ["operationId"] = operationId, ["requestHash"] = hash, ["sessionId"] = document.SessionId,
                ["entryId"] = request.EntryId, ["expectedRevision"] = request.ExpectedRevision,
                ["summarize"] = request.Summarize, ["customInstructions"] = request.CustomInstructions,
                ["replaceInstructions"] = request.ReplaceInstructions,
            }, _shutdown.Token).ConfigureAwait(false);
            var refreshed = await _process.Connection.GetStateAsync(_shutdown.Token).ConfigureAwait(false);
            if (refreshed.SessionId != document.SessionId || refreshed.SessionFile != path)
                throw new InvalidDataException("An extension changed the active session during navigation.");
            var entries = await _process.Connection.GetEntriesAsync(cancellationToken: _shutdown.Token).ConfigureAwait(false);
            _lastPiEntryId = entries.LeafId;
            var hydrated = ThreadProjectionReducer.Hydrate(Journal.Projection, entries.Entries, entries.LeafId, path,
                refreshed.Model?.ContextWindow, await _checkpoints.ListAsync(_thread.ThreadId, _shutdown.Token).ConfigureAwait(false),
                document.SessionId, await _database.ListSentMessagesAsync(_thread.ThreadId, _shutdown.Token).ConfigureAwait(false)) with
            {
                AgentActivities = [], Plan = await ReadPlanOnStartAsync(_shutdown.Token).ConfigureAwait(false),
                Queue = CreateQueueProjection(refreshed, Journal.Projection.Queue?.Messages ?? []),
            };
            Journal.ReplaceProjection(hydrated);
            document = await PiSessionDocument.ReadAsync(path, _shutdown.Token).ConfigureAwait(false);
            TouchRuntime();
            return new(EnvironmentService.CreateSessionSnapshot(_thread.ThreadId, path, document), result.GetProperty("cancelled").GetBoolean(),
                await ResolveNavigationPromptAsync(result.TryGetProperty("editorText", out var prompt) && prompt.ValueKind == JsonValueKind.String ? prompt.GetString() : null,
                    _shutdown.Token).ConfigureAwait(false));
        }
        catch (Exception error) when (started)
        {
            // A failed/timed-out SDK hook may have moved Pi's in-memory leaf. Restart from
            // the persisted file before accepting another prompt instead of guessing its context.
            await DisposePreviousProcessAsync().ConfigureAwait(false);
            _persistedSessionHydrated = false;
            Journal.Commit(new RuntimeStateChangedEvent(ThreadRuntimeState.Stopped));
            throw new HostOperationException(ProtocolErrorCodes.PiCommandRejected,
                "Branch navigation could not be confirmed. Refresh the tree to recover its saved state. " + error.Message);
        }
        finally
        {
            await _navigationControl.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { _navigation = null; }
            finally { _navigationControl.Release(); }
            if (started) Interlocked.Increment(ref _navigationGeneration);
            _lifecycle.Release();
        }
    }

    public async Task<bool> CancelSessionNavigationAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var requested = false;
        while (true)
        {
            await _navigationControl.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_navigation is not { } navigation || navigation.Id != operationId) return requested;
                requested = true;
                await navigation.Process.Connection.AbortAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { _navigationControl.Release(); }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> ResolveNavigationPromptAsync(string? text, CancellationToken cancellationToken)
    {
        if (text is null) return null;
        var messages = await _database.ListSentMessagesAsync(_thread.ThreadId, cancellationToken).ConfigureAwait(false);
        return SentMessageReference.Read(text) is { } reference && messages.TryGetValue(reference, out var message)
            ? message.Text : SentMessageReference.Remove(text);
    }
}
