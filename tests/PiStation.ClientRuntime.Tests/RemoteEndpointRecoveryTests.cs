using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PiStation.Host.Hosting;
using PiStation.Host.Security;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class RemoteEndpointRecoveryTests
{
    [Fact]
    public async Task VerifiedEndpointSwapPreservesLeaseAndRollsBackFailedPersistence()
    {
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var local = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions(), cancellationToken: timeout.Token);
        using var access = new RemoteAccessStore(Path.Combine(directory.CreateDirectory("access"), "access.db"));
        var issued = access.IssueSession(RemoteAccessLevel.ReadOnly, TimeSpan.FromHours(1), "Endpoint fixture");
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var transient = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(transient.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
        await using var first = await RemoteEnvironmentHost.StartAsync(local.Environment, access, IPAddress.Loopback, 0, certificate, cancellationToken: timeout.Token);
        await using var second = await RemoteEnvironmentHost.StartAsync(local.Environment, access, IPAddress.Loopback, 0, certificate, cancellationToken: timeout.Token);
        var options = new ClientRuntimeOptions
        {
            HubAddress = new(first.Address, "/environment"), BearerCredential = issued.Token,
            CertificateFingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256),
            ExpectedEnvironmentId = local.Environment.EnvironmentId, ClientId = ClientId.New(),
        };
        await using var client = new EnvironmentClient(options);
        await client.ConnectAsync(timeout.Token);
        var project = await local.Environment.AddProjectAsync(new(directory.CreateDirectory("project")), timeout.Token);
        var thread = await local.Environment.CreateThreadAsync(new(project.ProjectId), timeout.Token);
        await using var lease = client.SubscribeThread(thread.ThreadId);
        var store = lease.Store;
        var candidate = options with { HubAddress = new(second.Address, "/environment") };
        var committed = false;
        await client.ReplaceEndpointAsync(candidate, () => committed = true, timeout.Token);
        Assert.True(committed);
        Assert.Same(store, lease.Store);
        Assert.Equal(1, client.ActiveThreadStreamCount);
        await Assert.ThrowsAsync<IOException>(() => client.ReplaceEndpointAsync(options, () => throw new IOException("fixture commit failed"), timeout.Token));
        Assert.Equal(EnvironmentConnectionState.Connected, client.ConnectionState);
        Assert.Single(await client.ListProjectsAsync(timeout.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ReplaceEndpointAsync(candidate with { ExpectedEnvironmentId = EnvironmentId.New() }, () => { }, timeout.Token));
        Assert.DoesNotContain(issued.Token, client.ExportDiagnostics(), StringComparison.Ordinal);
        Assert.DoesNotContain(first.Address.AbsoluteUri, client.ExportDiagnostics(), StringComparison.Ordinal);
    }
}
