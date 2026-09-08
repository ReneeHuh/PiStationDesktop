using System.Globalization;
using System.Text.Json;
using PiStation.Host.Errors;
using PiStation.Protocol.Models;

namespace PiStation.Host.SourceControl;

#pragma warning disable CA1822, CA1859

public sealed partial class SourceControlHostingService
{
    private sealed record ReviewFilePosition(int Page = 1, int Index = 0, int Line = 0, int PageSize = 100);
    private sealed record ReviewFilePage(IReadOnlyList<PullRequestChangedFile> Files, ReviewFilePosition? Next, string? Notice);

    private async Task<PullRequestReviewSnapshot> ReadReviewPageAsync(SourceControlRepository repository, int number,
        string workspace, PullRequestReviewContinuation? page, CancellationToken cancellationToken)
    {
        if (page is not null && (!Enum.IsDefined(page.Kind) || string.IsNullOrEmpty(page.Cursor) || page.Cursor.Length > 4096 ||
            page.Repository != PullRequestReviewDefaults.RepositoryKey(repository) ||
            page.Number != number.ToString(CultureInfo.InvariantCulture) || string.IsNullOrEmpty(page.HeadCommitId)))
            throw ReviewError("The review continuation belongs to another pull request or is invalid. Reload the review.");

        using var response = await QueryReviewPageAsync(repository, number, workspace, page, cancellationToken).ConfigureAwait(false);
        var snapshot = ParseReviewSnapshot(repository, response, number);
        if (page is not null && !string.Equals(page.HeadCommitId, snapshot.HeadCommitId, StringComparison.OrdinalIgnoreCase))
            throw ReviewError("The pull request head changed. Reload the review to load more data.");
        if (page?.BaseCommitId is { } expectedBase && !string.Equals(expectedBase, snapshot.BaseCommitId, StringComparison.OrdinalIgnoreCase))
            throw ReviewError("The pull request base changed. Reload the review to load more data.");
        var pr = response.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest");
        var next = new List<PullRequestReviewContinuation>();
        var notices = new List<string>();
        if (page is null || page.Kind == PullRequestReviewPageKind.Files)
        {
            var position = page is null ? new ReviewFilePosition() : DecodeFilePosition(page.Cursor);
            var files = await ReadFilePageAsync(repository, number, workspace, position, cancellationToken).ConfigureAwait(false);
            snapshot = snapshot with { Files = files.Files };
            if (files.Next is not null)
                next.Add(Continuation(snapshot, PullRequestReviewPageKind.Files,
                    Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(files.Next))));
            if (files.Notice is not null) notices.Add(files.Notice);
            if (Number(pr, "changedFiles") > 3000)
                notices.Add("GitHub exposes at most 3,000 changed files through its review files API. Additional files must be inspected in the repository.");
        }
        if (page?.Kind == PullRequestReviewPageKind.ThreadComments)
        {
            using var threadResponse = await QueryReviewThreadAsync(repository, number, workspace, snapshot.HeadCommitId,
                page.ParentId ?? "", page.Cursor, cancellationToken).ConfigureAwait(false);
            var thread = threadResponse.RootElement.GetProperty("data").GetProperty("node");
            snapshot = snapshot with { Discussions = [ParseDiscussion(thread)] };
            AddConnection(thread.GetProperty("comments"), PullRequestReviewPageKind.ThreadComments, page.ParentId);
        }
        else
        {
            AddRoot("commits", PullRequestReviewPageKind.Commits);
            AddRoot("labels", PullRequestReviewPageKind.Labels);
            AddRoot("reviewRequests", PullRequestReviewPageKind.Reviewers);
            AddRoot("comments", PullRequestReviewPageKind.Comments);
            AddRoot("reviews", PullRequestReviewPageKind.Reviews);
            AddRoot("reviewThreads", PullRequestReviewPageKind.Threads);
            if ((page is null || page.Kind == PullRequestReviewPageKind.Checks) &&
                pr.TryGetProperty("headCommit", out var headCommits))
            {
                var headCommit = Nodes(headCommits).FirstOrDefault();
                if (headCommit.ValueKind == JsonValueKind.Object && headCommit.TryGetProperty("commit", out var commit) &&
                    commit.TryGetProperty("statusCheckRollup", out var rollup) && rollup.ValueKind == JsonValueKind.Object)
                    AddConnection(rollup.GetProperty("contexts"), PullRequestReviewPageKind.Checks);
            }
            if ((page is null || page.Kind == PullRequestReviewPageKind.Threads) && pr.TryGetProperty("reviewThreads", out var threads))
                foreach (var thread in Nodes(threads))
                    AddConnection(thread.GetProperty("comments"), PullRequestReviewPageKind.ThreadComments, Text(thread, "id"));
        }

