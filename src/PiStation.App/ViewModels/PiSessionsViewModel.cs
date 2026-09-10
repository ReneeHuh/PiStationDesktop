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

public sealed class PiSessionTreeRow(PiSessionTreeEntry entry, bool collapsed = false, bool showTimestamp = false)
{
    public PiSessionTreeEntry Entry { get; } = entry;
    public string Title => $"{(Entry.HasChildren ? collapsed ? "▶ " : "▼ " : "· ")}{Entry.Kind} · {Entry.Preview}";
    public string Details => $"{(Entry.IsActiveBranch ? "Active branch" : "Other branch")} · entry {Entry.Id} · parent {Entry.ParentId ?? "root"}" +
        (Entry.CanFork ? " · can fork here" : string.Empty) + (Entry.Label is null ? string.Empty : $" · Bookmark: {Entry.Label}") +
        (showTimestamp && Entry.LabelTimestamp is { } timestamp ? $" · {timestamp}" : string.Empty);
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
    private string? _selectedEntryId;
    private string _entryLabel = string.Empty;
    private string _searchQuery = string.Empty;
    private int _filterIndex;
    private bool _activeBranchOnly;
    private bool _showLabelTimestamps;
    public HashSet<string> CollapsedEntryIds { get; } = new(StringComparer.Ordinal);
    public bool ShowLabelTimestamps { get => _showLabelTimestamps; set { if (SetProperty(ref _showLabelTimestamps, value)) RefreshRows(); } }
    public bool CanFold => CanNavigate && SelectedEntry?.Entry.HasChildren == true;
    public ObservableCollection<PiSessionCandidateRow> Candidates { get; } = [];
    public ObservableCollection<PiSessionTreeRow> Entries { get; } = [];
    public PiSessionSnapshot? Snapshot { get; private set; }
    public int? BrowserNextOffset { get; private set; }
    public bool HasMoreCandidates => CanAct && BrowserNextOffset is not null;
    public bool HasMoreEntries => CanAct && IsCurrentQuery && Snapshot?.NextOffset is not null;
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
    public PiSessionTreeRow? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (!SetProperty(ref _selectedEntry, value)) return;
            if (value is not null) _selectedEntryId = value.Entry.Id;
            EntryLabel = value?.Entry.Label ?? string.Empty;
            RaiseActionState();
        }
    }
    public bool IsCurrentQuery => Snapshot is { } snapshot && snapshot.Filter == (PiSessionTreeFilter)FilterIndex &&
        (snapshot.SearchQuery ?? string.Empty) == SearchQuery && snapshot.ActiveBranchOnly == ActiveBranchOnly &&
        CollapsedEntryIds.SetEquals(snapshot.CollapsedEntryIds ?? []);
    public string SearchQuery { get => _searchQuery; set { if (SetProperty(ref _searchQuery, value)) RaiseActionState(); } }
    public int FilterIndex { get => _filterIndex; set { if (SetProperty(ref _filterIndex, value)) RaiseActionState(); } }
    public bool ActiveBranchOnly { get => _activeBranchOnly; set { if (SetProperty(ref _activeBranchOnly, value)) RaiseActionState(); } }
    public string EntryLabel { get => _entryLabel; set { if (SetProperty(ref _entryLabel, value)) OnPropertyChanged(nameof(CanSaveLabel)); } }
    public bool CanSaveLabel => CanNavigate && (string.IsNullOrWhiteSpace(EntryLabel) ? null : EntryLabel.Trim()) != SelectedEntry?.Entry.Label;
    public bool CanRemoveLabel => CanNavigate && SelectedEntry?.Entry.Label is not null;
    public bool CanNavigate => CanAct && IsCurrentQuery && SelectedEntry is not null;
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
        OnPropertyChanged(nameof(CanFold));
        OnPropertyChanged(nameof(CanSaveLabel));
        OnPropertyChanged(nameof(CanRemoveLabel));
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
        if (append && (Snapshot?.Revision != snapshot.Revision || Snapshot.ThreadId != snapshot.ThreadId ||
            Snapshot.Filter != snapshot.Filter || Snapshot.SearchQuery != snapshot.SearchQuery || Snapshot.ActiveBranchOnly != snapshot.ActiveBranchOnly ||
            !(Snapshot.CollapsedEntryIds ?? []).SequenceEqual(snapshot.CollapsedEntryIds ?? [])))
            throw new InvalidOperationException("The session search changed. Refresh the tree before loading more.");
        var selectedId = snapshot.SelectedEntryId ?? _selectedEntryId;
        var labelDraft = EntryLabel;
        Snapshot = snapshot;
        if (!append) Entries.Clear();
        var loadedIds = Entries.Select(row => row.Entry.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in snapshot.Entries.Where(entry => loadedIds.Add(entry.Id))) Entries.Add(new(entry, CollapsedEntryIds.Contains(entry.Id), ShowLabelTimestamps));
        SelectedEntry = Entries.FirstOrDefault(row => row.Entry.Id == selectedId);
        if (append && SelectedEntry is not null) EntryLabel = labelDraft;
        Summary = $"{snapshot.ActiveMessageCount} messages in the active branch · {snapshot.TotalEntries} tree entries\n" +
            $"Model: {snapshot.Model?.ProviderId ?? "unknown"} / {snapshot.Model?.ModelId ?? "unknown"} · thinking: {snapshot.ThinkingLevel ?? "unknown"}\n" +
            $"Tokens: {snapshot.TotalTokens?.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) ?? "unknown"} · cost: {snapshot.Cost?.ToString("C4", System.Globalization.CultureInfo.GetCultureInfo("en-US")) ?? "unknown"}";
        Status = $"Showing {Entries.Count} of {snapshot.MatchingEntries ?? snapshot.TotalEntries} matching entries." +
            (snapshot.IsTruncated ? " Load more to continue." : Entries.Count == 0 ? " Change the search or filters to find entries." : " Select an entry to edit its bookmark or switch branches.");
        RaiseActionState();
    }

    private void RefreshRows()
    {
        var selected = SelectedEntry?.Entry.Id;
        var labelDraft = EntryLabel;
        var entries = Entries.Select(row => row.Entry).ToArray();
        Entries.Clear();
        foreach (var entry in entries) Entries.Add(new(entry, CollapsedEntryIds.Contains(entry.Id), ShowLabelTimestamps));
        SelectedEntry = Entries.FirstOrDefault(row => row.Entry.Id == selected);
        EntryLabel = labelDraft;
    }

    internal void ClearThread()
    {
        Snapshot = null;
        CollapsedEntryIds.Clear();
        NavigationPrompt = string.Empty;
        OnPropertyChanged(nameof(HasMoreEntries));
        SelectedEntry = null;
        _selectedEntryId = null;
        Entries.Clear();
        Summary = string.Empty;
    }
}
