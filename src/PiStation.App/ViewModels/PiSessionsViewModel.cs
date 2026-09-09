using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed class PiSessionCandidateRow(PiSessionCandidate session)
{
    public PiSessionCandidate Session { get; } = session;
    public string Title => Session.Title;
    public string Details => $"{Session.EntryCount} entries · {Session.ModifiedUtc.LocalDateTime:g}\n{Session.ProjectDirectory}\n{Session.Path}";
}

public sealed class PiSessionTreeRow(PiSessionTreeEntry entry)
{
    public PiSessionTreeEntry Entry { get; } = entry;
    public string Title => $"{Entry.Kind} · {Entry.Preview}";
    public string Details => $"{(Entry.IsActiveBranch ? "Active branch" : "Other branch")} · entry {Entry.Id} · parent {Entry.ParentId ?? "root"}" +
        (Entry.CanFork ? " · can fork here" : string.Empty);
    public Microsoft.UI.Xaml.Thickness Indent => new(Math.Min(Entry.Depth, 8) * 8, 0, 0, 0);
}

public sealed class PiSessionsViewModel : ObservableObject
{
    private bool _isBusy;
    private bool _allowOperations;
    private bool _hasPendingImport;
    private bool _canCancelTransfer;
    private bool _canCancelNavigation;
    private string _navigationPrompt = string.Empty;
    private string _status = "Import a Pi session, or inspect the selected idle thread.";
    private string _directory = string.Empty;
    private string _summary = string.Empty;
    private PiSessionCandidateRow? _selectedCandidate;
    private PiSessionTreeRow? _selectedEntry;
    public ObservableCollection<PiSessionCandidateRow> Candidates { get; } = [];
    public ObservableCollection<PiSessionTreeRow> Entries { get; } = [];
    public PiSessionSnapshot? Snapshot { get; private set; }
    public int? BrowserNextOffset { get; private set; }
    public bool HasMoreCandidates => CanAct && BrowserNextOffset is not null;
    public bool HasMoreEntries => CanAct && Snapshot?.NextOffset is not null;
    public string Directory { get => _directory; set => SetProperty(ref _directory, value); }
    public string Status { get => _status; internal set => SetProperty(ref _status, value); }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public bool IsBusy { get => _isBusy; internal set { if (SetProperty(ref _isBusy, value)) RaiseActionState(); } }
    public bool AllowOperations { get => _allowOperations; internal set { if (SetProperty(ref _allowOperations, value)) RaiseActionState(); } }
    public bool HasPendingImport { get => _hasPendingImport; internal set { if (SetProperty(ref _hasPendingImport, value)) OnPropertyChanged(nameof(CanRetryImport)); } }
    public bool CanCancelTransfer { get => _canCancelTransfer; internal set => SetProperty(ref _canCancelTransfer, value); }
    public bool CanAct => !IsBusy && AllowOperations;
    public bool CanRetryImport => CanAct && HasPendingImport;
    public PiSessionCandidateRow? SelectedCandidate { get => _selectedCandidate; set => SetProperty(ref _selectedCandidate, value); }
    public PiSessionTreeRow? SelectedEntry { get => _selectedEntry; set { SetProperty(ref _selectedEntry, value); OnPropertyChanged(nameof(CanNavigate)); } }
    public bool CanNavigate => CanAct && Snapshot is not null && SelectedEntry is not null;
    public bool CanCancelNavigation { get => _canCancelNavigation; internal set => SetProperty(ref _canCancelNavigation, value); }
    public bool SummarizeBranch { get; set; }
    public string SummaryInstructions { get; set; } = string.Empty;
    public bool ReplaceSummaryInstructions { get; set; }
    public string NavigationPrompt { get => _navigationPrompt; internal set { SetProperty(ref _navigationPrompt, value); OnPropertyChanged(nameof(CanCopyNavigationPrompt)); } }
    public bool CanCopyNavigationPrompt => NavigationPrompt.Length > 0;
    public string NewTitle { get; set; } = string.Empty;

    private void RaiseActionState()
    {
        OnPropertyChanged(nameof(CanAct));
        OnPropertyChanged(nameof(CanRetryImport));
        OnPropertyChanged(nameof(HasMoreCandidates));
        OnPropertyChanged(nameof(HasMoreEntries));
        OnPropertyChanged(nameof(CanNavigate));
    }

    internal void Apply(PiSessionBrowserResult result, bool append = false)
    {
        Directory = result.Directory;
        SelectedCandidate = null;
        if (!append) Candidates.Clear();
        foreach (var session in result.Sessions.Where(session => !Candidates.Any(candidate => candidate.Session.Path == session.Path))) Candidates.Add(new(session));
        BrowserNextOffset = result.NextOffset;
        OnPropertyChanged(nameof(HasMoreCandidates));
        Status = $"Found {Candidates.Count} sessions. {result.SkippedFiles} unreadable or unsupported files skipped." +
            (result.IsTruncated ? " Load more to inspect additional files." : string.Empty);
    }

    internal void Apply(PiSessionSnapshot snapshot, bool append = false)
    {
        Snapshot = snapshot;
        SelectedEntry = null;
        if (!append) Entries.Clear();
        foreach (var entry in snapshot.Entries) Entries.Add(new(entry));
        Summary = $"{snapshot.ActiveMessageCount} messages in the active branch · {snapshot.TotalEntries} tree entries\n" +
            $"Model: {snapshot.Model?.ProviderId ?? "unknown"} / {snapshot.Model?.ModelId ?? "unknown"} · thinking: {snapshot.ThinkingLevel ?? "unknown"}\n" +
            $"Tokens: {snapshot.TotalTokens?.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) ?? "unknown"} · cost: {snapshot.Cost?.ToString("C4", System.Globalization.CultureInfo.GetCultureInfo("en-US")) ?? "unknown"}";
        Status = snapshot.IsTruncated ? $"Showing {Entries.Count} of {snapshot.TotalEntries} entries. Load more to continue." : "Select an entry to switch branches, a completed assistant response to fork, or copy the whole session.";
        OnPropertyChanged(nameof(HasMoreEntries));
    }

    internal void ClearThread()
    {
        Snapshot = null;
        NavigationPrompt = string.Empty;
        OnPropertyChanged(nameof(HasMoreEntries));
        SelectedEntry = null;
        Entries.Clear();
        Summary = string.Empty;
    }
}
