using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PiStation.ClientRuntime.Ssh;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.ClientRuntime.Tests;

public sealed class ManagedSshConnectionTests
{
    private static SshConnectionProfile Profile() => new(Guid.NewGuid(), "Windows workstation", "user@workstation",
        @"C:\Host's folder\PiStation.Server.exe", @"C:\PiStation data", null, null, ClientId.New());
    private static SshHostInfo Info() => new(SshHostInfo.CurrentBootstrapVersion, Protocol.ProtocolVersion.Current,
        EnvironmentId.New(), "Host", 52742, new string('A', 64), new string('B', 64), true);

    [Theory]
    [InlineData("-oProxyCommand=bad")]
    [InlineData("host;whoami")]
    [InlineData("host\nother")]
    [InlineData("ssh://user@host")]
    [InlineData("user host")]
    public void RejectsTargetsThatCouldBecomeOptionsOrShellCode(string target) =>
        Assert.Throws<ArgumentException>(() => (Profile() with { Target = target }).Validate());

    [Fact]
    public void CommandsUseDirectOpenSshStrictTrustAndEncodedLiteralWindowsPaths()
    {
        var profile = Profile();
        var control = SshCommands.Control(profile);
        Assert.Equal("ssh.exe", control.FileName);
        Assert.False(control.UseShellExecute);
        Assert.True(control.CreateNoWindow);
        Assert.Contains("BatchMode=yes", control.ArgumentList);
        Assert.Contains("StrictHostKeyChecking=yes", control.ArgumentList);
        Assert.Contains("ForwardAgent=no", control.ArgumentList);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(control.ArgumentList[^1]));
        Assert.Contains(@"& 'C:\Host''s folder\PiStation.Server.exe' 'attach'", script, StringComparison.Ordinal);
        Assert.Contains(@"'--data-root' 'C:\PiStation data'", script, StringComparison.Ordinal);
        var forward = SshCommands.Forward(profile, 32123, 52742);
        Assert.Contains("127.0.0.1:32123:127.0.0.1:52742", forward.ArgumentList);
        Assert.Contains("ExitOnForwardFailure=yes", forward.ArgumentList);
        Assert.DoesNotContain("-g", forward.ArgumentList);
    }

    [Fact]
    public async Task ParsesBoundedHandshakeWithoutLoggingCredentials()
    {
        var info = Info();
        using var reader = new StringReader("SSH banner\r\n" + Handshake(info));
        Assert.Equal(info, await ManagedSshConnection.ReadHandshakeAsync(reader, CancellationToken.None));
        Assert.DoesNotContain(info.BearerCredential, info.ToString(), StringComparison.Ordinal);
        using var oversized = new StringReader(new string('x', 32769));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ManagedSshConnection.ReadHandshakeAsync(oversized, CancellationToken.None));
    }

    [Fact]
    public async Task ReusesTransportAndRebuildsDroppedTunnelWithStableIdentityAndPort()
    {
        var profile = Profile();
        var info = Info();
        var spawned = new List<FakeProcess>();
        var commands = new List<ProcessStartInfo>();
        await using var connection = new ManagedSshConnection(profile, start =>
        {
            commands.Add(start);
            var process = new FakeProcess(start.ArgumentList.Contains("-N") ? "" : Handshake(info));
            spawned.Add(process);
            return process;
        }, (_, _, _) => Task.CompletedTask);
        await connection.EnsureConnectedAsync();
        var options = connection.CreateOptions();
        Assert.Equal(info.EnvironmentId, options.ExpectedEnvironmentId);
        Assert.Equal(info.CertificateFingerprint, options.CertificateFingerprint);
        await connection.EnsureConnectedAsync();
        Assert.Equal(2, spawned.Count);
        spawned[1].HasExited = true;
        await options.EnsureTransportAsync!(CancellationToken.None);
        Assert.Equal(3, spawned.Count);
        Assert.False(spawned[0].Disposed);
        Assert.True(spawned[1].Disposed);
        Assert.Equal(commands[1].ArgumentList, commands[2].ArgumentList);
        await connection.DisposeAsync();
        Assert.All(spawned, process => Assert.True(process.Disposed));
    }

    [Fact]
    public async Task RejectsSwappedEnvironmentBeforeOpeningForwardAndCleansOwnedControl()
    {
        var info = Info();
        var process = new FakeProcess(Handshake(info));
        var spawned = 0;
        await using var connection = new ManagedSshConnection(Profile() with { ExpectedEnvironmentId = EnvironmentId.New() },
            _ => { spawned++; return process; }, (_, _, _) => Task.CompletedTask);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.EnsureConnectedAsync());
        Assert.Contains("identity changed", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, spawned);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task RecreatesControlAfterItExitsButRetainsLocalAddress()
    {
        var info = Info();
        var processes = new List<FakeProcess>();
        await using var connection = new ManagedSshConnection(Profile(), start =>
        {
            var process = new FakeProcess(start.ArgumentList.Contains("-N") ? "" : Handshake(info));
            processes.Add(process);
            return process;
        }, (_, _, _) => Task.CompletedTask);
        await connection.EnsureConnectedAsync();
        var address = connection.Address;
        processes[0].HasExited = true;
        await connection.EnsureConnectedAsync();
        Assert.Equal(4, processes.Count);
        Assert.True(processes[0].Disposed);
        Assert.True(processes[1].Disposed);
        Assert.Equal(address, connection.Address);
    }

    [Fact]
    public async Task FailedForwardRepairDoesNotStopHealthyHost()
    {
        var info = Info();
        var processes = new List<FakeProcess>();
        await using var connection = new ManagedSshConnection(Profile(), start =>
        {
            var process = new FakeProcess(start.ArgumentList.Contains("-N") ? "" : Handshake(info)) { HasExited = processes.Count == 2 };
            processes.Add(process);
            return process;
        }, (_, _, _) => Task.CompletedTask);
        await connection.EnsureConnectedAsync();
        processes[1].HasExited = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.EnsureConnectedAsync());
        Assert.False(processes[0].Disposed);
        Assert.True(processes[2].Disposed);
        await connection.DisposeAsync();
        Assert.True(processes[0].Disposed);
    }

    [Fact]
    public async Task RediscoversAStoppedExternalDesktopInsteadOfRetryingItsStalePort()
    {
        var info = Info() with { StartedByConnection = false };
        var processes = new List<FakeProcess>();
        var externalStopped = false;
        await using var connection = new ManagedSshConnection(Profile(), start =>
        {
            if (!start.ArgumentList.Contains("-N") && processes.Count > 0) externalStopped = false;
            var process = new FakeProcess(start.ArgumentList.Contains("-N") ? "" : Handshake(info));
            processes.Add(process);
            return process;
        }, (_, _, _) => externalStopped ? Task.FromException(new HttpRequestException("Host closed.")) : Task.CompletedTask);
        await connection.EnsureConnectedAsync();
        externalStopped = true;
        await connection.EnsureConnectedAsync();
        Assert.Equal(4, processes.Count);
        Assert.True(processes[0].Disposed);
        Assert.True(processes[1].Disposed);
    }

    [Fact]
    public async Task RejectsIncompatibleHostBeforeForwarding()
    {
        var process = new FakeProcess(Handshake(Info() with { BootstrapVersion = 999 }));
        var count = 0;
        await using var connection = new ManagedSshConnection(Profile(), _ => { count++; return process; }, (_, _, _) => Task.CompletedTask);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.EnsureConnectedAsync());
        Assert.Contains("incompatible", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, count);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task CancellationDuringReadinessCleansBothProcesses()
    {
        var info = Info();
        using var cancellation = new CancellationTokenSource();
        var processes = new List<FakeProcess>();
        await using var connection = new ManagedSshConnection(Profile(), start =>
        {
            var process = new FakeProcess(start.ArgumentList.Contains("-N") ? "" : Handshake(info));
            processes.Add(process);
            return process;
        }, (_, _, token) => { cancellation.Cancel(); return Task.FromCanceled(token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.EnsureConnectedAsync(cancellation.Token));
        Assert.Equal(2, processes.Count);
        Assert.All(processes, process => Assert.True(process.Disposed));
    }

    [Fact]
    public void StoresCanonicalSshTargetAndEnvironmentIdentityWithoutEphemeralCredentialsOrPorts()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        var path = Path.Combine(directory.Path, "ssh.protected");
        var profile = Profile() with { ExpectedEnvironmentId = EnvironmentId.New() };
        var store = new SshConnectionStore(path);
        store.Save(profile);
        Assert.Equal(profile, Assert.Single(new SshConnectionStore(path).Load()));
        Assert.DoesNotContain(profile.Target, Encoding.UTF8.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);
        var document = Encoding.UTF8.GetString(WindowsProtectedStorage.Read(path));
        Assert.DoesNotContain("Credential", document, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", document, StringComparison.Ordinal);
        store.Forget(profile.Id);
        Assert.Empty(store.Load());
    }

    private static string Handshake(SshHostInfo info) => "PISTATION_SSH " + JsonSerializer.Serialize(info, ProtocolJsonContext.Default.SshHostInfo) + "\n";

    private sealed class FakeProcess(string output) : ISshProcess
    {
        public TextReader Output { get; } = new StringReader(output);
        public bool HasExited { get; set; }
        public bool Disposed { get; private set; }
        public string FailureMessage => "Fake SSH failure.";
        public ValueTask DisposeAsync() { Disposed = HasExited = true; Output.Dispose(); return ValueTask.CompletedTask; }
    }
}
