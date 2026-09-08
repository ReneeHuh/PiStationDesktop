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
    private string _status = "Import a Pi session, or inspect the selected idle thread.";
    private string _directory = string.Empty;
    private string _summary = string.Empty;
    private PiSessionCandidateRow? _selectedCandidate;
    private PiSessionTreeRow? _selectedEntry;
    public ObservableCollection<PiSessionCandidateRow> Candidates { get; } = [];
    public ObservableCollection<PiSessionTreeRow> Entries { get; } = [];
    public PiSessionSnapshot? Snapshot { get; private set; }
    public int? BrowserNextOffset { get; private set; }
    public bool HasMoreCandidates => BrowserNextOffset is not null;
    public bool HasMoreEntries => Snapshot?.NextOffset is not null;
    public string Directory { get => _directory; set => SetProperty(ref _directory, value); }
    public string Status { get => _status; internal set => SetProperty(ref _status, value); }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public bool IsBusy { get => _isBusy; internal set { if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(CanAct)); } }
    public bool CanAct => !IsBusy;
    public PiSessionCandidateRow? SelectedCandidate { get => _selectedCandidate; set => SetProperty(ref _selectedCandidate, value); }
    public PiSessionTreeRow? SelectedEntry { get => _selectedEntry; set => SetProperty(ref _selectedEntry, value); }
    public string NewTitle { get; set; } = string.Empty;

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
        Status = snapshot.IsTruncated ? $"Showing {Entries.Count} of {snapshot.TotalEntries} entries. Load more to continue." : "Select a completed assistant response to fork, or copy the whole session.";
        OnPropertyChanged(nameof(HasMoreEntries));
    }

    internal void ClearThread()
    {
        Snapshot = null;
        OnPropertyChanged(nameof(HasMoreEntries));
        SelectedEntry = null;
        Entries.Clear();
        Summary = string.Empty;
    }
}
