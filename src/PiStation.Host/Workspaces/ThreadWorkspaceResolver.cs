using PiStation.Host.Errors;
using PiStation.Host.Persistence;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host.Workspaces;

public sealed record ResolvedThreadWorkspace(
    ProjectDescriptor Project,
    HostThreadRecord? Thread,
    string ProjectRoot,
    string WorkspaceRoot,
    ThreadWorkspaceMode WorkspaceMode,
    long WorkspaceGeneration)
{
    public bool IsWorktree => WorkspaceMode == ThreadWorkspaceMode.Worktree;
}

public sealed class ThreadWorkspaceResolver(HostDatabase database)
{
    private readonly HostDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<ResolvedThreadWorkspace> ResolveAsync(
        ProjectId projectId,
        ThreadId? threadId = null,
        CancellationToken cancellationToken = default)
    {
        var project = await _database.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new HostOperationException(
                ProtocolErrorCodes.ProjectNotFound,
                $"Project '{projectId}' was not found.");
        HostThreadRecord? thread = null;
        if (threadId is not null)
        {
            thread = await _database.GetThreadAsync(threadId.Value, cancellationToken).ConfigureAwait(false)
                ?? throw new HostOperationException(
                    ProtocolErrorCodes.ThreadNotFound,
                    $"Thread '{threadId}' was not found.");
            if (thread.ProjectId != projectId)
            {
                throw new HostOperationException(
                    ProtocolErrorCodes.ThreadInvalid,
                    "The selected thread does not belong to the selected project.");
            }
        }

        var projectRoot = Normalize(project.CanonicalPath);
        var workspaceMode = thread?.WorkspaceMode ?? ThreadWorkspaceMode.Local;
        var workspaceRoot = workspaceMode == ThreadWorkspaceMode.Worktree
            ? Normalize(thread?.WorktreePath ?? throw new HostOperationException(
                ProtocolErrorCodes.WorktreeUnavailable,
                "The thread does not have a worktree path."))
            : projectRoot;
        if (!Directory.Exists(workspaceRoot))
        {
            throw new HostOperationException(
                workspaceMode == ThreadWorkspaceMode.Worktree
                    ? ProtocolErrorCodes.WorktreeUnavailable
                    : ProtocolErrorCodes.GitUnavailable,
                workspaceMode == ThreadWorkspaceMode.Worktree
                    ? "The thread worktree is unavailable."
                    : "The project directory is unavailable.");
        }

        return new ResolvedThreadWorkspace(
            project,
            thread,
            projectRoot,
            workspaceRoot,
            workspaceMode,
            thread?.WorkspaceGeneration ?? 0);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
