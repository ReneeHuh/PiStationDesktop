using System.Diagnostics;
using PiStation.Host.Errors;
using PiStation.Host.Git;
using PiStation.Host.Persistence;
using PiStation.Host.Projects;
using PiStation.Host.Workspaces;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class PullRequestCheckoutTests
{
    [Fact]
    public async Task ForkRefCheckoutPreservesSourceAndPersistsLinkedPromptAndModelAcrossRestart()
    {
        using var fixture = await Fixture.CreateAsync();
        File.WriteAllText(Path.Combine(fixture.Root, "code.txt"), "staged source edit");
        Git(fixture.Root, "add", "code.txt");
        File.WriteAllText(Path.Combine(fixture.Root, "code.txt"), "unstaged source edit");
        File.WriteAllText(Path.Combine(fixture.Root, "untracked.txt"), "keep");
        var sourceStatus = Git(fixture.Root, "status", "--porcelain=v1");
        var model = new PiModelSelection("test", "review-model");
        var thread = await fixture.CheckoutAsync(fixture.Request with { InheritedModel = model });

        Assert.Equal(ThreadWorkspaceMode.Worktree, thread.WorkspaceMode);
        Assert.Equal(fixture.Snapshot.HeadCommitId, Git(thread.WorktreePath!, "rev-parse", "HEAD").Trim());
        Assert.Equal("main", Git(fixture.Root, "branch", "--show-current").Trim());
        Assert.Equal(fixture.Snapshot.BaseCommitId, Git(fixture.Root, "rev-parse", "HEAD").Trim());
        Assert.Equal(sourceStatus, Git(fixture.Root, "status", "--porcelain=v1"));
        Assert.Equal("staged source edit", Git(fixture.Root, "show", ":code.txt"));
        Assert.Equal("unstaged source edit", File.ReadAllText(Path.Combine(fixture.Root, "code.txt")));
        Assert.Equal("42", thread.PullRequest?.Number);
        Assert.True(thread.HasUnsentDraft);
        Assert.Null(thread.PiSessionFile);
        Assert.Equal(SetupScriptState.None, thread.SetupScriptState);

        var restarted = new HostDatabase(fixture.Options);
        await restarted.InitializeAsync();
        var saved = await restarted.EnrichThreadDescriptorAsync((await restarted.GetThreadAsync(thread.ThreadId))!);
        Assert.Equal(thread.PullRequest, saved.PullRequest);
        Assert.Equal(thread.WorktreePath, saved.WorktreePath);
        var draft = await restarted.GetOrCreateThreadDraftAsync(thread.ThreadId);
        Assert.Contains(fixture.Snapshot.HeadCommitId, draft.Text);
        Assert.Contains(fixture.Snapshot.BaseCommitId, draft.Text);
        Assert.Contains(fixture.Snapshot.Body, draft.Text);
        Assert.Equal(model, (await restarted.GetThreadPiConfigurationAsync(thread.ThreadId))?.Model);
    }

    [Fact]
    public async Task ConcurrentClicksAndRestartReuseThreadWithoutResettingWorktreeOrDraft()
    {
        using var fixture = await Fixture.CreateAsync();
        var threads = await Task.WhenAll(fixture.CheckoutAsync(), fixture.CheckoutAsync());
        Assert.Equal(threads[0].ThreadId, threads[1].ThreadId);
        var thread = threads[0];
        File.WriteAllText(Path.Combine(thread.WorktreePath!, "code.txt"), "review edits");
        var draft = await fixture.Database.GetOrCreateThreadDraftAsync(thread.ThreadId);
        await fixture.Database.UpdateThreadDraftAsync(thread.ThreadId, draft.DraftId, draft.Revision, "My edited review prompt");
        var restarted = new HostDatabase(fixture.Options);
        await restarted.InitializeAsync();
        var resolver = new ThreadWorkspaceResolver(restarted);
        var service = new WorkspaceGitCommandService(restarted, new WorkspaceGitService(restarted, resolver), resolver, fixture.Options);
        var reopened = await service.CreatePullRequestReviewThreadAsync(fixture.Request, fixture.Snapshot);
        Assert.Equal(thread.ThreadId, reopened.ThreadId);
        Assert.Equal("review edits", File.ReadAllText(Path.Combine(reopened.WorktreePath!, "code.txt")));
        Assert.Equal("My edited review prompt", (await restarted.GetOrCreateThreadDraftAsync(thread.ThreadId)).Text);
        Assert.Single(await restarted.ListThreadsAsync(fixture.Project.ProjectId));
    }

    [Fact]
    public async Task HeadAdvancingDuringFetchCreatesNoThreadAndCanRetryAfterRefresh()
    {
        using var fixture = await Fixture.CreateAsync();
        Git(fixture.Origin, "update-ref", "refs/pull/42/head", fixture.Snapshot.BaseCommitId);
        await Assert.ThrowsAsync<HostOperationException>(() => fixture.CheckoutAsync());
        Assert.Empty(await fixture.Database.ListThreadsAsync(fixture.Project.ProjectId));
        Git(fixture.Origin, "update-ref", "refs/pull/42/head", fixture.Snapshot.HeadCommitId);
        Assert.NotNull(await fixture.CheckoutAsync());
    }

    [Fact]
    public async Task MissingFetchRefCanRetryWithSameReservation()
    {
        using var fixture = await Fixture.CreateAsync();
        Git(fixture.Origin, "update-ref", "-d", "refs/pull/42/head");
        await Assert.ThrowsAsync<HostOperationException>(() => fixture.CheckoutAsync());
        var pending = await fixture.ReserveAsync();
        Assert.False(pending.Completed);
        Assert.Empty(await fixture.Database.ListThreadsAsync(fixture.Project.ProjectId));
        Git(fixture.Origin, "update-ref", "refs/pull/42/head", fixture.Snapshot.HeadCommitId);
        Assert.Equal(pending.ThreadId, (await fixture.CheckoutAsync()).ThreadId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedWorktreeIsRecoveredOnlyWhenItsContentsAreUnchanged(bool dirty)
    {
        using var fixture = await Fixture.CreateAsync();
        var pending = await fixture.ReserveAsync();
        var path = Path.Combine(fixture.Options.WorktreeRoot, fixture.Project.ProjectId.Value, pending.ThreadId.Value);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Git(fixture.Root, "worktree", "add", "-b", $"pistation/pr-42-{pending.ThreadId.Value[..8]}", path, fixture.Snapshot.HeadCommitId);
        if (dirty)
        {
            File.WriteAllText(Path.Combine(path, "code.txt"), "keep interrupted edits");
            await Assert.ThrowsAsync<HostOperationException>(() => fixture.CheckoutAsync());
            Assert.Equal("keep interrupted edits", File.ReadAllText(Path.Combine(path, "code.txt")));
            Assert.Empty(await fixture.Database.ListThreadsAsync(fixture.Project.ProjectId));
        }
        else
        {
            Assert.Equal(pending.ThreadId, (await fixture.CheckoutAsync()).ThreadId);
            Assert.Single(await fixture.Database.ListThreadsAsync(fixture.Project.ProjectId));
        }
    }

    [Fact]
    public async Task OccupiedPathIsPreservedAndDeletedReviewIsNotResurrected()
    {
        using var fixture = await Fixture.CreateAsync();
        var pending = await fixture.ReserveAsync();
        var path = Path.Combine(fixture.Options.WorktreeRoot, fixture.Project.ProjectId.Value, pending.ThreadId.Value);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "existing file");
        await Assert.ThrowsAsync<HostOperationException>(() => fixture.CheckoutAsync());
        Assert.Equal("existing file", File.ReadAllText(path));
        File.Delete(path);
        var thread = await fixture.CheckoutAsync();
        await fixture.Database.DeleteThreadAsync(thread.ThreadId);
        await Assert.ThrowsAsync<HostOperationException>(() => fixture.CheckoutAsync());
        Assert.Empty(await fixture.Database.ListThreadsAsync(fixture.Project.ProjectId));
        Assert.True(Directory.Exists(thread.WorktreePath));
    }

    [Fact]
    public async Task StaleTargetAndChangedOriginAreRejectedBeforeReservation()
    {
        using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<HostOperationException>(() => fixture.CheckoutAsync(fixture.Request with
        { Target = fixture.Request.Target with { HeadCommitId = fixture.Snapshot.BaseCommitId } }));
        await Assert.ThrowsAsync<HostOperationException>(() => fixture.CheckoutAsync(fixture.Request with
        { Target = fixture.Request.Target with { Repository = "github.com/other/repo" } }));
        Git(fixture.Root, "remote", "set-url", "origin", fixture.Origin + "-other");
        await Assert.ThrowsAsync<HostOperationException>(() => fixture.CheckoutAsync());
        Assert.Empty(await fixture.Database.ListThreadsAsync(fixture.Project.ProjectId));
    }

    private sealed class Fixture(HostTestDirectory directory, HostOptions options, string root, string origin,
        HostDatabase database, ProjectDescriptor project, PullRequestReviewSnapshot snapshot) : IDisposable
    {
        public HostOptions Options { get; } = options;
        public string Root { get; } = root;
        public string Origin { get; } = origin;
        public HostDatabase Database { get; } = database;
        public ProjectDescriptor Project { get; } = project;
        public PullRequestReviewSnapshot Snapshot { get; } = snapshot;
        public CreatePullRequestReviewThreadRequest Request { get; } = new(new(new(project.ProjectId),
            PullRequestReviewDefaults.RepositoryKey(snapshot.Repository), "42", snapshot.HeadCommitId));
        private readonly WorkspaceOperationLocks _locks = new();

        public Task<ThreadDescriptor> CheckoutAsync(CreatePullRequestReviewThreadRequest? request = null)
        {
            var resolver = new ThreadWorkspaceResolver(Database);
            var service = new WorkspaceGitCommandService(Database, new WorkspaceGitService(Database, resolver), resolver, Options, _locks);
            return service.CreatePullRequestReviewThreadAsync(request ?? Request, Snapshot);
        }

        public Task<(ThreadId ThreadId, bool Completed)> ReserveAsync() => Database.ReservePullRequestCheckoutAsync(
            Project.ProjectId, Request.Target.Repository, "42", Snapshot.HeadCommitId, CancellationToken.None);

        public static async Task<Fixture> CreateAsync()
        {
            var directory = new HostTestDirectory();
            var options = directory.CreateOptions();
            var root = directory.CreateDirectory("repo");
            var origin = directory.CreateDirectory("origin.git");
            Git(origin, "init", "--bare", "--quiet");
            Git(root, "init", "--quiet", "--initial-branch=main");
            Git(root, "config", "user.name", "Review Tests");
            Git(root, "config", "user.email", "review@example.invalid");
            File.WriteAllText(Path.Combine(root, "code.txt"), "base");
            Git(root, "add", "code.txt");
            Git(root, "commit", "--quiet", "-m", "base");
            var baseId = Git(root, "rev-parse", "HEAD").Trim();
            Git(root, "remote", "add", "origin", origin);
            Git(root, "push", "--quiet", "origin", "HEAD:refs/heads/main");
            File.WriteAllText(Path.Combine(root, "code.txt"), "PR change");
            Git(root, "commit", "--quiet", "-am", "PR change");
            var head = Git(root, "rev-parse", "HEAD").Trim();
            Git(root, "push", "--quiet", "origin", "HEAD:refs/pull/42/head");
            Git(root, "reset", "--hard", baseId);
            var database = new HostDatabase(options);
            await database.InitializeAsync();
            var project = await new ProjectService(database).AddAsync(new(root));
            var snapshot = new PullRequestReviewSnapshot(
                new(SourceControlProvider.GitHub, "github.com", "owner", "repo", "https://github.com/owner/repo", origin, "main", false),
                new(SourceControlProvider.GitHub, "owner/repo", "42", "Fix the parser", "https://github.com/owner/repo/pull/42",
                    PullRequestState.Open, "contributor", "fork-only-branch", "main", false, [], [], PullRequestCheckState.Passed, DateTimeOffset.UtcNow),
                "Review the parser changes.", head, baseId, "reviewer", [], [], [], []);
            return new(directory, options, root, origin, database, project, snapshot);
        }

        public void Dispose() => directory.Dispose();
    }

    private static string Git(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, CreateNoWindow = true,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }
}
