using PiStation.Host.Persistence;
using PiStation.Host.Projects;
using PiStation.Host.SourceControl;
using PiStation.Host.Workspaces;
using PiStation.Protocol.Models;
using PiStation.TestFixtures;

namespace PiStation.Host.Tests;

public sealed class HostingDiscoveryTests
{
    [Theory]
    [InlineData(SourceControlProvider.GitHub, "github.company.test", "all")]
    [InlineData(SourceControlProvider.GitHub, "", "group:team1")]
    [InlineData(SourceControlProvider.GitLab, "gitlab.company.test", "all")]
    [InlineData(SourceControlProvider.GitLab, "", "group:1")]
    [InlineData(SourceControlProvider.Bitbucket, "", "team")]
    [InlineData(SourceControlProvider.AzureDevOps, "", HostingDiscoveryFixture.ProjectId)]
    public async Task AccountsAndRepositoriesUseExplicitReadOnlyProviderScope(SourceControlProvider provider, string host, string scope)
    {
        using var f = await Fixture.Create(); var location = new HostingBrowseLocation(provider, host, "acme");
        var accounts = await f.Service.ListHostingAccountsAsync(new(location));
        Assert.NotEmpty(accounts.Accounts); Assert.Null(accounts.NextPage);
        var repositories = await f.Service.BrowseHostedRepositoriesAsync(new(location, scope));
        var repository = Assert.Single(repositories.Repositories);
        Assert.StartsWith("https://", repository.CloneUrl, StringComparison.Ordinal); Assert.DoesNotContain('@', repository.CloneUrl);
        Assert.Null(repositories.NextPage); Assert.Contains("active credentials", repositories.Notice, StringComparison.Ordinal);
        if (provider == SourceControlProvider.Bitbucket) Assert.All(f.Http.Requests, path => Assert.StartsWith("/2.0/", path, StringComparison.Ordinal));
        else if (provider == SourceControlProvider.AzureDevOps)
        {
            Assert.All(f.Http.Commands, command => { Assert.Contains("https://dev.azure.com/acme", command.Args); Assert.Contains("--detect", command.Args); Assert.Contains("false", command.Args); });
            Assert.Contains(f.Http.Commands, command => command.Args.Contains(scope));
        }
        else Assert.All(f.Http.Commands, command => { Assert.Contains("--hostname", command.Args); Assert.Contains("GET", command.Args); });
    }

    [Theory]
    [InlineData(SourceControlProvider.GitHub, "all")]
    [InlineData(SourceControlProvider.GitLab, "group:1")]
    [InlineData(SourceControlProvider.Bitbucket, "team")]
    [InlineData(SourceControlProvider.AzureDevOps, HostingDiscoveryFixture.ProjectId)]
    public async Task PagingIsBoundedAndNeverFollowsProviderSuppliedNextUrls(SourceControlProvider provider, string scope)
    {
        using var f = await Fixture.Create(); f.Http.FullPage = true; var location = new HostingBrowseLocation(provider, Organization: "acme");
        var first = await f.Service.BrowseHostedRepositoriesAsync(new(location, scope)); Assert.Equal(2, first.NextPage);
        var second = await f.Service.BrowseHostedRepositoriesAsync(new(location, scope, 2)); Assert.NotEmpty(second.Repositories);
        if (provider != SourceControlProvider.AzureDevOps)
        {
            var last = await f.Service.BrowseHostedRepositoriesAsync(new(location, scope, 20));
            Assert.Null(last.NextPage); Assert.Contains("limit", last.Notice, StringComparison.Ordinal);
        }
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.BrowseHostedRepositoriesAsync(new(location, scope, 21)));
        Assert.DoesNotContain(f.Http.Requests, request => request.Contains("untrusted", StringComparison.Ordinal));
        if (provider == SourceControlProvider.AzureDevOps) Assert.Single(second.Repositories);
    }

    [Theory]
    [InlineData("https://github.com")]
    [InlineData("github.com/path")]
    [InlineData("user:secret@github.com")]
    [InlineData("--hostname")]
    public async Task InvalidHostIsRejectedBeforeProviderAccess(string host)
    {
        using var f = await Fixture.Create();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.ListHostingAccountsAsync(new(new(SourceControlProvider.GitHub, host))));
        Assert.Empty(f.Http.Commands);
    }

    [Theory]
    [InlineData("group:../evil")]
    [InlineData("group:x?token=secret")]
    [InlineData("group:")]
    [InlineData("https://evil.example")]
    public async Task InvalidScopeNeverBecomesAnApiPath(string scope)
    {
        using var f = await Fixture.Create();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.BrowseHostedRepositoriesAsync(new(new(SourceControlProvider.GitHub), scope)));
        Assert.Empty(f.Http.Commands);
    }

    [Theory]
    [InlineData("https://evil.example/team1/repo1.git")]
    [InlineData("https://token@github.com/team1/repo1.git")]
    [InlineData("https://github.com/team1/other.git")]
    [InlineData("https://github.com/team1/repo1.git?token=secret")]
    public async Task InvalidOrMismatchedCloneIdentityIsNotOffered(string clone)
    {
        using var f = await Fixture.Create(); f.Http.CloneOverride = clone;
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.BrowseHostedRepositoriesAsync(new(new(SourceControlProvider.GitHub), "all")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("not json")]
    public async Task MalformedProviderListDoesNotLookLikeAnEmptyAccount(string payload)
    {
        using var f = await Fixture.Create(); f.Http.OverrideJson = payload;
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.BrowseHostedRepositoriesAsync(new(new(SourceControlProvider.GitHub), "all")));
    }

    [Fact]
    public async Task FailureIsSanitizedAndCancellationStopsBeforeCommand()
    {
        using var f = await Fixture.Create(); f.Http.Failure = true;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => f.Service.ListHostingAccountsAsync(new(new(SourceControlProvider.GitHub))));
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); var count = f.Http.Commands.Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.ListHostingAccountsAsync(new(new(SourceControlProvider.GitHub)), canceled.Token));
        Assert.Equal(count, f.Http.Commands.Count);
    }

    [Fact]
    public async Task CloneRegistersProjectEvenWhenOptionalHostingDetailsFailAndProtectsNonemptyDestination()
    {
        using var f = await Fixture.Create(); var source = f.Directory.CreateDirectory("source");
        await ProviderReviewCommands.GitAsync(source, "init", "--quiet", "--initial-branch=main");
        var destination = Path.Combine(f.Directory.CreateDirectory("clones"), "repo");
        var result = await f.Service.CloneAsync(new(source, destination));
        Assert.True(result.Succeeded); Assert.NotNull(result.Project); Assert.True(Directory.Exists(Path.Combine(destination, ".git")));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.CloneAsync(new(source, destination)));
        Assert.True(Directory.Exists(Path.Combine(source, ".git")));
    }

    private sealed class Fixture : IDisposable
    {
        public HostTestDirectory Directory { get; } = new();
        public HostingDiscoveryFixture Http { get; } = new();
        public SourceControlHostingService Service { get; private set; } = null!;
        public static async Task<Fixture> Create()
        {
            var f = new Fixture(); var database = new HostDatabase(f.Directory.CreateOptions()); await database.InitializeAsync();
            f.Service = new(new ThreadWorkspaceResolver(database), new ProjectService(database), f.Http.Execute, bitbucket: f.Http.Client()); return f;
        }
        public void Dispose() { Service.Dispose(); Directory.Dispose(); }
    }
}