        // A page is a delta. Unrelated first-page data must not overwrite newer loaded pages.
        if (page is not null)
        {
            var kind = page.Kind;
            snapshot = snapshot with
            {
                Commits = kind == PullRequestReviewPageKind.Commits ? snapshot.Commits : [],
                Checks = kind == PullRequestReviewPageKind.Checks ? snapshot.Checks : [],
                Discussions = kind switch
                {
                    PullRequestReviewPageKind.ThreadComments => snapshot.Discussions,
                    PullRequestReviewPageKind.Threads => ParseDiscussionsForConnection(pr, "reviewThreads"),
                    PullRequestReviewPageKind.Comments => ParseDiscussionsForConnection(pr, "comments"),
                    PullRequestReviewPageKind.Reviews => ParseDiscussionsForConnection(pr, "reviews"),
                    _ => []
                },
                PullRequest = snapshot.PullRequest with
                {
                    Labels = kind == PullRequestReviewPageKind.Labels ? snapshot.PullRequest.Labels : [],
                    Reviewers = kind == PullRequestReviewPageKind.Reviewers ? snapshot.PullRequest.Reviewers : []
                }
            };
        }
        using var finalResponse = await QueryReviewAsync(repository, number, workspace, cancellationToken).ConfigureAwait(false);
        var finalReview = ParseReview(repository, finalResponse, number);
        if (!string.Equals(snapshot.HeadCommitId, finalReview.HeadCommitId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshot.BaseCommitId, finalReview.BaseCommitId, StringComparison.OrdinalIgnoreCase))
            throw ReviewError("The pull request head or base changed while loading review data. Reload the review.");
        if (snapshot.Body.Length > PullRequestReviewDefaults.MaximumBodyCharacters)
        {
            snapshot = snapshot with { Body = snapshot.Body[..PullRequestReviewDefaults.MaximumBodyCharacters] };
            notices.Add("Pull request body was truncated to the review limit.");
        }
        return snapshot with { NextPages = next, IsTruncated = next.Count > 0 || notices.Count > 0,
            Notice = notices.Count > 0 ? string.Join(" ", notices.Distinct(StringComparer.Ordinal)) : null };

