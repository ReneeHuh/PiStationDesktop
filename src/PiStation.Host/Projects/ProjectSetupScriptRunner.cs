using System.Globalization;
using PiStation.Host.Errors;
using PiStation.Host.Persistence;
using PiStation.Host.Terminals;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Models;

namespace PiStation.Host.Projects;

internal sealed class ProjectSetupScriptRunner(
    HostDatabase database,
    TerminalSessionRegistry terminals)
{
    private readonly HostDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly TerminalSessionRegistry _terminals = terminals ?? throw new ArgumentNullException(nameof(terminals));

    public async Task<ProjectSetupScriptResult> RunAsync(
        RunProjectSetupScriptRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = await _database.GetProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new HostOperationException(
                ProtocolErrorCodes.ProjectNotFound,
                $"Project '{request.ProjectId}' was not found.");
        var thread = await _database.GetThreadAsync(request.ThreadId, cancellationToken).ConfigureAwait(false)
            ?? throw new HostOperationException(
                ProtocolErrorCodes.ThreadNotFound,
                $"Thread '{request.ThreadId}' was not found.");
        if (thread.ProjectId != project.ProjectId ||
            thread.WorkspaceMode != ThreadWorkspaceMode.Worktree ||
            string.IsNullOrWhiteSpace(thread.WorktreePath))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.GitInvalid,
                "Setup scripts can only run for a thread-owned worktree.");
        }

        var script = project.Scripts?.FirstOrDefault(static candidate => candidate.RunOnWorktreeCreate);
        if (script is null)
        {
            await UpdateStateAsync(thread, SetupScriptState.None, null, cancellationToken).ConfigureAwait(false);
            return new ProjectSetupScriptResult(SetupScriptState.None, null, null, null, "No worktree setup script is configured.");
        }

        if (!project.AreRepositoryScriptsTrusted)
        {
            const string message = "Repository scripts are not trusted. Review t3.json and trust this project before running its setup script.";
            await UpdateStateAsync(thread, SetupScriptState.Pending, message, cancellationToken).ConfigureAwait(false);
            return new ProjectSetupScriptResult(SetupScriptState.Pending, script.Id, script.Name, null, message);
        }

        try
        {
            await UpdateStateAsync(thread, SetupScriptState.Running, $"Running {script.Name}", cancellationToken)
                .ConfigureAwait(false);
            var terminal = await _terminals.StartAsync(
                new StartTerminalSessionRequest(
                    project.ProjectId,
                    TerminalShellKind.PowerShell,
                    ThreadId: thread.ThreadId),
                cancellationToken).ConfigureAwait(false);
            var root = EscapePowerShellLiteral(project.CanonicalPath);
            var worktree = EscapePowerShellLiteral(thread.WorktreePath);
            var command = $"$env:T3CODE_PROJECT_ROOT='{root}'; $env:T3CODE_WORKTREE_PATH='{worktree}'; {script.Command}; if ($?) {{ exit 0 }} elseif ($LASTEXITCODE) {{ exit $LASTEXITCODE }} else {{ exit 1 }}\r";
            await _terminals.WriteAsync(
                new WriteTerminalInputRequest(terminal.TerminalSessionId, command),
                cancellationToken).ConfigureAwait(false);
            var message = $"{script.Name} started in {terminal.Name}.";
            await UpdateStateAsync(thread, SetupScriptState.Running, message, cancellationToken).ConfigureAwait(false);
            _ = MonitorCompletionAsync(thread, script, terminal.TerminalSessionId);
            return new ProjectSetupScriptResult(
                SetupScriptState.Running,
                script.Id,
                script.Name,
                terminal.TerminalSessionId,
                message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await UpdateStateAsync(
                thread,
                SetupScriptState.Cancelled,
                $"{script.Name} was cancelled before it started.",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var message = $"The setup script could not start: {exception.Message}";
            await UpdateStateAsync(thread, SetupScriptState.Failed, message, CancellationToken.None).ConfigureAwait(false);
            return new ProjectSetupScriptResult(SetupScriptState.Failed, script.Id, script.Name, null, message);
        }
    }

    private async Task MonitorCompletionAsync(
        HostThreadRecord thread,
        ProjectScript script,
        PiStation.Protocol.Identifiers.TerminalSessionId terminalSessionId)
    {
        try
        {
            var terminal = await _terminals.WaitForExitAsync(terminalSessionId, CancellationToken.None)
                .ConfigureAwait(false);
            var succeeded = terminal.State == TerminalSessionState.Exited && terminal.ExitCode == 0;
            var state = succeeded ? SetupScriptState.Succeeded : SetupScriptState.Failed;
            var message = succeeded
                ? $"{script.Name} completed successfully."
                : $"{script.Name} failed with exit code {terminal.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}.";
            await UpdateStateAsync(thread, state, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            try
            {
                await UpdateStateAsync(
                    thread,
                    SetupScriptState.Failed,
                    $"{script.Name} completion could not be observed: {exception.Message}",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private Task UpdateStateAsync(
        HostThreadRecord thread,
        SetupScriptState state,
        string? message,
        CancellationToken cancellationToken) =>
        _database.UpdateThreadWorkspaceAsync(
            thread.ThreadId,
            thread.WorkspaceMode,
            thread.BranchName,
            thread.WorktreePath,
            incrementGeneration: false,
            setupScriptState: state,
            setupScriptMessage: message,
            cancellationToken: cancellationToken);

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
