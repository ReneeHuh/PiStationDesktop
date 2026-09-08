using System.Diagnostics;

namespace PiStation.Host.Projects;

internal static class ProjectRepositoryIdentity
{
    public static async Task<string?> ReadAsync(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var process = new Process { StartInfo = new("git") { WorkingDirectory = directory,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in new[] { "config", "--get", "remote.origin.url" }) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var remote = (await output.ConfigureAwait(false)).Trim();
            await error.ConfigureAwait(false);
            if (process.ExitCode != 0 || remote.Length is 0 or > 4096) return null;
            string identity;
            if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) && !uri.IsFile)
                identity = uri.Host.ToLowerInvariant() + "/" + uri.AbsolutePath.Trim('/');
            else if (remote.Contains('@') && remote.IndexOf(':') > remote.IndexOf('@'))
                identity = remote[(remote.IndexOf('@') + 1)..].Replace(':', '/');
            else identity = Path.GetFullPath(remote, directory);
            return identity.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? identity[..^4] : identity;
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or OperationCanceledException or ArgumentException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        finally { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    }
}
