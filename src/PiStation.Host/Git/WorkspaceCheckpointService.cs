using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using PiStation.Host.Errors;
using PiStation.Host.Persistence;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Host.Workspaces;

namespace PiStation.Host.Git;

public sealed class WorkspaceCheckpointService(
    HostDatabase database,
    ThreadWorkspaceResolver? workspaceResolver = null,
    WorkspaceOperationLocks? workspaceLocks = null)
{
    private const int MaximumCommandOutputCharacters = 2 * 1024 * 1024;
    private const string RefPrefix = "refs/pistation/checkpoints";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly HostDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly ThreadWorkspaceResolver _workspaceResolver = workspaceResolver ?? new ThreadWorkspaceResolver(database);
    private readonly WorkspaceOperationLocks _workspaceLocks = workspaceLocks ?? new WorkspaceOperationLocks();

    public Task<IReadOnlyList<ThreadCheckpoint>> ListAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default) =>
        _database.ListThreadCheckpointsAsync(threadId, cancellationToken);

    public async Task<bool> EnsureBaselineAsync(
        ProjectDescriptor project,
        ThreadId threadId,
        int turnCount,
        CancellationToken cancellationToken = default)
    {
        ValidateTurnCount(turnCount, allowZero: true);
        var projectRoot = await GetWorkspaceRootAsync(project, threadId, cancellationToken).ConfigureAwait(false);
        await using var lease = await AcquireWorkspaceLockAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (await FindRepositoryAsync(projectRoot, cancellationToken).ConfigureAwait(false) is null)
        {
            return false;
        }

        var checkpointRef = CheckpointRef(threadId, turnCount);
        if (!await HasRefAsync(projectRoot, checkpointRef, cancellationToken).ConfigureAwait(false))
        {
            await CaptureRefAsync(projectRoot, checkpointRef, cancellationToken).ConfigureAwait(false);
        }

        var beforeRef = BeforeCheckpointRef(threadId, turnCount + 1);
        if (!await HasRefAsync(projectRoot, beforeRef, cancellationToken).ConfigureAwait(false))
        {
            await CaptureRefAsync(projectRoot, beforeRef, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public async Task<ThreadCheckpoint?> CaptureTurnAsync(
        ProjectDescriptor project,
        ThreadId threadId,
        TurnId turnId,
        int turnCount,
        string? piEntryIdBeforeTurn,
        string? piEntryIdAfterTurn,
        CancellationToken cancellationToken = default)
    {
        ValidateTurnCount(turnCount, allowZero: false);
        var projectRoot = await GetWorkspaceRootAsync(project, threadId, cancellationToken).ConfigureAwait(false);
        await using var lease = await AcquireWorkspaceLockAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (await FindRepositoryAsync(projectRoot, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        var fromRef = BeforeCheckpointRef(threadId, turnCount);
        if (!await HasRefAsync(projectRoot, fromRef, cancellationToken).ConfigureAwait(false))
        {
            var legacyFromRef = CheckpointRef(threadId, turnCount - 1);
            if (!await HasRefAsync(projectRoot, legacyFromRef, cancellationToken).ConfigureAwait(false))
            {
                throw Unavailable($"The pre-turn checkpoint for turn {turnCount} is unavailable.");
            }

            fromRef = legacyFromRef;
        }

        var targetRef = CheckpointRef(threadId, turnCount);
        await CaptureRefAsync(projectRoot, targetRef, cancellationToken).ConfigureAwait(false);
        var thread = await _database.GetThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
        var headShaBefore = await ResolveRefAsync(projectRoot, fromRef, cancellationToken).ConfigureAwait(false);
        var headShaAfter = await ResolveRefAsync(projectRoot, targetRef, cancellationToken).ConfigureAwait(false);
        var files = await ReadFileSummaryAsync(projectRoot, fromRef, targetRef, cancellationToken)
            .ConfigureAwait(false);
        var checkpoint = new ThreadCheckpoint(
            turnId,
            turnCount,
            targetRef,
            string.IsNullOrWhiteSpace(piEntryIdAfterTurn) ||
            turnCount > 1 && string.IsNullOrWhiteSpace(piEntryIdBeforeTurn)
                ? ThreadCheckpointStatus.Missing
                : ThreadCheckpointStatus.Ready,
            files,
            piEntryIdBeforeTurn,
            piEntryIdAfterTurn,
            DateTimeOffset.UtcNow,
            fromRef,
            thread?.WorkspaceGeneration ?? 0,
            thread?.BranchName,
            headShaBefore,
            headShaAfter);
        await _database.UpsertThreadCheckpointAsync(threadId, checkpoint, cancellationToken).ConfigureAwait(false);
        return checkpoint;
    }

    public async Task<ThreadCheckpoint> RecordCaptureErrorAsync(
        ThreadId threadId,
        TurnId turnId,
        int turnCount,
        string? piEntryIdBeforeTurn,
        string? piEntryIdAfterTurn,
        CancellationToken cancellationToken = default)
    {
        var checkpoint = new ThreadCheckpoint(
            turnId,
            turnCount,
            CheckpointRef(threadId, turnCount),
            ThreadCheckpointStatus.Error,
            [],
            piEntryIdBeforeTurn,
            piEntryIdAfterTurn,
            DateTimeOffset.UtcNow);
        await _database.UpsertThreadCheckpointAsync(threadId, checkpoint, cancellationToken).ConfigureAwait(false);
        return checkpoint;
    }

    public async Task<GetThreadCheckpointDiffResult> GetDiffAsync(
        GetThreadCheckpointDiffRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateDiffRequest(request);
        var (thread, project) = await GetThreadProjectAsync(request.ThreadId, cancellationToken).ConfigureAwait(false);
        var checkpoints = await ListAsync(thread.ThreadId, cancellationToken).ConfigureAwait(false);
        var target = checkpoints.SingleOrDefault(checkpoint => checkpoint.TurnCount == request.TurnCount)
            ?? throw Unavailable($"Checkpoint {request.TurnCount} is unavailable for this thread.");
        if (target.Status == ThreadCheckpointStatus.Error)
        {
            throw Unavailable($"Checkpoint {request.TurnCount} was not captured successfully.");
        }

        var fromTurnCount = request.Scope == CheckpointDiffScope.Turn ? request.TurnCount - 1 : 0;
        var firstCheckpoint = checkpoints.OrderBy(static checkpoint => checkpoint.TurnCount).FirstOrDefault();
        var fromRef = request.Scope == CheckpointDiffScope.Turn
            ? target.BeforeCheckpointRef ?? CheckpointRef(request.ThreadId, fromTurnCount)
            : firstCheckpoint?.BeforeCheckpointRef ?? CheckpointRef(request.ThreadId, 0);
        var projectRoot = await GetWorkspaceRootAsync(project, request.ThreadId, cancellationToken)
            .ConfigureAwait(false);
        if (target.WorkspaceGeneration != thread.WorkspaceGeneration)
        {
            throw Unavailable("The checkpoint belongs to an earlier workspace generation.");
        }
        if (await FindRepositoryAsync(projectRoot, cancellationToken).ConfigureAwait(false) is null ||
            !await HasRefAsync(projectRoot, fromRef, cancellationToken).ConfigureAwait(false) ||
            !await HasRefAsync(projectRoot, target.CheckpointRef, cancellationToken).ConfigureAwait(false))
        {
            throw Unavailable("One or more filesystem checkpoints are no longer available.");
        }

        var arguments = new List<string>
        {
            "diff",
            "--patch",
            "--no-color",
            "--no-ext-diff",
            "--no-textconv",
            "--relative",
        };
        if (request.IgnoreWhitespace)
        {
            arguments.Add("--ignore-all-space");
        }

        arguments.Add($"{fromRef}^{{commit}}");
        arguments.Add($"{target.CheckpointRef}^{{commit}}");
        arguments.Add("--");
        arguments.Add(request.RelativePath ?? ".");
        var diff = await RunGitAsync(
            projectRoot,
            arguments,
            request.MaximumCharacters,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(diff, "The checkpoint diff could not be read.");

        return new GetThreadCheckpointDiffResult(
            request.ThreadId,
            fromTurnCount,
            request.TurnCount,
            request.Scope,
            request.RelativePath,
            string.IsNullOrEmpty(diff.StandardOutput)
                ? "No textual changes in this checkpoint range."
                : diff.StandardOutput,
            diff.WasTruncated);
    }

    public async Task<string> CaptureRecoveryRefAsync(
        ProjectDescriptor project,
        ThreadId threadId,
        CancellationToken cancellationToken = default)
    {
        var projectRoot = await GetWorkspaceRootAsync(project, threadId, cancellationToken).ConfigureAwait(false);
        await using var lease = await AcquireWorkspaceLockAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        var recoveryRef = $"{CheckpointThreadPrefix(threadId)}/recovery/{Guid.NewGuid():N}";
        await CaptureRefAsync(projectRoot, recoveryRef, cancellationToken).ConfigureAwait(false);
        return recoveryRef;
    }

    public async Task RestoreAsync(
        ProjectDescriptor project,
        ThreadId threadId,
        int turnCount,
        CancellationToken cancellationToken = default)
    {
        ValidateTurnCount(turnCount, allowZero: true);
        var checkpointRef = CheckpointRef(threadId, turnCount);
        await RestoreRefAsync(project, threadId, checkpointRef, cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreRefAsync(
        ProjectDescriptor project,
        ThreadId threadId,
        string checkpointRef,
        CancellationToken cancellationToken = default)
    {
        var projectRoot = await GetWorkspaceRootAsync(project, threadId, cancellationToken).ConfigureAwait(false);
        await using var lease = await AcquireWorkspaceLockAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        var commit = await ResolveRefAsync(projectRoot, checkpointRef, cancellationToken).ConfigureAwait(false)
            ?? throw Unavailable("The requested filesystem checkpoint is unavailable.");
        var restore = await RunGitAsync(
            projectRoot,
            ["restore", "--source", commit, "--worktree", "--staged", "--", "."],
            16 * 1024,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(restore, "The workspace checkpoint could not be restored.");
        var clean = await RunGitAsync(
            projectRoot,
            ["clean", "-fd", "--", "."],
            16 * 1024,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(clean, "Untracked files could not be reconciled while restoring the checkpoint.");

        if (await HasHeadAsync(projectRoot, cancellationToken).ConfigureAwait(false))
        {
            var reset = await RunGitAsync(
                projectRoot,
                ["reset", "--quiet", "--", "."],
                16 * 1024,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(reset, "The restored workspace staging state could not be normalized.");
        }
    }

    public async Task DeleteFutureAsync(
        ProjectDescriptor project,
        ThreadId threadId,
        int turnCount,
        CancellationToken cancellationToken = default)
    {
        var refs = await _database.DeleteThreadCheckpointsAfterAsync(threadId, turnCount, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await DeleteRefsAsync(project, threadId, refs, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Metadata is authoritative; an unreachable hidden ref can be garbage-collected later.
        }
    }

    public async Task DeleteRefsAsync(
        ProjectDescriptor project,
        ThreadId threadId,
        IEnumerable<string> checkpointRefs,
        CancellationToken cancellationToken = default)
    {
        var projectRoot = await GetWorkspaceRootAsync(project, threadId, cancellationToken).ConfigureAwait(false);
        await using var lease = await AcquireWorkspaceLockAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        foreach (var checkpointRef in checkpointRefs.Distinct(StringComparer.Ordinal))
        {
            if (!checkpointRef.StartsWith(RefPrefix + "/", StringComparison.Ordinal))
            {
                continue;
            }

            await RunGitAsync(
                projectRoot,
                ["update-ref", "-d", checkpointRef],
                16 * 1024,
                cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string CheckpointRef(ThreadId threadId, int turnCount) =>
        $"{CheckpointThreadPrefix(threadId)}/turn/{turnCount}";

    internal static string BeforeCheckpointRef(ThreadId threadId, int turnCount) =>
        $"{CheckpointThreadPrefix(threadId)}/before/{turnCount}";

    private static string CheckpointThreadPrefix(ThreadId threadId)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(threadId.Value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"{RefPrefix}/{encoded}";
    }

    private async Task<(HostThreadRecord Thread, ProjectDescriptor Project)> GetThreadProjectAsync(
        ThreadId threadId,
        CancellationToken cancellationToken)
    {
        var thread = await _database.GetThreadAsync(threadId, cancellationToken).ConfigureAwait(false)
            ?? throw new HostOperationException(
                ProtocolErrorCodes.ThreadNotFound,
                $"Thread '{threadId}' was not found.");
        var project = await _database.GetProjectAsync(thread.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new HostOperationException(
                ProtocolErrorCodes.ProjectNotFound,
                $"Project '{thread.ProjectId}' was not found.");
        return (thread, project);
    }

    private async Task<string> GetWorkspaceRootAsync(
        ProjectDescriptor project,
        ThreadId threadId,
        CancellationToken cancellationToken)
    {
        var workspace = await _workspaceResolver.ResolveAsync(project.ProjectId, threadId, cancellationToken)
            .ConfigureAwait(false);
        return workspace.WorkspaceRoot;
    }

    private async ValueTask<IAsyncDisposable> AcquireWorkspaceLockAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var common = await RunGitAsync(
            workspaceRoot,
            ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            8 * 1024,
            cancellationToken).ConfigureAwait(false);
        var identity = common.ExitCode == 0 && !string.IsNullOrWhiteSpace(common.StandardOutput)
            ? common.StandardOutput.Trim()
            : workspaceRoot;
        return await _workspaceLocks.AcquireAsync(identity, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> FindRepositoryAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            projectRoot,
            ["rev-parse", "--show-toplevel"],
            8 * 1024,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            if (result.StandardError.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            EnsureSuccess(result, "The Git repository could not be resolved.");
        }

        var repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(result.StandardOutput.Trim()));
        if (!repositoryRoot.Equals(projectRoot, PathComparison) &&
            !projectRoot.StartsWith(repositoryRoot + Path.DirectorySeparatorChar, PathComparison))
        {
            throw Unavailable("Git resolved a repository outside the selected project's ancestry.");
        }

        return repositoryRoot;
    }

    private static async Task CaptureRefAsync(
        string projectRoot,
        string checkpointRef,
        CancellationToken cancellationToken)
    {
        var commonDirectoryResult = await RunGitAsync(
            projectRoot,
            ["rev-parse", "--git-common-dir"],
            8 * 1024,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(commonDirectoryResult, "The Git metadata directory could not be resolved.");
        var commonDirectoryValue = commonDirectoryResult.StandardOutput.Trim();
        var commonDirectory = Path.GetFullPath(Path.IsPathRooted(commonDirectoryValue)
            ? commonDirectoryValue
            : Path.Combine(projectRoot, commonDirectoryValue));
        var temporaryIndex = Path.Combine(commonDirectory, $"pistation-checkpoint-index-{Guid.NewGuid():N}");
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_INDEX_FILE"] = temporaryIndex,
            ["GIT_AUTHOR_NAME"] = "Pi Station",
            ["GIT_AUTHOR_EMAIL"] = "pistation@users.noreply.github.com",
            ["GIT_COMMITTER_NAME"] = "Pi Station",
            ["GIT_COMMITTER_EMAIL"] = "pistation@users.noreply.github.com",
        };

        try
        {
            var seedArguments = await HasHeadAsync(projectRoot, cancellationToken).ConfigureAwait(false)
                ? new[] { "read-tree", "HEAD" }
                : ["read-tree", "--empty"];
            var seed = await RunGitAsync(
                projectRoot,
                seedArguments,
                16 * 1024,
                cancellationToken,
                environment).ConfigureAwait(false);
            EnsureSuccess(seed, "The checkpoint index could not be initialized.");
            var add = await RunGitAsync(
                projectRoot,
                ["add", "-A", "--", "."],
                16 * 1024,
                cancellationToken,
                environment).ConfigureAwait(false);
            EnsureSuccess(add, "The workspace could not be added to the checkpoint index.");
            var tree = await RunGitAsync(
                projectRoot,
                ["write-tree"],
                8 * 1024,
                cancellationToken,
                environment).ConfigureAwait(false);
            EnsureSuccess(tree, "The checkpoint tree could not be written.");
            var treeId = tree.StandardOutput.Trim();
            if (string.IsNullOrWhiteSpace(treeId))
            {
                throw Unavailable("Git returned an empty checkpoint tree identity.");
            }

            var commit = await RunGitAsync(
                projectRoot,
                ["commit-tree", treeId, "-m", $"Pi Station checkpoint ref={checkpointRef}"],
                8 * 1024,
                cancellationToken,
                environment).ConfigureAwait(false);
            EnsureSuccess(commit, "The checkpoint commit could not be written.");
            var commitId = commit.StandardOutput.Trim();
            if (string.IsNullOrWhiteSpace(commitId))
            {
                throw Unavailable("Git returned an empty checkpoint commit identity.");
            }

            var update = await RunGitAsync(
                projectRoot,
                ["update-ref", checkpointRef, commitId],
                16 * 1024,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(update, "The hidden checkpoint reference could not be updated.");
        }
        finally
        {
            try
            {
                File.Delete(temporaryIndex);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<IReadOnlyList<ThreadCheckpointFile>> ReadFileSummaryAsync(
        string projectRoot,
        string fromRef,
        string toRef,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            projectRoot,
            [
                "diff", "--numstat", "--no-renames", "--relative",
                $"{fromRef}^{{commit}}", $"{toRef}^{{commit}}", "--", ".",
            ],
            MaximumCommandOutputCharacters,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "The checkpoint file summary could not be read.");
        var files = new List<ThreadCheckpointFile>();
        foreach (var line in result.StandardOutput.ReplaceLineEndings("\n")
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t', 3);
            if (fields.Length != 3)
            {
                continue;
            }

            _ = int.TryParse(fields[0], out var additions);
            _ = int.TryParse(fields[1], out var deletions);
            files.Add(new ThreadCheckpointFile(fields[2].Replace('\\', '/'), additions, deletions));
            if (files.Count == GitChangesDefaults.MaximumCheckpointFiles)
            {
                break;
            }
        }

        return files
            .OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<bool> HasHeadAsync(
        string projectRoot,
        CancellationToken cancellationToken) =>
        await ResolveRefAsync(projectRoot, "HEAD", cancellationToken).ConfigureAwait(false) is not null;

    private static async Task<bool> HasRefAsync(
        string projectRoot,
        string checkpointRef,
        CancellationToken cancellationToken) =>
        await ResolveRefAsync(projectRoot, checkpointRef, cancellationToken).ConfigureAwait(false) is not null;

    private static async Task<string?> ResolveRefAsync(
        string projectRoot,
        string reference,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            projectRoot,
            ["rev-parse", "--verify", "--quiet", $"{reference}^{{commit}}"],
            8 * 1024,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return null;
        }

        var commit = result.StandardOutput.Trim();
        return string.IsNullOrWhiteSpace(commit) ? null : commit;
    }

    private static void ValidateDiffRequest(GetThreadCheckpointDiffRequest request)
    {
        ValidateTurnCount(request.TurnCount, allowZero: false);
        if (!Enum.IsDefined(request.Scope) ||
            request.MaximumCharacters is < 1 or > GitChangesDefaults.MaximumDiffCharacters)
        {
            throw Invalid();
        }

        if (request.RelativePath is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.RelativePath) ||
            request.RelativePath.Length > GitChangesDefaults.MaximumRelativePathLength ||
            Path.IsPathRooted(request.RelativePath) ||
            request.RelativePath.StartsWith('/') ||
            request.RelativePath.StartsWith('\\') ||
            request.RelativePath.Contains(':') ||
            request.RelativePath.Contains('\0') ||
            request.RelativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Any(static segment => segment is "." or ".."))
        {
            throw Invalid();
        }
    }

    private static void ValidateTurnCount(int turnCount, bool allowZero)
    {
        if (turnCount < (allowZero ? 0 : 1))
        {
            throw Invalid();
        }
    }

    private static void EnsureSuccess(GitCommandResult result, string message)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? string.Empty
            : $" {result.StandardError.Trim()}";
        throw Unavailable(message + detail);
    }

    private static async Task<GitCommandResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        int maximumOutputCharacters,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        startInfo.Environment["GIT_PAGER"] = "cat";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["LC_ALL"] = "C";
        if (environment is not null)
        {
            foreach (var variable in environment)
            {
                startInfo.Environment[variable.Key] = variable.Value;
            }
        }

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.fsmonitor=false");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotePath=false");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw Unavailable("Git could not be started.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw Unavailable("Git is not installed or could not be started.");
        }

        var stdoutTask = ReadBoundedAsync(process.StandardOutput, maximumOutputCharacters);
        var stderrTask = ReadBoundedAsync(process.StandardError, 16 * 1024);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw Unavailable($"Git did not respond within {CommandTimeout.TotalSeconds:0} seconds.");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new GitCommandResult(
            process.ExitCode,
            stdout.Content,
            stderr.Content,
            stdout.WasTruncated || stderr.WasTruncated);
    }

    private static async Task<BoundedText> ReadBoundedAsync(StreamReader reader, int maximumCharacters)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 16 * 1024));
        var buffer = new char[4096];
        var wasTruncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            var remaining = maximumCharacters - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(read, remaining));
            }

            wasTruncated |= read > remaining;
        }

        return new BoundedText(builder.ToString(), wasTruncated);
    }

    private static void TryKill(Process process)
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

    private static HostOperationException Invalid() => new(
        ProtocolErrorCodes.CheckpointInvalid,
        "A checkpoint request contains an invalid turn, path, scope, or result limit.");

    private static HostOperationException Unavailable(string message) => new(
        ProtocolErrorCodes.CheckpointUnavailable,
        message);

    private sealed record BoundedText(string Content, bool WasTruncated);

    private sealed record GitCommandResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool WasTruncated);
}
