using System.Collections.ObjectModel;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class PullRequestReviewViewModel
{
    private PullRequestMergeMethod _selectedMergeMethod = PullRequestMergeMethod.Squash;
    private PullRequestUpdateMethod _selectedUpdateMethod;
    private PullRequestWorkflow? _selectedWorkflow;
    private PullRequestReviewTarget? _workflowsTarget;
    private int? _nextWorkflowPage;
    private long _workflowGeneration;
    private bool _loadingWorkflows;
    private string _workflowStatus = "Load workflows awaiting approval for this PR revision.";

    public IReadOnlyList<PullRequestMergeMethod> MergeMethods => Snapshot?.Advanced?.MergeMethods ?? [];
    public IReadOnlyList<PullRequestUpdateMethod> UpdateMethods => Snapshot?.Advanced?.UpdateMethods ??
        (PullRequest?.Provider == SourceControlProvider.GitHub ? Enum.GetValues<PullRequestUpdateMethod>() : []);
    public PullRequestMergeMethod SelectedMergeMethod { get => _selectedMergeMethod; set { if (SetProperty(ref _selectedMergeMethod, value)) RaiseAdvancedState(); } }
    public PullRequestUpdateMethod SelectedUpdateMethod { get => _selectedUpdateMethod; set { if (SetProperty(ref _selectedUpdateMethod, value)) RaiseAdvancedState(); } }
    public bool CanMerge => CanManage && Snapshot?.Advanced?.CanMerge == true && MergeMethods.Contains(SelectedMergeMethod);
    public bool CanEnableAutoMerge => CanManage && Snapshot?.Advanced?.CanEnableAutoMerge == true && MergeMethods.Contains(SelectedMergeMethod);
    public bool CanDisableAutoMerge => CanManage && Snapshot?.Advanced?.CanDisableAutoMerge == true;
    public bool CanUpdateBranch => CanManage && Snapshot?.Advanced?.CanUpdateBranch == true && UpdateMethods.Contains(SelectedUpdateMethod);
    public bool CanRevert => CanManage && Snapshot?.Advanced?.CanRevert == true;
    public bool CanReactToPullRequest => CanManage && Snapshot?.CanReact == true;
    public bool CanReactToComment => CanManage && SelectedComment?.CanReact == true;
    public string AdvancedStatus => Snapshot?.Advanced is { } state
        ? $"Mergeability: {state.Mergeability} · Branch: {state.BaseStatus} · Automatic merge: {(state.AutoMergeEnabled ? "enabled" : "disabled")}. Repository rules are enforced by {Snapshot.Repository.Provider}."
        : "Advanced actions depend on the provider and your repository permissions.";
    public ObservableCollection<PullRequestWorkflow> Workflows { get; } = [];
    public PullRequestWorkflow? SelectedWorkflow { get => _selectedWorkflow; set { if (SetProperty(ref _selectedWorkflow, value)) RaiseAdvancedState(); } }
    public string WorkflowStatus { get => _workflowStatus; private set => SetProperty(ref _workflowStatus, value); }
    public bool CanLoadWorkflows => CanManage && !_loadingWorkflows && Snapshot?.Advanced?.CanApproveWorkflows == true;
    public bool CanLoadMoreWorkflows => CanLoadWorkflows && _nextWorkflowPage is not null && WorkflowsMatchCurrentTarget;
    public bool CanApproveWorkflow => CanLoadWorkflows && WorkflowsMatchCurrentTarget && SelectedWorkflow is { } selected && Workflows.Contains(selected);
    private bool WorkflowsMatchCurrentTarget => Snapshot is { } snapshot && _workflowsTarget is { } target &&
        target.Workspace == _capturedTarget && target.Number == snapshot.PullRequest.Number && target.HeadCommitId == snapshot.HeadCommitId &&
        target.Repository == PullRequestReviewDefaults.RepositoryKey(snapshot.Repository);

    public async Task LoadWorkflowsAsync(bool more = false, CancellationToken cancellationToken = default)
    {
        if (!CanLoadWorkflows || more && !CanLoadMoreWorkflows || Snapshot is not { } snapshot || _capturedTarget is not { } workspace) return;
        var generation = ++_workflowGeneration;
        var load = _loadGeneration;
        var mutation = _mutationGeneration;
        var target = new PullRequestReviewTarget(workspace, PullRequestReviewDefaults.RepositoryKey(snapshot.Repository), snapshot.PullRequest.Number, snapshot.HeadCommitId);
        var page = more ? _nextWorkflowPage!.Value : 1;
        _loadingWorkflows = true;
        if (!more) { Workflows.Clear(); SelectedWorkflow = null; _workflowsTarget = null; _nextWorkflowPage = null; }
        RaiseAdvancedState();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _loadCancellation?.Token ?? CancellationToken.None);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var result = await _clientFactory().GetPullRequestWorkflowsAsync(new(target, page), timeout.Token);
            if (_disposed || generation != _workflowGeneration || load != _loadGeneration || mutation != _mutationGeneration || IsStaleHead || !CanManage || cancellationToken.IsCancellationRequested) return;
            if (result.Target != target || result.NextPage is { } next && (next != page + 1 || next > 10) || result.Workflows.Count > 100)
                throw new InvalidDataException("The workflow page does not match this request.");
            foreach (var workflow in result.Workflows) if (!Workflows.Any(item => item.Id == workflow.Id)) Workflows.Add(workflow);
            _workflowsTarget = target;
            _nextWorkflowPage = result.NextPage;
            WorkflowStatus = Workflows.Count == 0 ? "No workflows awaiting approval are associated with this PR revision."
                : $"{Workflows.Count} workflow(s) awaiting approval.{(result.NextPage is null ? "" : " More workflows are available.")}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { if (generation == _workflowGeneration && load == _loadGeneration) WorkflowStatus = $"Could not load workflows: {exception.Message}"; }
        finally { if (generation == _workflowGeneration) { _loadingWorkflows = false; RaiseAdvancedState(); } }
    }

    public Task<SourceControlOperationResult?> ToggleReactionAsync(PullRequestReactionContent content, bool comment = false, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(content) || !(comment ? CanReactToComment : CanReactToPullRequest)) return Task.FromResult<SourceControlOperationResult?>(null);
        var reactions = comment ? SelectedComment?.Reactions : Snapshot?.Reactions;
        return ManageAsync(PullRequestManagementAction.SetReaction, comment ? SelectedComment?.Id : null,
            content, !(reactions?.Any(reaction => reaction.Content == content && reaction.ViewerHasReacted) ?? false), cancellationToken);
    }

    private bool CanPerformAdvancedAction(PullRequestManagementAction action, string? item, PullRequestReactionContent? reaction, bool? reacted) => action switch
    {
        PullRequestManagementAction.Merge => CanMerge,
        PullRequestManagementAction.EnableAutoMerge => CanEnableAutoMerge,
        PullRequestManagementAction.DisableAutoMerge => CanDisableAutoMerge,
        PullRequestManagementAction.UpdateBranch => CanUpdateBranch,
        PullRequestManagementAction.Revert => CanRevert,
        PullRequestManagementAction.ApproveWorkflow => CanApproveWorkflow && SelectedWorkflow?.Id == item,
        PullRequestManagementAction.SetReaction => reaction is { } content && Enum.IsDefined(content) && reacted is not null &&
            (item is null ? CanReactToPullRequest : CanReactToComment && SelectedComment?.Id == item),
        _ => false
    };

    private void ResetWorkflows()
    {
        _workflowGeneration++;
        _loadingWorkflows = false;
        _workflowsTarget = null;
        _nextWorkflowPage = null;
        Workflows.Clear();
        _selectedWorkflow = null;
        WorkflowStatus = "Load workflows awaiting approval for this PR revision.";
        OnPropertyChanged(nameof(SelectedWorkflow));
        RaiseAdvancedState();
    }

    private void RaiseAdvancedState()
    {
        if (MergeMethods.Count > 0 && !MergeMethods.Contains(_selectedMergeMethod)) _selectedMergeMethod = MergeMethods[0];
        OnPropertyChanged(nameof(UpdateMethods));
        OnPropertyChanged(nameof(SelectedUpdateMethod));
        foreach (var property in new[] { nameof(MergeMethods), nameof(SelectedMergeMethod), nameof(CanMerge), nameof(CanEnableAutoMerge), nameof(CanDisableAutoMerge),
            nameof(CanUpdateBranch), nameof(CanRevert), nameof(CanReactToPullRequest), nameof(CanReactToComment), nameof(AdvancedStatus),
            nameof(CanLoadWorkflows), nameof(CanLoadMoreWorkflows), nameof(CanApproveWorkflow) }) OnPropertyChanged(property);
    }
}
