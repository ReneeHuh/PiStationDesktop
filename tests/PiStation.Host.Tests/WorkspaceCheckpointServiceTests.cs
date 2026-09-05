using System.Diagnostics;
using PiStation.Host.Git;
using PiStation.Host.Persistence;
using PiStation.Host.Projects;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class WorkspaceCheckpointServiceTests
{
    [Fact]
    public async Task CapturesSummarizesDiffsRestoresAndPrunesTurnCheckpoints()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var projectRoot = temporaryDirectory.CreateDirectory("project");
        InitializeRepository(projectRoot);
        var database = new HostDatabase(temporaryDirectory.CreateOptions());
        await database.InitializeAsync();
        var projects = new ProjectService(database);
        var project = await projects.AddAsync(new AddProjectRequest(projectRoot));
        var thread = await projects.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        var service = new WorkspaceCheckpointService(database);

        Assert.True(await service.EnsureBaselineAsync(project, thread.ThreadId, 0));
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "README.md"), "changed\n");
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "notes.txt"), "new note\n");
        var first = await service.CaptureTurnAsync(
            project,
            thread.ThreadId,
            TurnId.New(),
            1,
            null,
            "entry-0002");

        Assert.NotNull(first);
        Assert.Equal(ThreadCheckpointStatus.Ready, first.Status);
        Assert.Equal(2, first.Files.Count);
        Assert.Equal(2, first.Files.Sum(static file => file.Additions));
        Assert.Equal(1, first.Files.Sum(static file => file.Deletions));
        var workingTree = await new WorkspaceGitService(database).GetChangesAsync(
            new GetProjectChangesRequest(project.ProjectId));
        var readmeChange = workingTree.Changes.Single(static change => change.RelativePath == "README.md");
        var notesChange = workingTree.Changes.Single(static change => change.RelativePath == "notes.txt");
        Assert.Equal(GitFileStatus.None, readmeChange.StagedStatus);
        Assert.Equal(GitFileStatus.Modified, readmeChange.WorkingTreeStatus);
        Assert.Equal(GitFileStatus.Untracked, notesChange.WorkingTreeStatus);

        Assert.True(await service.EnsureBaselineAsync(project, thread.ThreadId, 1));
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "README.md"), "changed\nsecond\n");
        var second = await service.CaptureTurnAsync(
            project,
            thread.ThreadId,
            TurnId.New(),
            2,
            "entry-0002",
            "entry-0004");

        Assert.NotNull(second);
        var turnDiff = await service.GetDiffAsync(new GetThreadCheckpointDiffRequest(
            thread.ThreadId,
            2,
            CheckpointDiffScope.Turn));
        var fullDiff = await service.GetDiffAsync(new GetThreadCheckpointDiffRequest(
            thread.ThreadId,
            2,
            CheckpointDiffScope.FullThread));
        Assert.Equal(1, turnDiff.FromTurnCount);
        Assert.Contains("+second", turnDiff.DiffContent, StringComparison.Ordinal);
        Assert.Equal(0, fullDiff.FromTurnCount);
        Assert.Contains("+changed", fullDiff.DiffContent, StringComparison.Ordinal);
        Assert.Contains("+new note", fullDiff.DiffContent, StringComparison.Ordinal);

        await File.WriteAllTextAsync(Path.Combine(projectRoot, "README.md"), "uncheckpointed\n");
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "later.txt"), "discard me\n");
        await service.RestoreAsync(project, thread.ThreadId, 1);

        Assert.Equal("changed\n", (await File.ReadAllTextAsync(Path.Combine(projectRoot, "README.md")))
            .ReplaceLineEndings("\n"));
        Assert.Equal("new note\n", (await File.ReadAllTextAsync(Path.Combine(projectRoot, "notes.txt")))
            .ReplaceLineEndings("\n"));
        Assert.False(File.Exists(Path.Combine(projectRoot, "later.txt")));

        await service.DeleteFutureAsync(project, thread.ThreadId, 1);
        var retained = await service.ListAsync(thread.ThreadId);
        Assert.Equal(1, Assert.Single(retained).TurnCount);
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
