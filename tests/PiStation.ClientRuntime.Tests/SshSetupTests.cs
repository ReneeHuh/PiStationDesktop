using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiStation.ClientRuntime.Ssh;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.ClientRuntime.Tests;

public sealed class SshSetupTests
{
    [Fact]
    public async Task DiscoversIncludedAliasesAndKnownHostPortsWithoutPatternsHashesOrRevokedHosts()
    {
        using var directory = new ClientTestDirectory();
        var ssh = directory.CreateDirectory(".ssh");
        Directory.CreateDirectory(Path.Combine(ssh, "conf.d"));
        await File.WriteAllTextAsync(Path.Combine(ssh, "config"), "Host work *.example !no\n Include conf.d/*.conf\nHost=\"quoted-alias\" # comment\nMatch exec unsafe-command\n");
        await File.WriteAllTextAsync(Path.Combine(ssh, "conf.d", "work.conf"), "Host included\nInclude config\n");
        await File.WriteAllTextAsync(Path.Combine(ssh, "known_hosts"), "work,lan ssh-ed25519 key\n[remote]:2222 ssh-ed25519 key\n|1|hashed ssh-ed25519 key\n@revoked bad ssh-ed25519 key\n[::1]:2200 ssh-ed25519 key\n");
        var found = await SshHostDiscovery.DiscoverAsync(directory.Path);
        Assert.Equal(6, found.Count);
        Assert.Contains(found, host => host.Target == "work" && host.Source == "SSH config");
        Assert.Contains(found, host => host.Target == "included");
        Assert.Contains(found, host => host.Target == "quoted-alias");
        Assert.Contains(found, host => host.Target == "remote" && host.Port == 2222);
        Assert.Contains(found, host => host.Target == "::1" && host.Port == 2200);
    }

    [Fact]
    public void PortAndPasswordAreAppliedToBothSshProcessesWithoutWeakeningHostTrust()
    {
        var profile = Profile() with { Port = 2222 };
        const string secret = "password-not-an-argument";
        foreach (var command in new[] { SshCommands.Control(profile, secret), SshCommands.Forward(profile, 30001, 30002, secret) })
        {
            Assert.Contains("BatchMode=no", command.ArgumentList);
            Assert.Contains("StrictHostKeyChecking=yes", command.ArgumentList);
            Assert.Equal("2222", command.ArgumentList[command.ArgumentList.IndexOf("-p") + 1]);
            Assert.DoesNotContain(secret, string.Join(' ', command.ArgumentList), StringComparison.Ordinal);
            Assert.Equal(secret, command.Environment["PISTATION_SSH_AUTH_SECRET"]);
            Assert.Equal("force", command.Environment["SSH_ASKPASS_REQUIRE"]);
        }
        var batch = SshCommands.Control(profile);
        Assert.Contains("BatchMode=yes", batch.ArgumentList);
        Assert.False(batch.Environment.ContainsKey("PISTATION_SSH_AUTH_SECRET"));
    }

    [Fact]
    public async Task PromptsOnlyAfterAuthenticationFailureAndReusesSecretForForwarding()
    {
        var info = Info();
        var calls = 0;
        var prompts = 0;
        var processes = new List<AuthProcess>();
        await using var connection = new ManagedSshConnection(Profile(), command =>
        {
            calls++;
            if (calls == 1) Assert.Contains("BatchMode=yes", command.ArgumentList);
            else Assert.Equal("secret", command.Environment["PISTATION_SSH_AUTH_SECRET"]);
            var process = new AuthProcess(calls == 1 ? string.Empty : Handshake(info), calls == 1);
            processes.Add(process);
            return process;
        }, (_, _, _) => Task.CompletedTask, requestPassword: (request, _) =>
        {
            prompts++;
            Assert.Equal(1, request.Attempt);
            return Task.FromResult<string?>("secret");
        });
        await connection.EnsureConnectedAsync();
        Assert.Equal(3, calls);
        Assert.Equal(1, prompts);
        Assert.True(processes[0].Disposed);
        Assert.False(processes[1].Disposed);
    }

