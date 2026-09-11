using System.Diagnostics;
using System.Text.Json;
using PiStation.Host.Persistence;
using PiStation.Host.Projects;
using PiStation.Host.SourceControl;
using PiStation.Host.Workspaces;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Tests;

public sealed class RepositoryPublishingTests
{
    [Theory]
    [InlineData(SourceControlProvider.GitHub)]
    [InlineData(SourceControlProvider.GitLab)]
    [InlineData(SourceControlProvider.AzureDevOps)]
    public async Task PublishesCapturedCommitAndConfiguresUpstream(SourceControlProvider provider)
    {
        using var fixture = await Fixture.CreateAsync(provider);
        var head = await fixture.Git("rev-parse", "HEAD");
        var result = await fixture.Publish();
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(RepositoryPublicationStage.Pushed, result.Publication!.Stage);
        Assert.Equal(head, await fixture.BareHead());
        Assert.Equal("origin", await fixture.Git("config", "branch.main.remote"));
        Assert.Equal("refs/heads/main", await fixture.Git("config", "branch.main.merge"));
        Assert.Equal(fixture.RemoteUrl, await fixture.Git("remote", "get-url", "origin"));
        Assert.Equal(1, fixture.Creations);
        if (provider == SourceControlProvider.GitLab)
        {
            Assert.Contains(fixture.Calls, c => c.Args.Contains("namespaces/team%2Fsubgroup") && c.Args.Contains("gitlab.example"));
            Assert.Contains(fixture.Calls, c => c.Args.Contains("namespace_id=17") && c.Args.Contains("visibility=private"));
        }
        if (provider == SourceControlProvider.AzureDevOps)
        {
            var call = Assert.Single(fixture.Calls, c => c.Tool == "az");
            Assert.Contains("https://dev.azure.com/station", call.Args);
            Assert.Contains("Team Project", call.Args);
            Assert.Contains("false", call.Args);
            Assert.DoesNotContain("--private", call.Args);
            Assert.DoesNotContain("--visibility", call.Args);
        }
    }

    [Theory]
    [InlineData(SourceControlProvider.GitLab)]
    [InlineData(SourceControlProvider.AzureDevOps)]
    public async Task EmptyRepositoryCanResumeAfterFirstCommitWithoutCreatingAgain(SourceControlProvider provider)
    {
        using var fixture = await Fixture.CreateAsync(provider, empty: true);
        var empty = await fixture.Publish();
        Assert.True(empty.Succeeded, empty.Message);
        Assert.Equal(RepositoryPublicationStage.RemoteConfigured, empty.Publication!.Stage);
        Assert.Equal(0, fixture.Pushes);
        await fixture.Commit();
        var resumed = await fixture.Publish(resume: true);
        Assert.True(resumed.Succeeded, resumed.Message);
        Assert.Equal(await fixture.Git("rev-parse", "HEAD"), await fixture.BareHead());
        Assert.Equal(1, fixture.Creations);
        Assert.Equal(1, fixture.Pushes);
    }

    [Fact]
    public async Task PublishesDotPrefixedGithubRepositoryNames()
    {
        using var fixture = await Fixture.CreateAsync(SourceControlProvider.GitHub);
        fixture.ReturnedUrl = "https://github.example/team/.github.git";
        var result = await fixture.Service.PublishAsync(fixture.Request() with { RepositoryName = ".github" });
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(fixture.ReturnedUrl, await fixture.Git("remote", "get-url", "origin"));
        Assert.Equal(await fixture.Git("rev-parse", "HEAD"), await fixture.BareHead());
    }

    [Fact]
    public async Task LegacyAzureOrganizationUsesExplicitCanonicalOrganization()
    {
        using var fixture = await Fixture.CreateAsync(SourceControlProvider.AzureDevOps);
        fixture.ReturnedUrl = "https://station@station.visualstudio.com/Team%20Project/_git/repo";
        var result = await fixture.Service.PublishAsync(fixture.Request() with { OrganizationUrl = "https://station.visualstudio.com/" });
        Assert.True(result.Succeeded, result.Message);
        Assert.Contains(fixture.Calls, call => call.Tool == "az" && call.Args.Contains("https://dev.azure.com/station"));
        Assert.Equal(fixture.RemoteUrl, result.Publication!.RemoteUrl);
        fixture.ReturnedUrl = "https://another-organization.visualstudio.com/Team%20Project/_git/repo";
        Assert.False((await fixture.Publish(resume: true)).Succeeded);
        Assert.Equal(1, fixture.Pushes);
    }

