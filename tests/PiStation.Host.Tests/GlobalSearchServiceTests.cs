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

    [Fact]
    public async Task PagesPastBranchAndResultCapsWithoutRepeatingMessages()
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions();
        var root = directory.CreateDirectory("paged-search");
        InitializeRepository(root);
        for (var i = 0; i < 31; i++) RunGit(root, "branch", $"needle-{i:D3}");
        var database = new HostDatabase(options);
        var environment = await database.InitializeAsync();
        var project = await database.AddProjectAsync(root, "needle project");
        var thread = await database.CreateThreadAsync(project.ProjectId, "needle thread");
        var path = Path.Combine(options.SessionRoot, "paged.jsonl");
        await File.WriteAllLinesAsync(path, Enumerable.Range(0, 135).Select(i =>
            System.Text.Json.JsonSerializer.Serialize(new { type = "message", id = $"message-{i}", message = new { role = "user", content = "needle café 测试" } })));
        await database.UpdateThreadSessionFileAsync(thread.ThreadId, path);
        var resolver = new ThreadWorkspaceResolver(database);
        var search = new GlobalSearchService(database, new WorkspaceGitCommandService(database,
            new WorkspaceGitService(database, resolver), resolver, options), options, environment.EnvironmentId);
        var items = new List<GlobalSearchItem>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await search.SearchAsync(new("needle", 17, Continuation: cursor));
            Assert.InRange(page.Items.Count, 0, 17);
            items.AddRange(page.Items);
            Assert.NotEqual(cursor ?? "start", page.NextContinuation);
            cursor = page.NextContinuation;
            Assert.True(++pages < 30);
        } while (cursor is not null);
        Assert.Equal(31, items.Count(item => item.Kind == GlobalSearchResultKind.Branch));
        var messages = items.Where(item => item.Kind == GlobalSearchResultKind.Message).ToArray();
        Assert.Equal(135, messages.Length);
        Assert.Equal(135, messages.Select(item => item.MessageId).Distinct().Count());
        Assert.All(messages, item => Assert.Contains("café 测试", item.Snippet));
        Assert.Single(items, item => item.Kind == GlobalSearchResultKind.Project);
        Assert.Single(items, item => item.Kind == GlobalSearchResultKind.Thread);
        var beforeChange = await search.SearchAsync(new("needle", 3));
        RunGit(root, "branch", "needle-new");
        await Assert.ThrowsAsync<PiStation.Host.Errors.HostOperationException>(() =>
            search.SearchAsync(new("needle", 3, Continuation: beforeChange.NextContinuation)));
    }

    [Fact]
    public async Task ContinuesBeyondThreadScanWindow()
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions();
        var database = new HostDatabase(options);
        var environment = await database.InitializeAsync();
        var project = await database.AddProjectAsync(directory.CreateDirectory("many-threads"), "Project");
        for (var i = 0; i < 503; i++) await database.CreateThreadAsync(project.ProjectId, $"needle {i}");
        var resolver = new ThreadWorkspaceResolver(database);
        var search = new GlobalSearchService(database, new WorkspaceGitCommandService(database,
            new WorkspaceGitService(database, resolver), resolver, options), options, environment.EnvironmentId);
        var ids = new HashSet<string>();
        string? cursor = null;
        do
        {
            var page = await search.SearchAsync(new("needle", 100, Continuation: cursor));
            foreach (var item in page.Items) Assert.True(ids.Add(item.ThreadId!.ToString()!));
            cursor = page.NextContinuation;
        } while (cursor is not null);
        Assert.Equal(503, ids.Count);
        // A scan with no matches still advances beyond the 500-thread work budget.
        var empty = await search.SearchAsync(new("absent"));
        Assert.Empty(empty.Items);
        Assert.NotNull(empty.NextContinuation);
        Assert.Null((await search.SearchAsync(new("absent", Continuation: empty.NextContinuation))).NextContinuation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumesHistoryBeyondByteBudgetAndOversizedLines(bool oversized)
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions();
        var database = new HostDatabase(options);
        var environment = await database.InitializeAsync();
        var project = await database.AddProjectAsync(directory.CreateDirectory("history"), "Project");
        var thread = await database.CreateThreadAsync(project.ProjectId, "History");
        var path = Path.Combine(options.SessionRoot, "large.jsonl");
        await using (var writer = new StreamWriter(path))
        {
            var padding = new string('x', 512 * 1024);
            for (var i = 0; i < 66; i++)
            {
                if (oversized) await writer.WriteAsync(padding);
                else await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new { type = "message", message = new { role = "user", content = padding } }));
            }
            if (oversized) await writer.WriteLineAsync();
            await writer.WriteLineAsync("""{"type":"message","id":"tail","message":{"role":"assistant","content":"needle after budget"}}""");
        }
        await database.UpdateThreadSessionFileAsync(thread.ThreadId, path);
        var resolver = new ThreadWorkspaceResolver(database);
        var search = new GlobalSearchService(database, new WorkspaceGitCommandService(database,
            new WorkspaceGitService(database, resolver), resolver, options), options, environment.EnvironmentId);
        var first = await search.SearchAsync(new("needle"));
        Assert.Empty(first.Items);
        Assert.NotNull(first.NextContinuation);
        if (oversized) Assert.Contains("skipped", first.Notice);
        var second = await search.SearchAsync(new("needle", Continuation: first.NextContinuation));
        Assert.Equal("tail", Assert.Single(second.Items).MessageId);
        Assert.Null(second.NextContinuation);
        await Assert.ThrowsAsync<PiStation.Host.Errors.HostOperationException>(() => search.SearchAsync(new("different", Continuation: first.NextContinuation)));
        await File.AppendAllTextAsync(path, "\n");
        await Assert.ThrowsAsync<PiStation.Host.Errors.HostOperationException>(() => search.SearchAsync(new("needle", Continuation: first.NextContinuation)));
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