    [Fact]
    public async Task LimitsPasswordRetriesAndDoesNotPromptForHostOrBootstrapFailures()
    {
        var prompts = 0;
        await using (var denied = new ManagedSshConnection(Profile(), _ => new AuthProcess(string.Empty, true), (_, _, _) => Task.CompletedTask,
            requestPassword: (_, _) => { prompts++; return Task.FromResult<string?>("wrong"); }))
            await Assert.ThrowsAsync<SshAuthenticationException>(() => denied.EnsureConnectedAsync());
        Assert.Equal(2, prompts);
        await using var broken = new ManagedSshConnection(Profile(), _ => new AuthProcess(string.Empty, false), (_, _, _) => Task.CompletedTask,
            requestPassword: (_, _) => { prompts++; return Task.FromResult<string?>("unused"); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => broken.EnsureConnectedAsync());
        Assert.Equal(2, prompts);
    }

    [Fact]
    public async Task PasswordCancellationDoesNotTurnIntoATimeoutOrOpenAForward()
    {
        var spawned = 0;
        await using var connection = new ManagedSshConnection(Profile(), _ => { spawned++; return new AuthProcess(string.Empty, true); },
            (_, _, _) => Task.CompletedTask, requestPassword: (_, _) => Task.FromResult<string?>(null));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.EnsureConnectedAsync());
        Assert.Contains("canceled", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, spawned);
    }

    [Fact]
    public async Task PackageUploadUsesBoundedFramesAndRejectsChangedArchive()
    {
        using var directory = new ClientTestDirectory();
        var path = Path.Combine(directory.Path, "package.zip");
        var bytes = RandomNumberGenerator.GetBytes(70_000);
        await File.WriteAllBytesAsync(path, bytes);
        var bundle = new SshHostBundle(path, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
        using var output = new StringWriter();
        await bundle.SendAsync(output, CancellationToken.None);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, line => Assert.True(line.Length <= 32768));
        Assert.Equal(bytes, lines.SelectMany(Convert.FromBase64String).ToArray());
        await File.WriteAllBytesAsync(path, new byte[bytes.Length]);
        await Assert.ThrowsAsync<IOException>(() => bundle.SendAsync(output, CancellationToken.None));
    }

    [Fact]
    public async Task HandshakeAcceptsOneExplicitPackageRequestThenHostMetadata()
    {
        var info = Info();
        var hash = new string('A', 64);
        using var reader = new StringReader("PISTATION_PACKAGE " + hash + "\n" + Handshake(info));
        var requests = 0;
        Assert.Equal(info, await ManagedSshConnection.ReadHandshakeAsync(reader, CancellationToken.None, (actual, _) =>
        {
            requests++;
            Assert.Equal(hash, actual);
            return Task.CompletedTask;
        }));
        Assert.Equal(1, requests);
        using var repeated = new StringReader("PISTATION_PACKAGE " + hash + "\nPISTATION_PACKAGE " + hash + "\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ManagedSshConnection.ReadHandshakeAsync(repeated, CancellationToken.None, (_, _) => Task.CompletedTask));
    }

    [Fact]
    public void LegacySavedProfilesKeepTheirOldRemoteDataRootWhileNewProfilesUseTheSharedDefault()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        var path = Path.Combine(directory.Path, "ssh.protected");
        var old = Profile();
        var entry = new SshStorageEntry(old.Id, old.Name, old.Target, old.ServerPath, null, null, null, old.ClientId.Value);
        WindowsProtectedStorage.Write(path, JsonSerializer.SerializeToUtf8Bytes(new[] { entry }, SshStorageJsonContext.Default.SshStorageEntryArray));
        var store = new SshConnectionStore(path);
        var migrated = Assert.Single(store.Load());
        Assert.Equal(@"%LOCALAPPDATA%\PiStation\ssh-host", migrated.DataRoot);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(SshCommands.Control(migrated).ArgumentList[^1]));
        Assert.Contains("ExpandEnvironmentVariables", script, StringComparison.Ordinal);
        var added = Profile() with { Id = Guid.NewGuid(), ServerPath = string.Empty, Port = 2222 };
        store.Save(added);
        Assert.Equal(added, store.Load().Single(profile => profile.Id == added.Id));
        Assert.Equal(migrated, store.Load().Single(profile => profile.Id == old.Id));
    }

    private static SshConnectionProfile Profile() => new(Guid.NewGuid(), "Test", "alias", "PiStation.Server.exe", null, null, null, ClientId.New());
    private static SshHostInfo Info() => new(1, Protocol.ProtocolVersion.Current, EnvironmentId.New(), "Test", 23456, new string('A', 64), new string('B', 64), true);
    private static string Handshake(SshHostInfo info) => "PISTATION_SSH " + JsonSerializer.Serialize(info, ProtocolJsonContext.Default.SshHostInfo) + "\n";

    private sealed class AuthProcess(string output, bool denied) : ISshProcess
    {
        public TextReader Output { get; } = new StringReader(output);
        public bool HasExited => denied || Disposed;
        public bool AuthenticationFailed => denied;
        public bool Disposed { get; private set; }
        public string FailureMessage => denied ? "SSH authentication failed." : "Host failed.";
        public ValueTask DisposeAsync() { Disposed = true; Output.Dispose(); return ValueTask.CompletedTask; }
    }
}
