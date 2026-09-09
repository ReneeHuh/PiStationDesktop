using PiStation.App.Composition;
using PiStation.App.ViewModels;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.CommandSystem.Tests;

public sealed class BrowserPreferencesTests
{
    [Fact]
    public async Task SharedStoreMigratesRemoteProfileIdentitiesOnceWithoutResurrectingRemovedProfiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "pistation-browser-migration-" + Guid.NewGuid().ToString("N"));
        var layout = new ShellLayoutViewModel();
        try
        {
            var legacyRoot = Path.Combine(root, "remote-environments", new string('A', 64));
            Directory.CreateDirectory(legacyRoot);
            var malformedRoot = Path.Combine(root, "remote-environments", new string('B', 64));
            Directory.CreateDirectory(malformedRoot);
            await File.WriteAllTextAsync(Path.Combine(malformedRoot, "layout-settings.json"), "[]");
            var legacy = new ShellLayoutViewModel(Path.Combine(legacyRoot, "layout-settings.json"));
            var profile = legacy.AddBrowserProfile("Existing remote profile");
            var sharedPath = Path.Combine(root, "browser-settings.json");
            layout.UseSharedBrowserSettings(sharedPath);
            Assert.Contains(layout.BrowserProfiles, item => item.Id == profile.Id && item.Name == profile.Name);
            Assert.Contains(profile.Id, File.ReadAllText(sharedPath));
            await layout.RemoveBrowserProfileAsync(profile.Id, _ => Task.CompletedTask);
            // Reading a new store path exercises disk loading, independent of the process cache.
            var copiedPath = Path.Combine(root, "browser-copy.json");
            File.Copy(sharedPath, copiedPath);
            var reopened = new ShellLayoutViewModel();
            reopened.UseSharedBrowserSettings(copiedPath);
            Assert.DoesNotContain(reopened.BrowserProfiles, item => item.Id == profile.Id);
            reopened.ReleaseBrowserSettings();
        }
        finally { layout.ReleaseBrowserSettings(); Directory.Delete(root, true); }
    }

    [Fact]
    public void ProfileClearTargetsOnlyTheSelectedIdentityInKnownEnvironmentDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "pistation-profile-paths-" + Guid.NewGuid().ToString("N"));
        try
        {
            var local = Path.Combine(root, "browser-profiles");
            var remote = Path.Combine(root, "remote-environments", new string('A', 64), "browser-profiles");
            var unrelated = Path.Combine(root, "remote-environments", "notes", "browser-profiles");
            var id = "selected-profile";
            foreach (var profileRoot in new[] { local, remote, unrelated })
            {
                Directory.CreateDirectory(BrowserProfilePaths.ProfileDirectory(profileRoot, id));
                Directory.CreateDirectory(BrowserProfilePaths.ProfileDirectory(profileRoot, "another-profile"));
            }
            var paths = BrowserProfilePaths.ExistingProfileDirectories(root, remote, id);
            Assert.Equal(2, paths.Count);
            Assert.Contains(BrowserProfilePaths.ProfileDirectory(local, id), paths);
            Assert.Contains(BrowserProfilePaths.ProfileDirectory(remote, id), paths);
            Assert.DoesNotContain(paths, path => path.Contains("notes", StringComparison.Ordinal));
            Assert.StartsWith(Path.GetFullPath(local), BrowserProfilePaths.ProfileDirectory(local, "../../outside"), StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void InvalidDefaultsNormalizeAndProfileSelectionLocksAfterFirstNavigation()
    {
        var layout = new ShellLayoutViewModel();
        layout.SetBrowserDefaults(new((PreviewViewportPreset)100, double.NaN, (PreviewColorScheme)100));
        Assert.Equal(new BrowserDefaults(), layout.BrowserDefaults);
        var preview = new WorkbenchPreviewViewModel();
        preview.Reset(true);
        preview.AddTab();
        Assert.True(preview.CanChangeProfile);
        preview.PrepareNavigation("https://example.com", out _, out _);
        preview.ReturnToServers();
        Assert.False(preview.CanChangeProfile);
    }

    [Fact]
    public void DefaultsPersistAndOnlySeedNewTabs()
    {
        var root = Path.Combine(Path.GetTempPath(), "pistation-browser-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "layout.json");
            var layout = new ShellLayoutViewModel(path);
            var profile = layout.AddBrowserProfile("Work");
            layout.SetDefaultBrowserProfile(profile.Id);
            layout.SetBrowserDefaults(new(PreviewViewportPreset.Phone, 1.25, PreviewColorScheme.Dark));
            layout.BrowserLinkTargetIndex = 1;
            var restored = new ShellLayoutViewModel(path);
            Assert.Equal(layout.BrowserDefaults, restored.BrowserDefaults);
            Assert.Equal(BrowserLinkTarget.App, restored.BrowserLinkTarget);
            var preview = new WorkbenchPreviewViewModel();
            preview.Reset(true, browserProfiles: restored.BrowserProfiles, defaultProfileId: restored.DefaultBrowserProfileId, defaults: restored.BrowserDefaults);
            var first = preview.AddTab("https://example.com");
            Assert.Equal(profile.Id, first.ProfileId);
            Assert.Equal(390, first.SurfaceWidth);
            Assert.Equal(1.25, first.ZoomFactor);
            Assert.Equal(PreviewColorScheme.Dark, first.ColorScheme);
            preview.SetDefaults(new());
            Assert.Equal(1.25, first.ZoomFactor);
            Assert.Equal(1, preview.AddTab().ZoomFactor);
            var saved = preview.CreatePreference();
            preview.Reset(true, saved, browserProfiles: restored.BrowserProfiles, defaults: new());
            Assert.Equal(1.25, preview.Tabs[0].ZoomFactor);
            Assert.Equal(PreviewColorScheme.Dark, preview.Tabs[0].ColorScheme);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ProfileRenamePreservesIdentityAndFailedClearPreventsRemoval()
    {
        var layout = new ShellLayoutViewModel();
        var profile = layout.AddBrowserProfile("Before");
        layout.SetDefaultBrowserProfile(profile.Id);
        var preview = new WorkbenchPreviewViewModel();
        preview.Reset(true, browserProfiles: layout.BrowserProfiles, defaultProfileId: profile.Id);
        var tab = preview.AddTab("https://example.com");
        layout.RenameBrowserProfile(profile.Id, "After");
        Assert.Equal("After", layout.BrowserProfiles.Single(item => item.Id == profile.Id).Name);
        await Assert.ThrowsAsync<IOException>(() => layout.RemoveBrowserProfileAsync(profile.Id, _ => throw new IOException("locked")));
        Assert.Equal(profile.Id, layout.DefaultBrowserProfileId);
        Assert.Contains(layout.BrowserProfiles, item => item.Id == profile.Id);
        await layout.RemoveBrowserProfileAsync(profile.Id, id =>
        {
            Assert.Equal(profile.Id, id);
            Assert.Contains(layout.BrowserProfiles, item => item.Id == id);
            return Task.CompletedTask;
        });
        preview.ReplaceProfiles(layout.BrowserProfiles, layout.DefaultBrowserProfileId);
        Assert.Equal(profile.Id, tab.ProfileId); // Open tabs stay attached to their existing storage.
        Assert.Equal("default", preview.AddTab().ProfileId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => layout.RemoveBrowserProfileAsync("default", _ => Task.CompletedTask));
        Assert.Throws<InvalidOperationException>(() => layout.RenameBrowserProfile("default", "Other"));
    }

    [Fact]
    public void IncognitoTabsAndAddressesAreNotPersistedOrUsedAsDefault()
    {
        var preview = new WorkbenchPreviewViewModel();
        preview.Reset(true);
        preview.AddTab();
        preview.SelectProfile(preview.BrowserProfiles.Single(profile => profile.Id == "incognito"));
        preview.PrepareNavigation("https://example.com/private", out _, out _);
        Assert.Empty(preview.CreatePreference().Tabs);
        Assert.Empty(preview.CreatePreference().RecentUrls!);
        Assert.Null(preview.CreatePreference().ActiveTabId);
        var layout = new ShellLayoutViewModel();
        layout.SetDefaultBrowserProfile("incognito");
        Assert.Equal("default", layout.DefaultBrowserProfileId);
    }

    [Fact]
    public void SharedSettingsSynchronizeWindowsAndSurviveIndependentLayoutChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "pistation-browser-shared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var first = new ShellLayoutViewModel();
        var second = new ShellLayoutViewModel();
        try
        {
            var path = Path.Combine(root, "browser.json");
            first.UseSharedBrowserSettings(path);
            second.UseSharedBrowserSettings(path);
            first.BrowserDefaultZoomPercent = 150;
            Assert.Equal(150, second.BrowserDefaultZoomPercent);
            second.BrowserLinkTargetIndex = 1;
            Assert.Equal(BrowserLinkTarget.App, first.BrowserLinkTarget);
            var profile = first.AddBrowserProfile("Shared");
            Assert.Contains(second.BrowserProfiles, item => item.Id == profile.Id);
            second.IsSidebarCollapsed = true;
            Assert.Equal(150, first.BrowserDefaultZoomPercent);
            Assert.Contains("Shared", File.ReadAllText(path));
        }
        finally { first.ReleaseBrowserSettings(); second.ReleaseBrowserSettings(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("https://example.com", true, false, true)]
    [InlineData("https://example.com", true, true, false)]
    [InlineData("https://example.com", false, false, false)]
    [InlineData("mailto:hello@example.com", true, false, false)]
    [InlineData("https://user:secret@example.com", true, false, false)]
    public void LinkRoutingUsesThreadSchemeAndChatOverride(string address, bool thread, bool forceSystem, bool expected)
    {
        Assert.Equal(expected, BrowserLinkRouter.ShouldOpenInApp(new(address), BrowserLinkTarget.App, thread, forceSystem));
        Assert.False(BrowserLinkRouter.ShouldOpenInApp(new(address), BrowserLinkTarget.System, thread, false));
    }

    [Fact]
    public async Task FailedAppOpenFallsBackButCancellationDoesNotOpenElsewhere()
    {
        var external = 0;
        Task OpenSystem(Uri _) { external++; return Task.CompletedTask; }
        await BrowserLinkRouter.OpenAsync(new("https://example.com"), BrowserLinkTarget.App, true, false,
            _ => throw new InvalidOperationException("WebView unavailable"), OpenSystem);
        Assert.Equal(1, external);
        await BrowserLinkRouter.OpenAsync(new("https://example.com"), BrowserLinkTarget.App, true, false,
            _ => throw new OperationCanceledException(), OpenSystem);
        Assert.Equal(1, external);
    }

    [Fact]
    public void DiscoverySeparatesThreadOwnershipFromHostWideListeners()
    {
        var thread = ThreadId.New();
        var project = ProjectId.New();
        var owned = new DiscoveredPreviewServer("http://localhost:5173/", "localhost", 5173, "http", "node", 10,
            new(TerminalSessionId.New(), project, thread, "Terminal 1"));
        var unrelated = new DiscoveredPreviewServer("http://localhost:8080/", "localhost", 8080, "http");
        var preview = new WorkbenchPreviewViewModel();
        preview.Reset(true);
        preview.ApplyDiscovery(new(project, DateTimeOffset.UtcNow, [owned, unrelated], false), thread);
        Assert.Equal(2, preview.DiscoveredServers.Count);
        Assert.Contains("This thread", preview.DiscoveredServers[0].Ownership);
        Assert.Contains("No terminal owner", preview.DiscoveredServers[1].Ownership);
        var row = preview.DiscoveredServers[0];
        preview.ApplyDiscovery(new(project, DateTimeOffset.UtcNow, [owned, unrelated], false), thread);
        Assert.Same(row, preview.DiscoveredServers[0]); // An unchanged polling snapshot preserves UI selection/focus.
        preview.DiscoveryScopeIndex = 1;
        Assert.Equal(owned.Url, Assert.Single(preview.DiscoveredServers).Url);
        preview.ApplyDiscovery(new(project, DateTimeOffset.UtcNow, [owned, unrelated], false), ThreadId.New());
        Assert.Empty(preview.DiscoveredServers);
    }
}
