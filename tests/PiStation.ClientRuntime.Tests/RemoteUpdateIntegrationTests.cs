using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PiStation.Host.Hosting;
using PiStation.Host.Security;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class RemoteUpdateIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneOwnerActivatesOrRestoresCompatibleRuntimeAndKeepsEnvironmentAndDeviceGrant(bool failStartup)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var options = directory.CreateHostOptions();
        var repository = FindRepository();
        var configuration = AppContext.BaseDirectory.Contains("Release", StringComparison.Ordinal) ? "Release" : "Debug";
        var executable = Path.Combine(repository, "src", "PiStation.Server", "bin", configuration, "net10.0", "PiStation.Server.exe");
        var package = Path.Combine(repository, "src", "PiStation.App", "obj", "ssh-bundle", configuration, "host-win-x64.zip");
        var versionedFixture = Environment.GetEnvironmentVariable("PISTATION_TEST_UPDATE_PACKAGE");
        if (!string.IsNullOrWhiteSpace(versionedFixture)) package = Path.GetFullPath(versionedFixture);
        Assert.True(File.Exists(package), "Build the desktop to generate the matching production host package.");
        if (failStartup)
        {
            var broken = Path.Combine(directory.CreateDirectory("packages"), "startup-failure.zip");
            File.Copy(package, broken);
            using (var archive = System.IO.Compression.ZipFile.Open(broken, System.IO.Compression.ZipArchiveMode.Update))
            {
                archive.GetEntry("PiStation.Server.runtimeconfig.json")!.Delete();
                using var writer = new StreamWriter(archive.CreateEntry("PiStation.Server.runtimeconfig.json").Open());
                writer.Write("{}");
            }
            package = broken;
        }
        var port = ReservePort();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "supervise", "--data-root", options.ApplicationDataRoot, "--pi-executable", options.PiInstallation!.ExecutablePath,
            "--host", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--enable-remote-updates", "true" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            SshHostInfo? info = null;
            while (info is null)
            {
                if (process.HasExited) throw new InvalidOperationException("Fixture owner exited: " + await errors);
                info = await SshEnvironmentHost.TryDiscoverAsync(options.ApplicationDataRoot, timeout.Token);
                if (info is null) await Task.Delay(100, timeout.Token);
            }
            using var access = new RemoteAccessStore(Path.Combine(options.ApplicationDataRoot, "remote-access.db"));
            var grant = access.IssueSession(RemoteAccessLevel.Operate, label: "Update fixture");
            await using var client = new EnvironmentClient(new()
            {
                HubAddress = new Uri($"https://127.0.0.1:{port}/environment"), CertificateFingerprint = info.PairingCertificateFingerprint,
                BearerCredential = grant.Token, ExpectedEnvironmentId = info.EnvironmentId,
                ReconnectDelays = [TimeSpan.FromMilliseconds(100)], RetryJitter = 0,
            });
            await client.ConnectAsync(timeout.Token);
            var project = await client.AddProjectAsync(new(directory.CreateDirectory("project")), timeout.Token);
            var descriptor = await client.GetRemoteUpdateDescriptorAsync(timeout.Token);
            Assert.True(descriptor.Supported && descriptor.Enabled);
            Assert.Equal("standalone", descriptor.HostKind);
            var id = Guid.NewGuid();
            var staged = await client.StageRemoteUpdateAsync(package, id, cancellationToken: timeout.Token);
            Assert.Equal(RemoteUpdateState.Ready, staged.State);
            if (!string.IsNullOrWhiteSpace(versionedFixture)) Assert.NotEqual(descriptor.CurrentVersion, staged.TargetVersion);
            var committed = await client.CommitRemoteUpdateAsync(new(id), timeout.Token);
            Assert.Equal(RemoteUpdateState.WaitingForIdle, committed.State);
            Assert.Equal(committed, await client.CommitRemoteUpdateAsync(new(id), timeout.Token));
            RemoteUpdateReceipt? receipt = null;
            while (receipt?.State is not (RemoteUpdateState.Succeeded or RemoteUpdateState.Failed))
            {
                await Task.Delay(200, timeout.Token);
                if (client.ConnectionState != EnvironmentConnectionState.Connected) continue;
                try { receipt = await client.GetRemoteUpdateReceiptAsync(id, timeout.Token); }
                catch (Exception) when (!timeout.IsCancellationRequested) { }
            }
            Assert.Equal(failStartup ? RemoteUpdateState.Failed : RemoteUpdateState.Succeeded, receipt.State);
            if (failStartup) Assert.Contains("restored", receipt.Message!, StringComparison.Ordinal);
            Assert.Equal(info.EnvironmentId, client.Descriptor!.EnvironmentId);
            Assert.Equal(project.ProjectId, Assert.Single(await client.ListProjectsAsync(timeout.Token)).ProjectId);
            Assert.Equal(grant.Device.DeviceId, Assert.Single(access.ListDevices()).DeviceId);
            Assert.Equal(failStartup ? descriptor.CurrentVersion : staged.TargetVersion, (await client.GetRemoteUpdateDescriptorAsync(timeout.Token)).CurrentVersion);
            await client.DisconnectAsync(timeout.Token);
            Assert.False(process.HasExited);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            try { await Task.WhenAll(output, errors); } catch (OperationCanceledException) { }
        }
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private static string FindRepository()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "PiStationDesktop.slnx"))) return path.FullName;
        throw new DirectoryNotFoundException("The test repository is unavailable.");
    }
}
