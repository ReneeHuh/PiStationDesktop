using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PiStation.Host.Hosting;
using PiStation.Host.Security;
using PiStation.Host.SourceControl;
using PiStation.Protocol;
using PiStation.Protocol.Models;
using PiStation.TestFixtures;

namespace PiStation.ClientRuntime.Tests;

public sealed class HostingDiscoveryTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowseWithoutExistingProjectAcrossProvidersAndCloneWithOperateOnly(bool tls)
    {
        using var directory = new ClientTestDirectory(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var fixture = new HostingDiscoveryFixture();
        await using var local = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions(),
            sourceControlFactory: (resolver, projects) => new SourceControlHostingService(resolver, projects, fixture.Execute, bitbucket: fixture.Client()));
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
        Assert.Equal(60, ProtocolVersion.Current); Assert.Empty(await client.ListProjectsAsync(timeout.Token));
        foreach (var provider in new[] { SourceControlProvider.GitHub, SourceControlProvider.GitLab, SourceControlProvider.Bitbucket, SourceControlProvider.AzureDevOps })
        {
            var location = new HostingBrowseLocation(provider, Organization: "acme");
            var scopes = await client.ListHostingAccountsAsync(new(location), timeout.Token);
            var repositories = await client.BrowseHostedRepositoriesAsync(new(location, scopes.Accounts[0].Id), timeout.Token);
            Assert.Single(repositories.Repositories);
        }
        var source = directory.CreateDirectory("source"); await ProviderReviewCommands.GitAsync(source, "init", "--quiet", "--initial-branch=main");
        var destination = Path.Combine(directory.CreateDirectory("clones"), "selected-repository");
        if (tls)
        {
            await using var viewer = new EnvironmentClient(new() { HubAddress = options.HubAddress, CertificateFingerprint = options.CertificateFingerprint,
                BearerCredential = access.IssueSession(RemoteAccessLevel.ReadOnly).Token });
            await viewer.ConnectAsync(timeout.Token);
            Assert.NotEmpty((await viewer.ListHostingAccountsAsync(new(new(SourceControlProvider.GitHub)), timeout.Token)).Accounts);
            Assert.Single((await viewer.BrowseHostedRepositoriesAsync(new(new(SourceControlProvider.GitHub), "all"), timeout.Token)).Repositories);
            await Assert.ThrowsAnyAsync<Exception>(() => viewer.CloneHostedRepositoryAsync(new(source, destination), timeout.Token));
            Assert.False(Directory.Exists(destination));
        }
        var cloned = await client.CloneHostedRepositoryAsync(new(source, destination), timeout.Token);
        Assert.True(cloned.Succeeded); Assert.NotNull(cloned.Project);
        Assert.Single(await client.ListProjectsAsync(timeout.Token));
        await using var reconnected = new EnvironmentClient(options); await reconnected.ConnectAsync(timeout.Token);
        Assert.Single(await reconnected.ListProjectsAsync(timeout.Token));
        Assert.Single((await reconnected.BrowseHostedRepositoriesAsync(new(new(SourceControlProvider.Bitbucket), "team"), timeout.Token)).Repositories);
    }
}
