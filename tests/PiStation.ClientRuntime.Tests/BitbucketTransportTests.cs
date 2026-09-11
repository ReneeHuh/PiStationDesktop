using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PiStation.App.ViewModels;
using PiStation.Host.Hosting;
using PiStation.Host.Security;
using PiStation.Host.SourceControl;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;
using PiStation.TestFixtures;

namespace PiStation.ClientRuntime.Tests;

public sealed class BitbucketTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboxReviewsAndPartialReceiptsRemainScopedAcrossConnections(bool tls)
    {
        using var directory = new ClientTestDirectory(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var fixture = new BitbucketHttpFixture(); var root = directory.CreateDirectory("repo");
        await ProviderReviewCommands.GitAsync(root, "init", "--quiet", "--initial-branch=main");
        await ProviderReviewCommands.GitAsync(root, "remote", "add", "origin", "https://bitbucket.org/team/repo.git");
        await using var local = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions(),
            sourceControlFactory: (resolver, projects) => new SourceControlHostingService(resolver, projects, bitbucket: fixture.Client()));
        using var access = new RemoteAccessStore(Path.Combine(directory.CreateDirectory("access"), "access.db"));
        using var key = RSA.Create(2048);
        using var created = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
        await using var remote = tls ? await RemoteEnvironmentHost.StartAsync(local.Environment, access, IPAddress.Loopback, 0, certificate, cancellationToken: timeout.Token) : null;
        var options = new ClientRuntimeOptions { HubAddress = remote is null ? local.HubAddress : new(remote.Address, "/environment"),
            BearerCredential = remote is null ? local.BearerCredential : access.IssueSession(RemoteAccessLevel.Operate).Token,
            CertificateFingerprint = remote is null ? null : certificate.GetCertHashString(HashAlgorithmName.SHA256) };
        await using var client = new EnvironmentClient(options); await client.ConnectAsync(timeout.Token);
        var project = await client.AddProjectAsync(new(root), timeout.Token);
        var inbox = await PullRequestInbox.StartAsync([new(client.Descriptor!.EnvironmentId.Value, "Bitbucket fixture", client.ListProjectsAsync,
            client.DetectSourceControlAsync, client.ListPullRequestsAsync)], new(), timeout.Token);
        var row = Assert.Single(inbox.Rows);
        var store = new PullRequestReviewDraftStore(directory.CreateDirectory("drafts"));
        using var model = new PullRequestReviewViewModel(() => client, store);
        await model.LoadAsync(project.ProjectId, row.Target, row.PullRequest, timeout.Token);
        Assert.True(model.HasProviderDiff); Assert.True(model.HasReviewComposer); Assert.True(model.CanEditDetails);
        Assert.False(model.CanChangeDraft); Assert.False(model.CanEnableAutoMerge); Assert.False(model.CanCreateReviewThread);
        model.SetBody("Retained draft"); await model.SaveNowAsync(timeout.Token);
        Assert.Equal("Retained draft", (await store.LoadAsync(project.ProjectId, "bitbucket.org/team/repo", "7", timeout.Token))!.Body);
        var target = new PullRequestReviewTarget(row.Target, "bitbucket.org/team/repo", "7", BitbucketHttpFixture.Head);
        var request = new SubmitPullRequestReviewRequest(target, PullRequestReviewEvent.Approve, "Summary", [new("src/App.cs", 3, PullRequestDiffSide.Right, "Inline")], CommandId.New());
        if (tls)
        {
            await using var viewer = new EnvironmentClient(new() { HubAddress = options.HubAddress, CertificateFingerprint = options.CertificateFingerprint,
                BearerCredential = access.IssueSession(RemoteAccessLevel.ReadOnly).Token });
            await viewer.ConnectAsync(timeout.Token);
            Assert.Equal(SourceControlProvider.Bitbucket, (await viewer.GetPullRequestReviewAsync(new(row.Target, "7"), timeout.Token)).Repository.Provider);
            await Assert.ThrowsAnyAsync<Exception>(() => viewer.SubmitPullRequestReviewAsync(request, timeout.Token));
            Assert.Empty(fixture.Writes);
        }
        fixture.FailWrite = 2;
        var result = await client.SubmitPullRequestReviewAsync(request, timeout.Token);
        Assert.True(result.State == CommandReceiptState.DispatchUncertain, result.Message);
        Assert.Equal(1, result.ReviewProgress!.CompletedSteps);
        await client.DisconnectAsync(timeout.Token); await client.ConnectAsync(timeout.Token);
        var replay = await client.SubmitPullRequestReviewAsync(request, timeout.Token);
        Assert.Equal(result.ReviewProgress, replay.ReviewProgress); Assert.Equal(2, fixture.Writes.Count);
    }
}