    [Fact]
    public async Task PreservesConflictingFetchAndPushRemotesAndReusesPublicationRemote()
    {
        using var fixture = await Fixture.CreateAsync(SourceControlProvider.GitLab);
        await fixture.Git("remote", "add", "origin", "https://unrelated.example/owner/repo.git");
        await fixture.Git("remote", "add", "origin-1", fixture.RemoteUrl);
        await fixture.Git("remote", "set-url", "--push", "origin-1", "https://unrelated.example/push/repo.git");
        var first = await fixture.Publish();
        Assert.True(first.Succeeded, first.Message);
        Assert.Equal("origin-2", first.Publication!.RemoteName);
        Assert.Equal("https://unrelated.example/owner/repo.git", await fixture.Git("remote", "get-url", "origin"));
        Assert.Equal("https://unrelated.example/push/repo.git", await fixture.Git("remote", "get-url", "--push", "origin-1"));
        Assert.Equal("origin-2", (await fixture.Publish(resume: true)).Publication!.RemoteName);
        Assert.Equal(3, (await fixture.Git("remote")).Split('\n').Length);
    }

    [Fact]
    public async Task FailedPushHasDurableProgressAndExplicitResumeNeverRepeatsCreation()
    {
        using var fixture = await Fixture.CreateAsync(SourceControlProvider.GitLab);
        fixture.FailPush = true;
        var request = fixture.Request();
        var first = await fixture.RunReceipt(request);
        Assert.False(first.Succeeded);
        Assert.Equal(RepositoryPublicationStage.RemoteConfigured, first.Publication!.Stage);
        Assert.Equal(CommandReceiptState.DispatchUncertain, first.State);
        fixture.FailPush = false;
        var replay = await fixture.RunReceipt(request);
        Assert.Equal(first, replay);
        Assert.Equal(1, fixture.Pushes);
        Assert.True((await fixture.RunReceipt(fixture.Request() with { ResumeExisting = true })).Succeeded);
        Assert.Equal(1, fixture.Creations);
        Assert.Equal(2, fixture.Pushes);
    }

    [Fact]
    public async Task LostCreationResponseCanBeRecoveredByLookupWithoutAnotherCreate()
    {
        using var fixture = await Fixture.CreateAsync(SourceControlProvider.AzureDevOps);
        fixture.LoseCreationResponse = true;
        var result = await fixture.RunReceipt(fixture.Request());
        Assert.False(result.Succeeded);
        Assert.Null(result.Publication);
        Assert.Equal(CommandReceiptState.DispatchUncertain, result.State);
        Assert.Contains("Resume existing repository", result.Message);
        fixture.LoseCreationResponse = false;
        Assert.True((await fixture.Publish(resume: true)).Succeeded);
        Assert.Equal(1, fixture.Creations);
    }

    [Theory]
    [InlineData("https://elsewhere.example/team/subgroup/repo.git")]
    [InlineData("https://gitlab.example/other/repo.git")]
    [InlineData("https://secret:password@gitlab.example/team/subgroup/repo.git")]
    [InlineData("https://gitlab.example/team/subgroup/repo.git?token=secret")]
    [InlineData("file:///C:/unexpected/repo")]
    public async Task InvalidProviderDestinationsNeverChangeRemotesOrExposeSecrets(string url)
    {
        using var fixture = await Fixture.CreateAsync(SourceControlProvider.GitLab);
        fixture.ReturnedUrl = url;
        var result = await fixture.Publish();
        Assert.False(result.Succeeded);
        Assert.DoesNotContain("secret", result.Message);
        Assert.DoesNotContain("password", result.Message);
        Assert.Empty(await fixture.Git("remote"));
        Assert.Equal(0, fixture.Pushes);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("name")]
    [InlineData("host")]
    [InlineData("organization")]
    [InlineData("provider")]
    public async Task InvalidInputIsRejectedBeforeCallingAnyTool(string field)
    {
        using var fixture = await Fixture.CreateAsync(SourceControlProvider.GitLab);
        var request = fixture.Request();
        request = field switch
        {
            "owner" => request with { Owner = "../bad" },
            "name" => request with { RepositoryName = "--help" },
            "host" => request with { Host = "user:secret@gitlab.example" },
            "organization" => request with { Provider = SourceControlProvider.AzureDevOps, Owner = "project", OrganizationUrl = "https://dev.azure.com/one/two" },
            _ => request with { Provider = SourceControlProvider.Unknown },
        };
        var result = await fixture.Service.PublishAsync(request);
        Assert.Equal(CommandReceiptState.Rejected, result.State);
        Assert.Empty(fixture.Calls);
        Assert.DoesNotContain("secret", result.Message);
    }

