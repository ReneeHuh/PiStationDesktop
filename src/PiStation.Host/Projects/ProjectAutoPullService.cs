using System.Diagnostics;
using System.Text;
using PiStation.Protocol.Models;

namespace PiStation.Host.Projects;

internal sealed class ProjectAutoPullService(Action<string>? diagnosticSink = null)
{
    private const int MaximumOutputCharacters = 64 * 1024;
    private readonly Action<string>? _diagnosticSink = diagnosticSink;

    public async Task PullEligibleProjectsAsync(
        IReadOnlyList<ProjectDescriptor> projects,
        CancellationToken cancellationToken = default)
    {
        foreach (var project in projects.Where(static project => project.AutoPullDefaultBranch))
        {
            try
            {
                await PullIfOnDefaultBranchAsync(project, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _diagnosticSink?.Invoke($"Automatic pull skipped for {project.DisplayName}: {exception.Message}");
            }
        }
    }

    private async Task PullIfOnDefaultBranchAsync(ProjectDescriptor project, CancellationToken cancellationToken)
    {
        var current = await RunGitAsync(project.CanonicalPath, ["branch", "--show-current"], cancellationToken)
            .ConfigureAwait(false);
        var originHead = await RunGitAsync(
            project.CanonicalPath, ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"], cancellationToken)
            .ConfigureAwait(false);
        if (current.ExitCode != 0 || originHead.ExitCode != 0)
        {
            return;
        }
        var branch = current.Output.Trim();
        var defaultBranch = originHead.Output.Trim().Replace("origin/", string.Empty, StringComparison.Ordinal);
        if (!branch.Equals(defaultBranch, StringComparison.Ordinal))
        {
            return;
        }
        var status = await RunGitAsync(project.CanonicalPath, ["status", "--porcelain"], cancellationToken)
            .ConfigureAwait(false);
        if (status.ExitCode != 0 || !string.IsNullOrWhiteSpace(status.Output))
        {
            _diagnosticSink?.Invoke($"Automatic pull skipped for {project.DisplayName}: default branch is dirty.");
            return;
        }
        var pull = await RunGitAsync(project.CanonicalPath, ["pull", "--ff-only", "origin", defaultBranch], cancellationToken, TimeSpan.FromMinutes(5))
            .ConfigureAwait(false);
        _diagnosticSink?.Invoke(pull.ExitCode == 0
            ? $"Automatic pull completed for {project.DisplayName}."
            : $"Automatic pull failed for {project.DisplayName}: {pull.Error.Trim()}");
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) return (-1, string.Empty, "Git could not be started.");
        var stdout = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        var stderr = ReadBoundedAsync(process.StandardError, cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(MaximumOutputCharacters, 16 * 1024));
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return builder.ToString();
            }

            var remaining = MaximumOutputCharacters - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(read, remaining));
            }
        }
    }
}
