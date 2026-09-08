using System.Security.Cryptography;
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
    private const int MaximumLineBytes = 2 * 1024 * 1024;

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

        var projects = (await _database.ListProjectsAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(project => project.ProjectId.ToString(), StringComparer.Ordinal).ToArray();
        var threads = new List<HostThreadRecord>();
        foreach (var project in projects)
            threads.AddRange(await _database.ListThreadsAsync(project.ProjectId, request.IncludeArchivedThreads, cancellationToken).ConfigureAwait(false));
        threads.Sort((left, right) => string.CompareOrdinal(left.ThreadId.ToString(), right.ThreadId.ToString()));
        var scope = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            query, request.IncludeArchivedThreads,
            projects = projects.Select(project => new { project.ProjectId, project.DisplayName, project.CanonicalPath }),
            threads = threads.Select(thread => new { thread.ThreadId, thread.Title, thread.BranchName, thread.PiSessionFile, thread.IsArchived })
        })));
        var cursor = ReadCursor(request.Continuation, scope);
        if (cursor.ProjectIndex > projects.Length || cursor.ThreadIndex > threads.Count)
            throw SearchError("Search results changed. Start the search again.");
        var names = projects.ToDictionary(project => project.ProjectId, project => project.DisplayName);
        var candidates = new List<RankedItem>();
        var notices = new HashSet<string>(StringComparer.Ordinal);
        var work = 0;
        long bytesScanned = 0;
        while (candidates.Count < request.MaximumResults && work < MaximumThreadsScanned && bytesScanned < MaximumSessionBytesScanned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cursor.ProjectIndex < projects.Length)
            {
                var project = projects[cursor.ProjectIndex];
                if (!cursor.TitleVisited)
                {
                    cursor.TitleVisited = true;
                    AddIfMatch(candidates, query, project.DisplayName,
                        new GlobalSearchItem(GlobalSearchResultKind.Project, project.ProjectId, project.DisplayName,
                            project.DisplayName, project.CanonicalPath, UpdatedUtc: project.CreatedUtc), 40);
                    if (candidates.Count == request.MaximumResults) break;
                }
                try
                {
                    var refs = await _git.ListRefsAsync(new ListGitRefsRequest(new WorkspaceTarget(project.ProjectId),
                        Query: query, Cursor: cursor.BranchCursor, Limit: request.MaximumResults - candidates.Count,
                        IncludeMatchingRemoteRefs: true), cancellationToken).ConfigureAwait(false);
                    if (cursor.BranchSnapshot is not null && cursor.BranchSnapshot != refs.SnapshotId)
                        throw SearchError("Branches changed. Start the search again.");
                    cursor.BranchSnapshot = refs.SnapshotId;
                    foreach (var branch in refs.Refs)
                        AddIfMatch(candidates, query, branch.Name,
                            new GlobalSearchItem(GlobalSearchResultKind.Branch, project.ProjectId, project.DisplayName,
                                branch.Name, branch.IsRemote ? $"Remote branch in {project.DisplayName}" : $"Branch in {project.DisplayName}",
                                BranchName: branch.Name), 20);
                    work++;
                    if (refs.NextCursor is { } next)
                    {
                        if (next <= cursor.BranchCursor) throw SearchError("Branch search did not advance. Start the search again.");
                        cursor.BranchCursor = next;
                        continue;
                    }
                }
                catch (HostOperationException exception) when (exception.Code != ProtocolErrorCodes.GlobalSearchInvalid)
                {
                    // Ordinary folders still participate in project and conversation search.
                }
                cursor.ProjectIndex++;
                cursor.TitleVisited = false;
                cursor.BranchCursor = 0;
                cursor.BranchSnapshot = null;
                continue;
            }
            if (cursor.ThreadIndex >= threads.Count) break;
            var record = threads[cursor.ThreadIndex];
            var thread = record.ToDescriptor(_environmentId);
            var name = names[thread.ProjectId];
            work++;
            if (!cursor.TitleVisited)
            {
                cursor.TitleVisited = true;
                var description = string.Join(" • ", new[] { name, thread.BranchName, thread.IsArchived ? "Archived" : null }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
                AddIfMatch(candidates, query, thread.Title,
                    new GlobalSearchItem(GlobalSearchResultKind.Thread, thread.ProjectId, name, thread.Title,
                        description, thread.ThreadId, thread.BranchName, UpdatedUtc: thread.UpdatedUtc), 30);
                if (candidates.Count == request.MaximumResults) break;
            }
            if (!string.IsNullOrWhiteSpace(record.PiSessionFile) &&
                TryGetContainedSessionFile(record.PiSessionFile, out var path) && File.Exists(path))
            {
                var scanned = await SearchMessagesAsync(path, MaximumSessionBytesScanned - bytesScanned,
                    query, thread, name, candidates, request.MaximumResults, cursor, notices, cancellationToken).ConfigureAwait(false);
                bytesScanned += scanned;
                if (cursor.ByteOffset < cursor.FileLength) break;
            }
            else if (cursor.ByteOffset != 0)
                throw SearchError("Conversation history changed. Start the search again.");
            cursor.ThreadIndex++;
            cursor.TitleVisited = false;
            cursor.ByteOffset = cursor.FileLength = cursor.FileStamp = 0;
            cursor.SkippingLine = false;
        }
        var hasMore = cursor.ProjectIndex < projects.Length || cursor.ThreadIndex < threads.Count;
        return new GlobalSearchResult(candidates.OrderByDescending(candidate => candidate.Rank)
            .ThenByDescending(candidate => candidate.Item.UpdatedUtc)
            .ThenBy(candidate => candidate.Item.Title, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Item).ToArray(), hasMore || notices.Count > 0,
            hasMore ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor)) : null,
            notices.Count > 0 ? string.Join(" ", notices) : null);
    }

    // The cursor contains positions, never a client-supplied file path. Scope changes invalidate it.
    private sealed class SearchCursor
    {
        public string Scope { get; set; } = "";
        public int ProjectIndex { get; set; }
        public int BranchCursor { get; set; }
        public string? BranchSnapshot { get; set; }
        public int ThreadIndex { get; set; }
        public bool TitleVisited { get; set; }
        public long ByteOffset { get; set; }
        public long FileLength { get; set; }
        public long FileStamp { get; set; }
        public bool SkippingLine { get; set; }
    }

    private static SearchCursor ReadCursor(string? value, string scope)
    {
        if (value is null) return new SearchCursor { Scope = scope };
        try
        {
            if (value.Length > 4096) throw SearchError("The search continuation is too long.");
            var cursor = JsonSerializer.Deserialize<SearchCursor>(Convert.FromBase64String(value));
            if (cursor is null || cursor.Scope != scope || cursor.ProjectIndex < 0 || cursor.BranchCursor < 0 ||
                cursor.ThreadIndex < 0 || cursor.ByteOffset < 0 || cursor.FileLength < cursor.ByteOffset || cursor.FileStamp < 0)
                throw SearchError("Search results changed or the continuation is invalid. Start the search again.");
            return cursor;
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            throw SearchError("The search continuation is invalid. Start the search again.");
        }
    }

    private static HostOperationException SearchError(string message) => new(ProtocolErrorCodes.GlobalSearchInvalid, message);

    private static async Task<long> SearchMessagesAsync(string path, long byteLimit, string query,
        ThreadDescriptor thread, string projectName, List<RankedItem> candidates, int limit,
        SearchCursor cursor, HashSet<string> notices, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (cursor.ByteOffset > 0 && (info.Length != cursor.FileLength || info.LastWriteTimeUtc.Ticks != cursor.FileStamp))
            throw SearchError("Conversation history changed. Start the search again.");
        cursor.FileLength = info.Length;
        cursor.FileStamp = info.LastWriteTimeUtc.Ticks;
        var start = cursor.ByteOffset;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Position = start;
        var buffer = new byte[64 * 1024];
        using var line = new MemoryStream();
        while (cursor.ByteOffset < cursor.FileLength)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, cursor.FileLength - cursor.ByteOffset)), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw SearchError("Conversation history changed. Start the search again.");
            for (var i = 0; i < read; i++)
            {
                cursor.ByteOffset++;
                if (buffer[i] != (byte)'\n' && !cursor.SkippingLine)
                {
                    if (line.Length < MaximumLineBytes) line.WriteByte(buffer[i]);
                    else
                    {
                        cursor.SkippingLine = true;
                        line.SetLength(0);
                        notices.Add("A conversation entry exceeded the 2 MiB search limit and was skipped.");
                    }
                }
                if (buffer[i] == (byte)'\n' || cursor.ByteOffset == cursor.FileLength)
                {
                    if (!cursor.SkippingLine)
                    {
                        var value = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimStart('\uFEFF');
                        if (TryReadMessage(value, out var id, out var role, out var text) &&
                            text.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            var label = role == MessageRole.User ? "You" : "Pi";
                            candidates.Add(new RankedItem(new GlobalSearchItem(GlobalSearchResultKind.Message,
                                thread.ProjectId, projectName, thread.Title, $"{label} in {projectName}",
                                thread.ThreadId, thread.BranchName, id, role, CreateSnippet(text, query), thread.UpdatedUtc),
                                Rank(text, query) + 10));
                        }
                    }
                    line.SetLength(0);
                    cursor.SkippingLine = false;
                    if (candidates.Count == limit || cursor.ByteOffset - start >= byteLimit)
                        return cursor.ByteOffset - start;
                }
                // Oversized entries may span several pages; resume skipping without buffering them.
                if (cursor.SkippingLine && cursor.ByteOffset - start >= byteLimit)
                    return cursor.ByteOffset - start;
            }
        }
        return cursor.ByteOffset - start;
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
