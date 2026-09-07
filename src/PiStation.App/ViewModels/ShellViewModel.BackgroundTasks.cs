using PiStation.ClientRuntime;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    private SubmitBackgroundTaskRequest? _backgroundRecoveryRequest;
    private ThreadDraft? _backgroundSourceDraft;

    public string BackgroundTaskActionLabel => _backgroundRecoveryRequest is null ? "New task" : "Check task";
    public bool CanStartBackgroundTask => !_commandPending && _client?.ConnectionState == EnvironmentConnectionState.Connected &&
        (_backgroundRecoveryRequest is not null || (SelectedThread is not null && SelectedProject is not null &&
            (Composer.HasAttachments || Composer.ContextChips.Count > 0 || !string.IsNullOrWhiteSpace(PromptText))));

    private async Task StartIndependentBackgroundTaskAsync()
    {
        var client = RequireClient();
        if (_backgroundRecoveryRequest is null)
        {
            var thread = SelectedThread ?? throw new InvalidOperationException("Select a thread to use its project and Pi defaults.");
            var draft = await Composer.PrepareTurnAsync() ?? throw new InvalidOperationException("The draft is not ready.");
            if (draft.ThreadId != thread.ThreadId) throw new InvalidOperationException("The thread changed before submission.");
            _backgroundSourceDraft = draft;
            _backgroundRecoveryRequest = new(thread.ThreadId, draft.DraftId, draft.Revision, thread.WorkspaceMode, thread.BranchName);
            OnPropertyChanged(nameof(BackgroundTaskActionLabel));
        }
        // Preserve this exact request after a transport failure; Check task queries
        // its durable receipt instead of submitting the current/newer draft again.
        var result = await client.SubmitBackgroundTaskAsync(_backgroundRecoveryRequest);
        await QueueThreadListRefreshAsync(false, CancellationToken.None);
        await RefreshProjectGroupsAsync();
        if (result.State == BackgroundTaskState.Accepted)
        {
            var acceptedDraft = _backgroundSourceDraft!;
            _backgroundRecoveryRequest = null;
            _backgroundSourceDraft = null;
            OnPropertyChanged(nameof(BackgroundTaskActionLabel));
            try
            {
                await Composer.ClearAcceptedTurnAsync(acceptedDraft);
                ComposerPower.Status = $"New task {result.TaskThreadId} is running. Your next draft is ready.";
            }
            catch (Exception exception)
            {
                ReportRuntimeError($"Task {result.TaskThreadId} was accepted; your newer draft was preserved: {exception.Message}");
            }
        }
        else
        {
            ComposerPower.Status = $"{result.Message} Task: {result.TaskThreadId?.Value ?? "not created"}.";
            if (result.State == BackgroundTaskState.Rejected)
            {
                _backgroundRecoveryRequest = null;
                _backgroundSourceDraft = null;
                OnPropertyChanged(nameof(BackgroundTaskActionLabel));
            }
        }
        OnPropertyChanged(nameof(CanStartBackgroundTask));
    }
}
