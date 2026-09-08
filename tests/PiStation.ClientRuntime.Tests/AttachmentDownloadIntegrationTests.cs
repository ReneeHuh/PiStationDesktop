using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PiStation.Host.Persistence;
using PiStation.Host.Hosting;
using PiStation.Host.Security;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class AttachmentDownloadIntegrationTests
{
    [Fact]
    public async Task RemoteDraftAndSentAttachmentsSurviveHostAndClientRestartWithSeparateStorage()
    {
        using var hostDirectory = new ClientTestDirectory();
        using var clientDirectory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var options = hostDirectory.CreateHostOptions();
        var cache = clientDirectory.CreateDirectory("cache");
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aV1sAAAAASUVORK5CYII=");
        DraftAttachment? attachment = null;
        string? cachedPath = null;
        using var access = new RemoteAccessStore(Path.Combine(hostDirectory.CreateDirectory("access"), "access.db"));
        var session = access.IssueSession(RemoteAccessLevel.ReadOnly);
        using var certificate = CreateCertificate();
        for (var phase = 0; phase < 2; phase++)
        {
            await using var host = await EmbeddedEnvironmentHost.StartAsync(options, cancellationToken: timeout.Token);
            await using var owner = new EnvironmentClient(new() { HubAddress = host.HubAddress, BearerCredential = host.BearerCredential });
            await owner.ConnectAsync(timeout.Token);
            if (phase == 0)
            {
                var project = await owner.AddProjectAsync(new(hostDirectory.CreateDirectory("project")), timeout.Token);
                var thread = await owner.CreateThreadAsync(new(project.ProjectId), timeout.Token);
                var draft = await owner.GetThreadDraftAsync(thread.ThreadId, timeout.Token);
                using var source = new MemoryStream(bytes);
                var uploaded = await owner.UploadDraftAttachmentAsync(thread.ThreadId, draft.DraftId, draft.Revision,
                    "remote.png", "image/png", source, bytes.Length, timeout.Token);
                attachment = Assert.Single(uploaded.Draft!.Attachments);
                // Exercise the embedded listener too, using a separate local cache.
                var localPath = await owner.GetAttachmentFileAsync(attachment, clientDirectory.CreateDirectory("local-cache"), timeout.Token);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(localPath, timeout.Token));
            }

            await using var remote = await RemoteEnvironmentHost.StartAsync(host.Environment, access, IPAddress.Loopback, 0,
                certificate, cancellationToken: timeout.Token);
            var clientOptions = new ClientRuntimeOptions
            {
                HubAddress = new(remote.Address, "/environment"), BearerCredential = session.Token,
                CertificateFingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256),
                ExpectedEnvironmentId = attachment!.EnvironmentId,
            };
            await using var viewer = new EnvironmentClient(clientOptions);
            await viewer.ConnectAsync(timeout.Token);
            Assert.Contains("attachment.download", viewer.Descriptor!.Capabilities);
            Assert.DoesNotContain("attachment.upload", viewer.Descriptor.Capabilities);
            // A bogus client-visible ServerPath must never be read or sent to the host.
            var clientAttachment = attachment with { ServerPath = Path.Combine(clientDirectory.Path, "does-not-exist.png") };
            var downloaded = await viewer.GetAttachmentFileAsync(clientAttachment, cache, timeout.Token);
            Assert.StartsWith(cache + Path.DirectorySeparatorChar, downloaded, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(attachment.ServerPath, downloaded);
            Assert.False(File.Exists(clientAttachment.ServerPath));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(downloaded, timeout.Token));
            if (cachedPath is not null) Assert.Equal(cachedPath, downloaded);
            cachedPath = downloaded;
            // This is the same verified local file consumed by previews and Save as.
            var saved = Path.Combine(clientDirectory.Path, $"saved-{phase}.png");
            File.Copy(downloaded, saved);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(saved, timeout.Token));

            if (phase == 0)
            {
                var database = new HostDatabase(options);
                await database.InitializeAsync(timeout.Token);
                var draft = (await database.GetThreadDraftAsync(attachment.ThreadId, timeout.Token))!;
                await database.RetainSentMessageAsync(attachment.ThreadId, draft.DraftId, draft.Revision,
                    new SentMessageContent(Guid.NewGuid().ToString("N"), "sent", [attachment], []), timeout.Token);
                await owner.ClearThreadDraftAsync(attachment.ThreadId, draft.DraftId, draft.Revision, [attachment.AttachmentId], timeout.Token);
                Assert.Empty((await owner.GetThreadDraftAsync(attachment.ThreadId, timeout.Token)).Attachments);
                Assert.Equal(downloaded, await viewer.GetAttachmentFileAsync(clientAttachment, cache, timeout.Token));
            }
            else
            {
                await viewer.DisconnectAsync(timeout.Token);
                await viewer.ConnectAsync(timeout.Token);
                Assert.Equal(downloaded, await viewer.GetAttachmentFileAsync(clientAttachment, cache, timeout.Token));
                await Assert.ThrowsAsync<InvalidOperationException>(() => viewer.GetAttachmentFileAsync(
                    clientAttachment with { EnvironmentId = EnvironmentId.New() }, cache, timeout.Token));
                var otherThread = await owner.CreateThreadAsync(new((await owner.ListProjectsAsync(timeout.Token))[0].ProjectId), timeout.Token);
                var wrongThread = await Assert.ThrowsAsync<HttpRequestException>(() => viewer.GetAttachmentFileAsync(
                    clientAttachment with { ThreadId = otherThread.ThreadId }, cache, timeout.Token));
                Assert.Equal(HttpStatusCode.NotFound, wrongThread.StatusCode);

                await File.WriteAllBytesAsync(attachment.ServerPath, new byte[bytes.Length], timeout.Token);
                var changed = await Assert.ThrowsAsync<HttpRequestException>(() => viewer.GetAttachmentFileAsync(clientAttachment, cache, timeout.Token));
                Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
                File.Delete(attachment.ServerPath);
                var missing = await Assert.ThrowsAsync<HttpRequestException>(() => viewer.GetAttachmentFileAsync(clientAttachment, cache, timeout.Token));
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }
        }
    }

    [Fact]
    public async Task DownloadRequiresAuthenticationEnvironmentIdentityAndAnActiveGrantEvenWhenCached()
    {
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions(), cancellationToken: timeout.Token);
        await using var owner = new EnvironmentClient(new() { HubAddress = host.HubAddress, BearerCredential = host.BearerCredential });
        await owner.ConnectAsync(timeout.Token);
        var project = await owner.AddProjectAsync(new(directory.CreateDirectory("project")), timeout.Token);
        var thread = await owner.CreateThreadAsync(new(project.ProjectId), timeout.Token);
        var draft = await owner.GetThreadDraftAsync(thread.ThreadId, timeout.Token);
        var bytes = "host-only content"u8.ToArray();
        using var source = new MemoryStream(bytes);
        var upload = await owner.UploadDraftAttachmentAsync(thread.ThreadId, draft.DraftId, draft.Revision,
            "notes.txt", "text/plain", source, bytes.Length, timeout.Token);
        var attachment = Assert.Single(upload.Draft!.Attachments);
        using var access = new RemoteAccessStore(Path.Combine(directory.CreateDirectory("access"), "access.db"));
        var session = access.IssueSession(RemoteAccessLevel.ReadOnly);
        using var certificate = CreateCertificate();
        var pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        await using var remote = await RemoteEnvironmentHost.StartAsync(host.Environment, access, IPAddress.Loopback, 0,
            certificate, cancellationToken: timeout.Token);
        using var http = new HttpClient(RemoteTransport.CreateHandler(pin)) { BaseAddress = remote.Address };
        var route = $"attachments/{thread.ThreadId}/{attachment.AttachmentId}";
        using (var denied = await http.GetAsync(route, timeout.Token)) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", session.Token);
        using (var wrongEnvironment = await http.GetAsync(route, timeout.Token)) Assert.Equal(HttpStatusCode.Conflict, wrongEnvironment.StatusCode);
        var cache = directory.CreateDirectory("client-cache");
        await AttachmentFileCache.GetFileAsync(http, attachment, cache, timeout.Token);
        access.Revoke(Assert.Single(access.ListDevices()).DeviceId);
        var revoked = await Assert.ThrowsAsync<HttpRequestException>(() => AttachmentFileCache.GetFileAsync(http, attachment, cache, timeout.Token));
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        using var wrongPin = new HttpClient(RemoteTransport.CreateHandler(new string('0', 64))) { BaseAddress = remote.Address };
        wrongPin.DefaultRequestHeaders.Authorization = new("Bearer", session.Token);
        await Assert.ThrowsAsync<HttpRequestException>(() => AttachmentFileCache.GetFileAsync(wrongPin, attachment, cache, timeout.Token));
    }

    [Fact]
    public async Task SshListenerServesVerifiedAttachmentsThroughItsPinnedConnection()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var hostDirectory = new ClientTestDirectory();
        using var clientDirectory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var host = await SshEnvironmentHost.StartAsync(hostDirectory.CreateHostOptions(), cancellationToken: timeout.Token);
        await using var client = new EnvironmentClient(new()
        {
            HubAddress = new Uri($"https://127.0.0.1:{host.Info.Port}/environment"),
            BearerCredential = host.Info.BearerCredential, CertificateFingerprint = host.Info.CertificateFingerprint,
        });
        await client.ConnectAsync(timeout.Token);
        var project = await client.AddProjectAsync(new(hostDirectory.CreateDirectory("project")), timeout.Token);
        var thread = await client.CreateThreadAsync(new(project.ProjectId), timeout.Token);
        var draft = await client.GetThreadDraftAsync(thread.ThreadId, timeout.Token);
        var bytes = "SSH attachment"u8.ToArray();
        using var source = new MemoryStream(bytes);
        var upload = await client.UploadDraftAttachmentAsync(thread.ThreadId, draft.DraftId, draft.Revision,
            "ssh.txt", "text/plain", source, bytes.Length, timeout.Token);
        var attachment = Assert.Single(upload.Draft!.Attachments);
        var downloaded = await client.GetAttachmentFileAsync(attachment, clientDirectory.CreateDirectory("cache"), timeout.Token);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(downloaded, timeout.Token));
        Assert.NotEqual(attachment.ServerPath, downloaded);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=PiStation attachment test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
    }
}
