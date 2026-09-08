using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed class WorkbenchChangesViewModel : ObservableObject
{
    private string _branchDetail = "Select a workspace to inspect Git changes";
    private string _branchName = "No repository";
    private string _diffContent = string.Empty;
    private string _diffMessage = "Choose a changed file to inspect its diff.";
    private string _diffPath = "Select a change";
    private string _commitMessage = string.Empty;
    private string _newBranchName = string.Empty;
    private GitRefDescriptor? _selectedBranch;
    private bool _isBusy;
    private bool _allowOperations = true;

    public bool AllowOperations
    {
        get => _allowOperations;
        internal set
        {
            if (!SetProperty(ref _allowOperations, value)) return;
            OnPropertyChanged(nameof(CanRunGitCommand));
            OnPropertyChanged(nameof(CanCommit));
        }
    }
    private bool _isRepository;
    private string? _headSha;
    private string _statusToken = string.Empty;
    private string? _worktreePath;
    private WorkbenchChangeItemViewModel? _selectedChange;
    private string _sourceControlSummary = "Source control unavailable";
    private string _status = "Select a workspace to inspect changes";

    public ObservableCollection<WorkbenchChangeItemViewModel> Changes { get; } = [];

    public ObservableCollection<GitRefDescriptor> Branches { get; } = [];

    public string CommitMessage
    {
        get => _commitMessage;
        set => SetProperty(ref _commitMessage, value);
    }

    public string NewBranchName
    {
        get => _newBranchName;
        set => SetProperty(ref _newBranchName, value);
    }

    public GitRefDescriptor? SelectedBranch
    {
        get => _selectedBranch;
        set => SetProperty(ref _selectedBranch, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        internal set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanRunGitCommand));
                OnPropertyChanged(nameof(CanCommit));
            }
        }
    }

    public bool CanRunGitCommand => AllowOperations && !IsBusy;

    public bool CanCommit => CanRunGitCommand && _isRepository && Changes.Count != 0;

    public Visibility RemoveWorktreeVisibility => string.IsNullOrWhiteSpace(_worktreePath)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public string? WorktreePath => _worktreePath;

    internal string? HeadSha => _headSha;

    internal string StatusToken => _statusToken;

    public string BranchName
    {
        get => _branchName;
        internal set => SetProperty(ref _branchName, value);
    }

    public string BranchDetail
    {
        get => _branchDetail;
        internal set => SetProperty(ref _branchDetail, value);
    }

    public string Status
    {
        get => _status;
        internal set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusVisibility));
            }
        }
    }

    public Visibility StatusVisibility => string.IsNullOrEmpty(Status)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public string SourceControlSummary
    {
        get => _sourceControlSummary;
        internal set => SetProperty(ref _sourceControlSummary, value);
    }

    public WorkbenchChangeItemViewModel? SelectedChange
    {
        get => _selectedChange;
        internal set => SetProperty(ref _selectedChange, value);
    }

    public string DiffPath
    {
        get => _diffPath;
        internal set => SetProperty(ref _diffPath, value);
    }

    public string DiffContent
    {
        get => _diffContent;
        internal set
        {
            if (SetProperty(ref _diffContent, value))
            {
                OnPropertyChanged(nameof(DiffContentVisibility));
            }
        }
    }

    public Visibility DiffContentVisibility => string.IsNullOrEmpty(DiffContent)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public string DiffMessage
    {
        get => _diffMessage;
        internal set => SetProperty(ref _diffMessage, value);
    }

    internal void Reset(bool hasProject)
    {
        Changes.Clear();
        Branches.Clear();
        _isRepository = false;
        _headSha = null;
        _statusToken = string.Empty;
        _worktreePath = null;
        OnPropertyChanged(nameof(RemoveWorktreeVisibility));
        OnPropertyChanged(nameof(WorktreePath));
        IsBusy = false;
        BranchName = hasProject ? "Checking Git…" : "No repository";
        BranchDetail = hasProject
            ? "Resolving the project repository"
            : "Select a workspace to inspect Git changes";
        Status = hasProject ? "Loading changes…" : "Select a workspace to inspect changes";
        SourceControlSummary = hasProject ? "Checking source control…" : "Source control unavailable";
        ClearDiff();
    }

    internal void Apply(GetProjectChangesResult result)
    {
        Changes.Clear();
        _isRepository = result.IsRepository;
        _headSha = result.HeadSha;
        _statusToken = result.StatusToken;
        _worktreePath = result.IsWorktree ? result.WorkspacePath : null;
        OnPropertyChanged(nameof(RemoveWorktreeVisibility));
        OnPropertyChanged(nameof(WorktreePath));
        if (!result.IsRepository)
        {
            BranchName = "Not a Git repository";
            BranchDetail = "The selected project has no Git worktree";
            Status = "No source-control changes are available";
            SourceControlSummary = "No Git repository";
            ClearDiff();
            OnPropertyChanged(nameof(CanCommit));
            return;
        }

        BranchName = string.IsNullOrWhiteSpace(result.BranchName) ? "Detached HEAD" : result.BranchName;
        BranchDetail = FormatBranchDetail(result);
        foreach (var change in result.Changes)
        {
            Changes.Add(new WorkbenchChangeItemViewModel(change));
        }

        Status = result.Changes.Count == 0
            ? "Working tree clean"
            : result.IsTruncated
                ? $"Showing the first {result.Changes.Count} changed files"
                : $"{result.Changes.Count} changed file" + (result.Changes.Count == 1 ? string.Empty : "s");
        var additions = result.Changes.Sum(static change => change.Additions);
        var deletions = result.Changes.Sum(static change => change.Deletions);
        SourceControlSummary = result.Changes.Count == 0
            ? $"{BranchName} • clean"
            : $"{BranchName} • {result.Changes.Count} change" +
              (result.Changes.Count == 1 ? string.Empty : "s") +
              $" • +{additions} −{deletions}";
        ClearDiff();
        OnPropertyChanged(nameof(CanCommit));
    }

    internal bool Matches(GetProjectChangesResult result) =>
        _isRepository == result.IsRepository && _headSha == result.HeadSha && _statusToken == result.StatusToken &&
        (!result.IsRepository || BranchName == (string.IsNullOrWhiteSpace(result.BranchName) ? "Detached HEAD" : result.BranchName)) &&
        (!result.IsRepository || BranchDetail == FormatBranchDetail(result)) &&
        Changes.Select(change => change.Change).SequenceEqual(result.Changes);

    internal void ApplyRefs(ListGitRefsResult result)
    {
        var selectedName = SelectedBranch?.Name ?? BranchName;
        Branches.Clear();
        foreach (var branch in result.Refs)
        {
            Branches.Add(branch);
        }

        SelectedBranch = Branches.FirstOrDefault(branch =>
            string.Equals(branch.Name, selectedName, StringComparison.Ordinal));
    }

    internal void BeginOperation(string message)
    {
        IsBusy = true;
        Status = message;
    }

    internal void CompleteOperation(string message)
    {
        IsBusy = false;
        Status = message;
    }

    internal void ClearDiff()
    {
        SelectedChange = null;
        DiffPath = "Select a change";
        DiffContent = string.Empty;
        DiffMessage = "Choose a changed file to inspect its diff.";
    }

    internal void BeginCheckpointDiff(int turnCount, CheckpointDiffScope scope, string? relativePath)
    {
        SelectedChange = null;
        DiffPath = relativePath ?? (scope == CheckpointDiffScope.Turn
            ? $"Turn {turnCount} changes"
            : $"Thread through turn {turnCount}");
        DiffContent = string.Empty;
        DiffMessage = "Loading checkpoint diff…";
    }

    internal void ApplyCheckpointDiff(GetThreadCheckpointDiffResult result)
    {
        SelectedChange = null;
        DiffPath = result.RelativePath ?? (result.Scope == CheckpointDiffScope.Turn
            ? $"Turn {result.ToTurnCount} changes"
            : $"Thread through turn {result.ToTurnCount}");
        DiffContent = result.DiffContent;
        var range = result.Scope == CheckpointDiffScope.Turn
            ? $"Turn {result.ToTurnCount} checkpoint"
            : $"Full thread • turns 1–{result.ToTurnCount}";
        DiffMessage = result.IsTruncated ? range + " • preview truncated" : range;
    }

    private static string FormatBranchDetail(GetProjectChangesResult result)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.UpstreamName))
        {
            parts.Add(result.UpstreamName);
        }

        if (result.AheadCount != 0)
        {
            parts.Add($"↑{result.AheadCount}");
        }

        if (result.BehindCount != 0)
        {
            parts.Add($"↓{result.BehindCount}");
        }

        if (result.IsWorktree)
        {
            parts.Add("thread worktree");
        }

        return parts.Count == 0 ? "Local branch" : string.Join(" • ", parts);
    }
}

