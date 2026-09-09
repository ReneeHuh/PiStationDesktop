using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using PiStation.Host.Hosting;
using PiStation.Host.Security;
using PiStation.Host.Threads;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.ClientRuntime.Tests;

public sealed class BrowserAutomationTransportTests
{
    [Theory]
    [InlineData("local")]
    [InlineData("https")]
    [InlineData("ssh")]
    public async Task SlowThreadStartupDoesNotStarveBrowserHeartbeatsOrCancellation(string transport)
    {
        if (transport == "ssh" && !OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var host = await BrowserHost.StartAsync(directory, transport, timeout.Token);
        var project = (await host.Client.GetThreadAsync(host.Thread, timeout.Token)).ProjectId;
        await using var session = await host.Client.OpenBrowserAutomationAsync(new(host.Thread, BrowserAutomationAccess.Interact), timeout.Token);
        var first = host.WriteRequest("wait");
        var active = await NextAsync(session, timeout.Token);
        var second = await host.Client.CreateThreadAsync(new(project), timeout.Token);
        host.Factory.Pause = true;
        var starting = host.Client.GetComposerDiscoveryAsync(second.ThreadId, timeout.Token);
        Task? queued = null;
        try
        {
            await host.Factory.Started.Task.WaitAsync(timeout.Token);
            queued = host.Client.ListProjectsAsync(timeout.Token);
            // Exceed both the three-second client deadline and five-second host heartbeat.
            await Task.Delay(TimeSpan.FromSeconds(6), timeout.Token);
            Assert.False(starting.IsCompleted);
            Assert.True(session.IsActive, session.Error);
            Assert.False(active.CancellationToken.IsCancellationRequested);
            File.Delete(host.RequestPath(first.Id));
            await UntilAsync(() => active.CancellationToken.IsCancellationRequested, timeout.Token);
            var fresh = host.WriteRequest("status");
            var work = await NextAsync(session, timeout.Token);
            Assert.Equal(fresh.Id, work.Request.Id);
            await session.CompleteAsync(work, new(true));
            Assert.True((await host.ReadResultAsync(fresh.Id, timeout.Token)).Success);
            await session.DisposeAsync();
            Assert.False(File.Exists(Path.Combine(host.ThreadDirectory, "permission.json")));
            Assert.False(starting.IsCompleted);
            Assert.False(queued.IsCompleted);
        }
        finally
        {
            host.Factory.Release.TrySetResult();
            await starting;
            if (queued is not null) await queued;
        }
    }

    [Theory]
    [InlineData("local")]
    [InlineData("https")]
    [InlineData("ssh")]
    public async Task OneConnectionControlsTwoThreadsAndDeletingOneCancelsOnlyItsWork(string transport)
    {
        if (transport == "ssh" && !OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var host = await BrowserHost.StartAsync(directory, transport, timeout.Token);
        var first = await host.Client.GetThreadAsync(host.Thread, timeout.Token);
        var second = await host.Client.CreateThreadAsync(new(first.ProjectId), timeout.Token);
        await using var a = await host.Client.OpenBrowserAutomationAsync(new(host.Thread, BrowserAutomationAccess.Interact), timeout.Token);
        var requestA = host.WriteRequest("resize");
        var workA = await NextAsync(a, timeout.Token);
        await using var b = await host.Client.OpenBrowserAutomationAsync(new(second.ThreadId, BrowserAutomationAccess.Inspect), timeout.Token);
        var requestB = host.WriteRequest("snapshot", second.ThreadId);
        var workB = await NextAsync(b, timeout.Token);
        Assert.Equal(requestB.Id, workB.Request.Id);
        Assert.False(workA.CancellationToken.IsCancellationRequested);
        await host.Client.DeleteThreadAsync(new(second.ThreadId), timeout.Token);
        await UntilAsync(() => workB.CancellationToken.IsCancellationRequested, timeout.Token);
        Assert.True(a.IsActive, a.Error);
        Assert.False(workA.CancellationToken.IsCancellationRequested);
        await a.CompleteAsync(workA, new(true));
        Assert.True((await host.ReadResultAsync(requestA.Id, timeout.Token)).Success);
        Assert.False((await host.ReadResultAsync(requestB.Id, second.ThreadId, timeout.Token)).Success);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("https")]
    [InlineData("ssh")]
    public async Task AllOperationsAndLargeScreenshotsCrossAuthenticatedListeners(string transport)
    {
        if (transport == "ssh" && !OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var host = await BrowserHost.StartAsync(directory, transport, timeout.Token);
        await using var session = await host.Client.OpenBrowserAutomationAsync(new(host.Thread, BrowserAutomationAccess.Interact), timeout.Token);
        foreach (var operation in new[] { "status", "open", "resize", "set_appearance", "snapshot", "navigate", "click", "type", "press_key", "scroll", "wait", "screenshot" })
        {
            var request = host.WriteRequest(operation);
            var work = await NextAsync(session, timeout.Token);
            Assert.Equal(request.Id, work.Request.Id);
            Assert.Equal("background-tab", work.Request.Input.GetProperty("tabId").GetString());
            byte[]? png = null;
            if (operation == "screenshot")
            {
                // Exercise transport at the full image limit, beyond attachment and small hub defaults.
                png = RandomNumberGenerator.GetBytes(BrowserAutomationLimits.MaximumScreenshotBytes);
                new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
            }
            await session.CompleteAsync(work, new(true, JsonSerializer.SerializeToElement(new { ok = true, tabId = "background-tab" }), ScreenshotPng: png));
            var result = await host.ReadResultAsync(request.Id, timeout.Token);
            Assert.True(result.Success);
            if (png is null) Assert.Null(result.ScreenshotPng);
            else Assert.Equal<byte>(png, result.ScreenshotPng!);
            Assert.Null(session.TakeNext());
        }
    }

    [Fact]
    public async Task PiCancellationAndReconnectCancelWorkWithoutReplay()
    {
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var host = await BrowserHost.StartAsync(directory, "https", timeout.Token);
        var access = new OpenBrowserAutomationRequest(host.Thread, BrowserAutomationAccess.Interact);
        await using var session = await host.Client.OpenBrowserAutomationAsync(access, timeout.Token);
        var cancelled = host.WriteRequest("wait");
        var work = await NextAsync(session, timeout.Token);
        File.Delete(host.RequestPath(cancelled.Id));
        await UntilAsync(() => work.CancellationToken.IsCancellationRequested, timeout.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.CompleteAsync(work, new(true)));
        var interrupted = host.WriteRequest("click");
        var interruptedWork = await NextAsync(session, timeout.Token);
        await host.Client.DisconnectAsync(timeout.Token);
        await UntilAsync(() => interruptedWork.CancellationToken.IsCancellationRequested, timeout.Token);
        await session.DisposeAsync();
        await host.Client.ConnectAsync(timeout.Token);
        var failed = await host.ReadResultAsync(interrupted.Id, timeout.Token);
        Assert.False(failed.Success);
        await using var replacement = await host.Client.OpenBrowserAutomationAsync(access, timeout.Token);
        var fresh = host.WriteRequest("status");
        Assert.Equal(fresh.Id, (await NextAsync(replacement, timeout.Token)).Request.Id);
    }

    [Fact]
    public async Task ReadOnlyCannotGrantPollCompleteOrCloseAndRevocationStopsController()
    {
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var host = await BrowserHost.StartAsync(directory, "https", timeout.Token);
        var readOnly = host.Access!.IssueSession(RemoteAccessLevel.ReadOnly);
        await using var observer = new EnvironmentClient(host.Options with { BearerCredential = readOnly.Token });
        await observer.ConnectAsync(timeout.Token);
        Assert.DoesNotContain("browser.automation", observer.Descriptor!.Capabilities);
        await Assert.ThrowsAsync<NotSupportedException>(() => observer.OpenBrowserAutomationAsync(new(host.Thread, BrowserAutomationAccess.Inspect), timeout.Token));
        // Call the hub directly to verify enforcement cannot be bypassed by skipping client checks.
        await using var raw = new HubConnectionBuilder().WithUrl(host.Options.HubAddress, o =>
        {
            o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            o.AccessTokenProvider = () => Task.FromResult<string?>(readOnly.Token);
            o.HttpMessageHandlerFactory = _ => RemoteTransport.CreateHandler(host.Options.CertificateFingerprint);
        }).AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, ProtocolJsonContext.Default)).Build();
        await raw.StartAsync(timeout.Token);
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => raw.InvokeAsync<BrowserAutomationLease>("OpenBrowserAutomation", new OpenBrowserAutomationRequest(host.Thread, BrowserAutomationAccess.Inspect), timeout.Token));
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => raw.InvokeAsync<BrowserAutomationPoll>("PollBrowserAutomation", "lease", timeout.Token));
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => raw.InvokeAsync("CompleteBrowserAutomation", "lease", "request", new BrowserAutomationResult(true), timeout.Token));
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => raw.InvokeAsync("CloseBrowserAutomation", "lease", timeout.Token));
        await using var session = await host.Client.OpenBrowserAutomationAsync(new(host.Thread, BrowserAutomationAccess.Interact), timeout.Token);
        var request = host.WriteRequest("click");
        var work = await NextAsync(session, timeout.Token);
        host.Access.Revoke(host.DeviceId!);
        await UntilAsync(() => work.CancellationToken.IsCancellationRequested, timeout.Token);
        Assert.False((await host.ReadResultAsync(request.Id, timeout.Token)).Success);
    }

    [Fact]
    public async Task ASecondDesktopCannotTakeOverUntilTheThreadControllerIsClosed()
    {
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var host = await BrowserHost.StartAsync(directory, "local", timeout.Token);
        await using var session = await host.Client.OpenBrowserAutomationAsync(new(host.Thread, BrowserAutomationAccess.Inspect), timeout.Token);
        await using var other = new EnvironmentClient(host.Options);
        await other.ConnectAsync(timeout.Token);
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => other.OpenBrowserAutomationAsync(new(host.Thread, BrowserAutomationAccess.Interact), timeout.Token));
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => other.OpenBrowserAutomationAsync(new(ThreadId.New(), BrowserAutomationAccess.Interact), timeout.Token));
        var request = host.WriteRequest("status");
        var work = await NextAsync(session, timeout.Token);
        var project = (await host.Client.ListProjectsAsync(timeout.Token)).Single();
        var thread = await host.Client.CreateThreadAsync(new(project.ProjectId), timeout.Token);
        await using var next = await host.Client.OpenBrowserAutomationAsync(new(thread.ThreadId, BrowserAutomationAccess.Inspect), timeout.Token);
        Assert.True(session.IsActive, session.Error);
        Assert.False(work.CancellationToken.IsCancellationRequested);
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => other.OpenBrowserAutomationAsync(new(host.Thread, BrowserAutomationAccess.Interact), timeout.Token));
        await session.DisposeAsync();
        await UntilAsync(() => work.CancellationToken.IsCancellationRequested, timeout.Token);
        Assert.False((await host.ReadResultAsync(request.Id, timeout.Token)).Success);
        Assert.False(File.Exists(Path.Combine(host.ThreadDirectory, "permission.json")));
        await using var takeover = await other.OpenBrowserAutomationAsync(new(host.Thread, BrowserAutomationAccess.Inspect), timeout.Token);
        Assert.True(takeover.IsActive);
        Assert.True(next.IsActive, next.Error);
    }

    private static async Task<BrowserAutomationWork> NextAsync(BrowserAutomationSession session, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (session.TakeNext() is { } work) return work;
            Assert.True(session.IsActive, session.Error);
            await Task.Delay(20, token);
        }
    }

    private static async Task UntilAsync(Func<bool> done, CancellationToken token)
    {
        while (!done()) await Task.Delay(20, token);
    }

    private sealed class BrowserHost : IAsyncDisposable
    {
        private EmbeddedEnvironmentHost? _local;
        private RemoteEnvironmentHost? _remote;
        private SshEnvironmentHost? _ssh;
        private X509Certificate2? _certificate;
        internal RemoteAccessStore? Access { get; private set; }
        internal string? DeviceId { get; private set; }
        internal EnvironmentClient Client { get; private set; } = null!;
        internal ClientRuntimeOptions Options { get; private set; } = null!;
        internal ThreadId Thread { get; private set; }
        internal string ThreadDirectory { get; private set; } = null!;
        internal PausingProcessFactory Factory { get; private set; } = null!;

        internal static async Task<BrowserHost> StartAsync(ClientTestDirectory directory, string transport, CancellationToken token)
        {
            var host = new BrowserHost();
            var options = directory.CreateHostOptions() with { BrowserAutomationRoot = directory.CreateDirectory("host-inbox") };
            host.Factory = new(new PiProcessFactory(options));
            try
            {
                if (transport == "ssh")
                {
                    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                    host._ssh = await SshEnvironmentHost.StartAsync(options, host.Factory, cancellationToken: token);
                    host.Options = new() { HubAddress = new($"https://127.0.0.1:{host._ssh.Info.Port}/environment"), BearerCredential = host._ssh.Info.BearerCredential, CertificateFingerprint = host._ssh.Info.CertificateFingerprint };
                }
                else
                {
                    host._local = await EmbeddedEnvironmentHost.StartAsync(options, host.Factory, cancellationToken: token);
                    host.Options = new() { HubAddress = host._local.HubAddress, BearerCredential = host._local.BearerCredential };
                    if (transport == "https")
                    {
                        host.Access = new RemoteAccessStore(Path.Combine(directory.CreateDirectory("access"), "access.db"));
                        using var key = RSA.Create(2048);
                        var request = new CertificateRequest("CN=Browser test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
                        host._certificate = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
                        host._remote = await RemoteEnvironmentHost.StartAsync(host._local.Environment, host.Access, IPAddress.Loopback, 0, host._certificate, cancellationToken: token);
                        var issued = host.Access.IssueSession();
                        host.DeviceId = issued.Device.DeviceId;
                        host.Options = new() { HubAddress = new(host._remote.Address, "/environment"), BearerCredential = issued.Token, CertificateFingerprint = host._certificate.GetCertHashString(HashAlgorithmName.SHA256) };
                    }
                }
                host.Client = new(host.Options);
                await host.Client.ConnectAsync(token);
                var project = await host.Client.AddProjectAsync(new(directory.CreateDirectory("project")), token);
                host.Thread = (await host.Client.CreateThreadAsync(new(project.ProjectId), token)).ThreadId;
                host.ThreadDirectory = Path.Combine(options.BrowserAutomationRoot, host.Thread.Value);
                return host;
            }
            catch { await host.DisposeAsync(); throw; }
        }

        internal string RequestPath(string id) => Path.Combine(ThreadDirectory, "requests", id + ".json");
        internal BrowserAutomationRequest WriteRequest(string operation, ThreadId? threadId = null)
        {
            var threadDirectory = threadId is null ? ThreadDirectory : Path.Combine(Path.GetDirectoryName(ThreadDirectory)!, threadId.Value.Value);
            using var permission = JsonDocument.Parse(File.ReadAllText(Path.Combine(threadDirectory, "permission.json")));
            var request = new BrowserAutomationRequest(Guid.NewGuid().ToString("D"), operation,
                JsonSerializer.SerializeToElement(new { tabId = "background-tab", selector = "button", value = "hello", key = "Enter", url = "http://localhost:5173", condition = "loaded", mode = "fill", colorScheme = "dark" }),
                DateTimeOffset.UtcNow, permission.RootElement.GetProperty("controllerId").GetString());
            var path = Path.Combine(threadDirectory, "requests", request.Id + ".json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(request, ProtocolJsonContext.Default.BrowserAutomationRequest));
            File.Move(path + ".tmp", path);
            return request;
        }

        internal Task<BrowserAutomationResult> ReadResultAsync(string id, CancellationToken token) => ReadResultAsync(id, null, token);

        internal async Task<BrowserAutomationResult> ReadResultAsync(string id, ThreadId? threadId, CancellationToken token)
        {
            var threadDirectory = threadId is null ? ThreadDirectory : Path.Combine(Path.GetDirectoryName(ThreadDirectory)!, threadId.Value.Value);
            var path = Path.Combine(threadDirectory, "responses", id + ".json");
            await UntilAsync(() => File.Exists(path), token);
            return JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, token), ProtocolJsonContext.Default.BrowserAutomationResult)!;
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

    private sealed class PausingProcessFactory(IPiProcessFactory inner) : IPiProcessFactory
    {
        internal bool Pause { get; set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PiStation.PiRpc.Process.PiProcess> StartAsync(ProjectDescriptor project,
            PiStation.Host.Persistence.HostThreadRecord thread, CancellationToken cancellationToken = default)
        {
            if (Pause)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return await inner.StartAsync(project, thread, cancellationToken);
        }
    }
}
