using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PiStation.App.ViewModels;
using PiStation.Host.Hosting;
using PiStation.Host.Security;
using PiStation.Host.SourceControl;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;
using PiStation.Protocol.Serialization;
using PiStation.TestFixtures;

namespace PiStation.ClientRuntime.Tests;

public sealed class ProviderPullRequestReviewTransportTests
{
    [Theory]
    [InlineData(SourceControlProvider.GitLab, false)]
    [InlineData(SourceControlProvider.GitLab, true)]
    [InlineData(SourceControlProvider.AzureDevOps, false)]
    [InlineData(SourceControlProvider.AzureDevOps, true)]
    public async Task ProviderReviewCapabilitiesDraftsAndReceiptsSurviveProtectedTransport(SourceControlProvider provider, bool tls)
    {
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var commands = new ProviderReviewCommands(provider);
        var root = directory.CreateDirectory("repo");
        await commands.InitializeRepositoryAsync(root);
        await using var local = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions(),
            sourceControlFactory: (resolver, projects) => new SourceControlHostingService(resolver, projects, commands.RunAsync));
        using var access = new RemoteAccessStore(Path.Combine(directory.CreateDirectory("access"), "access.db"));
        using var key = RSA.Create(2048);
        using var created = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
        await using var remote = tls ? await RemoteEnvironmentHost.StartAsync(local.Environment, access, IPAddress.Loopback, 0, certificate, cancellationToken: timeout.Token) : null;
        var options = new ClientRuntimeOptions
        {
            HubAddress = remote is null ? local.HubAddress : new(remote.Address, "/environment"),
            BearerCredential = remote is null ? local.BearerCredential : access.IssueSession(RemoteAccessLevel.Operate).Token,
            CertificateFingerprint = remote is null ? null : certificate.GetCertHashString(HashAlgorithmName.SHA256),
        };
        await using var client = new EnvironmentClient(options);
        await client.ConnectAsync(timeout.Token);
        var project = await client.AddProjectAsync(new(root), timeout.Token);
        var workspace = new WorkspaceTarget(project.ProjectId);
        var inboxSource = new PullRequestInboxSource(client.Descriptor!.EnvironmentId.Value, "Fixture", client.ListProjectsAsync,
            client.DetectSourceControlAsync, client.ListPullRequestsAsync);
        var inbox = await PullRequestInbox.StartAsync([inboxSource], new(), timeout.Token);
        var inboxRow = Assert.Single(inbox.Rows);
        Assert.Equal(workspace, inboxRow.Target);
        Assert.Equal(provider, inboxRow.Repository.Provider);
        Assert.Equal("7", inboxRow.PullRequest.Number);
        Assert.False(inbox.HasFailures);
        var snapshot = await client.GetPullRequestReviewAsync(new(workspace, "7"), timeout.Token);
        var target = new PullRequestReviewTarget(workspace, PullRequestReviewDefaults.RepositoryKey(snapshot.Repository), "7", snapshot.HeadCommitId);
        var request = new ManagePullRequestRequest(target, PullRequestManagementAction.EditDetails, Title: "Updated", Body: "Updated description",
            ExpectedTitle: "Review fixture", ExpectedBody: "Description", OperationId: CommandId.New());
        if (remote is not null)
        {
            await using var viewer = new EnvironmentClient(new()
            {
                HubAddress = options.HubAddress, CertificateFingerprint = options.CertificateFingerprint,
                BearerCredential = access.IssueSession(RemoteAccessLevel.ReadOnly).Token,
            });
            await viewer.ConnectAsync(timeout.Token);
            using (var readOnlyReview = new PullRequestReviewViewModel(() => viewer,
                new PullRequestReviewDraftStore(directory.CreateDirectory("readonly-drafts")), () => false))
            {
                await readOnlyReview.LoadAsync(project.ProjectId, inboxRow.Target, inboxRow.PullRequest, timeout.Token);
                readOnlyReview.SetBody("Must remain local");
                Assert.False(readOnlyReview.CanWriteReview);
                Assert.False(readOnlyReview.CanSubmit);
                Assert.False(readOnlyReview.CanEditDetails);
                Assert.Null(await readOnlyReview.SubmitAsync(timeout.Token));
            }
            Assert.Equal(provider, (await viewer.GetPullRequestReviewAsync(new(workspace, "7"), timeout.Token)).Repository.Provider);
            await Assert.ThrowsAnyAsync<Exception>(() => viewer.ManagePullRequestAsync(request, timeout.Token));
            await Assert.ThrowsAnyAsync<Exception>(() => viewer.SubmitPullRequestReviewAsync(new(target, PullRequestReviewEvent.Comment, "Review", [], CommandId.New()), timeout.Token));
            Assert.Empty(commands.Writes);
        }
        var store = new PullRequestReviewDraftStore(directory.CreateDirectory("drafts"));
        using (var model = new PullRequestReviewViewModel(() => client, store))
        {
            await model.LoadAsync(project.ProjectId, workspace, snapshot.PullRequest, timeout.Token);
            Assert.True(model.CanReadReview);
            Assert.True(model.CanEditDetails);
            model.SetBody("Saved review body");
            model.ReviewEvent = PullRequestReviewEvent.RequestChanges;
            Assert.False(model.CanSubmit);
            if (provider == SourceControlProvider.GitLab)
            {
                Assert.True(model.HasProviderDiff);
                Assert.True(model.HasReviewComposer);
                Assert.Equal([PullRequestUpdateMethod.Rebase], model.UpdateMethods);
                model.SelectedUpdateMethod = PullRequestUpdateMethod.Merge;
                Assert.False(model.CanUpdateBranch);
                model.ReviewEvent = PullRequestReviewEvent.Approve;
                Assert.True(model.CanSubmit);
                await model.SaveNowAsync(timeout.Token);
                Assert.Equal("Saved review body", (await store.LoadAsync(project.ProjectId, target.Repository, "7", timeout.Token))!.Body);
            }
            else
            {
                Assert.False(model.HasProviderDiff);
                Assert.False(model.HasReviewComposer);
                Assert.Empty(model.ReviewEvents);
                Assert.False(model.CanUpdateBranch);
                Assert.False(model.CanRevert);
            }
        }
        var first = await client.ManagePullRequestAsync(request, timeout.Token);
        Assert.True(first.Succeeded, first.Message);
        Assert.Equal(new PullRequestWriteProgress(1, 1, "Title and description"), first.ReviewProgress);
        await client.DisconnectAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        var replay = await client.ManagePullRequestAsync(request, timeout.Token);
        Assert.Equal(JsonSerializer.Serialize(first, ProtocolJsonContext.Default.SourceControlOperationResult), JsonSerializer.Serialize(replay, ProtocolJsonContext.Default.SourceControlOperationResult));
        Assert.Single(commands.Writes);
        if (provider == SourceControlProvider.GitLab)
        {
            commands.FailedWrite = 3; // One edit is already confirmed; fail after the inline comment.
            var review = new SubmitPullRequestReviewRequest(target, PullRequestReviewEvent.Approve, "Summary",
                [new("src/App.cs", 3, PullRequestDiffSide.Right, "Inline")], CommandId.New());
            var partial = await client.SubmitPullRequestReviewAsync(review, timeout.Token);
            Assert.Equal(CommandReceiptState.DispatchUncertain, partial.State);
            Assert.Equal(1, partial.ReviewProgress!.CompletedSteps);
            await client.DisconnectAsync(timeout.Token);
            await client.ConnectAsync(timeout.Token);
            Assert.Equal(JsonSerializer.Serialize(partial, ProtocolJsonContext.Default.SourceControlOperationResult),
                JsonSerializer.Serialize(await client.SubmitPullRequestReviewAsync(review, timeout.Token), ProtocolJsonContext.Default.SourceControlOperationResult));
            Assert.Equal(3, commands.Writes.Count);
        }
    }
}
