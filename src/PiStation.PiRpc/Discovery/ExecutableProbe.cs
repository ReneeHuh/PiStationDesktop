using System.Diagnostics;

namespace PiStation.PiRpc.Discovery;

public sealed class ExecutableProbe : IExecutableProbe
{
    private readonly TimeSpan _timeout;

    public ExecutableProbe(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

    public async Task<string> GetVersionOutputAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start version probe '{executablePath}'.");
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(_timeout);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCancellation.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCancellation.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"Version probe '{executablePath}' exceeded {_timeout}.");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Version probe '{executablePath}' exited with code {process.ExitCode}: {Bound(stderr)}");
        }

        return string.IsNullOrWhiteSpace(stdout) ? Bound(stderr) : Bound(stdout);
    }

    private static string Bound(string value)
    {
        const int maximumCharacters = 4096;
        var trimmed = value.Trim();
        return trimmed.Length <= maximumCharacters ? trimmed : trimmed[^maximumCharacters..];
    }

    private static void TryKill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
