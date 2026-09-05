using System.Diagnostics;
using PiStation.Host.Git;
using PiStation.Host.Persistence;
using PiStation.Host.Search;
using PiStation.Host.Workspaces;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class GlobalSearchServiceTests
{
    [Fact]
    public async Task SearchesProjectsBranchesThreadsAndPersistedMessages()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectRoot = temporaryDirectory.CreateDirectory("command-station");
        InitializeRepository(projectRoot);
        RunGit(projectRoot, "branch", "feature/command-palette");

        var database = new HostDatabase(options);
        var environment = await database.InitializeAsync();
        var project = await database.AddProjectAsync(projectRoot, "Command Station");
        var thread = await database.CreateThreadAsync(
            project.ProjectId,
            "Refactor palette ranking",
            branchName: "feature/command-palette");
        var sessionFile = Path.Combine(options.SessionRoot, "search-session.jsonl");
        await File.WriteAllLinesAsync(
            sessionFile,
            [
                "{\"type\":\"message\",\"id\":\"message-user-1\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Find the cobalt keyboard conflict.\"}]}}",
                "{\"type\":\"message\",\"id\":\"message-assistant-1\",\"message\":{\"role\":\"assistant\",\"content\":\"The cobalt keyboard conflict is resolved.\"}}",
            ]);
        await database.UpdateThreadSessionFileAsync(thread.ThreadId, sessionFile);

        var resolver = new ThreadWorkspaceResolver(database);
        var git = new WorkspaceGitService(database, resolver);
        var commands = new WorkspaceGitCommandService(database, git, resolver, options);
        var search = new GlobalSearchService(database, commands, options, environment.EnvironmentId);

        var projectResult = await search.SearchAsync(new GlobalSearchRequest("Command Station"));
        var branchResult = await search.SearchAsync(new GlobalSearchRequest("command-palette"));
        var threadResult = await search.SearchAsync(new GlobalSearchRequest("palette ranking"));
        var messageResult = await search.SearchAsync(new GlobalSearchRequest("cobalt keyboard"));

        Assert.Contains(projectResult.Items, item =>
            item.Kind == GlobalSearchResultKind.Project && item.ProjectId == project.ProjectId);
        Assert.Contains(branchResult.Items, item =>
            item.Kind == GlobalSearchResultKind.Branch && item.BranchName == "feature/command-palette");
        Assert.Contains(threadResult.Items, item =>
            item.Kind == GlobalSearchResultKind.Thread && item.ThreadId == thread.ThreadId);
        var messages = messageResult.Items.Where(item => item.Kind == GlobalSearchResultKind.Message).ToArray();
        Assert.Equal(2, messages.Length);
        Assert.All(messages, item => Assert.Equal(thread.ThreadId, item.ThreadId));
        Assert.Contains(messages, item => item.MessageId == "message-user-1" &&
                                           item.MessageRole == PiStation.Protocol.Projections.MessageRole.User);
        Assert.Contains(messages, item => item.Snippet?.Contains("cobalt keyboard", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task RejectsInvalidGlobalSearchBounds()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var database = new HostDatabase(options);
        var environment = await database.InitializeAsync();
        var resolver = new ThreadWorkspaceResolver(database);
        var git = new WorkspaceGitService(database, resolver);
        var commands = new WorkspaceGitCommandService(database, git, resolver, options);
        var search = new GlobalSearchService(database, commands, options, environment.EnvironmentId);

        var empty = await Assert.ThrowsAsync<PiStation.Host.Errors.HostOperationException>(() =>
            search.SearchAsync(new GlobalSearchRequest("")));
        var oversized = await Assert.ThrowsAsync<PiStation.Host.Errors.HostOperationException>(() =>
            search.SearchAsync(new GlobalSearchRequest("valid", GlobalSearchDefaults.MaximumResults + 1)));

        Assert.Equal(ProtocolErrorCodes.GlobalSearchInvalid, empty.Code);
        Assert.Equal(ProtocolErrorCodes.GlobalSearchInvalid, oversized.Code);
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
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed. {standardOutput} {standardError}");
    }
}
