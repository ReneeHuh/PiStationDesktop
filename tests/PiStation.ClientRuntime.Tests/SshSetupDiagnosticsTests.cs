using System.ComponentModel;
using System.Text.Json;
using PiStation.ClientRuntime.Ssh;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.ClientRuntime.Tests;

public sealed class SshSetupDiagnosticsTests
{
    [Theory]
    [InlineData("REMOTE HOST IDENTIFICATION HAS CHANGED", false, SshSetupStep.HostKey, "fingerprint")]
    [InlineData("Host key verification failed", false, SshSetupStep.HostKey, "terminal")]
    [InlineData("Bad configuration option", false, SshSetupStep.Client, "configuration")]
    [InlineData("Could not resolve hostname", false, SshSetupStep.HostKey, "spelling")]
    [InlineData("Connection refused", false, SshSetupStep.HostKey, "OpenSSH Server")]
    [InlineData("Connection timed out", false, SshSetupStep.HostKey, "firewall")]
    [InlineData("Permission denied (publickey)", false, SshSetupStep.Authentication, "username")]
    [InlineData("PISTATION_HOST_NOT_RUNNING", false, SshSetupStep.Host, "Start PiStation")]
    [InlineData("PISTATION_HOST_AMBIGUOUS", false, SshSetupStep.Host, "data directory")]
    [InlineData("PISTATION_HOST_WRONG_OWNER", false, SshSetupStep.Host, "account")]
    [InlineData("PISTATION_HOST_DISCOVERY_FAILED", false, SshSetupStep.Host, "starting")]
    [InlineData("powershell.exe is not recognized", false, SshSetupStep.Host, "Windows")]
    [InlineData("Pi installation was not found", false, SshSetupStep.Host, "runtime settings")]
    [InlineData("channel 2: open failed: administratively prohibited", true, SshSetupStep.Tunnel, "PermitOpen")]
    [InlineData("Address already in use", true, SshSetupStep.Tunnel, "local port")]
    [InlineData("Connection refused", true, SshSetupStep.Tunnel, "PiStation")]
    [InlineData("unknown failure", true, SshSetupStep.Tunnel, "retry")]
    public void ClassifiesFailuresWithActionableGuidanceWithoutExposingRemoteOutput(string error, bool forward, SshSetupStep step, string guidance)
    {
        var failure = SshSetupDiagnostics.FromStandardError(error + "\ncredential=secret-never-show", forward);
        Assert.Equal(step, failure.Step);
        Assert.Contains(guidance, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-never-show", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetupChecksReachAuthenticatedPinnedHostWithoutStartingOrSavingAHost()
    {
        var info = Info();
        var checks = new Checks();
        var processes = new List<ProcessStub>();
        await using (var connection = new ManagedSshConnection(Profile(), command =>
        {
            Assert.Contains("StrictHostKeyChecking=yes", command.ArgumentList);
            var process = new ProcessStub(command.ArgumentList.Contains("-N") ? string.Empty : Handshake(info));
            processes.Add(process);
            return process;
        }, (_, observed, _) =>
        {
            Assert.Equal(info, observed);
            Assert.Equal(SshSetupState.Passed, checks.Latest(SshSetupStep.Authentication).State);
            return Task.CompletedTask;
        }, setupProgress: checks))
        {
            await connection.EnsureConnectedAsync();
            Assert.False(connection.Info.StartedByConnection);
            Assert.All(Enum.GetValues<SshSetupStep>(), step => Assert.Equal(SshSetupState.Passed, checks.Latest(step).State));
            Assert.DoesNotContain(info.BearerCredential, string.Join('\n', checks.Items.Select(c => c.DisplayText)), StringComparison.Ordinal);
        }
        Assert.Equal(2, processes.Count);
        Assert.All(processes, process => Assert.True(process.Disposed));
        // The discovery stdin contains only the read-only script and session-close input.
        Assert.DoesNotContain("PISTATION_PACKAGE", processes[0].Input.ToString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingOpenSshStopsAtTheLocalClientCheck()
    {
        var checks = new Checks();
        var probes = 0;
        await using var connection = new ManagedSshConnection(Profile(), _ => throw new Win32Exception(2),
            (_, _, _) => { probes++; return Task.CompletedTask; }, setupProgress: checks);
        await Assert.ThrowsAsync<Win32Exception>(() => connection.EnsureConnectedAsync());
        Assert.Equal(SshSetupState.Failed, checks.Latest(SshSetupStep.Client).State);
        Assert.Contains("Optional features", checks.Latest(SshSetupStep.Client).Message, StringComparison.Ordinal);
        Assert.Equal(SshSetupState.NotChecked, checks.Latest(SshSetupStep.HostKey).State);
        Assert.Equal(0, probes);
    }

    [Fact]
    public async Task UnknownHostKeyDoesNotPromptForAPasswordOrProbeTheTunnel()
    {
        var checks = new Checks();
        var process = new ProcessStub(string.Empty, "Host key verification failed") { HostKeyVerificationFailed = true };
        var prompts = 0;
        var probes = 0;
        await using var connection = new ManagedSshConnection(Profile(), _ => process,
            (_, _, _) => { probes++; return Task.CompletedTask; }, requestPassword: (_, _) => { prompts++; return Task.FromResult<string?>("secret"); }, setupProgress: checks);
        var error = await Assert.ThrowsAsync<ConnectionValidationException>(() => connection.EnsureConnectedAsync());
        Assert.Equal(ConnectionFailure.Identity, error.Failure);
        Assert.Equal(SshSetupState.Failed, checks.Latest(SshSetupStep.HostKey).State);
        Assert.Equal(SshSetupState.NotChecked, checks.Latest(SshSetupStep.Host).State);
        Assert.Equal(0, prompts);
        Assert.Equal(0, probes);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task PasswordRetryClearsTheFailedAttemptAndDoesNotExposeItsSecret()
    {
        var checks = new Checks();
        var calls = 0;
        var prompts = 0;
        await using var connection = new ManagedSshConnection(Profile(), command =>
        {
            calls++;
            return calls == 1 ? new ProcessStub(string.Empty, "Permission denied (publickey)") { AuthenticationFailed = true }
                : new ProcessStub(command.ArgumentList.Contains("-N") ? string.Empty : Handshake(Info()));
        }, (_, _, _) => Task.CompletedTask, requestPassword: (_, _) =>
        {
            prompts++;
            Assert.Equal(SshSetupState.Checking, checks.Latest(SshSetupStep.Authentication).State);
            return Task.FromResult<string?>("setup-password-must-not-appear");
        }, setupProgress: checks);
        await connection.EnsureConnectedAsync();
        Assert.Equal(1, prompts);
        Assert.All(Enum.GetValues<SshSetupStep>(), step => Assert.Equal(SshSetupState.Passed, checks.Latest(step).State));
        Assert.DoesNotContain(checks.Items, check => check.State == SshSetupState.Failed);
        Assert.DoesNotContain("setup-password-must-not-appear", string.Join('\n', checks.Items.Select(c => c.DisplayText)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PISTATION_HOST_NOT_RUNNING")]
    [InlineData("unknown host failure")]
    public async Task FailedHostDiscoveryKeepsSuccessfulAuthenticationAndLeavesTunnelUnchecked(string failure)
    {
        var checks = new Checks();
        var process = new ProcessStub("PISTATION_SSH_AUTHENTICATED\n", failure);
        await using var connection = new ManagedSshConnection(Profile(), _ => process, (_, _, _) => Task.CompletedTask, setupProgress: checks);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.EnsureConnectedAsync());
        Assert.Equal(SshSetupState.Passed, checks.Latest(SshSetupStep.Authentication).State);
        Assert.Equal(SshSetupState.Failed, checks.Latest(SshSetupStep.Host).State);
        Assert.Equal(SshSetupState.NotChecked, checks.Latest(SshSetupStep.Tunnel).State);
        Assert.True(process.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProtocolOrEnvironmentMismatchFailsCompatibilityWithoutOpeningAForward(bool protocolMismatch)
    {
        var checks = new Checks();
        var info = Info() with { ProtocolVersion = protocolMismatch ? 999 : Protocol.ProtocolVersion.Current };
        var spawned = 0;
        await using var connection = new ManagedSshConnection(Profile() with { ExpectedEnvironmentId = protocolMismatch ? info.EnvironmentId : EnvironmentId.New() },
            _ => { spawned++; return new ProcessStub(Handshake(info)); }, (_, _, _) => Task.CompletedTask, setupProgress: checks);
        await Assert.ThrowsAsync<ConnectionValidationException>(() => connection.EnsureConnectedAsync());
        Assert.Equal(SshSetupState.Passed, checks.Latest(SshSetupStep.Host).State);
        Assert.Equal(SshSetupState.Failed, checks.Latest(SshSetupStep.Compatibility).State);
        Assert.Equal(SshSetupState.NotChecked, checks.Latest(SshSetupStep.Tunnel).State);
        Assert.Equal(1, spawned);
    }

    [Fact]
    public async Task LiveForwardingDenialFailsPromptlyAndCleansOnlyItsOwnProcesses()
    {
        var checks = new Checks();
        var processes = new List<ProcessStub>();
        var probes = 0;
        await using var connection = new ManagedSshConnection(Profile(), command =>
        {
            var forward = command.ArgumentList.Contains("-N");
            var process = new ProcessStub(forward ? string.Empty : Handshake(Info()), forward ? "administratively prohibited" : string.Empty)
                { ForwardingFailed = forward };
            processes.Add(process);
            return process;
        }, (_, _, _) => { probes++; return Task.CompletedTask; }, setupProgress: checks);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.EnsureConnectedAsync());
        Assert.Equal(SshSetupState.Failed, checks.Latest(SshSetupStep.Tunnel).State);
        Assert.Contains("PermitOpen", checks.Latest(SshSetupStep.Tunnel).Message, StringComparison.Ordinal);
        Assert.Equal(0, probes);
        Assert.All(processes, process => Assert.True(process.Disposed));
    }

    [Fact]
    public async Task CancellationReportsCanceledTunnelAndDisposesTheCheckConnection()
    {
        var checks = new Checks();
        using var cancellation = new CancellationTokenSource();
        var processes = new List<ProcessStub>();
        await using var connection = new ManagedSshConnection(Profile(), command =>
        {
            var process = new ProcessStub(command.ArgumentList.Contains("-N") ? string.Empty : Handshake(Info()));
            processes.Add(process);
            return process;
        }, (_, _, token) => { cancellation.Cancel(); return Task.FromCanceled(token); }, setupProgress: checks);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.EnsureConnectedAsync(cancellation.Token));
        Assert.Equal(SshSetupState.Canceled, checks.Latest(SshSetupStep.Tunnel).State);
        Assert.All(processes, process => Assert.True(process.Disposed));
    }

    private static SshConnectionProfile Profile() => new(Guid.NewGuid(), "Setup", "user@host", string.Empty, null, null, null, ClientId.New());
    private static SshHostInfo Info() => new(1, Protocol.ProtocolVersion.Current, EnvironmentId.New(), "Host", 23456, new string('A', 64), new string('B', 64), false);
    private static string Handshake(SshHostInfo info) => "PISTATION_SSH_AUTHENTICATED\nPISTATION_SSH " + JsonSerializer.Serialize(info, ProtocolJsonContext.Default.SshHostInfo) + "\n";

    private sealed class Checks : IProgress<SshSetupCheck>
    {
        internal List<SshSetupCheck> Items { get; } = [];
        public void Report(SshSetupCheck value) => Items.Add(value);
        internal SshSetupCheck Latest(SshSetupStep step) => Items.Last(c => c.Step == step);
    }

    private sealed class ProcessStub(string output, string error = "") : ISshProcess
    {
        public TextReader Output { get; } = new StringReader(output);
        public TextWriter Input { get; } = new StringWriter();
        public bool HasExited => Disposed || AuthenticationFailed;
        public bool AuthenticationFailed { get; init; }
        public bool HostKeyVerificationFailed { get; init; }
        public bool ForwardingFailed { get; init; }
        public SshSetupFailure SetupFailure => SshSetupDiagnostics.FromStandardError(error, ForwardingFailed);
        public string FailureMessage => SetupFailure.Message;
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; Output.Dispose(); return ValueTask.CompletedTask; }
    }
}
