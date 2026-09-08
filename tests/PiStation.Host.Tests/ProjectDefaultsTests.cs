using PiStation.Host.Errors;
using PiStation.Host.Persistence;
using PiStation.Host.Projects;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class ProjectDefaultsTests
{
    [Fact]
    public void PackageDiscoveryPreservesPagingAndRejectsInstallableSourceInjection()
    {
        using var response = System.Text.Json.JsonDocument.Parse("""{"total":3,"objects":[{"package":{"name":"@example/pi-tool","version":"1.2.3","description":"Tool"}},{"package":{"name":"../outside","version":"--unsafe","description":"bad"}}]}""");
        var page = PiPackageCatalog.Parse(response.RootElement, 0);
        Assert.Equal("npm:@example/pi-tool@1.2.3", Assert.Single(page.Items).Source);
        Assert.Equal(2, page.NextOffset);
    }

    [Fact]
    public async Task NativeCustomizationSurvivesDefaultsSaveAndRepositoryRefresh()
    {
        using var directory = new HostTestDirectory();
        var root = directory.CreateDirectory("project");
        var database = new HostDatabase(directory.CreateOptions()); await database.InitializeAsync();
        var service = new ProjectService(database); var project = await service.AddAsync(new(root));
        var script = new ProjectScript("test", "Run tests", "dotnet test", ProjectScriptIcon.Play, true);
        await service.UpdateDefaultsAsync(new(project.ProjectId, ThreadWorkspaceMode.Local, null, null, "supervised", false, [script], "emoji:🚀", true));
        await service.UpdateDefaultsAsync(new(project.ProjectId, ThreadWorkspaceMode.Worktree, null, null, "supervised", true));
        await File.WriteAllTextAsync(Path.Combine(root, "t3.json"), """{"scripts":[{"name":"Other","command":"echo other"}]}""");
        var refreshed = Assert.Single(await service.ListAsync());
        Assert.Equal(script, Assert.Single(refreshed.Scripts!)); Assert.Equal("emoji:🚀", refreshed.Icon);
        Assert.Equal("supervised", refreshed.DefaultRuntimeModeId); Assert.True(refreshed.AutoPullDefaultBranch);
    }

    [Fact]
    public async Task SavedDefaultsSurviveRefreshRestartAndReAddWhileRepositoryScriptsRefresh()
    {
        using var directory = new HostTestDirectory();
        var root = directory.CreateDirectory("project");
        var database = new HostDatabase(directory.CreateOptions());
        await database.InitializeAsync();
        var service = new ProjectService(database);
        var project = await service.AddAsync(new(root));
        await service.UpdateDefaultsAsync(new(project.ProjectId, ThreadWorkspaceMode.Worktree,
            new("provider", "model"), PiThinkingLevel.XHigh, null, true));
        await File.WriteAllTextAsync(Path.Combine(root, "t3.json"), """{"scripts":[{"name":"Test","command":"dotnet test"}]}""");
        await database.InitializeAsync();
        var refreshed = Assert.Single(await new ProjectService(database).ListAsync());
        Assert.Equal(ThreadWorkspaceMode.Worktree, refreshed.DefaultWorkspaceMode);
        Assert.Equal(new("provider", "model"), refreshed.DefaultModel);
        Assert.Equal(PiThinkingLevel.XHigh, refreshed.DefaultThinkingLevel);
        Assert.True(refreshed.AutoPullDefaultBranch);
        Assert.Equal("dotnet test", Assert.Single(refreshed.Scripts!).Command);
        Assert.Equal(refreshed.DefaultModel, (await service.AddAsync(new(root))).DefaultModel);
    }

    [Fact]
    public async Task UnsupportedRuntimeModeIsRejectedWithoutChangingDefaults()
    {
        using var directory = new HostTestDirectory();
        var database = new HostDatabase(directory.CreateOptions());
        await database.InitializeAsync();
        var service = new ProjectService(database);
        var project = await service.AddAsync(new(directory.CreateDirectory("project")));
        await Assert.ThrowsAsync<HostOperationException>(() => service.UpdateDefaultsAsync(new(project.ProjectId,
            ThreadWorkspaceMode.Worktree, null, null, "invented", true)));
        Assert.Equal(ThreadWorkspaceMode.Local, (await database.GetProjectAsync(project.ProjectId))!.DefaultWorkspaceMode);
    }

    [Fact]
    public async Task NewThreadInheritsSelectionAndProjectDefaultsTakePrecedence()
    {
        using var directory = new HostTestDirectory();
        var database = new HostDatabase(directory.CreateOptions());
        await database.InitializeAsync();
        var service = new ProjectService(database);
        var project = await service.AddAsync(new(directory.CreateDirectory("project")));
        var first = await service.CreateThreadAsync(new(project.ProjectId, InheritedModel: new("p", "m"), InheritedThinkingLevel: PiThinkingLevel.High));
        var config = await database.GetOrCreateThreadPiConfigurationAsync(first.ThreadId);
        Assert.Equal(new("p", "m"), config.Model);
        Assert.Equal(PiThinkingLevel.High, config.ThinkingLevel);
        await service.UpdateDefaultsAsync(new(project.ProjectId, ThreadWorkspaceMode.Local, new("p", "override"), PiThinkingLevel.Low, null, false));
        var next = await service.CreateThreadAsync(new(project.ProjectId, InheritedModel: new("p", "m"), InheritedThinkingLevel: PiThinkingLevel.High));
        config = await database.GetOrCreateThreadPiConfigurationAsync(next.ThreadId);
        Assert.Equal(new("p", "override"), config.Model);
        Assert.Equal(PiThinkingLevel.Low, config.ThinkingLevel);
    }
}
