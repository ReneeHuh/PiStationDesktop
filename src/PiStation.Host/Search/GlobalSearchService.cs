using System.Text;
using System.Text.Json;
using PiStation.Host.Errors;
using PiStation.Host.Git;
using PiStation.Host.Persistence;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;

namespace PiStation.Host.Search;

public sealed class GlobalSearchService(
    HostDatabase database,
    WorkspaceGitCommandService git,
    HostOptions options,
    EnvironmentId environmentId)
{
    private const int MaximumThreadsScanned = 500;
    private const long MaximumSessionBytesScanned = 32L * 1024 * 1024;
    private const int MaximumCandidates = 2_000;

    private readonly HostDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly WorkspaceGitCommandService _git = git ?? throw new ArgumentNullException(nameof(git));
    private readonly string _sessionRoot = Path.GetFullPath(
        (options ?? throw new ArgumentNullException(nameof(options))).SessionRoot);
    private readonly EnvironmentId _environmentId = environmentId;

    public async Task<GlobalSearchResult> SearchAsync(
        GlobalSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = request.Query?.Trim() ?? string.Empty;
        if (query.Length is 0 or > GlobalSearchDefaults.MaximumQueryLength ||
            request.MaximumResults is < 1 or > GlobalSearchDefaults.MaximumResults)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.GlobalSearchInvalid,
                $"Global search requires a query up to {GlobalSearchDefaults.MaximumQueryLength} characters and a result limit between 1 and {GlobalSearchDefaults.MaximumResults}.");
        }

        var candidates = new List<RankedItem>();
        var projects = await _database.ListProjectsAsync(cancellationToken).ConfigureAwait(false);
        var projectNames = projects.ToDictionary(
            static project => project.ProjectId,
            static project => project.DisplayName);

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddIfMatch(
                candidates,
                query,
                project.DisplayName,
                new GlobalSearchItem(
                    GlobalSearchResultKind.Project,
                    project.ProjectId,
                    project.DisplayName,
                    project.DisplayName,
                    project.CanonicalPath,
                    UpdatedUtc: project.CreatedUtc),
                categoryBoost: 40);

            if (candidates.Count >= MaximumCandidates)
            {
                break;
            }

            try
            {
                var refs = await _git.ListRefsAsync(
                    new ListGitRefsRequest(
                        new WorkspaceTarget(project.ProjectId),
                        Query: query,
                        Limit: 25,
                        IncludeMatchingRemoteRefs: true),
                    cancellationToken).ConfigureAwait(false);
                foreach (var branch in refs.Refs)
                {
                    AddIfMatch(
                        candidates,
                        query,
                        branch.Name,
                        new GlobalSearchItem(
                            GlobalSearchResultKind.Branch,
                            project.ProjectId,
                            project.DisplayName,
                            branch.Name,
                            branch.IsRemote ? $"Remote branch in {project.DisplayName}" : $"Branch in {project.DisplayName}",
                            BranchName: branch.Name),
                        categoryBoost: 20);
                }
            }
            catch (HostOperationException)
            {
                // A folder that is not a repository still participates in project/thread search.
            }
        }

        var threadsScanned = 0;
        long sessionBytesScanned = 0;
        var scanTruncated = false;
        foreach (var project in projects)
        {
            var threads = await _database.ListThreadsAsync(
                project.ProjectId,
                request.IncludeArchivedThreads,
                cancellationToken).ConfigureAwait(false);
            foreach (var record in threads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++threadsScanned > MaximumThreadsScanned || candidates.Count >= MaximumCandidates)
                {
                    scanTruncated = true;
                    break;
                }

                var thread = record.ToDescriptor(_environmentId);
                var projectName = projectNames.GetValueOrDefault(thread.ProjectId, project.DisplayName);
                var threadDescription = string.Join(
                    " • ",
                    new[] { projectName, thread.BranchName, thread.IsArchived ? "Archived" : null }
                        .Where(static part => !string.IsNullOrWhiteSpace(part)));
                AddIfMatch(
                    candidates,
                    query,
                    thread.Title,
                    new GlobalSearchItem(
                        GlobalSearchResultKind.Thread,
                        thread.ProjectId,
                        projectName,
                        thread.Title,
                        threadDescription,
                        thread.ThreadId,
                        thread.BranchName,
                        UpdatedUtc: thread.UpdatedUtc),
                    categoryBoost: 30);

                if (string.IsNullOrWhiteSpace(record.PiSessionFile) ||
                    !TryGetContainedSessionFile(record.PiSessionFile, out var sessionFile) ||
                    !File.Exists(sessionFile))
                {
                    continue;
                }

                var remainingBytes = MaximumSessionBytesScanned - sessionBytesScanned;
                if (remainingBytes <= 0)
                {
                    scanTruncated = true;
                    break;
                }

                var fileLength = new FileInfo(sessionFile).Length;
                var byteLimit = Math.Min(fileLength, remainingBytes);
                sessionBytesScanned += byteLimit;
                scanTruncated |= fileLength > byteLimit;
                await SearchMessagesAsync(
                    sessionFile,
                    byteLimit,
                    query,
                    thread,
                    projectName,
                    candidates,
                    cancellationToken).ConfigureAwait(false);
            }

            if (scanTruncated &&
                (threadsScanned > MaximumThreadsScanned ||
                 sessionBytesScanned >= MaximumSessionBytesScanned ||
                 candidates.Count >= MaximumCandidates))
            {
                break;
            }
        }

        var ordered = candidates
            .OrderByDescending(static candidate => candidate.Rank)
            .ThenByDescending(static candidate => candidate.Item.UpdatedUtc)
            .ThenBy(static candidate => candidate.Item.Title, StringComparer.OrdinalIgnoreCase)
            .Select(static candidate => candidate.Item)
            .Take(request.MaximumResults)
            .ToArray();
        return new GlobalSearchResult(
            ordered,
            scanTruncated || candidates.Count > ordered.Length);
    }

    private static async Task SearchMessagesAsync(
        string path,
        long byteLimit,
        string query,
        ThreadDescriptor thread,
        string projectName,
        List<RankedItem> candidates,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = new byte[checked((int)byteLimit)];
        var bytesRead = 0;
        while (bytesRead < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(bytesRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            bytesRead += read;
        }

        using var boundedStream = new MemoryStream(bytes, 0, bytesRead, writable: false, publiclyVisible: true);
        using var reader = new StreamReader(
            boundedStream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: false);
        while (candidates.Count < MaximumCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length > 2 * 1024 * 1024 ||
                !TryReadMessage(line, out var messageId, out var role, out var text) ||
                text.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            var snippet = CreateSnippet(text, query);
            var label = role == MessageRole.User ? "You" : "Pi";
            candidates.Add(new RankedItem(
                new GlobalSearchItem(
                    GlobalSearchResultKind.Message,
                    thread.ProjectId,
                    projectName,
                    thread.Title,
                    $"{label} in {projectName}",
                    thread.ThreadId,
                    thread.BranchName,
                    messageId,
                    role,
                    snippet,
                    thread.UpdatedUtc),
                Rank(text, query) + 10));
        }
    }

    private bool TryGetContainedSessionFile(string value, out string path)
    {
        try
        {
            path = Path.GetFullPath(value);
            var rootPrefix = _sessionRoot.EndsWith(Path.DirectorySeparatorChar)
                ? _sessionRoot
                : _sessionRoot + Path.DirectorySeparatorChar;
            return path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            path = string.Empty;
            return false;
        }
    }

    private static bool TryReadMessage(
        string line,
        out string messageId,
        out MessageRole role,
        out string text)
    {
        messageId = string.Empty;
        role = MessageRole.Assistant;
        text = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "message" ||
                !root.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("role", out var roleProperty))
            {
                return false;
            }

            role = roleProperty.GetString() switch
            {
                "user" => MessageRole.User,
                "assistant" => MessageRole.Assistant,
                _ => MessageRole.System,
            };
            if (role is not (MessageRole.User or MessageRole.Assistant))
            {
                return false;
            }

            messageId = root.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty;
            if (!message.TryGetProperty("content", out var content))
            {
                return false;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                text = content.GetString() ?? string.Empty;
                return text.Length > 0;
            }

            if (content.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var builder = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var blockType) && blockType.GetString() == "text" &&
                    block.TryGetProperty("text", out var blockText))
                {
                    builder.Append(blockText.GetString());
                }
            }

            text = builder.ToString();
            return text.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void AddIfMatch(
        List<RankedItem> candidates,
        string query,
        string value,
        GlobalSearchItem item,
        int categoryBoost)
    {
        var rank = Rank(value, query);
        if (rank > 0)
        {
            candidates.Add(new RankedItem(item, rank + categoryBoost));
        }
    }

    private static int Rank(string value, string query)
    {
        if (string.Equals(value, query, StringComparison.OrdinalIgnoreCase))
        {
            return 300;
        }

        if (value.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 200;
        }

        return value.Contains(query, StringComparison.OrdinalIgnoreCase) ? 100 : 0;
    }

    private static string CreateSnippet(string value, string query)
    {
        var flattened = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var match = flattened.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        var half = GlobalSearchDefaults.MaximumSnippetLength / 2;
        var start = Math.Max(0, match - half);
        var length = Math.Min(GlobalSearchDefaults.MaximumSnippetLength, flattened.Length - start);
        var snippet = flattened.Substring(start, length);
        if (start > 0)
        {
            snippet = "…" + snippet;
        }

        if (start + length < flattened.Length)
        {
            snippet += "…";
        }

        return snippet;
    }

    private sealed record RankedItem(GlobalSearchItem Item, int Rank);
}
