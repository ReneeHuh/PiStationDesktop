using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using PiStation.Host.Hosting;
using PiStation.Host.Security;
using PiStation.Protocol.Models;
using PiStation.Server;

namespace PiStation.ClientRuntime.Tests;

public sealed class RemoteAuthCliTests
{
    [Theory]
    [InlineData("30s", 30)]
    [InlineData("5m", 300)]
    [InlineData("1.5 hours", 5400)]
    [InlineData("30d", 2592000)]
    public void HumanLifetimesAreParsed(string text, double seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), RemoteAuthCli.ParseTtl(text));

    [Theory]
    [InlineData("0s")]
    [InlineData("-1m")]
    [InlineData("forever")]
    [InlineData("181d")]
    [InlineData("NaNh")]
    public void InvalidLifetimesAreRejected(string text) =>
        Assert.Throws<ArgumentException>(() => RemoteAuthCli.ParseTtl(text));

    [Theory]
    [InlineData("auth session issue --json --token-only")]
    [InlineData("auth pairing create --token-only")]
    [InlineData("auth session list --ttl 5m")]
    [InlineData("auth session issue --access admin")]
    [InlineData("auth session issue --label")]
    [InlineData("auth pairing approve 00000000000000000000000000000000")]
    [InlineData("auth pairing approve 00000000000000000000000000000000 --code 12x456")]
    [InlineData("auth session revoke not-an-id")]
    [InlineData("status --json --json")]
    [InlineData("status --data-root a --base-dir b")]
    public void InvalidFlagsAreRejectedBeforeOpeningAnyDatabase(string command) =>
        Assert.Throws<ArgumentException>(() => RemoteAuthCli.Options.Parse(command.Split(' ')));

    [Fact]
    public void ParserPreservesLabelSubjectAndDataRoot()
    {
        var options = RemoteAuthCli.Options.Parse(["auth", "session", "issue", "--base-dir", @"C:\Test Data",
            "--label", "Build agent", "--subject", "automation", "--access", "read-only", "--ttl", "15 minutes", "--json"]);
        Assert.Equal(@"C:\Test Data", options.Root);
        Assert.Equal("Build agent", options.Get("--label"));
        Assert.Equal("automation", options.Get("--subject"));
        Assert.Equal(RemoteAccessLevel.ReadOnly, options.Access);
        Assert.Equal(TimeSpan.FromMinutes(15), options.Ttl);
        Assert.True(options.Json);
    }

    [Fact]
    public async Task OfflineSessionCommandsOnlyExposeTokenOnIssue()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var root = directory.CreateDirectory("offline");
        Assert.Equal(0, await RemoteAuthCli.RunAsync(["auth", "session", "issue", "--data-root", root,
            "--label", "CLI viewer", "--subject", "automation", "--access", "read-only", "--json"], output, error, default));
        using var issued = JsonDocument.Parse(output.ToString());
        var token = issued.RootElement.GetProperty("token").GetString()!;
        var id = issued.RootElement.GetProperty("device").GetProperty("deviceId").GetString()!;
        using var host = new RemoteAccessStore(Path.Combine(root, "remote-access.db"));
        var authorization = Assert.IsType<RemoteAuthorization>(host.Authenticate(token));
        Assert.Equal(RemoteAccessLevel.ReadOnly, authorization.Device.AccessLevel);
        Assert.Equal("automation", authorization.Device.Subject);

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await RemoteAuthCli.RunAsync(["auth", "session", "list", "--data-root", root, "--json"], output, error, default));
        Assert.DoesNotContain(token, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hash", output.ToString(), StringComparison.OrdinalIgnoreCase);
        using var listed = JsonDocument.Parse(output.ToString());
        Assert.Single(listed.RootElement.EnumerateArray());
        Assert.Equal("ReadOnly", listed.RootElement[0].GetProperty("accessLevel").GetString());

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await RemoteAuthCli.RunAsync(["auth", "session", "revoke", id, "--data-root", root, "--json"], output, error, default));
        Assert.False(authorization.IsActive);
        Assert.DoesNotContain(token, output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task TokenOnlyProducesExactlyOneCredentialLine()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        await RemoteAuthCli.RunAsync(["auth", "session", "issue", "--data-root", directory.Path, "--token-only"], output, error, default);
        Assert.Matches("^[A-F0-9]{64}\\r?\\n$", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task PairRequiresRunningHostButOfflineInvitationCanSupplyItsEndpoint()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            return RemoteAuthCli.RunAsync(["pair", "--data-root", directory.Path, "--no-qr"], output, error, default);
        });
        Assert.False(File.Exists(Path.Combine(directory.Path, "remote-access.db")));
        await RemoteAuthCli.RunAsync(["auth", "pairing", "create", "--data-root", directory.Path,
            "--base-url", "https://192.0.2.10:52740/", "--certificate", new string('A', 64), "--json"], output, error, default);
        using var result = JsonDocument.Parse(output.ToString());
        var link = RemoteInvitation.Parse(result.RootElement.GetProperty("pairingUrl").GetString()!);
        Assert.Equal(result.RootElement.GetProperty("token").GetString(), link.Token);
        using var store = new RemoteAccessStore(Path.Combine(directory.Path, "remote-access.db"));
        Assert.Single(store.ListInvitations());
    }

    [Fact]
    public async Task HeadlessPairingCanBeCreatedAndApprovedBySeparateCliCalls()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var options = directory.CreateHostOptions();
        await using var host = await SshEnvironmentHost.StartAsync(options, cancellationToken: timeout.Token);
        using var output = new StringWriter();
        using var error = new StringWriter();
        await RemoteAuthCli.RunAsync(["pair", "--data-root", options.ApplicationDataRoot, "--access", "read-only", "--json"], output, error, timeout.Token);
        using var created = JsonDocument.Parse(output.ToString());
        var invitation = RemoteInvitation.Parse(created.RootElement.GetProperty("pairingUrl").GetString()!);
        Assert.Equal(host.Info.PairingAddress, invitation.Address);
        var saved = await RemotePairingClient.PairAsync(invitation, "CLI-paired viewer", new ApprovalProgress(() =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            using var cli = new RemoteAccessStore(Path.Combine(options.ApplicationDataRoot, "remote-access.db"));
            var pending = Assert.Single(cli.ListPending());
            using var approved = new StringWriter();
            // RunAsync has no asynchronous discovery path for approvals.
            var task = RemoteAuthCli.RunAsync(["auth", "pairing", "approve", pending.RequestId,
                "--code", pending.VerificationCode!, "--data-root", options.ApplicationDataRoot, "--json"], approved, error, timeout.Token);
            Assert.True(task.IsCompletedSuccessfully);
            Assert.Equal(0, task.Result);
        }), timeout.Token);
        await using var viewer = new EnvironmentClient(saved.CreateOptions());
        await viewer.ConnectAsync(timeout.Token);
        Assert.Empty(await viewer.ListProjectsAsync(timeout.Token));
        Assert.DoesNotContain("thread.operate", viewer.Descriptor!.Capabilities);
        await Assert.ThrowsAnyAsync<Exception>(() => viewer.AddProjectAsync(new(directory.CreateDirectory("denied")), timeout.Token));
        using var http = new HttpClient(RemoteTransport.CreateHandler(invitation.CertificateFingerprint)) { BaseAddress = invitation.Address };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", saved.CreateOptions().BearerCredential);
        using var upload = await http.PostAsync("threads/fake/draft-attachments", new StringContent("denied"), timeout.Token);
        Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);

        output.GetStringBuilder().Clear();
        await RemoteAuthCli.RunAsync(["status", "--data-root", options.ApplicationDataRoot, "--json"], output, error, timeout.Token);
        Assert.DoesNotContain(host.Info.BearerCredential, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(invitation.Token, output.ToString(), StringComparison.Ordinal);
        using var status = JsonDocument.Parse(output.ToString());
        Assert.True(status.RootElement.GetProperty("running").GetBoolean());
    }

    [Fact]
    public async Task CliIssuedReadOnlySessionCannotUseSshBootstrapPrivileges()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var options = directory.CreateHostOptions();
        await using var host = await SshEnvironmentHost.StartAsync(options, sharingAddress: IPAddress.Loopback, sharingPort: 0, cancellationToken: timeout.Token);
        using var cli = new RemoteAccessStore(Path.Combine(options.ApplicationDataRoot, "remote-access.db"));
        var issued = cli.IssueSession(RemoteAccessLevel.ReadOnly);
        await using var client = new EnvironmentClient(new()
        {
            HubAddress = new Uri($"https://127.0.0.1:{host.Info.Port}/environment"), BearerCredential = issued.Token,
            CertificateFingerprint = host.Info.CertificateFingerprint,
        });
        await client.ConnectAsync(timeout.Token);
        Assert.Empty(await client.ListProjectsAsync(timeout.Token));
        await Assert.ThrowsAnyAsync<Exception>(() => client.AddProjectAsync(new(directory.CreateDirectory("forbidden")), timeout.Token));
        Assert.NotEqual(host.Info.Port, host.Info.PairingAddress!.Port);
        var discovered = await SshEnvironmentHost.TryDiscoverAsync(options.ApplicationDataRoot, timeout.Token);
        Assert.Equal(host.Info.PairingAddress, discovered!.PairingAddress);
    }

    [Fact]
    public async Task DiscoveryTracksDesktopSharingAndHeadlessReusePreservesItsCertificate()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var options = directory.CreateHostOptions();
        string fingerprint;
        await using (var desktop = await EmbeddedEnvironmentHost.StartAsync(options, cancellationToken: timeout.Token))
        {
            var before = (await SshEnvironmentHost.TryDiscoverAsync(options.ApplicationDataRoot, timeout.Token))!;
            Assert.True(before.PairingAddress!.IsLoopback);
            using var access = new RemoteAccessStore(Path.Combine(options.ApplicationDataRoot, "remote-access.db"));
            using var certificate = RemoteHostCertificate.LoadOrCreate(options.ApplicationDataRoot);
            fingerprint = certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
            await using (var sharing = await RemoteEnvironmentHost.StartAsync(desktop.Environment, access, IPAddress.Loopback, 0,
                certificate, cancellationToken: timeout.Token))
            {
                var active = (await SshEnvironmentHost.TryDiscoverAsync(options.ApplicationDataRoot, timeout.Token))!;
                Assert.Equal(sharing.Address, active.PairingAddress);
                Assert.Equal(fingerprint, active.PairingCertificateFingerprint);
            }
            var after = (await SshEnvironmentHost.TryDiscoverAsync(options.ApplicationDataRoot, timeout.Token))!;
            Assert.Equal(before.PairingAddress, after.PairingAddress);
            Assert.Equal(before.PairingCertificateFingerprint, after.PairingCertificateFingerprint);
        }
        await using var headless = await SshEnvironmentHost.StartAsync(options, sharingAddress: IPAddress.Loopback,
            sharingPort: 0, cancellationToken: timeout.Token);
        Assert.Equal(fingerprint, headless.Info.PairingCertificateFingerprint);
    }

    private sealed class ApprovalProgress(Action approve) : IProgress<string>
    {
        public void Report(string value) => approve();
    }
}
