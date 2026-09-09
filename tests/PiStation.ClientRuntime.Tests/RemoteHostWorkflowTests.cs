using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PiStation.Host;
using PiStation.Host.Hosting;
using PiStation.Host.Security;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class RemoteHostWorkflowTests
{
    private static byte[] Icon => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");

    [Fact]
    public async Task EndpointProbeChecksPinAndIdentityWithoutGrantingAnonymousAccess()
    {
        using var directory = new ClientTestDirectory();
        var options = directory.CreateHostOptions();
        await using var fixture = await RemoteFixture.StartAsync(options, directory);
        var environmentId = fixture.Client.Descriptor!.EnvironmentId;
        await RemoteEndpointProbe.VerifyAsync(fixture.Address, fixture.Fingerprint, environmentId);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => RemoteEndpointProbe.VerifyAsync(fixture.Address, new string('0', 64), environmentId));
        await Assert.ThrowsAsync<InvalidDataException>(() => RemoteEndpointProbe.VerifyAsync(fixture.Address, fixture.Fingerprint, Protocol.Identifiers.EnvironmentId.New()));
        using var anonymous = new HttpClient(RemoteTransport.CreateHandler(fixture.Fingerprint)) { BaseAddress = fixture.Address };
        using var identity = await anonymous.GetAsync(RemoteEndpointIdentity.Path);
        Assert.Equal(HttpStatusCode.OK, identity.StatusCode);
        using var body = JsonDocument.Parse(await identity.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
        using var hub = await anonymous.PostAsync("/environment/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, hub.StatusCode);
        using var files = await anonymous.GetAsync("/threads/fixture/attachments/fixture");
        Assert.Equal(HttpStatusCode.Unauthorized, files.StatusCode);
        var advertised = new Uri("https://fixture.example.ts.net:8443/");
        fixture.Listener.AdvertiseForwardedAddress(advertised);
        if (OperatingSystem.IsWindows())
        {
            var discovered = await SshEnvironmentHost.TryDiscoverAsync(options.CanonicalDataRoot);
            Assert.Equal(advertised, discovered!.PairingAddress);
            Assert.Equal(fixture.Fingerprint, discovered.PairingCertificateFingerprint);
        }
        var project = await fixture.Client.AddProjectAsync(new(directory.CreateDirectory("preview-project")));
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => fixture.Client.OpenRemotePreviewAsync(new(project.ProjectId, new Uri("https://localhost:8443/"))));
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => fixture.Client.OpenRemotePreviewAsync(new(project.ProjectId, fixture.Address)));
    }

    [Fact]
    public async Task RemoteRuntimeSettingsRoundTripPreservesArgumentsVariablesExtensionsAndDiscoveryChoice()
    {
        using var directory = new ClientTestDirectory();
        var options = directory.CreateHostOptions();
        options.ConfiguredPiExecutablePath = options.PiInstallation!.ExecutablePath;
        var extension = Path.Combine(directory.CreateDirectory("extensions"), "test.ts");
        await File.WriteAllTextAsync(extension, "export default () => {};");
        options.Extensions = new(true, [extension]);
        options.LaunchConfiguration = new(["--provider", "test", "--append-system-prompt", ""], new Dictionary<string, string?>
        { ["CUSTOM_VALUE"] = " value=with equals ", ["EMPTY_VALUE"] = "", ["REMOVE_VALUE"] = null, ["MULTILINE"] = "first\nsecond" }, 75, 8);
        await PiRuntimeSettingsStore.SaveAsync(options.CanonicalDataRoot, new(options.PiInstallation!.ExecutablePath, options.Extensions, options.LaunchConfiguration));
        await using var fixture = await RemoteFixture.StartAsync(options, directory);
        var loaded = await fixture.Client.GetPiRuntimeConfigurationAsync();
        var parsed = PiLaunchEditor.Parse(PiLaunchEditor.FormatArguments(loaded.Launch!), PiLaunchEditor.FormatEnvironment(loaded.Launch!),
            90, loaded.Launch!.ShutdownTimeoutSeconds, loaded.Launch);
        var result = await fixture.Client.ConfigurePiRuntimeAsync(new(loaded.ExecutablePath, loaded.Extensions, parsed));
        Assert.True(result.Available, result.Message);
        var after = await fixture.Client.GetPiRuntimeConfigurationAsync();
        Assert.Equal(loaded.ExecutablePath, after.ExecutablePath);
        Assert.Equal(loaded.Extensions.Paths, after.Extensions.Paths);
        Assert.True(after.Extensions.DiscoverInstalled);
        Assert.Equal(loaded.Launch.Arguments, after.Launch!.Arguments);
        Assert.Equal(loaded.Launch.EnvironmentVariables, after.Launch.EnvironmentVariables);
        Assert.Equal(90, after.Launch.CommandTimeoutSeconds);
        Assert.Equal(8, after.Launch.ShutdownTimeoutSeconds);
        var saved = PiRuntimeSettingsStore.Load(options.CanonicalDataRoot);
        Assert.Equal(after.Launch.EnvironmentVariables, saved.Launch!.EnvironmentVariables);
    }

    [Fact]
    public async Task RemoteIconUploadAndHostFileBrowsingDoNotDependOnClientPaths()
    {
        using var directory = new ClientTestDirectory();
        await using var fixture = await RemoteFixture.StartAsync(directory.CreateHostOptions(), directory);
        var projectPath = directory.CreateDirectory("host-project");
        var project = await fixture.Client.AddProjectAsync(new(projectPath));
        var request = new UpdateProjectDefaultsRequest(project.ProjectId, project.DefaultWorkspaceMode, null, null, null, false,
            Icon: @"Z:\client-only\missing.png", UpdateCustomization: true, UploadedIcon: new("selected.png", Icon));
        var updated = await fixture.Client.UpdateProjectDefaultsAsync(request);
        Assert.NotEqual(request.Icon, updated.Icon);
        Assert.True(File.Exists(updated.Icon));
        Assert.Equal(Icon, await fixture.Client.ReadProjectIconAsync(project.ProjectId));
        for (var index = 0; index < 205; index++) await File.WriteAllTextAsync(Path.Combine(projectPath, $"entry-{index:D3}.txt"), "host");
        var page = await fixture.Client.BrowseHostPathAsync(new(projectPath));
        Assert.Equal(200, page.Entries.Count);
        Assert.Equal(200, page.NextOffset);
        var next = await fixture.Client.BrowseHostPathAsync(new(projectPath, page.NextOffset!.Value));
        Assert.Equal(5, next.Entries.Count);
        Assert.Null(next.NextOffset);
        Assert.Equal(205, page.Entries.Concat(next.Entries).DistinctBy(entry => entry.Path).Count());
        Assert.All(page.Entries, entry => Assert.StartsWith(projectPath, entry.Path));
        Assert.NotEmpty((await fixture.Client.BrowseHostPathAsync(new())).Entries);
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.BrowseHostPathAsync(new("relative")));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.UpdateProjectDefaultsAsync(request with
        { UploadedIcon = new("invalid.png", new byte[20]) }));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.UpdateProjectDefaultsAsync(request with
        { UploadedIcon = new("too-large.png", new byte[ProjectIconLimits.MaximumBytes + 1]) }));
        Assert.Equal(Icon, await fixture.Client.ReadProjectIconAsync(project.ProjectId));
    }

    [Fact]
    public async Task RemoteDiagnosticsDownloadIsRedactedAndReadOnlyCannotReadRuntimeSecretsOrBrowseHost()
    {
        using var directory = new ClientTestDirectory();
        var options = directory.CreateHostOptions();
        await using var fixture = await RemoteFixture.StartAsync(options, directory);
        Assert.Null((await fixture.Client.GetPiRuntimeConfigurationAsync()).ExecutablePath);
        var destination = Path.Combine(directory.Path, "client", "chosen.json");
        var result = await fixture.Client.ExportDiagnosticsAsync(new(destination));
        var content = await File.ReadAllTextAsync(destination);
        Assert.Equal(destination, result.Path);
        Assert.Equal(new FileInfo(destination).Length, result.ByteLength);
        Assert.DoesNotContain(options.CanonicalDataRoot, content, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(content);
        Assert.Contains("[redacted]", content, StringComparison.Ordinal);
        await using var viewer = fixture.CreateClient(RemoteAccessLevel.ReadOnly);
        await viewer.ConnectAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => viewer.GetPiRuntimeConfigurationAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => viewer.BrowseHostPathAsync(new(directory.Path)));
        var rejectedDestination = Path.Combine(directory.Path, "rejected.json");
        await Assert.ThrowsAnyAsync<Exception>(() => viewer.ExportDiagnosticsAsync(new(rejectedDestination)));
        Assert.False(File.Exists(rejectedDestination));
        var project = await fixture.Client.AddProjectAsync(new(directory.CreateDirectory("project")));
        await fixture.Client.UpdateProjectDefaultsAsync(new(project.ProjectId, project.DefaultWorkspaceMode, null, null, null, false,
            UpdateCustomization: true, UploadedIcon: new("icon.png", Icon)));
        Assert.Equal(Icon, await viewer.ReadProjectIconAsync(project.ProjectId));
    }

    private sealed class RemoteFixture(EmbeddedEnvironmentHost local, RemoteEnvironmentHost remote,
        RemoteAccessStore access, X509Certificate2 certificate, EnvironmentClient client) : IAsyncDisposable
    {
        public EnvironmentClient Client => client;
        public Uri Address => remote.Address;
        public RemoteEnvironmentHost Listener => remote;
        public string Fingerprint => certificate.GetCertHashString(HashAlgorithmName.SHA256);
        public static async Task<RemoteFixture> StartAsync(HostOptions options, ClientTestDirectory directory)
        {
            var local = await EmbeddedEnvironmentHost.StartAsync(options);
            var access = new RemoteAccessStore(Path.Combine(directory.CreateDirectory("access"), "access.db"));
            using var key = RSA.Create(2048);
            using var created = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            var certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
            RemoteEnvironmentHost? remote = null;
            EnvironmentClient? client = null;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                remote = await RemoteEnvironmentHost.StartAsync(local.Environment, access, IPAddress.Loopback, 0, certificate, cancellationToken: timeout.Token);
                var issued = access.IssueSession(RemoteAccessLevel.Operate);
                client = new EnvironmentClient(new() { HubAddress = new(remote.Address, "/environment"), BearerCredential = issued.Token,
                    CertificateFingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256) });
                await client.ConnectAsync(timeout.Token);
                return new(local, remote, access, certificate, client);
            }
            catch
            {
                if (client is not null) await client.DisposeAsync();
                if (remote is not null) await remote.DisposeAsync();
                await local.DisposeAsync(); access.Dispose(); certificate.Dispose();
                throw;
            }
        }
        public EnvironmentClient CreateClient(RemoteAccessLevel level) => new(new()
        {
            HubAddress = new(remote.Address, "/environment"), BearerCredential = access.IssueSession(level).Token,
            CertificateFingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256),
        });
        public async ValueTask DisposeAsync()
        {
            await client.DisposeAsync(); await remote.DisposeAsync(); await local.DisposeAsync(); access.Dispose(); certificate.Dispose();
        }
    }
}
