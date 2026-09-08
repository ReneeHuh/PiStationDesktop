using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using PiStation.Host;
using PiStation.Host.Hosting;
using PiStation.Host.Persistence;
using PiStation.Host.Security;
using PiStation.Host.Sessions;
using PiStation.PiRpc.Sessions;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class PiSessionTransferIntegrationTests
{
    [Theory]
    [InlineData("local")]
    [InlineData("https")]
    [InlineData("ssh")]
    public async Task ImportsLocalFilesAndDownloadsEveryExportFormatAcrossListeners(string transport)
    {
        if (transport == "ssh" && !OperatingSystem.IsWindows()) return;
        using var hostDirectory = new ClientTestDirectory();
        using var clientDirectory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var host = await TransferHost.StartAsync(hostDirectory, transport, timeout.Token);
        var project = await host.Client.AddProjectAsync(new(hostDirectory.CreateDirectory("project")), timeout.Token);
        var file = Path.Combine(clientDirectory.Path, "local session.jsonl");
        var original = Fixture(clientDirectory.Path);
        await File.WriteAllTextAsync(file, original, timeout.Token);
        var import = await PiSessionImportFile.CreateAsync(project.ProjectId, file, "Café import", timeout.Token);
        // The attachment upload ceiling must not accidentally limit session transfers.
        Assert.True(import.Request.ByteLength > host.Options.MaximumFileAttachmentBytes);
        var uploaded = new ImmediateProgress();
        var thread = await host.Client.ImportPiSessionFileAsync(import, uploaded, timeout.Token);
        Assert.Equal(import.Request.ByteLength, uploaded.Bytes);
        Assert.Equal(import.Request.OperationId.ToString("N"), thread.ThreadId.Value);
        Assert.Equal("Café import", thread.Title);
        Assert.StartsWith(hostDirectory.Path, thread.PiSessionFile!, StringComparison.OrdinalIgnoreCase);
        var document = await PiSessionDocument.ReadAsync(thread.PiSessionFile!, timeout.Token);
        Assert.Equal(project.CanonicalPath, document.ProjectDirectory);
        var header = JsonNode.Parse((await File.ReadAllLinesAsync(thread.PiSessionFile!, timeout.Token))[0])!;
        Assert.StartsWith(hostDirectory.Path, header["parentSession"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllTextAsync(file, timeout.Token));
        Assert.Equal(thread.ThreadId, (await host.Client.ImportPiSessionFileAsync(import, cancellationToken: timeout.Token)).ThreadId);
        Assert.Single(await host.Client.ListThreadsAsync(project.ProjectId, timeout.Token));
        foreach (var format in Enum.GetValues<PiSessionExportFormat>())
        {
            var destination = Path.Combine(clientDirectory.Path, "export" + PiSessionTransferDefaults.Extension(format));
            await File.WriteAllTextAsync(destination, "previous destination", timeout.Token);
            var received = new ImmediateProgress();
            var result = await host.Client.DownloadPiSessionAsync(thread.ThreadId, destination, format, received, timeout.Token);
            Assert.Equal(destination, result.Path);
            Assert.Equal(result.Bytes, received.Bytes);
            Assert.Equal(result.Bytes, new FileInfo(destination).Length);
            if (format == PiSessionExportFormat.Jsonl)
                Assert.Equal(thread.PiSessionId, (await PiSessionDocument.ReadAsync(destination, timeout.Token)).SessionId);
            else if (format == PiSessionExportFormat.Html)
                Assert.Contains("An answer", await File.ReadAllTextAsync(destination, timeout.Token), StringComparison.Ordinal);
            else
            {
                using var archive = ZipFile.OpenRead(destination);
                Assert.NotNull(archive.GetEntry("session.jsonl"));
                Assert.NotNull(archive.GetEntry("transcript.html"));
            }
        }
        Assert.Empty(Directory.GetFiles(Path.Combine(host.Options.CanonicalDataRoot, "session-transfers")));
        Assert.Empty(Directory.GetFiles(clientDirectory.Path, "*.partial"));
        var configuration = await host.Client.GetThreadPiConfigurationAsync(thread.ThreadId, timeout.Token);
        Assert.Equal(new PiModelSelection("fake", "fake-fast"), configuration.Configuration.Model);
    }

    [Fact]
    public async Task SavedRetryRecoversAfterHostAndClientRestartWithoutTheOriginalFileAndRejectsChangedIntent()
    {
        using var hostDirectory = new ClientTestDirectory();
        using var clientDirectory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var recovery = new PiSessionImportRecoveryStore(clientDirectory.CreateDirectory("recovery"));
        EnvironmentId environmentId;
        ThreadId threadId;
        await using (var host = await TransferHost.StartAsync(hostDirectory, "https", timeout.Token))
        {
            var project = await host.Client.AddProjectAsync(new(hostDirectory.CreateDirectory("project")), timeout.Token);
            var path = Path.Combine(clientDirectory.Path, "session.jsonl");
            await File.WriteAllTextAsync(path, Fixture(clientDirectory.Path), timeout.Token);
            var import = await PiSessionImportFile.CreateAsync(project.ProjectId, path, cancellationToken: timeout.Token);
            environmentId = host.Client.Descriptor!.EnvironmentId;
            await recovery.SaveAsync(environmentId, import, timeout.Token);
            // Simulate a lost response by retaining the pending operation after the host commits.
            threadId = (await host.Client.ImportPiSessionFileAsync(import, cancellationToken: timeout.Token)).ThreadId;
            File.Delete(path);
        }
        await using var restarted = await TransferHost.StartAsync(hostDirectory, "https", timeout.Token);
        Assert.Equal(environmentId, restarted.Client.Descriptor!.EnvironmentId);
        var restored = (await new PiSessionImportRecoveryStore(clientDirectory.CreateDirectory("recovery")).LoadAsync(environmentId, timeout.Token))!;
        Assert.False(File.Exists(restored.FilePath));
        Assert.Equal(threadId, (await restarted.Client.ImportPiSessionFileAsync(restored, cancellationToken: timeout.Token)).ThreadId);
        Assert.Single(await restarted.Client.ListThreadsAsync(restored.Request.ProjectId, timeout.Token));
        var conflict = await Assert.ThrowsAsync<HttpRequestException>(() => restarted.Client.ImportPiSessionFileAsync(
            restored with { Request = restored.Request with { Title = "Changed intent" } }, cancellationToken: timeout.Token));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await recovery.ClearAsync(environmentId, Guid.NewGuid(), timeout.Token);
        Assert.NotNull(await recovery.LoadAsync(environmentId, timeout.Token));
        Assert.Null(await recovery.LoadAsync(EnvironmentId.New(), timeout.Token));
        await recovery.ClearAsync(environmentId, restored.Request.OperationId, timeout.Token);
        Assert.Null(await recovery.LoadAsync(environmentId, timeout.Token));
    }

    [Fact]
    public async Task BundlesPreserveAttachmentsAndCitationsAcrossUploadDownloadAndAnotherImport()
    {
        using var hostDirectory = new ClientTestDirectory();
        using var clientDirectory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var host = await TransferHost.StartAsync(hostDirectory, "https", timeout.Token);
        var project = await host.Client.AddProjectAsync(new(hostDirectory.CreateDirectory("project")), timeout.Token);
        var messageId = Guid.NewGuid().ToString("N");
        var session = Path.Combine(clientDirectory.Path, "session.jsonl");
        await File.WriteAllTextAsync(session, Fixture(clientDirectory.Path, SentMessageReference.Append("Question", messageId)), timeout.Token);
        var source = Path.Combine(clientDirectory.Path, "notes.txt");
        var bytes = "attachment bytes"u8.ToArray();
        await File.WriteAllBytesAsync(source, bytes, timeout.Token);
        var attachment = new DraftAttachment(EnvironmentId.New(), ThreadId.New(), DraftId.New(), AttachmentId.New(),
            "notes.txt", "text/plain", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), source, DateTimeOffset.UtcNow);
        var bundle = Path.Combine(clientDirectory.Path, "portable.zip");
        await PiSessionBundle.ExportAsync(bundle, session, await PiSessionDocument.ReadAsync(session, timeout.Token),
            [new(messageId, "Question", [attachment], [new("quote", "file", "source", "quoted text", Comment: "Preserve this comment")])], timeout.Token);
        File.Delete(source);
        var import = await PiSessionImportFile.CreateAsync(project.ProjectId, bundle, cancellationToken: timeout.Token);
        var thread = await host.Client.ImportPiSessionFileAsync(import, cancellationToken: timeout.Token);
        var database = new HostDatabase(host.Options);
        await database.InitializeAsync(timeout.Token);
        var message = Assert.Single(await database.ListSentMessagesAsync(thread.ThreadId, timeout.Token)).Value;
        Assert.Equal("Preserve this comment", Assert.Single(message.Citations).Comment);
        var retained = Assert.Single(message.Attachments);
        Assert.NotEqual(retained.FileName, Path.GetFileName(retained.ServerPath));
        var downloaded = await host.Client.GetAttachmentFileAsync(retained, clientDirectory.CreateDirectory("cache"), timeout.Token);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(downloaded, timeout.Token));
        var exported = Path.Combine(clientDirectory.Path, "roundtrip.zip");
        await host.Client.DownloadPiSessionAsync(thread.ThreadId, exported, PiSessionExportFormat.Bundle, cancellationToken: timeout.Token);
        var next = await host.Client.ImportPiSessionFileAsync(await PiSessionImportFile.CreateAsync(project.ProjectId, exported,
            cancellationToken: timeout.Token), cancellationToken: timeout.Token);
        var copied = Assert.Single(await database.ListSentMessagesAsync(next.ThreadId, timeout.Token)).Value;
        Assert.NotEqual(message.Id, copied.Id);
        Assert.Equal(next.ThreadId, Assert.Single(copied.Attachments).ThreadId);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(copied.Attachments).ServerPath, timeout.Token));
    }

    [Fact]
    public async Task TransferEndpointsRejectUnauthorizedReadOnlyWrongEnvironmentAndInvalidBodies()
    {
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        await using var host = await TransferHost.StartAsync(directory, "https", timeout.Token);
        var project = await host.Client.AddProjectAsync(new(directory.CreateDirectory("project")), timeout.Token);
        var file = Path.Combine(directory.Path, "session.jsonl");
        await File.WriteAllTextAsync(file, Fixture(directory.Path), timeout.Token);
        var import = await PiSessionImportFile.CreateAsync(project.ProjectId, file, cancellationToken: timeout.Token);
        var route = PiSessionFileTransfer.ImportUri(import.Request);
        using var http = host.CreateHttp();
        http.DefaultRequestHeaders.Authorization = null;
        using (var denied = await http.GetAsync(route, timeout.Token)) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", host.Access!.IssueSession(RemoteAccessLevel.ReadOnly).Token);
        using (var denied = await http.GetAsync(route, timeout.Token)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await http.GetAsync($"session-transfers/export/{ThreadId.New()}?format=Jsonl", timeout.Token)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var body = new ByteArrayContent(await File.ReadAllBytesAsync(file, timeout.Token)))
        using (var denied = await http.PutAsync(route, body, timeout.Token)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", host.ClientOptions.BearerCredential);
        http.DefaultRequestHeaders.Remove("X-PiStation-Environment-Id");
        using (var wrong = await http.GetAsync(route, timeout.Token)) Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        http.DefaultRequestHeaders.Add("X-PiStation-Environment-Id", host.Client.Descriptor!.EnvironmentId.Value);
        using (var corrupt = new ByteArrayContent(new byte[import.Request.ByteLength]))
        using (var rejected = await http.PutAsync(route, corrupt, timeout.Token)) Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Empty(await host.Client.ListThreadsAsync(project.ProjectId, timeout.Token));
        Assert.Empty(Directory.GetFiles(Path.Combine(host.Options.CanonicalDataRoot, "session-transfers")));
        await File.WriteAllTextAsync(file, "invalid JSONL", timeout.Token);
        var invalid = await PiSessionImportFile.CreateAsync(project.ProjectId, file, cancellationToken: timeout.Token);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => host.Client.ImportPiSessionFileAsync(invalid, cancellationToken: timeout.Token));
        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.Empty(await host.Client.ListThreadsAsync(project.ProjectId, timeout.Token));
        Assert.Empty(Directory.GetFiles(Path.Combine(host.Options.CanonicalDataRoot, "session-transfers")));
    }

    [Fact]
    public async Task CanceledUploadCanBeRetriedWithoutDuplicateThreadsOrStagingLeaks()
    {
        using var hostDirectory = new ClientTestDirectory();
        using var clientDirectory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var host = await TransferHost.StartAsync(hostDirectory, "https", timeout.Token);
        var project = await host.Client.AddProjectAsync(new(hostDirectory.CreateDirectory("project")), timeout.Token);
        var file = Path.Combine(clientDirectory.Path, "session.jsonl");
        await File.WriteAllTextAsync(file, Fixture(clientDirectory.Path) + new string(' ', 2 * 1024 * 1024) + "\n", timeout.Token);
        var import = await PiSessionImportFile.CreateAsync(project.ProjectId, file, cancellationToken: timeout.Token);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var progress = new CallbackProgress(_ => canceled.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Client.ImportPiSessionFileAsync(import, progress, canceled.Token));
        var thread = await host.Client.ImportPiSessionFileAsync(import, cancellationToken: timeout.Token);
        Assert.Equal(import.Request.OperationId.ToString("N"), thread.ThreadId.Value);
        Assert.Single(await host.Client.ListThreadsAsync(project.ProjectId, timeout.Token));
        Assert.Empty(Directory.GetFiles(Path.Combine(host.Options.CanonicalDataRoot, "session-transfers")));
    }

    private sealed class CallbackProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    private static string Fixture(string cwd, string prompt = "Question") => new JsonObject
    {
        ["type"] = "session", ["version"] = 3, ["id"] = Guid.NewGuid().ToString("N"), ["cwd"] = cwd,
    }.ToJsonString() + "\n" + """
        {"type":"model_change","id":"m","parentId":null,"provider":"fake","modelId":"fake-fast"}
        {"type":"thinking_level_change","id":"t","parentId":"m","thinkingLevel":"off"}
        """ + "\n" + new JsonObject
    {
        ["type"] = "message", ["id"] = "u", ["parentId"] = "t", ["message"] = new JsonObject { ["role"] = "user", ["content"] = prompt },
    }.ToJsonString() + "\n" + new JsonObject
    {
        ["type"] = "message", ["id"] = "a", ["parentId"] = "u", ["message"] = new JsonObject
        {
            ["role"] = "assistant", ["stopReason"] = "stop",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "An answer " + new string('x', 2048) }),
        },
    }.ToJsonString() + "\n";

    private sealed class ImmediateProgress : IProgress<long>
    {
        internal long Bytes { get; private set; }
        public void Report(long value) => Bytes = value;
    }

    private sealed class TransferHost : IAsyncDisposable
    {
        private EmbeddedEnvironmentHost? _local;
        private RemoteEnvironmentHost? _remote;
        private SshEnvironmentHost? _ssh;
        private X509Certificate2? _certificate;
        internal RemoteAccessStore? Access { get; private set; }
        internal HostOptions Options { get; private set; } = null!;
        internal EnvironmentClient Client { get; private set; } = null!;
        internal ClientRuntimeOptions ClientOptions { get; private set; } = null!;

        internal static async Task<TransferHost> StartAsync(ClientTestDirectory directory, string transport, CancellationToken token)
        {
            var host = new TransferHost { Options = directory.CreateHostOptions() with { MaximumImageAttachmentBytes = 512, MaximumFileAttachmentBytes = 1024 } };
            try
            {
                if (transport == "ssh")
                {
                    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                    host._ssh = await SshEnvironmentHost.StartAsync(host.Options, cancellationToken: token);
                    host.ClientOptions = new() { HubAddress = new Uri($"https://127.0.0.1:{host._ssh.Info.Port}/environment"),
                        BearerCredential = host._ssh.Info.BearerCredential, CertificateFingerprint = host._ssh.Info.CertificateFingerprint };
                }
                else
                {
                    host._local = await EmbeddedEnvironmentHost.StartAsync(host.Options, cancellationToken: token);
                    host.ClientOptions = new() { HubAddress = host._local.HubAddress, BearerCredential = host._local.BearerCredential };
                    if (transport == "https")
                    {
                        host.Access = new RemoteAccessStore(Path.Combine(directory.CreateDirectory("access"), "access.db"));
                        using var key = RSA.Create(2048);
                        var request = new CertificateRequest("CN=Session transfer test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
                        host._certificate = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
                        host._remote = await RemoteEnvironmentHost.StartAsync(host._local.Environment, host.Access, IPAddress.Loopback, 0, host._certificate, cancellationToken: token);
                        host.ClientOptions = new() { HubAddress = new(host._remote.Address, "/environment"), BearerCredential = host.Access.IssueSession().Token,
                            CertificateFingerprint = host._certificate.GetCertHashString(HashAlgorithmName.SHA256) };
                    }
                }
                host.Client = new EnvironmentClient(host.ClientOptions);
                await host.Client.ConnectAsync(token);
                return host;
            }
            catch { await host.DisposeAsync(); throw; }
        }

        internal HttpClient CreateHttp()
        {
            var http = new HttpClient(RemoteTransport.CreateHandler(ClientOptions.CertificateFingerprint)) { BaseAddress = new(ClientOptions.HubAddress, "/") };
            http.DefaultRequestHeaders.Authorization = new("Bearer", ClientOptions.BearerCredential);
            http.DefaultRequestHeaders.Add("X-PiStation-Environment-Id", Client.Descriptor!.EnvironmentId.Value);
            return http;
        }

        public async ValueTask DisposeAsync()
        {
            if (Client is not null) await Client.DisposeAsync();
            if (_remote is not null) await _remote.DisposeAsync();
            if (_local is not null) await _local.DisposeAsync();
            if (OperatingSystem.IsWindows() && _ssh is not null) await _ssh.DisposeAsync();
            Access?.Dispose();
            _certificate?.Dispose();
        }
    }
}
