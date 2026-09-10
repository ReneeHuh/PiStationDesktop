using System.Diagnostics;
using PiStation.Host.Errors;
using PiStation.Host.Git;
using PiStation.Host.Persistence;
using PiStation.Host.Projects;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class WorkspaceGitServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhitespacePreferenceFiltersPreviewWithoutChangingWorkingFileOrStatus(bool staged)
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectRoot = temporaryDirectory.CreateDirectory("project");
        InitializeRepository(projectRoot);
        var path = Path.Combine(projectRoot, "README.md");
        await File.WriteAllTextAsync(path, "  baseline \t\n");
        if (staged) RunGit(projectRoot, "add", "README.md");
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var project = await new ProjectService(database).AddAsync(new AddProjectRequest(projectRoot));
        var service = new WorkspaceGitService(database);
        var request = new GetProjectChangeDiffRequest(project.ProjectId, "README.md");
        var original = await service.GetDiffAsync(request);
        Assert.Contains("+  baseline", original.DiffContent, StringComparison.Ordinal);
        var filtered = await service.GetDiffAsync(request with { IgnoreWhitespace = true });
        Assert.DoesNotContain("+  baseline", filtered.DiffContent, StringComparison.Ordinal);
        Assert.Single((await service.GetChangesAsync(new GetProjectChangesRequest(project.ProjectId))).Changes);
        Assert.Equal("  baseline \t\n", await File.ReadAllTextAsync(path));
        Assert.Equal(original.DiffContent, (await service.GetDiffAsync(request)).DiffContent);
        await File.WriteAllTextAsync(path, "actual change\n");
        Assert.Contains("+actual change", (await service.GetDiffAsync(request with { IgnoreWhitespace = true })).DiffContent, StringComparison.Ordinal);
    }
    [Fact]
    public async Task ChangesReportBranchAndStagedUnstagedAndUntrackedFiles()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectRoot = temporaryDirectory.CreateDirectory("project");
        InitializeRepository(projectRoot);
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "README.md"), "changed\n");
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "staged.cs"), "class Staged;\n");
        RunGit(projectRoot, "add", "staged.cs");
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "notes.txt"), "untracked\n");
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var project = await new ProjectService(database).AddAsync(new AddProjectRequest(projectRoot));
        var service = new WorkspaceGitService(database);

        var result = await service.GetChangesAsync(new GetProjectChangesRequest(project.ProjectId));
        var limited = await service.GetChangesAsync(new GetProjectChangesRequest(project.ProjectId, 2));

        Assert.True(result.IsRepository);
        Assert.Equal("main", result.BranchName);
        Assert.Equal(3, result.Changes.Count);
        var modified = Assert.Single(result.Changes, static change => change.RelativePath == "README.md");
        Assert.Equal(GitFileStatus.None, modified.StagedStatus);
        Assert.Equal(GitFileStatus.Modified, modified.WorkingTreeStatus);
        var staged = Assert.Single(result.Changes, static change => change.RelativePath == "staged.cs");
        Assert.Equal(GitFileStatus.Added, staged.StagedStatus);
        Assert.Equal(GitFileStatus.None, staged.WorkingTreeStatus);
        var untracked = Assert.Single(result.Changes, static change => change.RelativePath == "notes.txt");
        Assert.Equal(GitFileStatus.None, untracked.StagedStatus);
        Assert.Equal(GitFileStatus.Untracked, untracked.WorkingTreeStatus);
        Assert.Equal(1, untracked.Additions);
        Assert.Equal(1, modified.Additions);
        Assert.Equal(1, modified.Deletions);
        Assert.Equal(2, limited.Changes.Count);
        Assert.True(limited.IsTruncated);
    }

    [Fact]
    public async Task DiffsDistinguishStagedWorkingTreeAndUntrackedContent()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectRoot = temporaryDirectory.CreateDirectory("project");
        InitializeRepository(projectRoot);
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "README.md"), "changed\n");
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "staged.cs"), "class Staged;\n");
        RunGit(projectRoot, "add", "staged.cs");
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "notes.txt"), "untracked\n");
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var project = await new ProjectService(database).AddAsync(new AddProjectRequest(projectRoot));
        var service = new WorkspaceGitService(database);

        var working = await service.GetDiffAsync(new GetProjectChangeDiffRequest(project.ProjectId, "README.md"));
        var staged = await service.GetDiffAsync(new GetProjectChangeDiffRequest(project.ProjectId, "staged.cs"));
        var untracked = await service.GetDiffAsync(new GetProjectChangeDiffRequest(project.ProjectId, "notes.txt"));
        var bounded = await service.GetDiffAsync(new GetProjectChangeDiffRequest(
            project.ProjectId,
            "README.md",
            MaximumCharacters: 48));

        Assert.True(working.HasWorkingTreeChanges);
        Assert.Contains("WORKING TREE CHANGES", working.DiffContent, StringComparison.Ordinal);
        Assert.Contains("+changed", working.DiffContent, StringComparison.Ordinal);
        Assert.True(staged.HasStagedChanges);
        Assert.Contains("STAGED CHANGES", staged.DiffContent, StringComparison.Ordinal);
        Assert.Contains("+class Staged;", staged.DiffContent, StringComparison.Ordinal);
        Assert.True(untracked.IsUntracked);
        Assert.Contains("UNTRACKED FILE", untracked.DiffContent, StringComparison.Ordinal);
        Assert.Contains("+untracked", untracked.DiffContent, StringComparison.Ordinal);
        Assert.DoesNotContain(projectRoot, untracked.DiffContent, StringComparison.OrdinalIgnoreCase);
        Assert.True(bounded.IsTruncated);
        Assert.True(bounded.DiffContent.Length <= 48);
    }

    [Fact]
    public async Task ChangesReturnAnExplicitNonRepositoryState()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectRoot = temporaryDirectory.CreateDirectory("project");
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var project = await new ProjectService(database).AddAsync(new AddProjectRequest(projectRoot));
        var service = new WorkspaceGitService(database);

        var result = await service.GetChangesAsync(new GetProjectChangesRequest(project.ProjectId));

        Assert.False(result.IsRepository);
        Assert.Empty(result.Changes);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("/etc/passwd")]
    public async Task DiffRejectsPathsOutsideTheProject(string relativePath)
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var service = new WorkspaceGitService(database);

        var exception = await Assert.ThrowsAsync<HostOperationException>(() => service.GetDiffAsync(
            new GetProjectChangeDiffRequest(PiStation.Protocol.Identifiers.ProjectId.New(), relativePath)));

        Assert.Equal(ProtocolErrorCodes.GitInvalid, exception.Code);
    }

    private static void InitializeRepository(string projectRoot)
    {
        RunGit(projectRoot, "init", "--quiet", "--initial-branch=main");
        RunGit(projectRoot, "config", "user.email", "pistation@example.invalid");
        RunGit(projectRoot, "config", "user.name", "Pi Station Tests");
        File.WriteAllText(Path.Combine(projectRoot, "README.md"), "baseline\n");
        RunGit(projectRoot, "add", "README.md");
        RunGit(projectRoot, "commit", "--quiet", "-m", "baseline");
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Git did not start.");
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, standardError);
    }
}
