using PiStation.Host.Errors;
using PiStation.Host.Persistence;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Models;
using PiStation.Host.Workspaces;
using System.Text;
using System.Text.RegularExpressions;

namespace PiStation.Host.Files;

public sealed class WorkspaceFileSearchService(
    HostDatabase database,
    HostOptions options,
    ThreadWorkspaceResolver? workspaceResolver = null)
{
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".hg",
        ".svn",
        ".vs",
        "bin",
        "node_modules",
        "obj",
    };

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly HostDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly HostOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ThreadWorkspaceResolver _workspaceResolver = workspaceResolver ?? new ThreadWorkspaceResolver(database);

    public async Task<SearchProjectFilesResult> SearchAsync(
        SearchProjectFilesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Offset < 0 || request.ScanOffset < 0 || request.ScanOffset > int.MaxValue - _options.MaximumFileSearchScannedFiles || request.Query is null ||
            string.IsNullOrWhiteSpace(request.ProjectId.Value) ||
            request.Query.Length > FileSearchDefaults.MaximumQueryLength ||
            request.MaximumResults is < 1 or > FileSearchDefaults.MaximumResults)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.FileSearchInvalid,
                $"File searches accept at most {FileSearchDefaults.MaximumQueryLength} query characters and " +
                $"between 1 and {FileSearchDefaults.MaximumResults} results.");
        }

        var workspace = await _workspaceResolver.ResolveAsync(
            request.ProjectId,
            request.ThreadId,
            cancellationToken).ConfigureAwait(false);
        var root = workspace.WorkspaceRoot;
        if (!Directory.Exists(root))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.FileSearchUnavailable,
                "The project directory is not currently available.");
        }

        var query = request.Query.Trim().Replace('\\', '/');
        var candidates = new List<SearchCandidate>();
        var directories = new Queue<string>();
        directories.Enqueue(root);
        var scannedFiles = 0;
        var reachedScanLimit = false;
        while (directories.Count != 0 && !reachedScanLimit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = directories.Dequeue();
            foreach (var entry in EnumerateEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetAttributes(entry, out var attributes) ||
                    (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(entry);
                if (!IsContained(root, fullPath))
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!ExcludedDirectoryNames.Contains(Path.GetFileName(fullPath)))
                    {
                        directories.Enqueue(fullPath);
                    }

                    continue;
                }

                scannedFiles++;
                if (scannedFiles <= request.ScanOffset) continue;
                if (scannedFiles - request.ScanOffset > _options.MaximumFileSearchScannedFiles)
                {
                    reachedScanLimit = true;
                    break;
                }

                var relativePath = NormalizeRelativePath(root, fullPath);
                if (relativePath is null)
                {
                    continue;
                }

                var fileName = Path.GetFileName(fullPath);
                var score = GetMatchScore(fileName, relativePath, query);
                if (score >= 0)
                {
                    candidates.Add(new SearchCandidate(relativePath, fileName, score));
                }
            }
        }

        var ordered = candidates
            .OrderBy(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.RelativePath.Length)
            .ThenBy(static candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static candidate => candidate.RelativePath, StringComparer.Ordinal)
            .ToArray();
        return new SearchProjectFilesResult(
            request.ProjectId,
            query,
            ordered
                .Skip(request.Offset)
                .Take(request.MaximumResults)
                .Select(static candidate => new ProjectFileMatch(candidate.RelativePath, candidate.FileName))
                .ToArray(),
            reachedScanLimit || ordered.Length > (long)request.Offset + request.MaximumResults,
            ordered.Length > (long)request.Offset + request.MaximumResults ? request.Offset + request.MaximumResults : reachedScanLimit ? 0 : null,
            ordered.Length > (long)request.Offset + request.MaximumResults ? request.ScanOffset : reachedScanLimit ? request.ScanOffset + _options.MaximumFileSearchScannedFiles : 0);
    }

    public async Task<ListProjectEntriesResult> ListAsync(
        ListProjectEntriesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Offset < 0 || string.IsNullOrWhiteSpace(request.ProjectId.Value) ||
            request.MaximumResults is < 1 or > WorkspaceEntryDefaults.MaximumResults)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.FileSearchInvalid,
                $"Workspace listings accept between 1 and {WorkspaceEntryDefaults.MaximumResults} entries.");
        }

        var root = await ResolveRootAsync(request.ProjectId, request.ThreadId, cancellationToken)
            .ConfigureAwait(false);
        var entries = new List<ProjectWorkspaceEntry>();
        var visitedEntries = 0;
        var directories = new Queue<string>();
        directories.Enqueue(root);
        var truncated = false;
        while (directories.Count != 0 && !truncated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = directories.Dequeue();
            foreach (var entry in EnumerateEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetAttributes(entry, out var attributes) ||
                    (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(entry);
                if (!IsContained(root, fullPath))
                {
                    continue;
                }

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (isDirectory && ExcludedDirectoryNames.Contains(Path.GetFileName(fullPath)))
                {
                    continue;
                }

                var relativePath = NormalizeRelativePath(root, fullPath);
                if (relativePath is null)
                {
                    continue;
                }

                if (entries.Count >= request.MaximumResults)
                {
                    truncated = true;
                    break;
                }

                if (visitedEntries++ >= request.Offset) entries.Add(new ProjectWorkspaceEntry(
                    relativePath,
                    Path.GetFileName(fullPath),
                    isDirectory,
                    isDirectory ? 0 : TryGetFileLength(fullPath)));
                if (isDirectory)
                {
                    directories.Enqueue(fullPath);
                }
            }
        }

        return new ListProjectEntriesResult(
            request.ProjectId,
            entries
                .OrderBy(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static entry => entry.RelativePath, StringComparer.Ordinal)
                .ToArray(),
            truncated,
            truncated && entries.Count == request.MaximumResults ? request.Offset + entries.Count : null);
    }

    public async Task<SearchProjectContentsResult> SearchContentsAsync(
        SearchProjectContentsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Offset);
        ArgumentOutOfRangeException.ThrowIfNegative(request.ScanOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.ScanOffset, int.MaxValue - _options.MaximumFileSearchScannedFiles);
        if (string.IsNullOrWhiteSpace(request.ProjectId.Value) ||
            string.IsNullOrEmpty(request.Query) ||
            request.Query.Length > ContentSearchDefaults.MaximumQueryLength ||
            request.MaximumResults is < 1 or > ContentSearchDefaults.MaximumResults)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.FileContentSearchInvalid,
                $"Content searches require 1-{ContentSearchDefaults.MaximumQueryLength} query characters and " +
                $"between 1 and {ContentSearchDefaults.MaximumResults} results.");
        }

        Regex? expression = null;
        if (request.UseRegularExpression)
        {
            try
            {
                expression = new Regex(
                    request.Query,
                    RegexOptions.CultureInvariant |
                    (request.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
                    TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException exception)
            {
                throw new HostOperationException(
                    ProtocolErrorCodes.FileContentSearchInvalid,
                    $"The content-search regular expression is invalid: {exception.Message}");
            }
        }

        var root = await ResolveRootAsync(request.ProjectId, request.ThreadId, cancellationToken)
            .ConfigureAwait(false);
        var matches = new List<ProjectContentMatch>();
        var skippedMatches = 0;
        var hasMoreMatches = false;
        var directories = new Queue<string>();
        directories.Enqueue(root);
        var scannedFiles = 0;
        var truncated = false;
        while (directories.Count != 0 && !truncated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = directories.Dequeue();
            foreach (var entry in EnumerateEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetAttributes(entry, out var attributes) ||
                    (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(entry);
                if (!IsContained(root, fullPath))
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!ExcludedDirectoryNames.Contains(Path.GetFileName(fullPath)))
                    {
                        directories.Enqueue(fullPath);
                    }

                    continue;
                }

                if (++scannedFiles <= request.ScanOffset) continue;
                if (scannedFiles - request.ScanOffset > _options.MaximumFileSearchScannedFiles)
                {
                    truncated = true;
                    break;
                }

                var length = TryGetFileLength(fullPath);
                if (length < 0 || length > ContentSearchDefaults.MaximumFileBytes)
                {
                    continue;
                }

                var text = await TryReadSearchTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
                if (text is null)
                {
                    continue;
                }

                var relativePath = NormalizeRelativePath(root, fullPath);
                if (relativePath is null)
                {
                    continue;
                }

                var lineNumber = 0;
                using var reader = new StringReader(text);
                while (reader.ReadLine() is { } sourceLine)
                {
                    lineNumber++;
                    var line = sourceLine.Length > ContentSearchDefaults.MaximumLineCharacters
                        ? sourceLine[..ContentSearchDefaults.MaximumLineCharacters]
                        : sourceLine;
                    List<ProjectContentMatchRange> ranges;
                    try
                    {
                        ranges = FindMatchRanges(line, request, expression);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        throw new HostOperationException(
                            ProtocolErrorCodes.FileContentSearchInvalid,
                            "The content-search regular expression took too long to evaluate.");
                    }

                    if (ranges.Count == 0)
                    {
                        continue;
                    }

                    if (skippedMatches++ < request.Offset) continue;
                    if (matches.Count >= request.MaximumResults) { truncated = true; hasMoreMatches = true; break; }
                    matches.Add(new ProjectContentMatch(
                        relativePath,
                        Path.GetFileName(fullPath),
                        lineNumber,
                        line,
                        ranges));
                }

                if (truncated)
                {
                    break;
                }
            }
        }

        return new SearchProjectContentsResult(request.ProjectId, request.Query, matches, truncated,
            hasMoreMatches ? request.Offset + matches.Count : truncated ? 0 : null,
            hasMoreMatches ? request.ScanOffset : truncated ? request.ScanOffset + _options.MaximumFileSearchScannedFiles : 0);
    }

    private async Task<string> ResolveRootAsync(
        PiStation.Protocol.Identifiers.ProjectId projectId,
        PiStation.Protocol.Identifiers.ThreadId? threadId,
        CancellationToken cancellationToken)
    {
        var workspace = await _workspaceResolver.ResolveAsync(projectId, threadId, cancellationToken)
            .ConfigureAwait(false);
        if (!Directory.Exists(workspace.WorkspaceRoot))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.FileSearchUnavailable,
                "The project directory is not currently available.");
        }

        return workspace.WorkspaceRoot;
    }

    private static List<ProjectContentMatchRange> FindMatchRanges(
        string line,
        SearchProjectContentsRequest request,
        Regex? expression)
    {
        var ranges = new List<ProjectContentMatchRange>();
        if (expression is not null)
        {
            foreach (Match match in expression.Matches(line))
            {
                if (match.Length > 0 && (!request.WholeWord || IsWholeWord(line, match.Index, match.Length)))
                {
                    ranges.Add(new ProjectContentMatchRange(match.Index, match.Index + match.Length));
                }
            }

            return ranges;
        }

        var comparison = request.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var start = 0;
        while (start <= line.Length - request.Query.Length)
        {
            var index = line.IndexOf(request.Query, start, comparison);
            if (index < 0)
            {
                break;
            }

            if (!request.WholeWord || IsWholeWord(line, index, request.Query.Length))
            {
                ranges.Add(new ProjectContentMatchRange(index, index + request.Query.Length));
            }

            start = index + Math.Max(1, request.Query.Length);
        }

        return ranges;
    }

    private static bool IsWholeWord(string line, int index, int length)
    {
        static bool IsWord(char value) => char.IsLetterOrDigit(value) || value == '_';
        return (index == 0 || !IsWord(line[index - 1]) || !IsWord(line[index])) &&
               (index + length >= line.Length ||
                !IsWord(line[index + length]) ||
                !IsWord(line[index + length - 1]));
    }

    private static async Task<string?> TryReadSearchTextAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes.AsSpan().Contains((byte)0))
            {
                return null;
            }

            return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long TryGetFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static string[] EnumerateEntries(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ThenBy(static path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            attributes = default;
            return false;
        }
    }

    private static bool IsContained(string root, string path)
    {
        var prefix = root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private static string? NormalizeRelativePath(string root, string fullPath)
    {
        var relativePath = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return null;
        }

        return relativePath
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static int GetMatchScore(string fileName, string relativePath, string query)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        if (fileName.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (fileName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (fileName.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (relativePath.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        return relativePath.Contains(query, StringComparison.OrdinalIgnoreCase) ? 4 : -1;
    }

    private sealed record SearchCandidate(string RelativePath, string FileName, int Score);
}
