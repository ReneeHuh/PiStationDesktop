using PiStation.App.ViewModels;
using PiStation.ClientRuntime;
using PiStation.Protocol.Identifiers;

namespace PiStation.CommandSystem.Tests;

public sealed class BrowserWorkspaceTests
{
    [Fact]
    public void CatalogRestoresGrantedBackgroundThreadsAndReleasesDeletedTargets()
    {
        var project = ProjectId.New();
        var thread = ThreadId.New();
        var layout = new ShellLayoutViewModel();
        layout.SavePreviewAutomationPermission($"{project.Value}:{thread.Value}", PreviewAutomationAccess.Interact);
        var registry = new BrowserWorkspaceRegistry(layout);
        registry.SynchronizeCatalog([project], [(project, thread)]);
        var workspace = Assert.Single(registry.Workspaces);
        Assert.Equal(thread, workspace.ThreadId);
        Assert.Equal(PreviewAutomationAccess.Interact, workspace.Model.AutomationPermission);
        registry.SynchronizeCatalog([project], []);
        Assert.Empty(registry.Workspaces);
        Assert.Equal(PreviewAutomationAccess.Off, workspace.Model.AutomationPermission);
        Assert.Equal(PreviewAutomationAccess.Off, layout.GetPreviewAutomationPermission(workspace.Key));
    }

    [Fact]
    public void ThreadSwitchingRetainsDocumentsPermissionsAndAgentTargets()
    {
        var layout = new ShellLayoutViewModel();
        var registry = new BrowserWorkspaceRegistry(layout);
        var project = ProjectId.New();
        var a = registry.GetOrAdd(project, ThreadId.New());
        var first = a.Model.AddTab("https://example.com/a");
        var controllers = new Dictionary<BrowserWorkspace, string> { [a] = "controller" };
        a.Model.SetAutomationPermission(PreviewAutomationAccess.Interact);
        a.AgentTabId = first.TabId;
        Assert.Equal("controller", controllers[a]);
        var humanTab = a.Model.AddTab("https://example.com/human");
        var b = registry.GetOrAdd(project, ThreadId.New());
        var foreignTab = b.Model.AddTab("https://example.com/b");
        b.Model.SetAutomationPermission(PreviewAutomationAccess.Inspect);

        Assert.Same(first, a.ResolveAgentTab(null));
        Assert.Same(humanTab, a.Model.ActiveTab);
        Assert.Null(a.ResolveAgentTab(foreignTab.TabId));
        Assert.Same(a, registry.GetOrAdd(project, a.ThreadId));
        Assert.Equal(PreviewAutomationAccess.Interact, a.Model.AutomationPermission);
        a.Model.CloseTab(first);
        Assert.Null(a.ResolveAgentTab(null)); // A closed pinned tab must never silently target the human's tab.
        Assert.Same(humanTab, a.ResolveAgentTab(humanTab.TabId));
        registry.Remove(b);
        Assert.False(registry.Contains(b));
        Assert.Equal(PreviewAutomationAccess.Off, b.Model.AutomationPermission);
        Assert.True(registry.Contains(a));
    }

    [Fact]
    public void NewTabsUseUpdatedDefaultsAndKeepExistingTabState()
    {
        var layout = new ShellLayoutViewModel();
        var registry = new BrowserWorkspaceRegistry(layout);
        var a = registry.GetOrAdd(ProjectId.New(), ThreadId.New());
        var original = a.Model.AddTab();
        var profile = layout.AddBrowserProfile("Testing");
        layout.SetDefaultBrowserProfile(profile.Id);
        layout.SetBrowserDefaults(new(PreviewViewportPreset.Phone, 1.25, PreviewColorScheme.Dark));
        registry.UpdateSettings();
        var next = a.Model.AddTab();
        Assert.Equal(profile.Id, next.ProfileId);
        Assert.Equal(PreviewColorScheme.Dark, next.ColorScheme);
        Assert.Equal(1.25, next.ZoomFactor);
        Assert.Equal(390, next.SurfaceWidth);
        Assert.Equal("default", original.ProfileId);
        Assert.Equal(PreviewColorScheme.System, original.ColorScheme);
        next.MarkBrowserStarted();
        Assert.False(a.Model.CanChangeProfile);
    }

    [Fact]
    public void FreeformDimensionsAndAppearanceSurvivePersistenceInTheirOwnThread()
    {
        var layout = new ShellLayoutViewModel();
        var registry = new BrowserWorkspaceRegistry(layout);
        var workspace = registry.GetOrAdd(ProjectId.New(), ThreadId.New());
        var tab = workspace.Model.AddTab("https://example.com");
        var before = tab.ViewportRevision;
        tab.ApplyAutomationViewport(new("freeform", 1024, 768));
        tab.SetColorScheme(PreviewColorScheme.Dark);
        Assert.True(tab.ViewportRevision > before);
        registry.Persist(workspace);
        var restored = new BrowserWorkspaceRegistry(layout).GetOrAdd(workspace.ProjectId, workspace.ThreadId).Model.ActiveTab!;
        Assert.Equal(new BrowserViewportSetting("freeform", 1024, 768), restored.ViewportSetting);
        Assert.Equal(PreviewColorScheme.Dark, restored.ColorScheme);
        var revision = tab.ViewportRevision;
        tab.RotateViewport();
        Assert.True(tab.ViewportRevision > revision);
        Assert.Equal(768, tab.SurfaceWidth);
        Assert.Equal(1024, tab.SurfaceHeight);
    }

    [Fact]
    public void TabLimitDoesNotSilentlyReuseAnExistingTab()
    {
        var registry = new BrowserWorkspaceRegistry(new ShellLayoutViewModel());
        var workspace = registry.GetOrAdd(ProjectId.New(), ThreadId.New());
        for (var i = 0; i < WorkbenchPreviewViewModel.MaximumTabs; i++) workspace.Model.AddTab();
        var active = workspace.Model.ActiveTab;
        Assert.Throws<InvalidOperationException>(() => workspace.Model.AddTab());
        Assert.Same(active, workspace.Model.ActiveTab);
        Assert.Equal(WorkbenchPreviewViewModel.MaximumTabs, workspace.Model.Tabs.Count);
    }

    [Fact]
    public void DuplicateSavedIdsCannotResolveIntoAnotherThreadsDocument()
    {
        var layout = new ShellLayoutViewModel();
        var project = ProjectId.New();
        var threadA = ThreadId.New();
        var threadB = ThreadId.New();
        var saved = new PreviewWorkspacePreference("same", [new("same", "https://example.com", "Page", PreviewViewportPreset.Responsive, 0, 0)]);
        layout.SavePreviewWorkspace($"{project.Value}:{threadA.Value}", saved);
        layout.SavePreviewWorkspace($"{project.Value}:{threadB.Value}", saved);
        var registry = new BrowserWorkspaceRegistry(layout);
        var a = registry.GetOrAdd(project, threadA);
        var b = registry.GetOrAdd(project, threadB);
        Assert.NotEqual(a.Model.ActiveTab!.TabId, b.Model.ActiveTab!.TabId);
        Assert.Null(a.ResolveAgentTab(b.Model.ActiveTab.TabId));
        Assert.Same(b, registry.Find(b.Model.ActiveTab.TabId));
    }
}
