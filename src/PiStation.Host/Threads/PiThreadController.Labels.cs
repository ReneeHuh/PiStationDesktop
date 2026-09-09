using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiStation.Host.Errors;
using PiStation.PiRpc.Sessions;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Serialization;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Threads;

public sealed partial class PiThreadController
{
    public Task<PiSessionSnapshot> SetSessionLabelAsync(SetPiSessionLabelRequest request, CancellationToken cancellationToken)
    {
        if (request.ThreadId != _thread.ThreadId || request.OperationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.EntryId) || request.EntryId.Length > 256 ||
            request.ExpectedRevision?.Length != 64 || request.Label?.Length > 256 || request.Label?.Any(char.IsControl) == true)
            throw new InvalidDataException("Select a session entry and a single-line label of at most 256 characters.");
        return WithSessionAsync(async (document, path) =>
        {
            var state = await _process!.Connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
            if (state.IsStreaming || state.IsCompacting || state.PendingMessageCount != 0 || Journal.Projection.ShellExecution?.IsActive == true)
                throw new HostOperationException(ProtocolErrorCodes.ThreadBusy, "Finish active work and queued prompts before editing labels.");
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, ProtocolJsonContext.Default.SetPiSessionLabelRequest)));
            var operationId = request.OperationId.ToString("N");
            var previous = document.Entries.FirstOrDefault(entry => entry["customType"]?.ToString() == "pistation.session-label" && entry["data"]?["operationId"]?.ToString() == operationId);
            if (previous is not null)
            {
                if (previous["data"]?["requestHash"]?.ToString() != hash) throw new InvalidDataException("This label operation identity was already used for a different edit.");
                return EnvironmentService.CreateSessionSnapshot(_thread.ThreadId, path, document);
            }
            if (request.ExpectedRevision != document.Revision || !document.Entries.Any(entry => entry["id"]?.ToString() == request.EntryId))
                throw new InvalidDataException("The session changed. Refresh its tree before editing labels.");
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _navigationGeneration);
            Journal.Commit(new RuntimeStateChangedEvent(ThreadRuntimeState.Hydrating));
            try
            {
                // Complete accepted edits even if the requesting connection drops; Pi stores a replay receipt.
                await _process.Connection.SetSessionLabelAsync(new JsonObject
                {
                    ["action"] = "label", ["operationId"] = operationId, ["requestHash"] = hash,
                    ["sessionId"] = document.SessionId, ["entryId"] = request.EntryId,
                    ["expectedRevision"] = request.ExpectedRevision, ["label"] = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim(),
                }, _shutdown.Token).ConfigureAwait(false);
                var refreshed = await _process.Connection.GetStateAsync(_shutdown.Token).ConfigureAwait(false);
                if (refreshed.SessionFile != path || refreshed.SessionId != document.SessionId)
                    throw new InvalidDataException("The active session changed during label editing.");
                document = await PiSessionDocument.ReadAsync(path, _shutdown.Token).ConfigureAwait(false);
                _lastPiEntryId = document.LeafId;
                Journal.ReplaceProjection(Journal.Projection with { LastEntryId = document.LeafId, RuntimeState = ThreadRuntimeState.Ready });
                return EnvironmentService.CreateSessionSnapshot(_thread.ThreadId, path, document);
            }
            catch (Exception error)
            {
                await DisposePreviousProcessAsync().ConfigureAwait(false);
                _persistedSessionHydrated = false;
                Journal.Commit(new RuntimeStateChangedEvent(ThreadRuntimeState.Stopped));
                throw new HostOperationException(ProtocolErrorCodes.PiCommandRejected, "The label edit could not be confirmed. Refresh the tree to recover its saved state. " + error.Message);
            }
            finally { Interlocked.Increment(ref _navigationGeneration); }
        }, cancellationToken);
    }
}