    [Fact]
    public async Task IncorrectNamespaceAndDetachedHeadAreRejectedBeforeCreation()
    {
        using var fixture = await Fixture.CreateAsync(SourceControlProvider.GitLab);
        fixture.WrongNamespace = true;
        Assert.False((await fixture.Publish()).Succeeded);
        Assert.Equal(0, fixture.Creations);
        fixture.WrongNamespace = false;
        await fixture.Git("checkout", "--detach", "--quiet");
        Assert.False((await fixture.Publish()).Succeeded);
        Assert.Equal(0, fixture.Creations);
    }

    [Fact]
    public async Task BranchChangeDuringCreationStopsBeforeConfiguringRemote()
    {
        using var fixture = await Fixture.CreateAsync(SourceControlProvider.GitLab);
        fixture.AfterCreate = () => fixture.Git("checkout", "-b", "different", "--quiet");
        var result = await fixture.Publish();
        Assert.False(result.Succeeded);
        Assert.Equal(RepositoryPublicationStage.RepositoryReady, result.Publication!.Stage);
        Assert.Empty(await fixture.Git("remote"));
        Assert.Equal(0, fixture.Pushes);
    }

    [Fact]
    public async Task PushPinsReviewedCommitEvenIfLocalBranchAdvances()
    {
        using var fixture = await Fixture.CreateAsync(SourceControlProvider.GitLab);
        var original = await fixture.Git("rev-parse", "HEAD");
        fixture.BeforePush = fixture.Commit;
        Assert.True((await fixture.Publish()).Succeeded);
        Assert.NotEqual(original, await fixture.Git("rev-parse", "HEAD"));
        Assert.Equal(original, await fixture.BareHead());
    }

    [Fact]
    public async Task GitUrlRewriteStopsBeforeAnyPush()
    {
        using var fixture = await Fixture.CreateAsync(SourceControlProvider.GitLab);
        await fixture.Git("config", "url.https://unexpected.example/.insteadOf", "https://gitlab.example/");
        var result = await fixture.Publish();
        Assert.False(result.Succeeded);
        Assert.Contains("rewrites", result.Message);
        Assert.Equal(0, fixture.Pushes);
    }

