using PiStation.Host.Errors;
using PiStation.PiRpc.Diagnostics;
using PiStation.PiRpc.Process;
using PiStation.PiRpc.Transport;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Receipts;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Threads;

public sealed partial class PiThreadController
{
    private ShellRun? _shellRun;
    private Task? _shellTask;
    private bool _shellRestored;

    private sealed class ShellRun(PiProcess process, PiShellExecution execution)
    {
        public PiProcess Process { get; } = process;
        public PiShellExecution Execution { get; set; } = execution;
        // Cancellation must not overtake the shell request on stdin.
        public TaskCompletionSource<bool> Dispatched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task RestoreShellAsync(CancellationToken token)
    {
        if (_shellRestored) return;
        var saved = await _database.GetPiShellAsync(_thread.ThreadId, token).ConfigureAwait(false);
        if (saved is { IsActive: true })
        {
            saved = saved with { State = PiShellExecutionState.Interrupted, CompletedUtc = DateTimeOffset.UtcNow,
                Error = "The host restarted before the shell result was saved. The command may have changed files; it was not run again." };
            await _database.SavePiShellAsync(_thread.ThreadId, saved, token).ConfigureAwait(false);
        }
        if (saved is not null) Journal.Commit(new PiShellChangedEvent(saved));
        _shellRestored = true;
    }

    public async Task StartShellAsync(string command, bool excludeFromContext, ClientId clientId, CommandId commandId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > PiShellExecution.MaximumCommandLength || command.Contains('\0'))
            throw new HostOperationException(ProtocolErrorCodes.PiCommandRejected, "Enter a shell command of at most 32,768 characters without null characters.");
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var process = RequireReadyProcess();
            if (Journal.Projection.Plan is { Mode: not "off" } ||
                Journal.Projection.Compaction?.State == ContextCompactionState.Running ||
                Journal.Projection.Queue?.PendingMessageCount > 0 ||
                Journal.Projection.Timeline.Any(item => item is ApprovalTimelineItem { State: InteractionState.Pending } or QuestionTimelineItem { State: InteractionState.Pending }))
                throw new HostOperationException(ProtocolErrorCodes.ThreadBusy, "Finish active work and interactions and leave plan mode before running a shell command.");
            var state = await process.Connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
            if (state.IsStreaming || state.IsCompacting || state.PendingMessageCount > 0)
                throw new HostOperationException(ProtocolErrorCodes.ThreadBusy, "Pi must be idle before running a shell command.");
            var execution = new PiShellExecution(commandId, clientId, command, excludeFromContext,
                PiShellExecutionState.Running, "", false, null, null, null, DateTimeOffset.UtcNow);
            await _database.RecordSettlementActivityAsync(_thread.ThreadId, true, execution.StartedUtc, cancellationToken).ConfigureAwait(false);
            await _database.SetThreadSettlementAutomaticallyAsync(_thread.ThreadId, false, cancellationToken).ConfigureAwait(false);
            await _database.SavePiShellAsync(_thread.ThreadId, execution, cancellationToken).ConfigureAwait(false);
            await _database.UpdateReceiptStateAsync(clientId, commandId, CommandReceiptState.Accepted,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var run = new ShellRun(process, execution);
            lock (_stateLock) _shellRun = run;
            TouchRuntime();
            Journal.Commit(new PiShellChangedEvent(execution));
            Journal.Commit(new RuntimeStateChangedEvent(ThreadRuntimeState.Running));
            // The host owns execution after acceptance. A client disconnect must not replay or cancel it.
            _shellTask = RunShellAsync(run);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task CancelShellAsync(CommandId executionId, CancellationToken token)
    {
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ShellRun? run;
            lock (_stateLock) run = _shellRun;
            if (run is null || run.Execution.CommandId != executionId || !run.Execution.IsActive)
                throw new HostOperationException(ProtocolErrorCodes.ThreadBusy, "That shell command is no longer running. Refresh its result.");
            if (!await run.Dispatched.Task.WaitAsync(token).ConfigureAwait(false))
                throw new HostOperationException(ProtocolErrorCodes.PiCommandRejected, "The shell command could not be dispatched.");
            lock (_stateLock)
            {
                run.Execution = run.Execution with { State = PiShellExecutionState.CancelRequested };
                Journal.Commit(new PiShellChangedEvent(run.Execution));
            }
            await run.Process.Connection.AbortBashAsync(token).ConfigureAwait(false);
            // Only the bash result establishes that execution actually ended.
        }
        finally { _lifecycle.Release(); }
    }

    private void AppendShellOutput(ShellRun run, string delta)
    {
        lock (_stateLock)
        {
            if (!ReferenceEquals(_shellRun, run) || !run.Execution.IsActive) return;
            var combined = run.Execution.Output + delta;
            var truncated = combined.Length > PiShellExecution.MaximumOutputLength;
            run.Execution = run.Execution with
            {
                Output = TailShellOutput(combined),
                Truncated = run.Execution.Truncated || truncated,
            };
            Journal.Commit(new PiShellChangedEvent(run.Execution));
        }
        TouchRuntime();
    }