        void AddRoot(string field, PullRequestReviewPageKind kind)
        {
            if ((page is null || page.Kind == kind) && pr.TryGetProperty(field, out var connection)) AddConnection(connection, kind);
        }
        void AddConnection(JsonElement connection, PullRequestReviewPageKind kind, string? parentId = null)
        {
            if (!HasNext(connection)) return;
            var cursor = NestedText(connection, "pageInfo", "endCursor");
            if (string.IsNullOrEmpty(cursor))
                throw ReviewError($"GitHub reported more {kind} without a continuation cursor. Retry loading the review.");
            if (page is not null && kind == page.Kind && parentId == page.ParentId && cursor == page.Cursor)
                throw ReviewError("GitHub returned a review continuation that did not advance. Retry loading the review.");
            next.Add(Continuation(snapshot, kind, cursor, parentId));
        }
    }

    private static PullRequestReviewContinuation Continuation(PullRequestReviewSnapshot snapshot, PullRequestReviewPageKind kind,
        string cursor, string? parentId = null) => new(kind, cursor, PullRequestReviewDefaults.RepositoryKey(snapshot.Repository),
        snapshot.PullRequest.Number, snapshot.HeadCommitId, parentId, snapshot.BaseCommitId);

    private Task<JsonDocument> QueryReviewPageAsync(SourceControlRepository repository, int number, string workspace,
        PullRequestReviewContinuation? page, CancellationToken cancellationToken)
    {
        if (page is null || page.Kind is PullRequestReviewPageKind.Files or PullRequestReviewPageKind.ThreadComments)
            return QueryReviewAsync(repository, number, workspace, cancellationToken);
        var field = page.Kind switch
        {
            PullRequestReviewPageKind.Commits => "commits", PullRequestReviewPageKind.Checks => "contexts",
            PullRequestReviewPageKind.Threads => "reviewThreads", PullRequestReviewPageKind.Comments => "comments",
            PullRequestReviewPageKind.Reviews => "reviews", PullRequestReviewPageKind.Labels => "labels",
            PullRequestReviewPageKind.Reviewers => "reviewRequests", _ => throw ReviewError("Unknown review page.")
        };
        var query = ReviewQuery.Replace("$number:Int!", "$number:Int!, $cursor:String!", StringComparison.Ordinal);
        // Replace only the root connection (general comments and nested thread comments share a name).
        var position = query.IndexOf(field + "(first:100)", StringComparison.Ordinal);
        if (position < 0) throw ReviewError("The review connection is unavailable.");
        query = query[..position] + field + "(first:100, after:$cursor)" + query[(position + field.Length + "(first:100)".Length)..];
        return ExecutePagedReviewQueryAsync(repository, workspace, query,
            new { owner = repository.Owner, name = repository.Name, number, cursor = page.Cursor }, cancellationToken);
    }

    private async Task<JsonDocument> QueryReviewThreadAsync(SourceControlRepository repository, int number, string workspace,
        string head, string threadId, string? cursor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId) || threadId.Length > 1024) throw ReviewError("The review thread ID is invalid.");
        const string query = """
query($threadId:ID!, $cursor:String) {
  node(id:$threadId) { ... on PullRequestReviewThread {
    id isResolved isOutdated path line originalLine diffSide viewerCanReply viewerCanResolve viewerCanUnresolve
    pullRequest { number headRefOid repository { nameWithOwner } }
    comments(first:100, after:$cursor) { nodes { id body createdAt url author { login } } pageInfo { hasNextPage endCursor } }
  } }
}
""";
        var response = await ExecutePagedReviewQueryAsync(repository, workspace, query, new { threadId, cursor }, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!response.RootElement.GetProperty("data").TryGetProperty("node", out var node) || node.ValueKind != JsonValueKind.Object ||
                Text(node, "id") != threadId || !node.TryGetProperty("pullRequest", out var pr) || Number(pr, "number") != number ||
                !string.Equals(NestedText(pr, "repository", "nameWithOwner"), $"{repository.Owner}/{repository.Name}", StringComparison.OrdinalIgnoreCase))
                throw ReviewError("The review thread does not belong to the selected pull request.");
            if (!string.Equals(Text(pr, "headRefOid"), head, StringComparison.OrdinalIgnoreCase))
                throw ReviewError("The pull request head changed. Reload the review.");
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    private static IReadOnlyList<PullRequestDiscussion> ParseDiscussionsForConnection(JsonElement pr, string field)
    {
        var empty = JsonSerializer.SerializeToElement(new { nodes = Array.Empty<object>(), pageInfo = new { hasNextPage = false } });
        var filtered = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
        {
            ["reviewThreads"] = field == "reviewThreads" ? pr.GetProperty(field) : empty,
            ["comments"] = field == "comments" ? pr.GetProperty(field) : empty,
            ["reviews"] = field == "reviews" ? pr.GetProperty(field) : empty
        });
        return ParseDiscussions(filtered, out _, out _);
    }

    private static ReviewFilePosition DecodeFilePosition(string value)
    {
        try
        {
            var position = JsonSerializer.Deserialize<ReviewFilePosition>(Convert.FromBase64String(value));
            if (position is null || position.PageSize is not (1 or 5 or 25 or 100) ||
                position.Page < 1 || position.Page > 3000 / position.PageSize ||
                position.Index < 0 || position.Index >= position.PageSize || position.Line < 0)
                throw ReviewError("The changed-file continuation is invalid.");
            return position;
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        { throw ReviewError("The changed-file continuation is invalid."); }
    }

    private async Task<JsonDocument> QueryFilesAsync(SourceControlRepository repository, int number, string workspace,
        int page, int pageSize, CancellationToken cancellationToken)
    {
        var endpoint = $"repos/{repository.Owner}/{repository.Name}/pulls/{number}/files?per_page={pageSize}&page={page}";
        var result = await RunReviewCommandAsync(["api", "--hostname", repository.Host, "--include", endpoint], workspace, null, cancellationToken).ConfigureAwait(false);
        EnsureReviewProviderSucceeded(result);
        if (result.StandardOutput.Length >= 2 * 1024 * 1024) throw ReviewError("GitHub changed-file response exceeded the host safety limit.");
        try
        {
            var document = JsonDocument.Parse(ReviewResponseBody(result.StandardOutput));
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > pageSize)
            { document.Dispose(); throw ReviewError("GitHub returned an invalid changed-file page."); }
            return document;
        }
        catch (JsonException exception) { throw ReviewError($"GitHub returned invalid changed-file JSON: {exception.Message}"); }
    }

    private async Task<ReviewFilePage> ReadFilePageAsync(SourceControlRepository repository, int number, string workspace,
        ReviewFilePosition position, CancellationToken cancellationToken)
    {
        var batch = await QueryFilePageWithFallbackAsync(repository, number, workspace, position, cancellationToken).ConfigureAwait(false);
        using var response = batch.Response;
        position = batch.Position;
        var items = response.RootElement;
        if (position.Index >= items.GetArrayLength() && (position.Index != 0 || position.Line != 0))
            throw ReviewError("Changed files changed while paging. Reload the review.");
        var files = new List<PullRequestChangedFile>();
        var linesRemaining = PullRequestReviewDefaults.MaximumDiffLines;
        ReviewFilePosition? next = null;
        for (var i = position.Index; i < items.GetArrayLength(); i++)
        {
            var file = ParseFile(items[i]);
            var offset = i == position.Index ? position.Line : 0;
            if (offset > file.Lines.Count) throw ReviewError("The changed-file patch changed while paging. Reload the review.");
            var lines = file.Lines.Skip(offset).Take(linesRemaining).ToArray();
            files.Add(file with { Lines = lines, PatchLineOffset = offset });
            linesRemaining -= lines.Length;
            if (offset + lines.Length < file.Lines.Count) { next = position with { Index = i, Line = offset + lines.Length }; break; }
            if (linesRemaining == 0 && i + 1 < items.GetArrayLength()) { next = position with { Index = i + 1, Line = 0 }; break; }
        }
        if (next is null && items.GetArrayLength() == position.PageSize && position.Page * position.PageSize < 3000)
            next = new ReviewFilePosition(position.Page + 1, PageSize: position.PageSize);
        return new ReviewFilePage(files, next, files.Any(file => file.PatchUnavailable)
            ? "One or more changed files have no patch (binary or omitted by GitHub)." : null);
    }

    private async Task<IReadOnlyList<PullRequestChangedFile>> GetWriteFilesAsync(SourceControlRepository repository, int number,
        string workspace, IReadOnlyList<PullRequestInlineComment> comments, CancellationToken cancellationToken)
    {
        if (comments.Count > PullRequestReviewDefaults.MaximumInlineComments) throw ReviewError("A review may contain at most 50 inline comments.");
        var needed = comments.Select(comment => comment?.Path ?? "").ToHashSet(StringComparer.Ordinal);
        var files = new List<PullRequestChangedFile>();
        var position = new ReviewFilePosition();
        while (needed.Count > 0 && (position.Page - 1) * position.PageSize < 3000)
        {
            var batch = await QueryFilePageWithFallbackAsync(repository, number, workspace, position, cancellationToken).ConfigureAwait(false);
            using var response = batch.Response;
            position = batch.Position;
            foreach (var item in response.RootElement.EnumerateArray().Skip(position.Index))
                if (needed.Remove(Text(item, "filename") ?? "")) files.Add(ParseFile(item));
            if (response.RootElement.GetArrayLength() < position.PageSize) break;
            position = new ReviewFilePosition(position.Page + 1, PageSize: position.PageSize);
        }
        return files;
    }

    private async Task<(JsonDocument Response, ReviewFilePosition Position)> QueryFilePageWithFallbackAsync(
        SourceControlRepository repository, int number, string workspace, ReviewFilePosition position, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return (await QueryFilesAsync(repository, number, workspace, position.Page, position.PageSize, cancellationToken).ConfigureAwait(false), position);
            }
            catch (HostOperationException exception) when (position.PageSize > 1 && exception.Message.Contains("response exceeded", StringComparison.Ordinal))
            {
                var absoluteIndex = (position.Page - 1) * position.PageSize + position.Index;
                var size = position.PageSize switch { 100 => 25, 25 => 5, _ => 1 };
                position = new ReviewFilePosition(absoluteIndex / size + 1, absoluteIndex % size, position.Line, size);
            }
        }
    }

    private async Task<JsonDocument> ExecutePagedReviewQueryAsync(SourceControlRepository repository, string workspace,
        string query, object variables, CancellationToken cancellationToken)
    {
        foreach (var size in new[] { 100, 25, 5, 1 })
        {
            try
            {
                return await ExecuteGraphQlAsync(repository, workspace,
                    query.Replace("first:100", $"first:{size}", StringComparison.Ordinal), variables, cancellationToken).ConfigureAwait(false);
            }
            catch (HostOperationException exception) when (size > 1 && exception.Message.Contains("response exceeded", StringComparison.Ordinal))
            {
                // A page of long comments can exceed the byte bound even with a valid item count.
                // Request fewer nodes and retain GitHub's actual end cursor.
            }
        }
        throw ReviewError("GitHub review data exceeded the host safety limit.");
    }
}
