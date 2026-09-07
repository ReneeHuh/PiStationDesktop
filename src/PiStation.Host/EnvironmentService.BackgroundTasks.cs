using PiStation.Protocol;
using PiStation.Protocol.Commands;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.Host;

public sealed partial class EnvironmentService
{
    private readonly SemaphoreSlim _backgroundSubmissionGate = new(1, 1);
    private static readonly ClientId BackgroundClientId = ClientId.Parse("pistation-background-tasks");

    public async Task<BackgroundTaskResult> SubmitBackgroundTaskAsync(SubmitBackgroundTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(request.DraftRevision);
        if (!Enum.IsDefined(request.WorkspaceMode)) throw new ArgumentException("Unknown workspace mode.", nameof(request));
        await _backgroundSubmissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await _database.GetBackgroundTaskAsync(request, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                if (existing.State is BackgroundTaskState.Dispatching or BackgroundTaskState.Uncertain)
                {
                    var receipt = await _database.GetReceiptAsync(BackgroundClientId, CommandId.Parse(existing.SubmissionId), cancellationToken).ConfigureAwait(false);
                    if (receipt is not null)
                    {
                        existing = BackgroundResult(existing, receipt.Receipt);
                        await _database.SaveBackgroundTaskAsync(request, existing, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    else existing = existing with { State = BackgroundTaskState.Uncertain,
                        Message = "Dispatch was interrupted without a conclusive receipt. Inspect the task; it will not be submitted again automatically." };
                }
                return existing.State == BackgroundTaskState.Preparing
                    ? existing with { State = BackgroundTaskState.Rejected, Message = "Preparation was interrupted before dispatch. Inspect the task thread, then edit your draft before trying again." }
                    : existing;
            }

            var source = await GetThreadAsync(request.SourceThreadId, cancellationToken).ConfigureAwait(false);
            var draft = await GetThreadDraftAsync(source.ThreadId, cancellationToken).ConfigureAwait(false);
            if (draft.DraftId != request.DraftId || draft.Revision != request.DraftRevision)
                return new(Guid.NewGuid().ToString("N"), null, BackgroundTaskState.Rejected,
                    "The draft changed. Reload it before submitting a new task.");
            if (string.IsNullOrWhiteSpace(draft.Text) && draft.Attachments.Count == 0 && (draft.Context?.Count ?? 0) == 0)
                return new(Guid.NewGuid().ToString("N"), null, BackgroundTaskState.Rejected,
                    "Enter a prompt or attach context before starting a new task.");
            var configuration = await _database.GetOrCreateThreadPiConfigurationAsync(source.ThreadId, cancellationToken).ConfigureAwait(false);
            var operation = new BackgroundTaskResult(Guid.NewGuid().ToString("N"), null,
                BackgroundTaskState.Preparing, "Preparing independent task");
            await _database.SaveBackgroundTaskAsync(request, operation, create: true, cancellationToken).ConfigureAwait(false);

            // Once reserved, a dropped UI connection must not interrupt bookkeeping or
            // cause an automatic redispatch. Preparation itself is still time-bounded.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var token = timeout.Token;
            var dispatchStarted = false;
            try
            {
                var task = await CreateThreadAsync(new(source.ProjectId, WorkspaceMode: request.WorkspaceMode,
                    BaseBranch: request.BaseBranch), token).ConfigureAwait(false);
                operation = operation with { TaskThreadId = task.ThreadId };
                await _database.SaveBackgroundTaskAsync(request, operation, cancellationToken: token).ConfigureAwait(false);
                var taskDraft = await _database.CopyBackgroundDraftAsync(request, task.ThreadId, token).ConfigureAwait(false);
                var taskConfiguration = await _database.GetOrCreateThreadPiConfigurationAsync(task.ThreadId, token).ConfigureAwait(false);
                var configurationUpdate = await _database.UpdateThreadPiConfigurationAsync(task.ThreadId, taskConfiguration.Revision,
                    configuration.Model, configuration.ThinkingLevel, configuration.RuntimeModeId, token).ConfigureAwait(false);
                if (!configurationUpdate.WasUpdated)
                    throw new InvalidOperationException("The task's Pi settings changed during preparation. No prompt was dispatched.");
                while (task.SetupScriptState is SetupScriptState.Pending or SetupScriptState.Running)
                {
                    await Task.Delay(100, token).ConfigureAwait(false);
                    task = await GetThreadAsync(task.ThreadId, token).ConfigureAwait(false);
                }
                if (task.SetupScriptState is SetupScriptState.Failed or SetupScriptState.Cancelled)
                    throw new InvalidOperationException(task.SetupScriptMessage ?? "The worktree setup did not complete.");
                operation = operation with { State = BackgroundTaskState.Dispatching, Message = "Dispatching independent task" };
                await _database.SaveBackgroundTaskAsync(request, operation, cancellationToken: token).ConfigureAwait(false);
                dispatchStarted = true;
                var receipt = await ExecuteThreadCommandAsync(new(ProtocolVersion.Current, source.EnvironmentId,
                    BackgroundClientId, CommandId.Parse(operation.SubmissionId), task.ThreadId, null, null,
                    new ThreadStartTurnCommand(ComposerContextDefaults.AppendToPrompt(taskDraft.Text.Trim(), taskDraft.Context),
                        taskDraft.DraftId, taskDraft.Revision, taskDraft.Attachments.Select(a => a.AttachmentId).ToArray())), token).ConfigureAwait(false);
                operation = BackgroundResult(operation, receipt);
                await _database.SaveBackgroundTaskAsync(request, operation, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                if (operation.State == BackgroundTaskState.Accepted)
                {
                    try
                    {
                        await _database.ClearThreadDraftAsync(task.ThreadId, taskDraft.DraftId, taskDraft.Revision,
                            taskDraft.Attachments.Select(a => a.AttachmentId).ToArray(), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception) { /* The accepted task is durable; a leftover task draft can be cleared later. */ }
                }
                return operation;
            }
            catch (Exception exception)
            {
                operation = operation with { State = dispatchStarted ? BackgroundTaskState.Uncertain : BackgroundTaskState.Rejected,
                    Message = $"{(dispatchStarted ? "Task dispatch needs checking" : "Task preparation failed")}: {exception.Message}. Your source draft was preserved." };
                await _database.SaveBackgroundTaskAsync(request, operation, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                return operation;
            }
        }
        finally { _backgroundSubmissionGate.Release(); }
    }

    private static BackgroundTaskResult BackgroundResult(BackgroundTaskResult operation, CommandReceipt receipt) => operation with
    {
        Receipt = receipt,
        State = receipt.State is CommandReceiptState.Accepted or CommandReceiptState.Completed ? BackgroundTaskState.Accepted
            : receipt.State is CommandReceiptState.Rejected or CommandReceiptState.Failed ? BackgroundTaskState.Rejected : BackgroundTaskState.Uncertain,
        Message = receipt.State is CommandReceiptState.Accepted or CommandReceiptState.Completed
            ? "Independent task accepted" : $"Task submission: {receipt.State}. {receipt.ErrorCode}",
    };
}