    private async Task RunShellAsync(ShellRun run)
    {
        PiBashResult? result = null;
        PiSessionEntries? entries = null;
        Exception? failure = null;
        try
        {
            result = await run.Process.Connection.ExecuteBashAsync(run.Execution.Command, run.Execution.ExcludeFromContext,
                delta => AppendShellOutput(run, delta), () => run.Dispatched.TrySetResult(true), _shutdown.Token).ConfigureAwait(false);
            // Pi appends a bashExecution entry without a message event. Keep the next
            // turn's checkpoint anchored after this shell command.
            entries = await run.Process.Connection.GetEntriesAsync(cancellationToken: _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        finally { run.Dispatched.TrySetResult(false); }

        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            PiShellExecution completed;
            lock (_stateLock)
            {
                if (!ReferenceEquals(_shellRun, run)) return;
                if (entries is not null) _lastPiEntryId = entries.LeafId;
                completed = run.Execution with
                {
                    State = result is not null
                        ? result.Cancelled ? PiShellExecutionState.Cancelled : result.ExitCode == 0 ? PiShellExecutionState.Completed : PiShellExecutionState.Failed
                        : failure is PiRpcCommandException ? PiShellExecutionState.Failed : PiShellExecutionState.Interrupted,
                    Output = result is null ? run.Execution.Output : TailShellOutput(result.Output),
                    Truncated = run.Execution.Truncated || result?.Truncated == true || result?.Output.Length > PiShellExecution.MaximumOutputLength,
                    ExitCode = result?.ExitCode, FullOutputPath = result?.FullOutputPath,
                    Error = failure is null ? null : LimitPreview(failure.Message, 4096), CompletedUtc = DateTimeOffset.UtcNow,
                };
                run.Execution = completed;
                _shellRun = null;
            }
            await _database.SavePiShellAsync(_thread.ThreadId, completed).ConfigureAwait(false);
            await _database.RecordSettlementActivityAsync(_thread.ThreadId, false, completed.CompletedUtc!.Value).ConfigureAwait(false);
            await _database.UpdateReceiptStateAsync(completed.ClientId, completed.CommandId,
                failure is null ? CommandReceiptState.Completed : CommandReceiptState.Failed,
                failure is null ? null : failure is PiRpcCommandException ? ProtocolErrorCodes.PiCommandRejected : ProtocolErrorCodes.PiRuntimeCrashed).ConfigureAwait(false);
            Journal.Commit(new PiShellChangedEvent(completed, entries?.LeafId));
            if (result is not null)
                Journal.Commit(new MessageCompletedEvent(ThreadProjectionReducer.ReadShellMessage(
                    entries?.LeafId ?? $"shell-{completed.CommandId.Value}", completed.Command, completed.Output, completed.ExcludeFromContext,
                    result.Cancelled, completed.Truncated, completed.ExitCode, completed.FullOutputPath)));
            if (Journal.Projection.RuntimeState == ThreadRuntimeState.Running)
            {
                if (failure is not null && (result is not null || failure is not PiRpcCommandException))
                    Journal.Commit(new RuntimeFailedEvent(ToProtocolError(failure)));
                else Journal.Commit(new RuntimeStateChangedEvent(ThreadRuntimeState.Ready));
            }
            TouchRuntime();
        }
        catch (Exception error)
        {
            Journal.Commit(new PiShellChangedEvent(run.Execution with
            {
                State = PiShellExecutionState.Interrupted,
                CompletedUtc = DateTimeOffset.UtcNow,
                Error = "The shell result could not be saved. Check the workspace before retrying. " + LimitPreview(error.Message, 4096),
            }));
            Journal.Commit(new RuntimeFailedEvent(ToProtocolError(error)));
        }
        finally { _lifecycle.Release(); }
    }

    private async Task InterruptShellAsync(PiProcess process)
    {
        PiShellExecution? interrupted = null;
        lock (_stateLock)
        {
            if (_shellRun is { } run && ReferenceEquals(run.Process, process))
            {
                interrupted = run.Execution with { State = PiShellExecutionState.Interrupted, CompletedUtc = DateTimeOffset.UtcNow,
                    Error = "The Pi runtime stopped before the shell result was confirmed. The command was not run again." };
                _shellRun = null;
            }
        }
        if (interrupted is null) return;
        await _database.SavePiShellAsync(_thread.ThreadId, interrupted).ConfigureAwait(false);
        await _database.UpdateReceiptStateAsync(interrupted.ClientId, interrupted.CommandId,
            CommandReceiptState.Failed, ProtocolErrorCodes.PiRuntimeCrashed).ConfigureAwait(false);
        Journal.Commit(new PiShellChangedEvent(interrupted));
    }

    private static string TailShellOutput(string output)
    {
        if (output.Length <= PiShellExecution.MaximumOutputLength) return output;
        var start = output.Length - PiShellExecution.MaximumOutputLength;
        if (char.IsLowSurrogate(output[start])) start++;
        return output[start..];
    }
}
