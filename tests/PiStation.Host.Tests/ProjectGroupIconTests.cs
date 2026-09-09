using System.Diagnostics;
using PiStation.Host.Errors;
using PiStation.Host.Persistence;
using PiStation.Host.Projects;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class ProjectGroupIconTests
{
    private static byte[] ImageBytes => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");

    [Fact]
    public async Task GroupIconsShareOneStoredImageAndPreserveCheckoutSettingsAcrossReload()
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions();
        var firstRoot = await CheckoutAsync(directory, "first", "https://example.test/team/repo.git");
        var secondRoot = await CheckoutAsync(directory, "second", "git@example.test:team/repo.git");
        var custom = new ProjectScript("custom", "Custom", "echo custom", ProjectScriptIcon.Play, true);
        ProjectId firstId, secondId;
        string storedIcon;
        await using (var environment = await EnvironmentService.CreateAsync(options))
        {
            var first = await environment.AddProjectAsync(new(firstRoot)); firstId = first.ProjectId;
            var second = await environment.AddProjectAsync(new(secondRoot)); secondId = second.ProjectId;
            await environment.UpdateProjectDefaultsAsync(new(firstId, ThreadWorkspaceMode.Worktree, new("p", "m"),
                PiThinkingLevel.High, "supervised", true, [custom], "emoji:🧪", true));
            var database = new HostDatabase(options);
            await database.InitializeAsync();
            await database.SetProjectScriptsTrustAsync(firstId, true, default);
            var updated = await environment.UpdateProjectIconsAsync(new([firstId, secondId], UploadedIcon: new("shared.png", ImageBytes)));
            Assert.Equal(2, updated.Length);
            storedIcon = updated[0].Icon!;
            Assert.All(updated, project => Assert.Equal(storedIcon, project.Icon));
            Assert.Equal(ImageBytes, await File.ReadAllBytesAsync(storedIcon));
            Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(storedIcon)!));
            var savedFirst = updated.Single(p => p.ProjectId == firstId);
            Assert.Equal(custom, Assert.Single(savedFirst.Scripts!));
            Assert.Equal(new("p", "m"), savedFirst.DefaultModel);
            Assert.Equal(ThreadWorkspaceMode.Worktree, savedFirst.DefaultWorkspaceMode);
            Assert.True(savedFirst.AreRepositoryScriptsTrusted);
            var savedSecond = updated.Single(p => p.ProjectId == secondId);
            Assert.Empty(savedSecond.Scripts!); Assert.False(savedSecond.AreRepositoryScriptsTrusted);
            Assert.Null(savedSecond.DefaultModel);
            // An icon-only update must not freeze the peer's repository settings.
            await File.WriteAllTextAsync(Path.Combine(secondRoot, "t3.json"), """{"scripts":[{"name":"Fresh","command":"echo fresh"}]}""");
        }
        await using (var restarted = await EnvironmentService.CreateAsync(options))
        {
            var projects = await restarted.ListProjectsAsync();
            Assert.All(projects, project => Assert.Equal(storedIcon, project.Icon));
            Assert.Equal("echo fresh", Assert.Single(projects.Single(p => p.ProjectId == secondId).Scripts!).Command);
            await restarted.UpdateProjectIconsAsync(new([firstId, secondId], "emoji:🚀"));
            Assert.All(await restarted.ListProjectsAsync(), project => Assert.Equal("emoji:🚀", project.Icon));
            await File.WriteAllBytesAsync(Path.Combine(firstRoot, "icon.png"), ImageBytes);
            await File.WriteAllBytesAsync(Path.Combine(secondRoot, "logo.png"), ImageBytes);
            var automatic = await restarted.UpdateProjectIconsAsync(new([firstId, secondId]));
            Assert.Equal(Path.Combine(firstRoot, "icon.png"), automatic.Single(p => p.ProjectId == firstId).Icon);
            Assert.Equal(Path.Combine(secondRoot, "logo.png"), automatic.Single(p => p.ProjectId == secondId).Icon);
            // Editing scripts after choosing Automatic must keep following repository icons.
            await restarted.UpdateProjectDefaultsAsync(new(firstId, ThreadWorkspaceMode.Worktree, new("p", "m"),
                PiThinkingLevel.High, "supervised", true, [custom], Path.Combine(firstRoot, "icon.png"), true, UpdateIcon: false));
            await File.WriteAllBytesAsync(Path.Combine(firstRoot, "favicon.png"), ImageBytes);
        }
        var reloadedDatabase = new HostDatabase(options); await reloadedDatabase.InitializeAsync();
        var reloaded = await new ProjectService(reloadedDatabase).ListAsync();
        Assert.Equal(Path.Combine(firstRoot, "favicon.png"), reloaded.Single(p => p.ProjectId == firstId).Icon);
        Assert.Equal(custom, Assert.Single(reloaded.Single(p => p.ProjectId == firstId).Scripts!));
    }

    [Fact]
    public async Task InvalidGroupsAndInvalidImagesDoNotPartiallyChangeIcons()
    {
        using var directory = new HostTestDirectory();
        await using var environment = await EnvironmentService.CreateAsync(directory.CreateOptions());
        var first = await environment.AddProjectAsync(new(await CheckoutAsync(directory, "first", "https://example.test/a.git")));
        var second = await environment.AddProjectAsync(new(await CheckoutAsync(directory, "second", "https://example.test/b.git")));
        await environment.UpdateProjectIconsAsync(new([first.ProjectId], "emoji:🚀"));
        await Assert.ThrowsAsync<ArgumentException>(() => environment.UpdateProjectIconsAsync(new([first.ProjectId, second.ProjectId], "emoji:🧪")));
        await Assert.ThrowsAsync<HostOperationException>(() => environment.UpdateProjectIconsAsync(new([first.ProjectId, ProjectId.New()], "emoji:🧪")));
        await Assert.ThrowsAsync<ArgumentException>(() => environment.UpdateProjectIconsAsync(new([first.ProjectId, first.ProjectId], "emoji:🧪")));
        await Assert.ThrowsAnyAsync<Exception>(() => environment.UpdateProjectIconsAsync(new([first.ProjectId], UploadedIcon: new("bad.png", new byte[20]))));
        var projects = await environment.ListProjectsAsync();
        Assert.Equal("emoji:🚀", projects.Single(p => p.ProjectId == first.ProjectId).Icon);
        Assert.Null(projects.Single(p => p.ProjectId == second.ProjectId).Icon);
    }

    private static async Task<string> CheckoutAsync(HostTestDirectory directory, string name, string origin)
    {
        var root = directory.CreateDirectory(name);
        foreach (var arguments in new[] { new[] { "init", "--quiet" }, new[] { "remote", "add", "origin", origin } })
        {
            using var process = new Process { StartInfo = new("git") { WorkingDirectory = root, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); await output;
            Assert.True(process.ExitCode == 0, await error);
        }
        return root;
    }
}
