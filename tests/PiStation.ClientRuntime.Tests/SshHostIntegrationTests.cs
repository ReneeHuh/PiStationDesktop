using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using PiStation.ClientRuntime.Ssh;
using PiStation.Host.Hosting;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.ClientRuntime.Tests;

public sealed class SshHostIntegrationTests
{
    [Fact]
    public async Task SshReusesDesktopProjectsWithoutOwningItAndHeadlessRestartKeepsTheSameEnvironment()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var options = directory.CreateHostOptions();
        SshHostInfo first;
        await using (var desktop = await EmbeddedEnvironmentHost.StartAsync(options, cancellationToken: timeout.Token))
        {
            await using var local = new EnvironmentClient(new() { HubAddress = desktop.HubAddress, BearerCredential = desktop.BearerCredential });
            await local.ConnectAsync(timeout.Token);
            var project = await local.AddProjectAsync(new(directory.CreateDirectory("desktop-project")), timeout.Token);
            var profile = new SshConnectionProfile(Guid.NewGuid(), "Shared desktop", "unused", string.Empty,
                options.ApplicationDataRoot, @"C:\not-installed\pi.exe", null, ClientId.New());
            await using var attach = new OwnedProcess(SshCommands.Control(profile).ArgumentList[^1], SshRunningHostDiscovery.EncodedScript(profile));
            first = (await ManagedSshConnection.ReadHandshakeAsync(attach.Process.StandardOutput, timeout.Token))!;
            if (first is null) throw new InvalidOperationException("The attach fixture exited before discovery: " + await attach.Errors);
            Assert.False(first.StartedByConnection);
            Assert.Equal("desktop", first.HostKind);
            Assert.Equal(desktop.Environment.EnvironmentId, first.EnvironmentId);
            await using var remote = new EnvironmentClient(ClientOptions(first));
            await remote.ConnectAsync(timeout.Token);
            Assert.Equal(project.ProjectId, Assert.Single(await remote.ListProjectsAsync(timeout.Token)).ProjectId);
            await remote.AddProjectAsync(new(directory.CreateDirectory("ssh-project")), timeout.Token);
            Assert.Equal(2, (await local.ListProjectsAsync(timeout.Token)).Count);
            await attach.StopAsync(timeout.Token);
            Assert.NotNull(await SshEnvironmentHost.TryDiscoverAsync(options.ApplicationDataRoot, timeout.Token));
            Assert.Equal(2, (await local.ListProjectsAsync(timeout.Token)).Count);
        }
        await using var headless = await SshEnvironmentHost.StartAsync(options, cancellationToken: timeout.Token);
        Assert.Equal(first.EnvironmentId, headless.Info.EnvironmentId);
        Assert.Equal(first.CertificateFingerprint, headless.Info.CertificateFingerprint);
        Assert.Equal(first.BearerCredential, headless.Info.BearerCredential);
        await using var reopened = new EnvironmentClient(ClientOptions(headless.Info));
        await reopened.ConnectAsync(timeout.Token);
        Assert.Equal(2, (await reopened.ListProjectsAsync(timeout.Token)).Count);
    }

    [Fact]
    public async Task DiscoveryOfStoppedHostFailsWithoutCreatingDataOrLaunchingSavedExecutable()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var root = Path.Combine(directory.Path, "Not started's data");
        // A legacy profile can still name a valid executable. Discovery must not invoke it.
        var profile = new SshConnectionProfile(Guid.NewGuid(), "Stopped host", "unused", FindServerExecutable(),
            root, null, null, ClientId.New());
        await using var discovery = new OwnedProcess(SshCommands.Control(profile).ArgumentList[^1], SshRunningHostDiscovery.EncodedScript(profile));
        var authenticated = false;
        var info = await ManagedSshConnection.ReadHandshakeAsync(discovery.Process.StandardOutput, timeout.Token, authenticated: () => authenticated = true);
        Assert.True(authenticated);
        await discovery.Process.WaitForExitAsync(timeout.Token);
        Assert.Null(info);
        Assert.Equal(1, discovery.Process.ExitCode);
        Assert.Contains("PISTATION_HOST_NOT_RUNNING", await discovery.Errors, StringComparison.Ordinal);
        Assert.False(Directory.Exists(root));

        // Starting PiStation manually makes the same connection work; leaving the connection
        // must not stop that host or disturb its data.
        await using var host = await SshEnvironmentHost.StartAsync(directory.CreateHostOptions() with { ApplicationDataRoot = root }, cancellationToken: timeout.Token);
        await using var retry = new OwnedProcess(SshCommands.Control(profile).ArgumentList[^1], SshRunningHostDiscovery.EncodedScript(profile));
        var connected = await ManagedSshConnection.ReadHandshakeAsync(retry.Process.StandardOutput, timeout.Token);
        Assert.NotNull(connected);
        Assert.False(connected.StartedByConnection);
        Assert.Equal(host.Info.EnvironmentId, connected.EnvironmentId);
        await retry.StopAsync(timeout.Token);
        Assert.NotNull(await SshEnvironmentHost.TryDiscoverAsync(root, timeout.Token));
    }

    [Fact]
    public async Task SignalRAutomaticallyRebuildsDroppedForwardWithoutChangingClientOrEnvironment()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var host = await SshEnvironmentHost.StartAsync(directory.CreateHostOptions(), cancellationToken: timeout.Token);
        var hostInfo = host.Info;
        var profile = new SshConnectionProfile(Guid.NewGuid(), "Forward test", "unused", "PiStation.Server.exe",
            null, null, hostInfo.EnvironmentId, ClientId.New());
        var forwards = new List<TestForwardProcess>();
        var controlStarts = 0;
        await using var transport = new ManagedSshConnection(profile, command =>
        {
            if (!command.ArgumentList.Contains("-N")) { controlStarts++; return new TestControlProcess(hostInfo); }
            var mapping = command.ArgumentList[command.ArgumentList.IndexOf("-L") + 1].Split(':');
            var forward = new TestForwardProcess(int.Parse(mapping[1], System.Globalization.CultureInfo.InvariantCulture), hostInfo.Port);
            forwards.Add(forward);
            return forward;
        }, async (address, info, token) =>
        {
            using var http = new HttpClient(RemoteTransport.CreateHandler(info.CertificateFingerprint));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", info.BearerCredential);
            using var response = await http.GetAsync(new Uri(address, "/ssh/health"), token);
            response.EnsureSuccessStatusCode();
        });
        await transport.EnsureConnectedAsync(timeout.Token);
        await using var client = new EnvironmentClient(transport.CreateOptions());
        var connectedCount = 0;
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += (_, args) =>
        {
            if (args.State == EnvironmentConnectionState.Connected && Interlocked.Increment(ref connectedCount) == 2)
                restored.TrySetResult();
        };
        await client.ConnectAsync(timeout.Token);
        var project = await client.AddProjectAsync(new(directory.CreateDirectory("forward-project")), timeout.Token);
        await forwards[0].DisposeAsync();
        await restored.Task.WaitAsync(timeout.Token);
        Assert.Equal(2, forwards.Count);
        Assert.Equal(1, controlStarts);
        Assert.Equal(project.ProjectId, Assert.Single(await client.ListProjectsAsync(timeout.Token)).ProjectId);
        Assert.Equal(profile.ClientId, transport.CreateOptions().ClientId);
        Assert.Equal(host.Info.EnvironmentId, client.Descriptor!.EnvironmentId);
    }

    [Fact]
    public async Task WindowsJobClosesOnlyItsCapturedSshProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
        };
        foreach (var value in new[] { "-NoProfile", "-NonInteractive", "-Command", "[System.Threading.Thread]::Sleep(-1)" }) start.ArgumentList.Add(value);
        using var process = Process.Start(start)!;
        try
        {
            using var job = SshProcessJob.Attach(process);
            Assert.False(process.HasExited);
            job.Dispose();
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.HasExited);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    [Fact]
    public async Task HeadlessHostIsPinnedAuthenticatedDiscoverableAndPersistsIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var options = directory.CreateHostOptions();
        SshHostInfo first;
        await using (var host = await SshEnvironmentHost.StartAsync(options, cancellationToken: timeout.Token))
        {
            first = host.Info;
            var discovered = await SshEnvironmentHost.TryDiscoverAsync(options.ApplicationDataRoot, timeout.Token);
            Assert.Equal(first, discovered);
            await Assert.ThrowsAsync<IOException>(() => EmbeddedEnvironmentHost.StartAsync(options, cancellationToken: timeout.Token));
            var address = new Uri($"https://127.0.0.1:{first.Port}/");
            using var http = new HttpClient(RemoteTransport.CreateHandler(first.CertificateFingerprint));
            using var denied = await http.GetAsync(new Uri(address, "ssh/health"), timeout.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first.BearerCredential);
            using var ready = await http.GetAsync(new Uri(address, "ssh/health"), timeout.Token);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            await using var client = new EnvironmentClient(ClientOptions(first));
            await client.ConnectAsync(timeout.Token);
            Assert.Contains("remote.access", client.Descriptor!.Capabilities);
            Assert.DoesNotContain("editor.open", client.Descriptor.Capabilities);
            Assert.Contains("preview.discover", client.Descriptor.Capabilities);
            var project = await client.AddProjectAsync(new(directory.CreateDirectory("ssh-project")), timeout.Token);
            Assert.Equal(project.ProjectId, Assert.Single(await client.ListProjectsAsync(timeout.Token)).ProjectId);
            await using var wrongPin = new EnvironmentClient(ClientOptions(first) with { CertificateFingerprint = new string('0', 64) });
            await Assert.ThrowsAnyAsync<Exception>(() => wrongPin.ConnectAsync(timeout.Token));
        }
        Assert.Null(await SshEnvironmentHost.TryDiscoverAsync(options.ApplicationDataRoot, timeout.Token));
        await using var restarted = await SshEnvironmentHost.StartAsync(options, cancellationToken: timeout.Token);
        Assert.Equal(first.EnvironmentId, restarted.Info.EnvironmentId);
        Assert.Equal(first.BearerCredential, restarted.Info.BearerCredential);
        Assert.Equal(first.CertificateFingerprint, restarted.Info.CertificateFingerprint);
        await using var restoredClient = new EnvironmentClient(ClientOptions(restarted.Info));
        await restoredClient.ConnectAsync(timeout.Token);
        Assert.Single(await restoredClient.ListProjectsAsync(timeout.Token));
    }

    [Fact]
    public async Task WindowsPowerShellBootstrapStartsHostAndStopsOnlyItsOwnedProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var options = directory.CreateHostOptions() with { ApplicationDataRoot = directory.CreateDirectory("SSH host's data with spaces") };
        var profile = new SshConnectionProfile(Guid.NewGuid(), "CLI test", "unused",
            FindServerExecutable(), options.ApplicationDataRoot, options.PiInstallation!.ExecutablePath, null, ClientId.New());
        // Keep coverage of the deferred startup implementation, explicitly outside the current flow.
        var command = SshCommands.Control(profile, allowHostStartup: true);
        // Exercise exactly the encoded Windows bootstrap, without enabling sshd/firewall or
        // depending on the developer's SSH keys. Real cross-machine SSH remains a manual check.
        await using var owner = new OwnedProcess(command.ArgumentList[^1]);
        var first = await ManagedSshConnection.ReadHandshakeAsync(owner.Process.StandardOutput, timeout.Token);
        Assert.NotNull(first);
        Assert.True(first.StartedByConnection);
        await using (var reused = new OwnedProcess(command.ArgumentList[^1]))
        {
            var second = await ManagedSshConnection.ReadHandshakeAsync(reused.Process.StandardOutput, timeout.Token);
            Assert.NotNull(second);
            Assert.False(second.StartedByConnection);
            Assert.Equal(first.EnvironmentId, second.EnvironmentId);
            await reused.StopAsync(timeout.Token);
            Assert.False(owner.Process.HasExited);
            await using var client = new EnvironmentClient(ClientOptions(first));
            await client.ConnectAsync(timeout.Token);
        }
        await owner.StopAsync(timeout.Token);
        Assert.Null(await SshEnvironmentHost.TryDiscoverAsync(options.ApplicationDataRoot, timeout.Token));
        await using var restarted = new OwnedProcess(command.ArgumentList[^1]);
        var third = await ManagedSshConnection.ReadHandshakeAsync(restarted.Process.StandardOutput, timeout.Token);
        Assert.NotNull(third);
        Assert.Equal(first.EnvironmentId, third.EnvironmentId);
        Assert.Equal(first.BearerCredential, third.BearerCredential);
        await restarted.StopAsync(timeout.Token);
    }

    private static ClientRuntimeOptions ClientOptions(SshHostInfo info) => new()
    {
        HubAddress = new Uri($"https://127.0.0.1:{info.Port}/environment"), BearerCredential = info.BearerCredential,
        CertificateFingerprint = info.CertificateFingerprint, ExpectedEnvironmentId = info.EnvironmentId,
    };

    private static string FindServerExecutable()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "PiStationDesktop.slnx"))) continue;
            var configuration = AppContext.BaseDirectory.Contains("Release", StringComparison.Ordinal) ? "Release" : "Debug";
            return Path.Combine(directory.FullName, "src", "PiStation.Server", "bin", configuration, "net10.0", "PiStation.Server.exe");
        }
        throw new DirectoryNotFoundException("Solution root was not found.");
    }

    private sealed class OwnedProcess : IAsyncDisposable
    {
        private readonly Task<string> _error;
        public Task<string> Errors => _error;
        public OwnedProcess(string encodedCommand, string? discoveryScript = null)
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var value in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", encodedCommand }) start.ArgumentList.Add(value);
            Process = Process.Start(start)!;
            _error = Process.StandardError.ReadToEndAsync();
            if (discoveryScript is not null)
            {
                Process.StandardInput.WriteLine(discoveryScript);
                Process.StandardInput.Flush();
            }
        }
        public Process Process { get; }
        public async Task StopAsync(CancellationToken token)
        {
            if (!Process.HasExited)
            {
                await Process.StandardInput.WriteLineAsync("stop");
                Process.StandardInput.Close();
                await Process.WaitForExitAsync(token);
            }
            Assert.Equal(0, Process.ExitCode);
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    await Process.WaitForExitAsync();
                }
                await _error;
            }
            finally { Process.Dispose(); }
        }
    }

    private sealed class TestControlProcess(SshHostInfo info) : ISshProcess
    {
        public TextWriter Input { get; } = new StringWriter();
        public TextReader Output { get; } = new StringReader("PISTATION_SSH " + JsonSerializer.Serialize(info, ProtocolJsonContext.Default.SshHostInfo) + "\n");
        public bool HasExited { get; private set; }
        public string FailureMessage => "Test control exited.";
        public ValueTask DisposeAsync() { HasExited = true; Output.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class TestForwardProcess : ISshProcess
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _accept;
        private readonly List<Task> _copies = [];
        private readonly int _remotePort;
        public TestForwardProcess(int localPort, int remotePort)
        {
            _remotePort = remotePort;
            _listener = new TcpListener(IPAddress.Loopback, localPort);
            _listener.Start();
            _accept = AcceptAsync();
        }
        public TextReader Output { get; } = new StringReader("");
        public bool HasExited { get; private set; }
        public string FailureMessage => "Test forward exited.";

        private async Task AcceptAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                    _copies.Add(CopyAsync(client));
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (_lifetime.IsCancellationRequested) { }
        }

        private async Task CopyAsync(TcpClient inbound)
        {
            using (inbound)
            using (var outbound = new TcpClient())
            {
                try
                {
                    await outbound.ConnectAsync(IPAddress.Loopback, _remotePort, _lifetime.Token);
                    var outgoing = inbound.GetStream().CopyToAsync(outbound.GetStream(), _lifetime.Token);
                    var incoming = outbound.GetStream().CopyToAsync(inbound.GetStream(), _lifetime.Token);
                    await Task.WhenAny(outgoing, incoming);
                    inbound.Close(); outbound.Close();
                    await Task.WhenAll(outgoing, incoming);
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (HasExited) return;
            HasExited = true;
            await _lifetime.CancelAsync();
            _listener.Stop();
            await _accept;
            await Task.WhenAll(_copies);
            Output.Dispose();
            _lifetime.Dispose();
        }
    }
}
