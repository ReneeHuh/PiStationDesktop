using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using PiStation.Host;
using PiStation.Host.Hosting;
using PiStation.Host.Updates;
using PiStation.Protocol.Models;

namespace PiStation.Server;

/// <summary>Owns only the child it starts. Runtime packages never overwrite this launcher.</summary>
[SupportedOSPlatform("windows")]
internal static class ServerUpdateLauncher
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var root = HostOptions.DefaultDataRoot;
        for (var i = 1; i + 1 < args.Length; i += 2)
            if (args[i] is "--data-root" or "--base-dir") root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(args[i + 1]));
        var ownerDirectory = Path.Combine(root, "update-owner");
        Directory.CreateDirectory(ownerDirectory);
        using var ownerLock = new FileStream(Path.Combine(ownerDirectory, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (await SshEnvironmentHost.TryDiscoverAsync(root, cancellationToken).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("Stop the separately running host once before starting its update-capable launcher.");
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The launcher executable is unavailable.");
        if (!Path.GetFileName(executable).Equals("PiStation.Server.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Start supervise with PiStation.Server.exe so its owned runtime is explicit.");
        var activePath = Path.Combine(ownerDirectory, "active-runtime.txt");
        if (File.Exists(activePath))
        {
            var active = Path.GetFullPath(File.ReadAllText(activePath));
            if (!active.StartsWith(Path.GetFullPath(Path.Combine(root, "remote-updates")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(active) != "PiStation.Server.exe" || !File.Exists(active))
                throw new InvalidDataException("The saved runtime path is invalid.");
            executable = active;
        }
        var childArgs = new List<string> { "serve" };
        childArgs.AddRange(args.Skip(1));
        childArgs.AddRange(["--owner-directory", ownerDirectory]);
        var handoffPath = Path.Combine(ownerDirectory, "activate.json");
        if (File.Exists(handoffPath))
        {
            var interrupted = StandaloneUpdateOwner.ReadHandoff(handoffPath);
            ValidateHandoff(interrupted, root);
            File.Delete(handoffPath);
        }
        RemoteUpdateCoordinator.FailInterruptedActivations(root);
        Process? child = null;
        SshHostInfo? expected = null;
        try
        {
            child = Start(executable, childArgs);
            expected = await WaitReadyAsync(child, root, null, null, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                await child.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (!File.Exists(handoffPath)) return child.ExitCode;
                var update = StandaloneUpdateOwner.ReadHandoff(handoffPath);
                ValidateHandoff(update, root);
                var previous = executable;
                try
                {
                    await ServerUpdatePackage.ValidateAsync(update.PackagePath, update.RuntimeDirectory, cancellationToken).ConfigureAwait(false);
                    executable = Path.Combine(update.RuntimeDirectory, "PiStation.Server.exe");
                    child.Dispose();
                    child = Start(executable, childArgs);
                    await WaitReadyAsync(child, root, expected, update.TargetVersion, cancellationToken).ConfigureAwait(false);
                    File.WriteAllText(activePath + ".tmp", executable);
                    File.Move(activePath + ".tmp", activePath, overwrite: true);
                    RemoteUpdateCoordinator.CompleteActivation(update.ReceiptPath, true, "The owner verified the new runtime with the existing environment identity.");
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    await StopOwnedAsync(child).ConfigureAwait(false);
                    child.Dispose();
                    // Accepted archives explicitly share database compatibility version 1 with this launcher.
                    executable = previous;
                    child = Start(previous, childArgs);
                    await WaitReadyAsync(child, root, expected, null, cancellationToken).ConfigureAwait(false);
                    RemoteUpdateCoordinator.CompleteActivation(update.ReceiptPath, false, "Activation failed; the previous compatible runtime was restored.");
                }
                File.Delete(handoffPath);
            }
            return 0;
        }
        finally
        {
            if (child is not null) { await StopOwnedAsync(child).ConfigureAwait(false); child.Dispose(); }
        }
    }

    private static Process Start(string executable, IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("The owned server did not start.");
    }

    private static async Task StopOwnedAsync(Process child)
    {
        if (child.HasExited) return;
        try
        {
            await child.StandardInput.WriteLineAsync("stop").ConfigureAwait(false);
            await child.StandardInput.FlushAsync().ConfigureAwait(false);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (Exception) when (!child.HasExited)
        {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    private static void ValidateHandoff(StagedRemoteUpdate update, string root)
    {
        var expected = Path.GetFullPath(Path.Combine(root, "remote-updates", update.RequestId.ToString("N")));
        if (update.RequestId == Guid.Empty || Path.GetFullPath(update.DataRoot) != Path.GetFullPath(root) ||
            Path.GetFullPath(update.RuntimeDirectory) != Path.Combine(expected, "runtime") ||
            Path.GetFullPath(update.PackagePath) != Path.Combine(expected, "package.zip") ||
            Path.GetFullPath(update.ReceiptPath) != Path.Combine(expected, "receipt.json"))
            throw new InvalidDataException("The activation request is outside the owner's staging directory.");
    }

    private static async Task<SshHostInfo> WaitReadyAsync(Process child, string root, SshHostInfo? expected, string? version, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (!child.HasExited)
        {
            var info = await SshEnvironmentHost.TryDiscoverAsync(root, timeout.Token).ConfigureAwait(false);
            if (info is not null)
            {
                if (expected is not null && (info.EnvironmentId != expected.EnvironmentId || info.CertificateFingerprint != expected.CertificateFingerprint))
                    throw new InvalidDataException("The updated runtime changed its environment identity.");
                if (version is not null && NormalizeVersion(info.ServerVersion) != NormalizeVersion(version))
                    throw new InvalidDataException("The updated runtime reported a different version.");
                using var handler = new HttpClientHandler { AllowAutoRedirect = false };
                handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null &&
                    cert.NotBefore.ToUniversalTime() <= DateTime.UtcNow && cert.NotAfter.ToUniversalTime() > DateTime.UtcNow &&
                    cert.GetCertHashString(HashAlgorithmName.SHA256) == info.CertificateFingerprint;
                using var client = new HttpClient(handler);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", info.BearerCredential);
                using var response = await client.GetAsync($"https://127.0.0.1:{info.Port}/ssh/health", timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return info;
            }
            await Task.Delay(200, timeout.Token).ConfigureAwait(false);
        }
        throw new InvalidOperationException("The updated runtime exited before becoming ready.");
    }
    private static Version NormalizeVersion(string? value)
    {
        var parsed = Version.Parse((value ?? "0.0.0").Split('+')[0].Split('-')[0]);
        return new(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
    }
}
