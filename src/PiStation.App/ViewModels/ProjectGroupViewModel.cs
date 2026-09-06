using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PiStation.ClientRuntime;
using PiStation.Protocol.Models;
using Windows.Storage;

namespace PiStation.App.ViewModels;

public sealed class ProjectGroupViewModel : ObservableObject
{
    private bool _isExpanded = true;
    public ProjectGroupViewModel(ProjectDescriptor project)
    {
        Project = project;
        try { _isExpanded = ApplicationData.Current.LocalSettings.Values[$"ProjectExpanded.{project.ProjectId.Value}"] as bool? ?? true; }
        catch (InvalidOperationException) { }
    }
    public ProjectDescriptor Project { get; }
    public string DisplayName => Project.DisplayName;
    public string CanonicalPath => Project.CanonicalPath;
    public string? Icon => Project.Icon;
    public ObservableCollection<ThreadDescriptor> Threads { get; } = [];
    public IReadOnlyList<ThreadDescriptor> AllThreads { get; private set; } = [];
    public string Summary => Threads.Count == 1 ? "1 task" : $"{Threads.Count} tasks";
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
    public void Apply(IReadOnlyList<ThreadDescriptor> threads, ThreadInboxShelf shelf)
    {
        AllThreads = threads.ToArray();
        var visible = ThreadInbox.Select(threads, shelf, DateTimeOffset.UtcNow);
        if (Threads.SequenceEqual(visible)) return;
        Threads.Clear();
        foreach (var thread in visible) Threads.Add(thread);
        OnPropertyChanged(nameof(Summary));
    }
}
