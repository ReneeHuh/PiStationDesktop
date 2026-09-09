using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PiStation.ClientRuntime;
using PiStation.Protocol.Models;
using Windows.Storage;

namespace PiStation.App.ViewModels;

public sealed class ProjectGroupViewModel : ObservableObject
{
    private bool _isExpanded = true;
    private bool _showAll;
    private ThreadInboxShelf _shelf;
    private SidebarPreferences _preferences = new();
    private int _visibleCount;
    public ProjectGroupViewModel(ProjectDescriptor project)
    {
        Project = project;
        GroupKey = project.ProjectId.Value;
        Members.Add(project);
        try { _isExpanded = ApplicationData.Current.LocalSettings.Values[$"ProjectExpanded.{project.ProjectId.Value}"] as bool? ?? true; }
        catch (InvalidOperationException) { }
    }
    public ProjectDescriptor Project { get; private set; }
    public string GroupKey { get; set; }
    public ObservableCollection<ProjectDescriptor> Members { get; } = [];
    public string DisplayName => Project.DisplayName + (Members.Count > 1 ? $" · {Members.Count} checkouts" : "");
    public void SetMembers(IReadOnlyList<ProjectDescriptor> members)
    {
        Project = members[0]; Members.Clear(); foreach (var member in members) Members.Add(member);
        foreach (var name in new[] { nameof(Project), nameof(DisplayName), nameof(CanonicalPath), nameof(Icon), nameof(Emoji), nameof(ImageIcon) }) OnPropertyChanged(name);
    }
    public string CanonicalPath => Project.CanonicalPath;
    public string? Icon => Project.Icon;
    public string Emoji => Project.Icon?.StartsWith("emoji:", StringComparison.Ordinal) == true ? Project.Icon[6..] : "";
    private bool _remoteIcon;
    private byte[]? _imageContent;
    private string? _requestedIcon;
    private bool _iconLoadPending;
    private int _iconGeneration;
    public object? ImageIcon => string.IsNullOrEmpty(Emoji) ? _remoteIcon ? _imageContent : Project.Icon : null;
    internal async Task LoadRemoteIconAsync(IEnvironmentClient client)
    {
        _remoteIcon = true;
        if (_requestedIcon == Project.Icon && (_imageContent is not null || _iconLoadPending)) return;
        var generation = ++_iconGeneration;
        _requestedIcon = Project.Icon;
        _imageContent = null;
        _iconLoadPending = false;
        OnPropertyChanged(nameof(ImageIcon));
        if (string.IsNullOrEmpty(Project.Icon) || !string.IsNullOrEmpty(Emoji)) return;
        _iconLoadPending = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var content = await client.ReadProjectIconAsync(Project.ProjectId, timeout.Token);
            if (generation != _iconGeneration) return;
            if (content?.Length <= ProjectIconLimits.MaximumBytes) _imageContent = content;
            OnPropertyChanged(nameof(ImageIcon));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Leave the image empty when the host icon is missing or inaccessible.
            if (generation == _iconGeneration) _requestedIcon = null;
        }
        finally { if (generation == _iconGeneration) _iconLoadPending = false; }
    }
    public ObservableCollection<ThreadDescriptor> Threads { get; } = [];
    public IReadOnlyList<ThreadDescriptor> AllThreads { get; private set; } = [];
    public string Summary => Threads.Count < _visibleCount ? $"{Threads.Count} of {_visibleCount} tasks" : $"{Threads.Count} tasks";
    public bool CanShowMore => Threads.Count < _visibleCount;
    public void ShowAll() { _showAll = true; Apply(AllThreads, _shelf, _preferences); }
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            try { ApplicationData.Current.LocalSettings.Values[$"ProjectExpanded.{Project.ProjectId.Value}"] = value; }
            catch (InvalidOperationException) { }
        }
    }
    public void Apply(IReadOnlyList<ThreadDescriptor> threads, ThreadInboxShelf shelf, SidebarPreferences? preferences = null)
    {
        _shelf = shelf; _preferences = preferences ?? _preferences;
        AllThreads = threads.ToArray();
        var visible = ThreadInbox.Select(threads, shelf, DateTimeOffset.UtcNow);
        _visibleCount = visible.Count;
        if (_preferences.ThreadSort == 1) visible = visible.OrderByDescending(thread => thread.IsPinned).ThenBy(thread => thread.PinnedOrder)
            .ThenByDescending(thread => thread.CreatedUtc).ToArray();
        if (!_showAll) visible = visible.Take(_preferences.PreviewCount).ToArray();
        if (Threads.SequenceEqual(visible)) { OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(CanShowMore)); return; }
        Threads.Clear();
        foreach (var thread in visible) Threads.Add(thread);
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CanShowMore));
    }
}