    [Fact]
    public async Task ConcurrentPublicationIsRejectedWhileAcceptedOperationFinishes()
    {
        using var fixture = await Fixture.CreateAsync(SourceControlProvider.GitLab);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.AfterCreate = async () => { entered.SetResult(); await release.Task; };
        var first = fixture.Publish();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { Assert.Equal(CommandReceiptState.Rejected, (await fixture.Publish()).State); }
        finally { release.SetResult(); }
        Assert.True((await first).Succeeded);
        Assert.Equal(1, fixture.Creations);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HostTestDirectory _directory = new();
        private ProjectId _project;
        private SourceControlProvider _provider;
        public string Root { get; private set; } = "";
        public string Bare { get; private set; } = "";
        public HostDatabase Database { get; private set; } = null!;
        public SourceControlHostingService Service { get; private set; } = null!;
        public bool FailPush { get; set; }
        public bool LoseCreationResponse { get; set; }
        public bool WrongNamespace { get; set; }
        public string? ReturnedUrl { get; set; }
        public Func<Task>? AfterCreate { get; set; }
        public Func<Task>? BeforePush { get; set; }
        public int Creations { get; private set; }
        public int Pushes { get; private set; }
        public List<(string Tool, string[] Args)> Calls { get; } = [];
        public string RemoteUrl => _provider switch
        {
            SourceControlProvider.GitHub => "https://github.example/team/repo.git",
            SourceControlProvider.GitLab => "https://gitlab.example/team/subgroup/repo.git",
            _ => "https://dev.azure.com/station/Team%20Project/_git/repo",
        };

        public PublishHostedRepositoryRequest Request() => new(_project, _provider,
            _provider == SourceControlProvider.GitLab ? "team/subgroup" : _provider == SourceControlProvider.AzureDevOps ? "Team Project" : "team",
            "repo", OperationId: CommandId.New(), Host: _provider == SourceControlProvider.GitHub ? "github.example" : "gitlab.example",
            OrganizationUrl: "https://dev.azure.com/station");
        public Task<SourceControlOperationResult> Publish(bool resume = false) => Service.PublishAsync(Request() with { ResumeExisting = resume });
        public Task<SourceControlOperationResult> RunReceipt(PublishHostedRepositoryRequest request) => new HostingOperationRunner(Database).RunAsync(
            request.OperationId, "Publish repository", JsonSerializer.Serialize(request, ProtocolJsonContext.Default.PublishHostedRepositoryRequest),
            token => Service.PublishAsync(request, token));

        public static async Task<Fixture> CreateAsync(SourceControlProvider provider, bool empty = false)
        {
            var fixture = new Fixture { _provider = provider };
            fixture.Root = fixture._directory.CreateDirectory("repo");
            fixture.Bare = fixture._directory.CreateDirectory("destination.git");
            await fixture.Git("init", "--quiet", "--initial-branch=main");
            await fixture.Git("config", "user.email", "publishing@example.invalid");
            await fixture.Git("config", "user.name", "Publishing Fixture");
            await fixture.Git("init", "--bare", "--quiet", fixture.Bare);
            if (!empty) await fixture.Commit();
            fixture.Database = new(fixture._directory.CreateOptions());
            await fixture.Database.InitializeAsync();
            var projects = new ProjectService(fixture.Database);
            fixture._project = (await projects.AddAsync(new(fixture.Root))).ProjectId;
            fixture.Service = new(new ThreadWorkspaceResolver(fixture.Database), projects, fixture.Execute);
            return fixture;
        }

        public async Task Commit() => await Git("commit", "--allow-empty", "--quiet", "--no-gpg-sign", "-m", Guid.NewGuid().ToString("N"));
        public Task<string> BareHead() => Git("--git-dir=" + Bare, "rev-parse", "refs/heads/main");
        public async Task<string> Git(params string[] args)
        {
            var result = await RunGit(args, CancellationToken.None);
            Assert.True(result.Item1 == 0, result.Item3);
            return result.Item2.Trim();
        }

        private async Task<(int, string, string)> Execute(string tool, IReadOnlyList<string> args, string root, string? input, CancellationToken token)
        {
            Assert.Equal(Root, root);
            Assert.Null(input);
            Calls.Add((tool, args.ToArray()));
            if (tool == "git")
            {
                if (!args.Contains("push")) return await RunGit(args, token);
                Pushes++;
                if (FailPush) return (1, "", "fixture push failure");
                if (BeforePush is not null) await BeforePush();
                var local = args.ToArray();
                // The production command's only network write is redirected to our own bare fixture.
                // No provider authentication or repository is touched by these tests.
                local[^2] = Bare;
                return await RunGit(local, token);
            }
            if (args.Any(a => a.StartsWith("namespaces/", StringComparison.Ordinal)))
                return (0, JsonSerializer.Serialize(new { id = 17, full_path = WrongNamespace ? "other" : "team/subgroup" }), "");
            var create = args.Contains("POST") || args.Contains("create");
            if (create)
            {
                Creations++;
                if (AfterCreate is not null) await AfterCreate();
                if (LoseCreationResponse) throw new IOException("secret provider failure detail");
                if (tool == "gh") return (0, RemoteUrl, "");
            }
            var property = _provider switch { SourceControlProvider.GitHub => "clone_url", SourceControlProvider.GitLab => "http_url_to_repo", _ => "remoteUrl" };
            var url = ReturnedUrl ?? (_provider == SourceControlProvider.AzureDevOps ? RemoteUrl.Replace("https://", "https://station@", StringComparison.Ordinal) : RemoteUrl);
            return (0, JsonSerializer.Serialize(new Dictionary<string, string> { [property] = url, ["default_branch"] = "main" }), "");
        }

        private async Task<(int, string, string)> RunGit(IReadOnlyList<string> args, CancellationToken token)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = _directory.GetPath("empty-global-config");
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            return (process.ExitCode, await stdout, await stderr);
        }

        public void Dispose() { Service.Dispose(); _directory.Dispose(); }
    }
}