public sealed class WorkbenchChangeItemViewModel(ProjectChange change)
{
    public ProjectChange Change { get; } = change ?? throw new ArgumentNullException(nameof(change));

    public string RelativePath => Change.RelativePath;

    public string FileName => Change.FileName;

    public string StatusCode => Change.WorkingTreeStatus == GitFileStatus.Untracked
        ? "U"
        : Change.StagedStatus != GitFileStatus.None && Change.WorkingTreeStatus != GitFileStatus.None
            ? ShortStatus(Change.StagedStatus) + ShortStatus(Change.WorkingTreeStatus)
            : ShortStatus(Change.StagedStatus != GitFileStatus.None
                ? Change.StagedStatus
                : Change.WorkingTreeStatus);

    public string StatusLabel
    {
        get
        {
            if (Change.WorkingTreeStatus == GitFileStatus.Untracked)
            {
                return "Untracked";
            }

            if (Change.StagedStatus != GitFileStatus.None && Change.WorkingTreeStatus != GitFileStatus.None)
            {
                return $"Staged {Describe(Change.StagedStatus)} • Working tree {Describe(Change.WorkingTreeStatus)}";
            }

            return Change.StagedStatus != GitFileStatus.None
                ? $"Staged {Describe(Change.StagedStatus)}"
                : Describe(Change.WorkingTreeStatus);
        }
    }

    public string AreaLabel => Change.WorkingTreeStatus == GitFileStatus.Untracked
        ? "UNTRACKED"
        : Change.StagedStatus != GitFileStatus.None && Change.WorkingTreeStatus != GitFileStatus.None
            ? "BOTH"
            : Change.StagedStatus != GitFileStatus.None ? "STAGED" : "WORKTREE";

    public string AccessibleName => $"{RelativePath}, {StatusLabel}";

    public string LineSummary => Change.Additions == 0 && Change.Deletions == 0
        ? string.Empty
        : $"+{Change.Additions} −{Change.Deletions}";

    private static string ShortStatus(GitFileStatus status) => status switch
    {
        GitFileStatus.Added => "A",
        GitFileStatus.Modified => "M",
        GitFileStatus.Deleted => "D",
        GitFileStatus.Renamed => "R",
        GitFileStatus.Copied => "C",
        GitFileStatus.TypeChanged => "T",
        GitFileStatus.Unmerged => "!",
        GitFileStatus.Untracked => "U",
        _ => "·",
    };

    private static string Describe(GitFileStatus status) => status switch
    {
        GitFileStatus.TypeChanged => "Type changed",
        GitFileStatus.Unmerged => "Conflict",
        _ => status.ToString(),
    };
}
