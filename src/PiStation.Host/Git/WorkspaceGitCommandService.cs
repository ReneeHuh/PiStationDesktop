using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiStation.Host.Errors;
using PiStation.Host.Persistence;
using PiStation.Host.Workspaces;
using PiStation.Protocol;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Git;

public sealed class WorkspaceGitCommandService
{
    private const int MaximumOutputCharacters = 2 * 1024 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(5);
    private readonly HostDatabase _database;
    private readonly WorkspaceGitService _queries;
    private readonly ThreadWorkspaceResolver _resolver;
    private readonly HostOptions _options;
    private readonly WorkspaceOperationLocks _locks;

    public WorkspaceGitCommandService(
        HostDatabase database,
        WorkspaceGitService queries,
        ThreadWorkspaceResolver resolver,
        HostOptions options,
        WorkspaceOperationLocks? locks = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _locks = locks ?? new WorkspaceOperationLocks();
    }

    public async Task<ListGitRefsResult> ListRefsAsync(
        ListGitRefsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateListRefs(request);
        var workspace = await _resolver.ResolveAsync(
            request.Target.ProjectId,
            request.Target.ThreadId,
            cancellationToken).ConfigureAwait(false);
        if (!await IsRepositoryAsync(workspace.WorkspaceRoot, cancellationToken).ConfigureAwait(false))
        {
            return new ListGitRefsResult([], false, false, null, 0);
        }

        if (request.Refresh)
        {
            var origin = await RunAsync(
                workspace.WorkspaceRoot,
                ["remote", "get-url", "origin"],
                allowNonZeroExit: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (origin.ExitCode == 0)
            {
                await RunAsync(
                    workspace.WorkspaceRoot,
                    ["fetch", "--prune", "origin"],
                    timeout: NetworkTimeout,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        var current = (await RunAsync(
            workspace.WorkspaceRoot,
            ["symbolic-ref", "--quiet", "--short", "HEAD"],
            allowNonZeroExit: true,
            cancellationToken: cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
        var defaultRef = (await RunAsync(
            workspace.WorkspaceRoot,
            ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"],
            allowNonZeroExit: true,
            cancellationToken: cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
        if (defaultRef.StartsWith("origin/", StringComparison.Ordinal))
        {
            defaultRef = defaultRef["origin/".Length..];
        }

        var worktreeBranches = await ReadWorktreeBranchMapAsync(workspace.WorkspaceRoot, cancellationToken)
            .ConfigureAwait(false);
        var refsResult = await RunAsync(
            workspace.WorkspaceRoot,
            ["for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes"],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var refs = new List<GitRefDescriptor>();
        foreach (var raw in refsResult.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fullName = raw.TrimEnd('\r');
            if (fullName.EndsWith("/HEAD", StringComparison.Ordinal))
            {
                continue;
            }

            var isRemote = fullName.StartsWith("refs/remotes/", StringComparison.Ordinal);
            if (request.RefKind == GitRefKind.Local && isRemote && !request.IncludeMatchingRemoteRefs ||
                request.RefKind == GitRefKind.Remote && !isRemote)
            {
                continue;
            }

            var name = isRemote
                ? fullName["refs/remotes/".Length..]
                : fullName["refs/heads/".Length..];
            if (!string.IsNullOrWhiteSpace(request.Query) &&
                !name.Contains(request.Query.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var remoteName = isRemote && name.Contains('/') ? name[..name.IndexOf('/')] : null;
            var localName = isRemote && name.Contains('/') ? name[(name.IndexOf('/') + 1)..] : name;
            worktreeBranches.TryGetValue(localName, out var worktreePath);
            refs.Add(new GitRefDescriptor(
                name,
                isRemote,
                remoteName,
                !isRemote && string.Equals(name, current, StringComparison.Ordinal),
                string.Equals(localName, defaultRef, StringComparison.Ordinal),
                worktreePath));
        }

        var ordered = refs
            .OrderByDescending(static item => item.IsCurrent)
            .ThenByDescending(static item => item.IsDefault)
            .ThenBy(static item => item.IsRemote)
            .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Name, StringComparer.Ordinal)
            .ToArray();
        var page = ordered.Skip(request.Cursor).Take(request.Limit).ToArray();
        var remotes = await RunAsync(
            workspace.WorkspaceRoot,
            ["remote"],
            allowNonZeroExit: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new ListGitRefsResult(
            page,
            true,
            remotes.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(static remote => string.Equals(remote.Trim(), "origin", StringComparison.Ordinal)),
            request.Cursor + page.Length < ordered.Length ? request.Cursor + page.Length : null,
            ordered.Length);
    }

    public async Task<ListGitWorktreesResult> ListWorktreesAsync(
        ListGitWorktreesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var workspace = await _resolver.ResolveAsync(request.ProjectId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!await IsRepositoryAsync(workspace.ProjectRoot, cancellationToken).ConfigureAwait(false))
        {
            return new ListGitWorktreesResult(request.ProjectId, []);
        }

        var result = await RunAsync(
            workspace.ProjectRoot,
            ["worktree", "list", "--porcelain", "-z"],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var worktrees = new List<GitWorktreeDescriptor>();
        foreach (var record in result.StandardOutput.Split("\0\0", StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var path = fields.FirstOrDefault(static field => field.StartsWith("worktree ", StringComparison.Ordinal))?[9..];
            var head = fields.FirstOrDefault(static field => field.StartsWith("HEAD ", StringComparison.Ordinal))?[5..] ?? string.Empty;
            var branch = fields.FirstOrDefault(static field => field.StartsWith("branch refs/heads/", StringComparison.Ordinal))?
                ["branch refs/heads/".Length..];
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var dirty = Directory.Exists(path) && !string.IsNullOrEmpty((await RunAsync(
                path,
                ["status", "--porcelain=v1", "-z", "--untracked-files=all"],
                allowNonZeroExit: true,
                cancellationToken: cancellationToken).ConfigureAwait(false)).StandardOutput);
            worktrees.Add(new GitWorktreeDescriptor(
                Path.GetFullPath(path),
                branch,
                head,
                PathsEqual(path, workspace.WorkspaceRoot),
                fields.Any(static field => field.StartsWith("locked", StringComparison.Ordinal)),
                fields.Any(static field => field.StartsWith("prunable", StringComparison.Ordinal)),
                dirty));
        }

        return new ListGitWorktreesResult(request.ProjectId, worktrees);
    }

    public async Task<ExecuteWorkspaceGitCommandResult> ExecuteAsync(
        ExecuteWorkspaceGitCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateExecuteRequest(request);
        var now = DateTimeOffset.UtcNow;
        var receiptThreadId = request.Target.ThreadId ??
            (request.Command as GitCreateWorktreeCommand)?.AssignToThreadId;
        var received = new WorkspaceCommandReceipt(
            request.EnvironmentId,
            request.ClientId,
            request.CommandId,
            request.Target.ProjectId,
            receiptThreadId,
            CommandReceiptState.Received,
            null,
            now,
            now);
        var body = JsonSerializer.Serialize(request, ProtocolJsonContext.Default.ExecuteWorkspaceGitCommandRequest);
        var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var acquisition = await _database.AcquireWorkspaceReceiptAsync(received, bodyHash, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(acquisition.StoredReceipt.BodyHash, bodyHash, StringComparison.Ordinal) ||
            acquisition.StoredReceipt.Receipt.ProjectId != request.Target.ProjectId ||
            acquisition.StoredReceipt.Receipt.ThreadId != receiptThreadId)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.CommandConflict,
                "The workspace command ID was already used with a different request.");
        }

        if (!acquisition.WasCreated)
        {
            return new ExecuteWorkspaceGitCommandResult(
                acquisition.StoredReceipt.Receipt,
                acquisition.StoredReceipt.Result);
        }

        await _database.UpdateWorkspaceReceiptAsync(
            request.ClientId,
            request.CommandId,
            CommandReceiptState.Dispatching,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            var workspace = await _resolver.ResolveAsync(
                request.Target.ProjectId,
                request.Target.ThreadId,
                cancellationToken).ConfigureAwait(false);
            var lockIdentity = await ResolveLockIdentityAsync(workspace.WorkspaceRoot, cancellationToken)
                .ConfigureAwait(false);
            await using var lease = await _locks.AcquireAsync(lockIdentity, cancellationToken).ConfigureAwait(false);
            await ValidateExpectedStateAsync(request, cancellationToken).ConfigureAwait(false);
            var result = await DispatchAsync(request, workspace, cancellationToken).ConfigureAwait(false);
            var stored = await _database.UpdateWorkspaceReceiptAsync(
                request.ClientId,
                request.CommandId,
                CommandReceiptState.Completed,
                result: result,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new ExecuteWorkspaceGitCommandResult(stored.Receipt, stored.Result);
        }
        catch (HostOperationException exception)
        {
            var failure = new WorkspaceGitOperationResult(
                DescribeOperation(request.Command),
                "rejected",
                Message: exception.Message,
                Progress: [new GitProgressEntry("validation", exception.Message, IsError: true)]);
            var stored = await _database.UpdateWorkspaceReceiptAsync(
                request.ClientId,
                request.CommandId,
                CommandReceiptState.Rejected,
                exception.Code,
                failure,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return new ExecuteWorkspaceGitCommandResult(stored.Receipt, stored.Result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _database.UpdateWorkspaceReceiptAsync(
                request.ClientId,
                request.CommandId,
                CommandReceiptState.DispatchUncertain,
                ProtocolErrorCodes.DispatchUncertain,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            var failure = new WorkspaceGitOperationResult(
                DescribeOperation(request.Command),
                "failed",
                Message: exception.Message,
                Progress: [new GitProgressEntry("execute", exception.Message, IsError: true)]);
            var stored = await _database.UpdateWorkspaceReceiptAsync(
                request.ClientId,
                request.CommandId,
                CommandReceiptState.Failed,
                ProtocolErrorCodes.GitCommandFailed,
                failure,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return new ExecuteWorkspaceGitCommandResult(stored.Receipt, stored.Result);
        }
    }

    public async Task<ExecuteWorkspaceGitCommandResult?> GetResultAsync(
        ClientId clientId,
        CommandId commandId,
        CancellationToken cancellationToken = default)
    {
        var stored = await _database.GetWorkspaceReceiptAsync(clientId, commandId, cancellationToken)
            .ConfigureAwait(false);
        return stored is null ? null : new ExecuteWorkspaceGitCommandResult(stored.Receipt, stored.Result);
    }

    public async Task<WorkspaceGitOperationResult> CreateManagedWorktreeAsync(
        ProjectId projectId,
        ThreadId threadId,
        string? baseRef,
        bool startFromOrigin,
        string? branchName,
        CancellationToken cancellationToken = default)
    {
        var workspace = await _resolver.ResolveAsync(projectId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await RequireRepositoryAsync(workspace.ProjectRoot, cancellationToken).ConfigureAwait(false);
        var lockIdentity = await ResolveLockIdentityAsync(workspace.ProjectRoot, cancellationToken)
            .ConfigureAwait(false);
        await using var lease = await _locks.AcquireAsync(lockIdentity, cancellationToken).ConfigureAwait(false);

        var resolvedBase = string.IsNullOrWhiteSpace(baseRef)
            ? (await RunAsync(
                workspace.ProjectRoot,
                ["branch", "--show-current"],
                allowNonZeroExit: true,
                cancellationToken: cancellationToken).ConfigureAwait(false)).StandardOutput.Trim()
            : baseRef.Trim();
        if (string.IsNullOrWhiteSpace(resolvedBase))
        {
            resolvedBase = "HEAD";
        }

        if (startFromOrigin)
        {
            var remotes = await RunAsync(
                workspace.ProjectRoot,
                ["remote"],
                allowNonZeroExit: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!remotes.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Any(static remote => string.Equals(remote.Trim(), "origin", StringComparison.Ordinal)))
            {
                throw new HostOperationException(
                    ProtocolErrorCodes.GitNoUpstream,
                    "The repository does not have an origin remote.");
            }

            await RunAsync(
                workspace.ProjectRoot,
                ["fetch", "--prune", "origin"],
                timeout: NetworkTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var originRef = resolvedBase.StartsWith("origin/", StringComparison.Ordinal)
                ? resolvedBase
                : $"origin/{resolvedBase}";
            var exists = await RunAsync(
                workspace.ProjectRoot,
                ["show-ref", "--verify", "--quiet", $"refs/remotes/{originRef}"],
                allowNonZeroExit: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (exists.ExitCode != 0)
            {
                throw new HostOperationException(
                    ProtocolErrorCodes.GitInvalid,
                    $"Remote base ref '{originRef}' does not exist.");
            }

            resolvedBase = originRef;
        }

        return await CreateWorktreeAsync(
            workspace,
            new GitCreateWorktreeCommand(resolvedBase, branchName, threadId),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkspaceGitOperationResult> DispatchAsync(
        ExecuteWorkspaceGitCommandRequest request,
        ResolvedThreadWorkspace workspace,
        CancellationToken cancellationToken) => request.Command switch
    {
        GitInitCommand command => await InitializeAsync(workspace, command, cancellationToken).ConfigureAwait(false),
        GitPullCommand => await PullAsync(request.Target, workspace, cancellationToken).ConfigureAwait(false),
        GitCreateBranchCommand command => await CreateBranchAsync(request.Target, workspace, command, cancellationToken)
            .ConfigureAwait(false),
        GitSwitchBranchCommand command => await SwitchBranchAsync(request.Target, workspace, command, cancellationToken)
            .ConfigureAwait(false),
        GitCreateWorktreeCommand command => await CreateWorktreeAsync(workspace, command, cancellationToken)
            .ConfigureAwait(false),
        GitRemoveWorktreeCommand command => await RemoveWorktreeAsync(request.Target, workspace, command, cancellationToken)
            .ConfigureAwait(false),
        GitRunActionCommand command => await RunActionAsync(request.Target, workspace, command, cancellationToken)
            .ConfigureAwait(false),
        _ => throw Invalid("The Git command is not supported."),
    };

    private static string DescribeOperation(WorkspaceGitCommand command) => command switch
    {
        GitInitCommand => "init",
        GitPullCommand => "pull",
        GitCreateBranchCommand => "create_branch",
        GitSwitchBranchCommand => "switch_branch",
        GitCreateWorktreeCommand => "create_worktree",
        GitRemoveWorktreeCommand => "remove_worktree",
        GitRunActionCommand action => action.Action.ToString().ToLowerInvariant(),
        _ => "git",
    };

    private static async Task<WorkspaceGitOperationResult> InitializeAsync(
        ResolvedThreadWorkspace workspace,
        GitInitCommand command,
        CancellationToken cancellationToken)
    {
        if (workspace.IsWorktree)
        {
            throw Invalid("A linked worktree cannot be initialized as a repository.");
        }

        var exactRoot = await RunAsync(
            workspace.ProjectRoot,
            ["rev-parse", "--show-toplevel"],
            allowNonZeroExit: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (exactRoot.ExitCode == 0)
        {
            if (!PathsEqual(exactRoot.StandardOutput.Trim(), workspace.ProjectRoot))
            {
                throw new HostOperationException(
                    ProtocolErrorCodes.GitConflict,
                    "The project is nested inside another repository. PiStation will not create a nested repository automatically.");
            }

            return new WorkspaceGitOperationResult("init", "skipped_already_repository");
        }

        await ValidateBranchNameAsync(workspace.ProjectRoot, command.InitialBranch, cancellationToken)
            .ConfigureAwait(false);
        await RunAsync(
            workspace.ProjectRoot,
            ["init", "-b", command.InitialBranch],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new WorkspaceGitOperationResult("init", "initialized", command.InitialBranch);
    }

    private async Task<WorkspaceGitOperationResult> PullAsync(
        WorkspaceTarget target,
        ResolvedThreadWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var status = await RequireCleanStatusAsync(target, requireUpstream: true, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(status.BranchName))
        {
            throw new HostOperationException(ProtocolErrorCodes.GitDetachedHead, "Cannot pull from detached HEAD.");
        }

        var separator = status.UpstreamName!.IndexOf('/');
        if (separator > 0)
        {
            var remote = status.UpstreamName[..separator];
            await RunAsync(
                workspace.WorkspaceRoot,
                ["fetch", "--prune", remote],
                timeout: NetworkTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        status = await RequireCleanStatusAsync(target, requireUpstream: true, cancellationToken)
            .ConfigureAwait(false);
        if (status.AheadCount > 0 && status.BehindCount > 0)
        {
            throw new HostOperationException(ProtocolErrorCodes.GitDiverged, "The branch has diverged from its upstream.");
        }

        if (status.BehindCount == 0)
        {
            return new WorkspaceGitOperationResult("pull", "skipped_up_to_date", status.BranchName, status.UpstreamName);
        }

        await RunAsync(
            workspace.WorkspaceRoot,
            ["pull", "--ff-only"],
            timeout: NetworkTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new WorkspaceGitOperationResult("pull", "pulled", status.BranchName, status.UpstreamName);
    }

    private async Task<WorkspaceGitOperationResult> CreateBranchAsync(
        WorkspaceTarget target,
        ResolvedThreadWorkspace workspace,
        GitCreateBranchCommand command,
        CancellationToken cancellationToken)
    {
        await RequireRepositoryAsync(workspace.WorkspaceRoot, cancellationToken).ConfigureAwait(false);
        await ValidateBranchNameAsync(workspace.WorkspaceRoot, command.BranchName, cancellationToken)
            .ConfigureAwait(false);
        if (command.SwitchToBranch)
        {
            await RequireCleanStatusAsync(target, requireUpstream: false, cancellationToken).ConfigureAwait(false);
            await EnsureBranchSwitchAllowedAsync(target, cancellationToken).ConfigureAwait(false);
        }

        await RunAsync(
            workspace.WorkspaceRoot,
            command.SwitchToBranch
                ? ["switch", "-c", command.BranchName]
                : ["branch", command.BranchName],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (command.SwitchToBranch && target.ThreadId is not null)
        {
            await _database.UpdateThreadWorkspaceAsync(
                target.ThreadId.Value,
                workspace.WorkspaceMode,
                command.BranchName,
                workspace.IsWorktree ? workspace.WorkspaceRoot : null,
                incrementGeneration: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return new WorkspaceGitOperationResult(
            "create_branch",
            command.SwitchToBranch ? "created_and_switched" : "created",
            command.BranchName);
    }

    private async Task<WorkspaceGitOperationResult> SwitchBranchAsync(
        WorkspaceTarget target,
        ResolvedThreadWorkspace workspace,
        GitSwitchBranchCommand command,
        CancellationToken cancellationToken)
    {
        await RequireCleanStatusAsync(target, requireUpstream: false, cancellationToken).ConfigureAwait(false);
        await EnsureBranchSwitchAllowedAsync(target, cancellationToken).ConfigureAwait(false);
        var localExists = (await RunAsync(
            workspace.WorkspaceRoot,
            ["show-ref", "--verify", "--quiet", $"refs/heads/{command.BranchName}"],
            allowNonZeroExit: true,
            cancellationToken: cancellationToken).ConfigureAwait(false)).ExitCode == 0;
        if (localExists)
        {
            await RunAsync(workspace.WorkspaceRoot, ["switch", command.BranchName], cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var remoteExists = (await RunAsync(
                workspace.WorkspaceRoot,
                ["show-ref", "--verify", "--quiet", $"refs/remotes/{command.BranchName}"],
                allowNonZeroExit: true,
                cancellationToken: cancellationToken).ConfigureAwait(false)).ExitCode == 0;
            if (!remoteExists || !command.BranchName.Contains('/'))
            {
                throw Invalid($"Branch '{command.BranchName}' does not exist.");
            }

            var localName = command.BranchName[(command.BranchName.IndexOf('/') + 1)..];
            await ValidateBranchNameAsync(workspace.WorkspaceRoot, localName, cancellationToken)
                .ConfigureAwait(false);
            await RunAsync(
                workspace.WorkspaceRoot,
                ["switch", "--track", "-c", localName, command.BranchName],
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var branch = (await RunAsync(
            workspace.WorkspaceRoot,
            ["branch", "--show-current"],
            cancellationToken: cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
        if (target.ThreadId is not null)
        {
            await _database.UpdateThreadWorkspaceAsync(
                target.ThreadId.Value,
                workspace.WorkspaceMode,
                branch,
                workspace.IsWorktree ? workspace.WorkspaceRoot : null,
                incrementGeneration: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return new WorkspaceGitOperationResult("switch_branch", "switched", branch);
    }

    private async Task<WorkspaceGitOperationResult> CreateWorktreeAsync(
        ResolvedThreadWorkspace workspace,
        GitCreateWorktreeCommand command,
        CancellationToken cancellationToken)
    {
        await RequireRepositoryAsync(workspace.ProjectRoot, cancellationToken).ConfigureAwait(false);
        var threadId = command.AssignToThreadId
            ?? throw Invalid("A managed PiStation worktree must be assigned to a thread.");
        var thread = await _database.GetThreadAsync(threadId, cancellationToken).ConfigureAwait(false)
            ?? throw new HostOperationException(ProtocolErrorCodes.ThreadNotFound, $"Thread '{threadId}' was not found.");
        if (thread.ProjectId != workspace.Project.ProjectId)
        {
            throw Invalid("The worktree thread does not belong to the project.");
        }
        if (thread.WorkspaceMode == ThreadWorkspaceMode.Worktree || !string.IsNullOrWhiteSpace(thread.WorktreePath))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.WorktreeAlreadyExists,
                "The thread already owns a worktree.");
        }
        if ((await _database.ListThreadCheckpointsAsync(threadId, cancellationToken).ConfigureAwait(false)).Count != 0)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.GitConflict,
                "A worktree cannot be assigned after the thread has captured turn checkpoints.");
        }

        var branch = string.IsNullOrWhiteSpace(command.NewBranchName)
            ? $"pistation/{SanitizeBranchComponent(thread.Title)}-{thread.ThreadId.Value[..Math.Min(8, thread.ThreadId.Value.Length)]}"
            : command.NewBranchName.Trim();
        await ValidateBranchNameAsync(workspace.ProjectRoot, branch, cancellationToken).ConfigureAwait(false);
        var worktreePath = Path.GetFullPath(Path.Combine(
            _options.WorktreeRoot,
            workspace.Project.ProjectId.Value,
            thread.ThreadId.Value));
        if (!IsContained(_options.WorktreeRoot, worktreePath))
        {
            throw Invalid("The generated worktree path is invalid.");
        }
        if (Directory.Exists(worktreePath) || File.Exists(worktreePath))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.WorktreeAlreadyExists,
                "The managed worktree path already exists.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
        var worktreeAdded = false;
        try
        {
            await RunAsync(
                workspace.ProjectRoot,
                ["worktree", "add", "-b", branch, worktreePath, command.BaseRef],
                timeout: NetworkTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            worktreeAdded = true;
            if (File.Exists(Path.Combine(worktreePath, ".gitmodules")))
            {
                _ = await RunAsync(
                    worktreePath,
                    ["submodule", "update", "--init", "--recursive"],
                    timeout: NetworkTimeout,
                    allowNonZeroExit: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            await _database.UpdateThreadWorkspaceAsync(
                threadId,
                ThreadWorkspaceMode.Worktree,
                branch,
                worktreePath,
                incrementGeneration: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (worktreeAdded)
            {
                _ = await RunAsync(
                    workspace.ProjectRoot,
                    ["worktree", "remove", "--force", worktreePath],
                    timeout: NetworkTimeout,
                    allowNonZeroExit: true,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
                _ = await RunAsync(
                    workspace.ProjectRoot,
                    ["branch", "-D", branch],
                    allowNonZeroExit: true,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        return new WorkspaceGitOperationResult("create_worktree", "created", branch, WorktreePath: worktreePath);
    }

    private async Task<WorkspaceGitOperationResult> RemoveWorktreeAsync(
        WorkspaceTarget target,
        ResolvedThreadWorkspace workspace,
        GitRemoveWorktreeCommand command,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(command.WorktreePath);
        if (!IsContained(_options.WorktreeRoot, fullPath))
        {
            throw Invalid("Only PiStation-managed worktrees can be removed.");
        }

        var threadId = target.ThreadId
            ?? throw Invalid("A managed worktree removal must target its owning thread.");
        var thread = await _database.GetThreadAsync(threadId, cancellationToken).ConfigureAwait(false)
            ?? throw new HostOperationException(ProtocolErrorCodes.ThreadNotFound, $"Thread '{threadId}' was not found.");
        if (thread.ProjectId != target.ProjectId ||
            string.IsNullOrWhiteSpace(thread.WorktreePath) ||
            !PathsEqual(thread.WorktreePath, fullPath))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.WorktreeOwnershipMismatch,
                "The worktree is not owned by the targeted thread.");
        }
        if ((await _database.ListThreadCheckpointsAsync(threadId, cancellationToken).ConfigureAwait(false)).Count != 0)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.GitConflict,
                "This worktree has turn checkpoints. Delete the thread before removing its workspace.");
        }

        var dirty = Directory.Exists(fullPath) && !string.IsNullOrEmpty((await RunAsync(
            fullPath,
            ["status", "--porcelain=v1", "-z", "--untracked-files=all"],
            allowNonZeroExit: true,
            cancellationToken: cancellationToken).ConfigureAwait(false)).StandardOutput);
        if (dirty && !command.Force)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.GitDirtyWorktree,
                "The worktree has uncommitted changes and cannot be removed.");
        }

        if (command.Force && !string.Equals(command.ConfirmationToken, fullPath, PathComparison()))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.WorktreeConfirmationRequired,
                "Force removal requires confirmation of the exact worktree path.");
        }

        var arguments = new List<string> { "worktree", "remove" };
        if (command.Force)
        {
            arguments.Add("--force");
        }

        arguments.Add(fullPath);
        await RunAsync(workspace.ProjectRoot, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _database.UpdateThreadWorkspaceAsync(
            threadId,
            ThreadWorkspaceMode.Local,
            branchName: null,
            worktreePath: null,
            incrementGeneration: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new WorkspaceGitOperationResult("remove_worktree", "removed", WorktreePath: fullPath);
    }

    private async Task<WorkspaceGitOperationResult> RunActionAsync(
        WorkspaceTarget target,
        ResolvedThreadWorkspace workspace,
        GitRunActionCommand command,
        CancellationToken cancellationToken)
    {
        var progress = new List<GitProgressEntry>();
        string? commitSha = null;
        string? branch = null;
        if (command.Action is GitActionKind.Commit or GitActionKind.CommitPush)
        {
            var status = await _queries.GetChangesAsync(
                new GetProjectChangesRequest(target.ProjectId, ThreadId: target.ThreadId),
                cancellationToken).ConfigureAwait(false);
            RequireCommittable(status);
            progress.Add(new GitProgressEntry("commit", "Staging changes"));
            string[] stagedPaths;
            List<string> commitArguments;
            if (command.FilePaths is { Count: > 0 })
            {
                ValidatePaths(command.FilePaths);
                var add = new List<string> { "--literal-pathspecs", "add", "-A", "--" };
                add.AddRange(command.FilePaths);
                await RunAsync(workspace.WorkspaceRoot, add, cancellationToken: cancellationToken).ConfigureAwait(false);
                stagedPaths = command.FilePaths.ToArray();
                commitArguments = ["--literal-pathspecs", "commit", "--only", "-m"];
            }
            else
            {
                await RunAsync(workspace.WorkspaceRoot, ["add", "-A"], cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var staged = await RunAsync(
                    workspace.WorkspaceRoot,
                    ["diff", "--cached", "--name-only"],
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                stagedPaths = staged.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                commitArguments = ["commit", "-m"];
            }

            if (stagedPaths.Length == 0)
            {
                return new WorkspaceGitOperationResult(
                    "commit",
                    "skipped_no_changes",
                    Progress: progress);
            }

            var message = NormalizeCommitMessage(command.CommitMessage, stagedPaths);
            commitArguments.Add(message);
            if (command.FilePaths is { Count: > 0 })
            {
                commitArguments.Add("--");
                commitArguments.AddRange(command.FilePaths);
            }
            progress.Add(new GitProgressEntry("commit", "Running commit hooks"));
            var commit = await RunAsync(
                workspace.WorkspaceRoot,
                commitArguments,
                timeout: NetworkTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var line in commit.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                progress.Add(new GitProgressEntry("hook", line.TrimEnd('\r')));
            }

            commitSha = (await RunAsync(
                workspace.WorkspaceRoot,
                ["rev-parse", "HEAD"],
                cancellationToken: cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
            progress.Add(new GitProgressEntry("commit", $"Created {commitSha[..Math.Min(8, commitSha.Length)]}"));
        }

        if (command.Action is GitActionKind.Push or GitActionKind.CommitPush)
        {
            var status = await _queries.GetChangesAsync(
                new GetProjectChangesRequest(target.ProjectId, ThreadId: target.ThreadId),
                cancellationToken)
                .ConfigureAwait(false);
            if (!status.IsRepository)
            {
                throw new HostOperationException(ProtocolErrorCodes.GitUnavailable, "The workspace is not a Git repository.");
            }
            if (status.HasConflicts || status.OperationState != GitRepositoryOperationState.None)
            {
                throw new HostOperationException(ProtocolErrorCodes.GitConflict, "Resolve the current Git operation before pushing.");
            }
            branch = status.BranchName;
            if (string.IsNullOrWhiteSpace(branch))
            {
                throw new HostOperationException(ProtocolErrorCodes.GitDetachedHead, "Cannot push from detached HEAD.");
            }

            if (status.BehindCount > 0)
            {
                throw new HostOperationException(ProtocolErrorCodes.GitDiverged, "Pull or rebase before pushing.");
            }

            progress.Add(new GitProgressEntry("push", "Pushing branch"));
            if (string.IsNullOrWhiteSpace(status.UpstreamName))
            {
                var remote = string.IsNullOrWhiteSpace(command.RemoteName) ? "origin" : command.RemoteName.Trim();
                var remoteExists = (await RunAsync(
                    workspace.WorkspaceRoot,
                    ["remote", "get-url", remote],
                    allowNonZeroExit: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false)).ExitCode == 0;
                if (!remoteExists)
                {
                    throw new HostOperationException(
                        ProtocolErrorCodes.GitNoUpstream,
                        $"Remote '{remote}' is not configured.");
                }

                await RunAsync(
                    workspace.WorkspaceRoot,
                    ["push", "-u", remote, $"HEAD:refs/heads/{branch}"],
                    timeout: NetworkTimeout,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RunAsync(
                    workspace.WorkspaceRoot,
                    ["push"],
                    timeout: NetworkTimeout,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            progress.Add(new GitProgressEntry("push", "Push completed"));
        }

        return new WorkspaceGitOperationResult(
            command.Action.ToString().ToLowerInvariant(),
            command.Action == GitActionKind.Commit ? "committed" : command.Action == GitActionKind.Push ? "pushed" : "committed_and_pushed",
            branch,
            CommitSha: commitSha,
            Progress: progress);
    }

    private async Task<GetProjectChangesResult> RequireCleanStatusAsync(
        WorkspaceTarget target,
        bool requireUpstream,
        CancellationToken cancellationToken)
    {
        var status = await _queries.GetChangesAsync(
            new GetProjectChangesRequest(target.ProjectId, ThreadId: target.ThreadId),
            cancellationToken).ConfigureAwait(false);
        if (!status.IsRepository)
        {
            throw new HostOperationException(ProtocolErrorCodes.GitUnavailable, "The workspace is not a Git repository.");
        }

        if (status.Changes.Count != 0)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.GitDirtyWorktree,
                $"The worktree has {status.Changes.Count} uncommitted change(s).");
        }

        if (status.HasConflicts || status.OperationState != GitRepositoryOperationState.None)
        {
            throw new HostOperationException(ProtocolErrorCodes.GitConflict, "A Git operation or conflict is in progress.");
        }

        if (requireUpstream && string.IsNullOrWhiteSpace(status.UpstreamName))
        {
            throw new HostOperationException(ProtocolErrorCodes.GitNoUpstream, "The current branch has no upstream.");
        }

        return status;
    }

    private async Task ValidateExpectedStateAsync(
        ExecuteWorkspaceGitCommandRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Command is GitInitCommand)
        {
            return;
        }

        var status = await _queries.GetChangesAsync(
            new GetProjectChangesRequest(request.Target.ProjectId, ThreadId: request.Target.ThreadId),
            cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(request.ExpectedBranchName) &&
            !string.Equals(request.ExpectedBranchName, status.BranchName, StringComparison.Ordinal))
        {
            throw new HostOperationException(ProtocolErrorCodes.GitConflict, "The current branch changed before the command ran.");
        }

        if (!string.IsNullOrWhiteSpace(request.ExpectedStatusToken) &&
            !string.Equals(request.ExpectedStatusToken, status.StatusToken, StringComparison.Ordinal))
        {
            throw new HostOperationException(ProtocolErrorCodes.GitConflict, "The worktree changed before the command ran.");
        }

        if (!string.IsNullOrWhiteSpace(request.ExpectedHeadSha))
        {
            var workspace = await _resolver.ResolveAsync(
                request.Target.ProjectId,
                request.Target.ThreadId,
                cancellationToken).ConfigureAwait(false);
            var head = (await RunAsync(
                workspace.WorkspaceRoot,
                ["rev-parse", "HEAD"],
                cancellationToken: cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
            if (!string.Equals(request.ExpectedHeadSha, head, StringComparison.OrdinalIgnoreCase))
            {
                throw new HostOperationException(ProtocolErrorCodes.GitConflict, "HEAD changed before the command ran.");
            }
        }
    }

    private async Task EnsureBranchSwitchAllowedAsync(WorkspaceTarget target, CancellationToken cancellationToken)
    {
        if (target.ThreadId is null)
        {
            return;
        }

        var checkpoints = await _database.ListThreadCheckpointsAsync(target.ThreadId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (checkpoints.Count != 0)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.GitConflict,
                "This thread already has turns. Open the branch in a new thread to preserve checkpoint history.");
        }
    }

    private static void RequireCommittable(GetProjectChangesResult status)
    {
        if (!status.IsRepository)
        {
            throw new HostOperationException(ProtocolErrorCodes.GitUnavailable, "The workspace is not a Git repository.");
        }

        if (status.HasConflicts || status.OperationState != GitRepositoryOperationState.None)
        {
            throw new HostOperationException(ProtocolErrorCodes.GitConflict, "Resolve Git conflicts before committing.");
        }

        if (status.Changes.Count == 0)
        {
            throw new HostOperationException(ProtocolErrorCodes.GitInvalid, "There are no changes to commit.");
        }
    }

    private static void ValidateListRefs(ListGitRefsRequest request)
    {
        if (request.Cursor < 0 || request.Limit is < 1 or > GitOperationsDefaults.MaximumRefs ||
            request.Query?.Length > GitOperationsDefaults.MaximumRefQueryLength)
        {
            throw Invalid("The Git ref query is invalid.");
        }
    }

    private void ValidateExecuteRequest(ExecuteWorkspaceGitCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Command);
        if (request.ProtocolVersion != ProtocolVersion.Current || request.EnvironmentId != _database.EnvironmentId ||
            string.IsNullOrWhiteSpace(request.ClientId.Value) || string.IsNullOrWhiteSpace(request.CommandId.Value) ||
            string.IsNullOrWhiteSpace(request.Target.ProjectId.Value))
        {
            throw Invalid("The workspace Git command envelope is invalid.");
        }

        if (request.Command is GitRunActionCommand action)
        {
            if (action.CommitMessage?.Length > GitOperationsDefaults.MaximumCommitMessageLength ||
                action.FilePaths?.Count > GitOperationsDefaults.MaximumSelectedPaths)
            {
                throw Invalid("The Git action exceeds protocol limits.");
            }
            if (!string.IsNullOrWhiteSpace(action.RemoteName) && !IsSafeRemoteName(action.RemoteName))
            {
                throw Invalid("The Git remote name is invalid.");
            }
        }

        if (request.Command is GitCreateWorktreeCommand worktree && string.IsNullOrWhiteSpace(worktree.BaseRef))
        {
            throw Invalid("A worktree base ref is required.");
        }
        if (request.Command is GitCreateWorktreeCommand assignedWorktree &&
            request.Target.ThreadId is { } targetThreadId &&
            assignedWorktree.AssignToThreadId is { } assignedThreadId &&
            targetThreadId != assignedThreadId)
        {
            throw Invalid("The worktree target and assigned thread do not match.");
        }

        if (request.Command is GitRemoveWorktreeCommand remove && string.IsNullOrWhiteSpace(remove.WorktreePath))
        {
            throw Invalid("A worktree path is required.");
        }
    }

    private static void ValidatePaths(IReadOnlyList<string> paths)
    {
        if (paths.Count is < 1 or > GitOperationsDefaults.MaximumSelectedPaths ||
            paths.Any(static path =>
                string.IsNullOrWhiteSpace(path) ||
                Path.IsPathRooted(path) ||
                path.Contains('\0') ||
                path.Replace('\\', '/').Split('/').Any(static segment => segment is ".." or ".")))
        {
            throw Invalid("One or more selected Git paths are invalid.");
        }
    }

    private static bool IsSafeRemoteName(string remoteName) =>
        remoteName.Length <= 255 &&
        remoteName[0] != '-' &&
        remoteName.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static string NormalizeCommitMessage(string? message, string[] stagedPaths)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            return message.Trim();
        }

        var first = Path.GetFileName(stagedPaths[0].Trim());
        return stagedPaths.Length == 1 ? $"Update {first}" : $"Update {stagedPaths.Length} files";
    }

    private static async Task ValidateBranchNameAsync(
        string cwd,
        string branch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch.Length > 255)
        {
            throw Invalid("The branch name is invalid.");
        }

        var result = await RunAsync(
            cwd,
            ["check-ref-format", "--branch", branch],
            allowNonZeroExit: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw Invalid($"Branch name '{branch}' is invalid.");
        }
    }

    private static async Task RequireRepositoryAsync(string cwd, CancellationToken cancellationToken)
    {
        if (!await IsRepositoryAsync(cwd, cancellationToken).ConfigureAwait(false))
        {
            throw new HostOperationException(ProtocolErrorCodes.GitUnavailable, "The workspace is not a Git repository.");
        }
    }

    private static async Task<bool> IsRepositoryAsync(string cwd, CancellationToken cancellationToken) =>
        (await RunAsync(
            cwd,
            ["rev-parse", "--is-inside-work-tree"],
            allowNonZeroExit: true,
            cancellationToken: cancellationToken).ConfigureAwait(false)).ExitCode == 0;

    private static async Task<string> ResolveLockIdentityAsync(string cwd, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            cwd,
            ["rev-parse", "--git-common-dir"],
            allowNonZeroExit: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return cwd;
        }

        var value = result.StandardOutput.Trim();
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(cwd, value));
    }

    private static async Task<Dictionary<string, string>> ReadWorktreeBranchMapAsync(
        string cwd,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            cwd,
            ["worktree", "list", "--porcelain", "-z"],
            allowNonZeroExit: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in result.StandardOutput.Split("\0\0", StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var path = fields.FirstOrDefault(static field => field.StartsWith("worktree ", StringComparison.Ordinal))?[9..];
            var branch = fields.FirstOrDefault(static field => field.StartsWith("branch refs/heads/", StringComparison.Ordinal))?
                ["branch refs/heads/".Length..];
            if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(branch))
            {
                values[branch] = Path.GetFullPath(path);
            }
        }

        return values;
    }

    private static string SanitizeBranchComponent(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (builder.Length != 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-') switch
        {
            "" => "thread",
            var result => result[..Math.Min(48, result.Length)],
        };
    }

    private static bool IsContained(string root, string candidate)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var canonicalCandidate = Path.GetFullPath(candidate);
        return canonicalCandidate.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, PathComparison());
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathComparison());

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static HostOperationException Invalid(string message) =>
        new(ProtocolErrorCodes.GitInvalid, message);

    private static async Task<GitCommandResult> RunAsync(
        string cwd,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null,
        bool allowNonZeroExit = false,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout ?? DefaultTimeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GIT_PAGER"] = "cat";
        process.StartInfo.Environment["PAGER"] = "cat";
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                throw new HostOperationException(ProtocolErrorCodes.GitUnavailable, "Git could not be started.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new HostOperationException(ProtocolErrorCodes.GitUnavailable, $"Git could not be started: {exception.Message}");
        }

        var stdoutTask = ReadBoundedAsync(process.StandardOutput, linked.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new HostOperationException(ProtocolErrorCodes.GitCommandFailed, "The Git command timed out.");
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var result = new GitCommandResult(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false));
        if (result.ExitCode != 0 && !allowNonZeroExit)
        {
            var error = result.StandardError;
            var errorCode = error.Contains("Authentication", StringComparison.OrdinalIgnoreCase) ||
                            error.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
                ? ProtocolErrorCodes.GitAuthenticationRequired
                : error.Contains("already checked out at", StringComparison.OrdinalIgnoreCase)
                    ? ProtocolErrorCodes.WorktreeBranchInUse
                    : error.Contains("not possible to fast-forward", StringComparison.OrdinalIgnoreCase)
                        ? ProtocolErrorCodes.GitDiverged
                        : error.Contains("no upstream branch", StringComparison.OrdinalIgnoreCase)
                            ? ProtocolErrorCodes.GitNoUpstream
                            : error.Contains("conflict", StringComparison.OrdinalIgnoreCase)
                                ? ProtocolErrorCodes.GitConflict
                                : ProtocolErrorCodes.GitCommandFailed;
            throw new HostOperationException(errorCode, SanitizeError(result.StandardError));
        }

        return result;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[8192];
        var builder = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var remaining = MaximumOutputCharacters - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(read, remaining));
            }
        }

        return builder.ToString();
    }

    private static string SanitizeError(string stderr)
    {
        var message = stderr.Trim();
        return string.IsNullOrWhiteSpace(message)
            ? "The Git command failed."
            : message[..Math.Min(message.Length, 2_000)];
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
        catch
        {
        }
    }

    private sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError);
}
