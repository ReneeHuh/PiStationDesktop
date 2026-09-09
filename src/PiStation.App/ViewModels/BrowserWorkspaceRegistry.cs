using System.Collections.Specialized;
using PiStation.Protocol.Identifiers;

namespace PiStation.App.ViewModels;

/// <summary>Browser state belongs to an environment's threads, independently of selection.</summary>
internal sealed class BrowserWorkspaceRegistry(ShellLayoutViewModel layout)
{
    private readonly Dictionary<string, BrowserWorkspace> _workspaces = new(StringComparer.Ordinal);
    public IEnumerable<BrowserWorkspace> Workspaces => _workspaces.Values;
    public event EventHandler? Changed;

    public BrowserWorkspace GetOrAdd(ProjectId projectId, ThreadId? threadId)
    {
        var key = $"{projectId.Value}:{threadId?.Value ?? "project"}";
        if (_workspaces.TryGetValue(key, out var existing)) return existing;
        var model = new WorkbenchPreviewViewModel();
        model.Reset(true, layout.GetPreviewWorkspace(key), layout.GetPreviewUrl(projectId.Value),
            layout.BrowserProfiles, layout.DefaultBrowserProfileId, layout.GetPreviewAutomationPermission(key), layout.BrowserDefaults);
        // Old/imported preferences may repeat a tab id across threads. Keep runtime identities unique.
        var ids = _workspaces.Values.SelectMany(workspace => workspace.Model.Tabs).Select(tab => tab.TabId).ToHashSet(StringComparer.Ordinal);
        if (model.Tabs.Any(tab => !ids.Add(tab.TabId)))
        {
            var saved = model.CreatePreference();
            var tabs = saved.Tabs.Select(tab => tab with { TabId = Guid.NewGuid().ToString("N") }).ToArray();
            model.Reset(true, saved with { Tabs = tabs, ActiveTabId = tabs.FirstOrDefault()?.TabId }, null,
                layout.BrowserProfiles, layout.DefaultBrowserProfileId, layout.GetPreviewAutomationPermission(key), layout.BrowserDefaults);
        }
        var workspace = new BrowserWorkspace(key, projectId, threadId, model);
        _workspaces.Add(key, workspace);
        model.Tabs.CollectionChanged += OnTabsChanged;
        model.PropertyChanged += OnModelChanged;
        Changed?.Invoke(this, EventArgs.Empty);
        return workspace;
    }

    public BrowserWorkspace? Find(string? tabId) => tabId is null ? null :
        _workspaces.Values.FirstOrDefault(workspace => workspace.Model.FindTab(tabId) is not null);
    public bool Contains(BrowserWorkspace workspace) => _workspaces.GetValueOrDefault(workspace.Key) == workspace;
    public void Persist(BrowserWorkspace workspace)
    {
        if (Contains(workspace)) layout.SavePreviewWorkspace(workspace.Key, workspace.Model.CreatePreference());
    }
    public void UpdateSettings()
    {
        foreach (var workspace in _workspaces.Values)
        {
            workspace.Model.SetDefaults(layout.BrowserDefaults);
            workspace.Model.ReplaceProfiles(layout.BrowserProfiles, layout.DefaultBrowserProfileId);
        }
    }
    public void SynchronizeCatalog(IEnumerable<ProjectId> projects, IEnumerable<(ProjectId Project, ThreadId Thread)> threads)
    {
        var projectIds = projects.ToHashSet();
        var threadProjects = threads.ToDictionary(item => item.Thread, item => item.Project);
        foreach (var workspace in _workspaces.Values.ToArray())
            if (!projectIds.Contains(workspace.ProjectId) || workspace.ThreadId is { } thread &&
                (!threadProjects.TryGetValue(thread, out var project) || project != workspace.ProjectId)) Remove(workspace);
        // Previously granted threads can answer agents after reconnect/startup without being selected first.
        foreach (var (thread, project) in threadProjects)
            if (projectIds.Contains(project) && layout.GetPreviewAutomationPermission($"{project.Value}:{thread.Value}") != PreviewAutomationAccess.Off)
                GetOrAdd(project, thread);
    }
    public void Remove(BrowserWorkspace workspace)
    {
        if (!_workspaces.Remove(workspace.Key)) return;
        layout.SavePreviewAutomationPermission(workspace.Key, PreviewAutomationAccess.Off);
        layout.ClearPreviewWorkspace(workspace.Key);
        workspace.Model.Tabs.CollectionChanged -= OnTabsChanged;
        workspace.Model.PropertyChanged -= OnModelChanged;
        workspace.Model.SetAutomationPermission(PreviewAutomationAccess.Off);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkbenchPreviewViewModel.AutomationPermission) or nameof(WorkbenchPreviewViewModel.ActiveTab))
            Changed?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class BrowserWorkspace(string key, ProjectId projectId, ThreadId? threadId, WorkbenchPreviewViewModel model)
{
    public string Key { get; } = key;
    public ProjectId ProjectId { get; } = projectId;
    public ThreadId? ThreadId { get; } = threadId;
    public WorkbenchPreviewViewModel Model { get; } = model;
    public string? AgentTabId { get; set; }
    public WorkbenchPreviewTabViewModel? ResolveAgentTab(string? explicitTabId)
    {
        var target = explicitTabId ?? AgentTabId;
        return target is null ? Model.ActiveTab : Model.FindTab(target);
    }
}
