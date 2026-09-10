using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using PiStation.Host.Errors;
using PiStation.Host.Persistence;
using PiStation.Host.Workspaces;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host.Git;

public sealed class WorkspaceGitService(
    HostDatabase database,
    ThreadWorkspaceResolver? workspaceResolver = null)
{
    private const int MaximumStatusCharacters = 2 * 1024 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly HostDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly ThreadWorkspaceResolver _workspaceResolver = workspaceResolver ?? new ThreadWorkspaceResolver(database);

    public async Task<GetProjectChangesResult> GetChangesAsync(
        GetProjectChangesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ProjectId.Value) ||
            request.MaximumResults is < 1 or > GitChangesDefaults.MaximumResults)
        {
            throw InvalidRequest();
        }

        var workspace = await _workspaceResolver.ResolveAsync(
            request.ProjectId,
            request.ThreadId,
            cancellationToken).ConfigureAwait(false);
        var projectRoot = workspace.WorkspaceRoot;
        var repository = await FindRepositoryAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (repository is null)
        {
            return new GetProjectChangesResult(
                request.ProjectId,
                IsRepository: false,
                BranchName: string.Empty,
                UpstreamName: null,
                AheadCount: 0,
                BehindCount: 0,
                Changes: [],
                IsTruncated: false,
                WorkspacePath: projectRoot,
                IsWorktree: workspace.IsWorktree);
        }

        var status = await RunGitAsync(
            projectRoot,
            ["status", "--porcelain=v1", "-z", "--branch", "--untracked-files=all", "--", "."],
            MaximumStatusCharacters,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(status, "Git status could not be read.");

        var output = CompleteNullTerminatedOutput(status.StandardOutput, status.WasTruncated);
        var entries = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var branch = entries.Length != 0 && entries[0].StartsWith("## ", StringComparison.Ordinal)
            ? ParseBranch(entries[0][3..])
            : new BranchInfo("Detached HEAD", null, 0, 0);
        var firstChangeIndex = entries.Length != 0 && entries[0].StartsWith("## ", StringComparison.Ordinal)
            ? 1
            : 0;
        var projectPrefix = GetProjectPrefix(repository, projectRoot);
        var changes = ParseChanges(entries, firstChangeIndex, projectPrefix);
        var statistics = new Dictionary<string, (int Additions, int Deletions)>(
            await ReadLineStatisticsAsync(projectRoot, cancellationToken).ConfigureAwait(false),
            StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes.Where(static change => change.WorkingTreeStatus == GitFileStatus.Untracked))
        {
            if (!statistics.ContainsKey(change.RelativePath))
            {
                statistics[change.RelativePath] = (
                    await CountTextLinesAsync(projectRoot, change.RelativePath, cancellationToken).ConfigureAwait(false),
                    0);
            }
        }
        var ordered = changes
            .Select(change => statistics.TryGetValue(change.RelativePath, out var counts)
                ? change with { Additions = counts.Additions, Deletions = counts.Deletions }
                : change)
            .OrderBy(static change => change.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static change => change.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var selected = ordered.Take(request.MaximumResults).ToArray();
        var head = await RunGitAsync(
            projectRoot,
            ["rev-parse", "--verify", "HEAD"],
            8 * 1024,
            cancellationToken).ConfigureAwait(false);

        return new GetProjectChangesResult(
            request.ProjectId,
            IsRepository: true,
            branch.Name,
            branch.Upstream,
            branch.Ahead,
            branch.Behind,
            selected,
            status.WasTruncated || ordered.Length > selected.Length,
            selected.Any(static change => change.StagedStatus == GitFileStatus.Unmerged ||
                                          change.WorkingTreeStatus == GitFileStatus.Unmerged),
            await ReadOperationStateAsync(projectRoot, cancellationToken).ConfigureAwait(false),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output))).ToLowerInvariant(),
            projectRoot,
            workspace.IsWorktree,
            head.ExitCode == 0 ? head.StandardOutput.Trim() : null);
    }

    public async Task<GetProjectChangeDiffResult> GetDiffAsync(
        GetProjectChangeDiffRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRelativePath(request.RelativePath, request.MaximumCharacters);
        var workspace = await _workspaceResolver.ResolveAsync(
            request.ProjectId,
            request.ThreadId,
            cancellationToken).ConfigureAwait(false);
        var projectRoot = workspace.WorkspaceRoot;
        var normalizedPath = request.RelativePath.Replace('\\', '/');
        var platformPath = normalizedPath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, platformPath));
        if (!IsContained(projectRoot, fullPath) || HasReparsePoint(projectRoot, platformPath))
        {
            throw InvalidRequest();
        }

        if (await FindRepositoryAsync(projectRoot, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.GitUnavailable,
                "The selected project is not inside a Git repository.");
        }

        var staged = await RunDiffAsync(projectRoot, normalizedPath, staged: true, request.MaximumCharacters, request.IgnoreWhitespace, cancellationToken)
            .ConfigureAwait(false);
        var workingTree = await RunDiffAsync(projectRoot, normalizedPath, staged: false, request.MaximumCharacters, request.IgnoreWhitespace, cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(staged, "The staged diff could not be read.");
        EnsureSuccess(workingTree, "The working-tree diff could not be read.");
        var hasStagedChanges = !string.IsNullOrWhiteSpace(staged.StandardOutput);
        var hasWorkingTreeChanges = !string.IsNullOrWhiteSpace(workingTree.StandardOutput);
        var isUntracked = false;
        GitCommandResult? untracked = null;
        if (!hasStagedChanges && !hasWorkingTreeChanges && File.Exists(fullPath))
        {
            var pathStatus = await RunGitAsync(
                projectRoot,
                ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--", normalizedPath],
                16 * 1024,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(pathStatus, "Git status could not be read for the selected file.");
            isUntracked = pathStatus.StandardOutput.StartsWith("?? ", StringComparison.Ordinal);
            if (isUntracked)
            {
                untracked = await RunGitAsync(
                    projectRoot,
                    ["diff", "--no-index", "--no-color", "--no-ext-diff", "--no-textconv", "--", "/dev/null", normalizedPath],
                    request.MaximumCharacters,
                    cancellationToken).ConfigureAwait(false);
                EnsureSuccess(untracked, "The untracked-file diff could not be read.", allowDiffExitCode: true);
            }
        }

        var sections = new List<string>();
        if (hasStagedChanges)
        {
            sections.Add("STAGED CHANGES\n\n" + staged.StandardOutput.TrimEnd());
        }

        if (hasWorkingTreeChanges)
        {
            sections.Add("WORKING TREE CHANGES\n\n" + workingTree.StandardOutput.TrimEnd());
        }

        if (isUntracked && untracked is not null)
        {
            sections.Add("UNTRACKED FILE\n\n" + untracked.StandardOutput.TrimEnd());
        }

        var content = sections.Count == 0
            ? "No textual diff is available for this change."
            : string.Join("\n\n", sections);
        content = RemoveAbsoluteProjectPath(content, projectRoot);
        var combinedWasTruncated = content.Length > request.MaximumCharacters;
        if (combinedWasTruncated)
        {
            content = content[..request.MaximumCharacters];
        }

        return new GetProjectChangeDiffResult(
            request.ProjectId,
            normalizedPath,
            content,
            hasStagedChanges,
            hasWorkingTreeChanges,
            isUntracked,
            combinedWasTruncated || staged.WasTruncated || workingTree.WasTruncated || untracked?.WasTruncated == true);
    }

    private static async Task<IReadOnlyDictionary<string, (int Additions, int Deletions)>> ReadLineStatisticsAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            projectRoot,
            ["diff", "--numstat", "HEAD", "--", "."],
            2 * 1024 * 1024,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            result = await RunGitAsync(
                projectRoot,
                ["diff", "--cached", "--numstat", "--", "."],
                2 * 1024 * 1024,
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                return new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
            }
        }

        var values = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t', 3);
            if (parts.Length != 3)
            {
                continue;
            }

            _ = int.TryParse(parts[0], out var additions);
            _ = int.TryParse(parts[1], out var deletions);
            values[parts[2].Replace('\\', '/')] = (additions, deletions);
        }

        return values;
    }

    private static async Task<int> CountTextLinesAsync(
        string projectRoot,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var probe = new byte[Math.Min(8 * 1024, checked((int)Math.Min(stream.Length, 8 * 1024)))];
            var read = await stream.ReadAsync(probe, cancellationToken).ConfigureAwait(false);
            if (probe.AsSpan(0, read).Contains((byte)0))
            {
                return 0;
            }

            stream.Position = 0;
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            var lines = 0;
            while (lines < 1_000_000 && await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                lines++;
            }

            return lines;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static async Task<GitRepositoryOperationState> ReadOperationStateAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        foreach (var (name, state) in new[]
                 {
                     ("MERGE_HEAD", GitRepositoryOperationState.Merge),
                     ("rebase-merge", GitRepositoryOperationState.Rebase),
                     ("rebase-apply", GitRepositoryOperationState.Rebase),
                     ("REBASE_HEAD", GitRepositoryOperationState.Rebase),
                     ("CHERRY_PICK_HEAD", GitRepositoryOperationState.CherryPick),
                     ("REVERT_HEAD", GitRepositoryOperationState.Revert),
                     ("BISECT_LOG", GitRepositoryOperationState.Bisect),
                 })
        {
            var result = await RunGitAsync(
                projectRoot,
                ["rev-parse", "--git-path", name],
                8 * 1024,
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0 &&
                (File.Exists(result.StandardOutput.Trim()) || Directory.Exists(result.StandardOutput.Trim())))
            {
                return state;
            }
        }

        return GitRepositoryOperationState.None;
    }

    private static async Task<string?> FindRepositoryAsync(string projectRoot, CancellationToken cancellationToken)
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
        if (!repositoryRoot.Equals(projectRoot, PathComparison) && !IsContained(repositoryRoot, projectRoot))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.GitUnavailable,
                "Git resolved a repository outside the selected project's ancestry.");
        }

        return repositoryRoot;
    }

    private static Task<GitCommandResult> RunDiffAsync(
        string projectRoot,
        string relativePath,
        bool staged,
        int maximumCharacters,
        bool ignoreWhitespace,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "diff" };
        if (ignoreWhitespace) arguments.Add("--ignore-all-space");
        if (staged)
        {
            arguments.Add("--cached");
        }

        arguments.AddRange(["--no-color", "--no-ext-diff", "--no-textconv", "--relative", "--unified=3", "--", relativePath]);
        return RunGitAsync(
            projectRoot,
            arguments,
            maximumCharacters,
            cancellationToken);
    }

    private static List<ProjectChange> ParseChanges(
        string[] entries,
        int firstChangeIndex,
        string projectPrefix)
    {
        var changes = new List<ProjectChange>();
        for (var index = firstChangeIndex; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.Length < 4)
            {
                continue;
            }

            var indexCode = entry[0];
            var workingCode = entry[1];
            var repositoryPath = entry[3..].Replace('\\', '/');
            string? originalRepositoryPath = null;
            if (indexCode is 'R' or 'C' && index + 1 < entries.Length)
            {
                originalRepositoryPath = entries[++index].Replace('\\', '/');
            }

            var relativePath = ToProjectRelative(repositoryPath, projectPrefix);
            var originalRelativePath = originalRepositoryPath is null
                ? null
                : ToProjectRelative(originalRepositoryPath, projectPrefix);
            relativePath ??= originalRelativePath;
            if (relativePath is null)
            {
                continue;
            }

            var unmerged = IsUnmerged(indexCode, workingCode);
            var untracked = indexCode == '?' && workingCode == '?';
            var stagedStatus = unmerged
                ? GitFileStatus.Unmerged
                : untracked ? GitFileStatus.None : MapStatus(indexCode);
            var workingTreeStatus = unmerged
                ? GitFileStatus.Unmerged
                : untracked ? GitFileStatus.Untracked : MapStatus(workingCode);
            changes.Add(new ProjectChange(
                relativePath,
                relativePath.Split('/')[^1],
                originalRelativePath,
                stagedStatus,
                workingTreeStatus));
        }

        return changes;
    }

    private static GitFileStatus MapStatus(char status) => status switch
    {
        'A' => GitFileStatus.Added,
        'M' => GitFileStatus.Modified,
        'D' => GitFileStatus.Deleted,
        'R' => GitFileStatus.Renamed,
        'C' => GitFileStatus.Copied,
        'T' => GitFileStatus.TypeChanged,
        'U' => GitFileStatus.Unmerged,
        '?' => GitFileStatus.Untracked,
        _ => GitFileStatus.None,
    };

    private static bool IsUnmerged(char indexStatus, char workingTreeStatus) =>
        (indexStatus, workingTreeStatus) is
            ('D', 'D') or ('A', 'U') or ('U', 'D') or ('U', 'A') or ('D', 'U') or ('A', 'A') or ('U', 'U');

    private static BranchInfo ParseBranch(string value)
    {
        var ahead = ParseCounter(value, "ahead");
        var behind = ParseCounter(value, "behind");
        var bracket = value.IndexOf(" [", StringComparison.Ordinal);
        var branchAndUpstream = bracket >= 0 ? value[..bracket] : value;
        if (branchAndUpstream.StartsWith("No commits yet on ", StringComparison.Ordinal))
        {
            return new BranchInfo(branchAndUpstream["No commits yet on ".Length..], null, ahead, behind);
        }

        if (branchAndUpstream.Equals("HEAD (no branch)", StringComparison.Ordinal))
        {
            return new BranchInfo("Detached HEAD", null, ahead, behind);
        }

        var upstreamSeparator = branchAndUpstream.IndexOf("...", StringComparison.Ordinal);
        return upstreamSeparator < 0
            ? new BranchInfo(branchAndUpstream, null, ahead, behind)
            : new BranchInfo(
                branchAndUpstream[..upstreamSeparator],
                branchAndUpstream[(upstreamSeparator + 3)..],
                ahead,
                behind);
    }

    private static int ParseCounter(string value, string label)
    {
        var match = Regex.Match(value, $@"\b{label} (?<count>\d+)\b", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["count"].Value, out var count) ? count : 0;
    }

    private static string GetProjectPrefix(string repositoryRoot, string projectRoot)
    {
        var relative = Path.GetRelativePath(repositoryRoot, projectRoot)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        return relative == "." ? string.Empty : relative.Trim('/');
    }

    private static string? ToProjectRelative(string repositoryPath, string projectPrefix)
    {
        if (string.IsNullOrEmpty(projectPrefix))
        {
            return IsSafeGitPath(repositoryPath) ? repositoryPath : null;
        }

        var prefix = projectPrefix + "/";
        return repositoryPath.StartsWith(prefix, PathComparison)
            ? repositoryPath[prefix.Length..]
            : null;
    }

    private static bool IsSafeGitPath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) &&
        path != ".." &&
        !path.StartsWith("../", StringComparison.Ordinal) &&
        !path.Contains('\0');

    private static void ValidateRelativePath(string? relativePath, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.Length > GitChangesDefaults.MaximumRelativePathLength ||
            maximumCharacters is < 1 or > GitChangesDefaults.MaximumDiffCharacters ||
            Path.IsPathRooted(relativePath) ||
            relativePath.StartsWith('/') ||
            relativePath.StartsWith('\\') ||
            relativePath.Contains(':') ||
            relativePath.Contains('\0'))
        {
            throw InvalidRequest();
        }

        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
        {
            throw InvalidRequest();
        }
    }

    private static bool IsContained(string root, string path) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    private static bool HasReparsePoint(string root, string relativePath)
    {
        var current = root;
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }

    private static string CompleteNullTerminatedOutput(string output, bool wasTruncated)
    {
        if (!wasTruncated || output.EndsWith('\0'))
        {
            return output;
        }

        var lastTerminator = output.LastIndexOf('\0');
        return lastTerminator < 0 ? string.Empty : output[..(lastTerminator + 1)];
    }

    private static string RemoveAbsoluteProjectPath(string content, string projectRoot) => content
        .Replace(projectRoot, ".", PathComparison)
        .Replace(projectRoot.Replace('\\', '/'), ".", PathComparison);

    private static void EnsureSuccess(
        GitCommandResult result,
        string message,
        bool allowDiffExitCode = false)
    {
        if (result.ExitCode == 0 || allowDiffExitCode && result.ExitCode == 1)
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? string.Empty
            : $" {result.StandardError.Trim()}";
        throw new HostOperationException(ProtocolErrorCodes.GitUnavailable, message + detail);
    }

    private static async Task<GitCommandResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        int maximumOutputCharacters,
        CancellationToken cancellationToken)
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
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.fsmonitor=false");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("status.relativePaths=false");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw GitUnavailable("Git could not be started.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw GitUnavailable("Git is not installed or could not be started.");
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
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw GitUnavailable($"Git did not respond within {CommandTimeout.TotalSeconds:0} seconds.");
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

    private static HostOperationException InvalidRequest() => new(
        ProtocolErrorCodes.GitInvalid,
        "Git changes require a known project, a contained relative path, and valid result limits.");

    private static HostOperationException GitUnavailable(string message) => new(
        ProtocolErrorCodes.GitUnavailable,
        message);

    private sealed record BranchInfo(string Name, string? Upstream, int Ahead, int Behind);

    private sealed record BoundedText(string Content, bool WasTruncated);

    private sealed record GitCommandResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool WasTruncated);
}
