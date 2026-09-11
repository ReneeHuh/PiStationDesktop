using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiStation.Host.Errors;
using PiStation.Host.Workspaces;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.Host.SourceControl;

#pragma warning disable CA1822, CA1859, CA1865

// GitHub review support is deliberately kept in a partial so the existing list/create
// adapters remain easy to audit. All provider input is sent as an argument-list or JSON
// stdin value; no user value is ever interpolated into a shell command.
public sealed partial class SourceControlHostingService
{
    private const int MaximumPullRequestNumber = 2_000_000_000;
    private const int MaximumReviewPayloadCharacters = 100_000;
    private const string ReviewQuery = """
query($owner:String!, $name:String!, $number:Int!) {
  viewer { login }
  repository(owner:$owner, name:$name) {
    id viewerPermission mergeCommitAllowed squashMergeAllowed rebaseMergeAllowed autoMergeAllowed
    pullRequest(number:$number) {
      id number title url state isDraft body updatedAt changedFiles viewerCanUpdate
      author { login }
      headRefName baseRefName headRefOid baseRefOid
      mergeable mergeStateStatus isCrossRepository viewerCanUpdateBranch viewerCanEnableAutoMerge viewerCanDisableAutoMerge
      autoMergeRequest { enabledAt } viewerCanReact reactionGroups { content viewerHasReacted reactors { totalCount } }
      labels(first:100) { nodes { name } pageInfo { hasNextPage endCursor } }
      reviewRequests(first:100) { nodes { requestedReviewer { ... on User { login } ... on Team { name slug organization { login } } } } pageInfo { hasNextPage endCursor } }
      commits(first:100) { nodes { commit { oid messageHeadline committedDate url author { name user { login } } } } pageInfo { hasNextPage endCursor } }
      headCommit: commits(last:1) { nodes { commit { statusCheckRollup { contexts(first:100) { nodes { ... on CheckRun { id name status conclusion detailsUrl } ... on StatusContext { id context state targetUrl } } pageInfo { hasNextPage endCursor } } } } } }
      comments(first:100) { nodes { id body createdAt url viewerCanUpdate viewerCanDelete viewerDidAuthor author { login } viewerCanReact reactionGroups { content viewerHasReacted reactors { totalCount } } } pageInfo { hasNextPage endCursor } }
      reviews(first:100) { nodes { id body submittedAt url state viewerCanUpdate viewerCanDelete viewerDidAuthor author { login } viewerCanReact reactionGroups { content viewerHasReacted reactors { totalCount } } } pageInfo { hasNextPage endCursor } }
      reviewThreads(first:100) {
        nodes {
          id isResolved isOutdated path line originalLine diffSide viewerCanReply viewerCanResolve viewerCanUnresolve
          comments(first:100) {
            nodes { id databaseId body createdAt url viewerCanUpdate viewerCanDelete viewerDidAuthor author { login } viewerCanReact reactionGroups { content viewerHasReacted reactors { totalCount } } }
            pageInfo { hasNextPage endCursor }
          }
        }
        pageInfo { hasNextPage endCursor }
      }
    }
  }
}
""";

    public async Task<PullRequestReviewSnapshot> GetPullRequestReviewAsync(
        GetPullRequestReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || request.Target is null) throw ReviewError("A pull request review target is required.");
        var workspace = await _resolver.ResolveAsync(request.Target.ProjectId, request.Target.ThreadId, cancellationToken).ConfigureAwait(false);
        var repository = await DetectAsync(new DetectSourceControlRequest(request.Target), cancellationToken).ConfigureAwait(false);
        var number = ValidateNumber(request.Number);
        if (repository.Provider == SourceControlProvider.Bitbucket)
            return await ReadBitbucketReviewAsync(repository, number, request.Page, cancellationToken).ConfigureAwait(false);
        if (repository.Provider is SourceControlProvider.GitLab or SourceControlProvider.AzureDevOps)
            return await ReadProviderReviewAsync(repository, number, workspace.WorkspaceRoot, request.Page, cancellationToken).ConfigureAwait(false);
        EnsureGitHub(repository);
        return await ReadReviewPageAsync(repository, number, workspace.WorkspaceRoot, request.Page, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SourceControlOperationResult> SubmitPullRequestReviewAsync(
        SubmitPullRequestReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        var dispatched = false;
        try
        {
            if (request is null) return Rejected("A review request is required.", null);
            if (request.Target is null || request.Comments is null || request.Body is null)
                throw ReviewError("The review target, body, and comments are required.");
            if (await IsAdditionalReviewProviderAsync(request.Target.Workspace, cancellationToken).ConfigureAwait(false))
                return await WriteProviderReviewAsync(request.Target, request.OperationId, request, cancellationToken).ConfigureAwait(false);
            var (workspace, repository, number, selected) = await ValidateWriteTargetAsync(request.Target, cancellationToken, request.Comments).ConfigureAwait(false);
            if (request.Comments.Count > PullRequestReviewDefaults.MaximumInlineComments)
                throw ReviewError("A review may contain at most 50 inline comments.");
            if (request.Body.Length > PullRequestReviewDefaults.MaximumBodyCharacters)
                throw ReviewError("The review body is too long.");
            if (!Enum.IsDefined(request.Event)) throw ReviewError("The review event is invalid.");
            if (request.Event is PullRequestReviewEvent.Comment or PullRequestReviewEvent.RequestChanges && string.IsNullOrWhiteSpace(request.Body))
                throw ReviewError("Comment and request-changes reviews require a non-empty body.");
            var payloadCharacters = request.Body.Length;
            foreach (var comment in request.Comments)
            {
                if (comment is null || string.IsNullOrWhiteSpace(comment.Path))
                    throw ReviewError("Each inline comment must specify a path.");
                if (string.IsNullOrWhiteSpace(comment.Body) || comment.Body.Length > PullRequestReviewDefaults.MaximumBodyCharacters || comment.Line <= 0 || comment.Path.Length > 4096)
                    throw ReviewError("Inline comment bodies must not be empty or exceed the review limit.");
                if (payloadCharacters > MaximumReviewPayloadCharacters - comment.Path.Length - comment.Body.Length)
                    throw ReviewError("The review payload is too large.");
                payloadCharacters += comment.Path.Length + comment.Body.Length;
                ValidateInlineComment(selected, comment);
            }
            if (payloadCharacters > MaximumReviewPayloadCharacters)
                throw ReviewError("The review payload is too large.");
            var command = BuildSubmitReviewCommand(repository, number, request.Target.HeadCommitId, request.Event, request.Body, request.Comments);
            dispatched = true;
            var result = await RunReviewCommandAsync(command.Arguments, workspace.WorkspaceRoot, command.Input, cancellationToken).ConfigureAwait(false);
            EnsureReviewProviderSucceeded(result);
            EnsureJsonResponse(ReviewResponseBody(result.StandardOutput), "GitHub returned an invalid review response.");
            return new SourceControlOperationResult(true, "Pull request review submitted.", repository, selected.Descriptor, OperationId: request.OperationId);
        }
        catch (ConfirmedProviderRejectionException exception) { return Rejected(exception.Message, request?.OperationId); }
        catch (Exception exception) when (!dispatched && exception is not OperationCanceledException)
        {
            return Rejected(exception.Message, request.OperationId);
        }
    }

    public async Task<SourceControlOperationResult> ReplyPullRequestThreadAsync(
        ReplyPullRequestThreadRequest request,
        CancellationToken cancellationToken = default)
    {
        var dispatched = false;
        try
        {
            if (request is null) return Rejected("A thread reply request is required.", null);
            if (request.Target is null || request.ThreadId is null) throw ReviewError("The thread target and ID are required.");
            if (await IsAdditionalReviewProviderAsync(request.Target.Workspace, cancellationToken).ConfigureAwait(false))
                return await WriteProviderReviewAsync(request.Target, request.OperationId, request, cancellationToken).ConfigureAwait(false);
            var (workspace, repository, _, selected) = await ValidateWriteTargetAsync(request.Target, cancellationToken, threadId: request.ThreadId).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > PullRequestReviewDefaults.MaximumBodyCharacters)
                throw ReviewError("A thread reply must contain 1–32768 characters.");
            var thread = FindThread(selected, request.ThreadId);
            if (!thread.CanReply) throw ReviewError("You do not have permission to reply to this review thread.");
            var mutation = """
mutation($threadId:ID!, $body:String!) {
  addPullRequestReviewThreadReply(input:{pullRequestReviewThreadId:$threadId body:$body}) { comment { id } }
}
""";
            dispatched = true;
            using var mutationResponse = await ExecuteGraphQlAsync(repository, workspace.WorkspaceRoot, mutation,
                new { threadId = thread.Id, body = request.Body }, cancellationToken).ConfigureAwait(false);
            return new SourceControlOperationResult(true, "Review thread reply added.", repository, selected.Descriptor, OperationId: request.OperationId);
        }
        catch (ConfirmedProviderRejectionException exception) { return Rejected(exception.Message, request?.OperationId); }
        catch (Exception exception) when (!dispatched && exception is not OperationCanceledException) { return Rejected(exception.Message, request.OperationId); }
    }

    public async Task<SourceControlOperationResult> SetPullRequestThreadResolvedAsync(
        SetPullRequestThreadResolvedRequest request,
        CancellationToken cancellationToken = default)
    {
        var dispatched = false;
        try
        {
            if (request is null) return Rejected("A thread resolution request is required.", null);
            if (request.Target is null || request.ThreadId is null) throw ReviewError("The thread target and ID are required.");
            if (await IsAdditionalReviewProviderAsync(request.Target.Workspace, cancellationToken).ConfigureAwait(false))
                return await WriteProviderReviewAsync(request.Target, request.OperationId, request, cancellationToken).ConfigureAwait(false);
            var (workspace, repository, _, selected) = await ValidateWriteTargetAsync(request.Target, cancellationToken, threadId: request.ThreadId).ConfigureAwait(false);
            var thread = FindThread(selected, request.ThreadId);
            if (!thread.CanResolve) throw ReviewError("You do not have permission to resolve this review thread.");
            if (thread.IsResolved == request.IsResolved)
                return new SourceControlOperationResult(true, request.IsResolved ? "Review thread is already resolved." : "Review thread is already open.", repository, selected.Descriptor, OperationId: request.OperationId);
            var mutation = request.IsResolved
            ? "mutation($threadId:ID!){ resolveReviewThread(input:{threadId:$threadId}) { thread { id isResolved } } }"
            : "mutation($threadId:ID!){ unresolveReviewThread(input:{threadId:$threadId}) { thread { id isResolved } } }";
            dispatched = true;
            using var mutationResponse = await ExecuteGraphQlAsync(repository, workspace.WorkspaceRoot, mutation,
                new { threadId = thread.Id }, cancellationToken).ConfigureAwait(false);
            return new SourceControlOperationResult(true, request.IsResolved ? "Review thread resolved." : "Review thread reopened.", repository, selected.Descriptor, OperationId: request.OperationId);
        }
        catch (ConfirmedProviderRejectionException exception) { return Rejected(exception.Message, request?.OperationId); }
        catch (Exception exception) when (!dispatched && exception is not OperationCanceledException) { return Rejected(exception.Message, request.OperationId); }
    }

    private async Task<(ResolvedThreadWorkspace Workspace, SourceControlRepository Repository, int Number, ParsedReview Selected)> ValidateWriteTargetAsync(
        PullRequestReviewTarget target, CancellationToken cancellationToken,
        IReadOnlyList<PullRequestInlineComment>? comments = null, string? threadId = null)
    {
        if (target is null || target.Workspace is null || string.IsNullOrWhiteSpace(target.Repository) || string.IsNullOrWhiteSpace(target.HeadCommitId))
            throw ReviewError("The review target, repository, and head commit are required.");
        var workspace = await _resolver.ResolveAsync(target.Workspace.ProjectId, target.Workspace.ThreadId, cancellationToken).ConfigureAwait(false);
        var repository = await DetectAsync(new DetectSourceControlRequest(target.Workspace), cancellationToken).ConfigureAwait(false);
        EnsureGitHub(repository);
        if (!string.Equals(target.Repository, PullRequestReviewDefaults.RepositoryKey(repository), StringComparison.Ordinal))
            throw ReviewError("The selected repository no longer matches the workspace remote. Reload the pull request.");
        var number = ValidateNumber(target.Number);
        using var response = await QueryReviewAsync(repository, number, workspace.WorkspaceRoot, cancellationToken).ConfigureAwait(false);
        var initial = ParseReview(repository, response, number);
        if (!string.Equals(initial.HeadCommitId, target.HeadCommitId, StringComparison.OrdinalIgnoreCase))
            throw ReviewError("The pull request head changed. Reload the review before writing.");
        var files = await GetWriteFilesAsync(repository, number, workspace.WorkspaceRoot, comments ?? [], cancellationToken).ConfigureAwait(false);
        PullRequestDiscussion? additionalThread = null;
        if (threadId is not null && !initial.Discussions.Any(thread => thread.Id == threadId))
        {
            using var threadResponse = await QueryReviewThreadAsync(repository, number, workspace.WorkspaceRoot, target.HeadCommitId, threadId, null, cancellationToken).ConfigureAwait(false);
            additionalThread = ParseDiscussion(threadResponse.RootElement.GetProperty("data").GetProperty("node"));
        }
        using var finalResponse = await QueryReviewAsync(repository, number, workspace.WorkspaceRoot, cancellationToken).ConfigureAwait(false);
        var selected = ParseReview(repository, finalResponse, number);
        if (!string.Equals(initial.HeadCommitId, selected.HeadCommitId, StringComparison.OrdinalIgnoreCase) || !string.Equals(selected.HeadCommitId, target.HeadCommitId, StringComparison.OrdinalIgnoreCase))
            throw ReviewError("The pull request head changed while loading its files. Reload the review before writing.");
        return (workspace, repository, number, selected with
        {
            Files = files,
            Discussions = additionalThread is null ? selected.Discussions : [.. selected.Discussions, additionalThread]
        });
    }

    private async Task<JsonDocument> QueryReviewAsync(SourceControlRepository repository, int number, string workspace, CancellationToken cancellationToken)
    {
        var variables = new { owner = repository.Owner, name = repository.Name, number };
        return await ExecutePagedReviewQueryAsync(repository, workspace, ReviewQuery, variables, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> ExecuteGraphQlAsync(SourceControlRepository repository, string workspace, string query, object variables, CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Serialize(new { query, variables });
        var args = new[] { "api", "graphql", "--hostname", repository.Host, "--method", "POST", "--include", "--input", "-" };
        var result = await RunReviewCommandAsync(args, workspace, input, cancellationToken).ConfigureAwait(false);
        if (result.StandardOutput.Length >= 2 * 1024 * 1024) throw ReviewError("GitHub GraphQL response exceeded the host safety limit.");
        JsonDocument document;
        try { document = JsonDocument.Parse(ReviewResponseBody(result.StandardOutput)); }
        catch (JsonException exception) { throw ReviewError($"GitHub returned invalid GraphQL JSON: {exception.Message}"); }
        if (document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var details = string.Join(" ", errors.EnumerateArray().Select(error => Text(error, "message") ?? error.GetRawText()));
            var httpStatus = FindHttpStatus(result.StandardOutput);
            if (IsDefinitiveGraphQlFailure(document.RootElement) &&
                (httpStatus is null || httpStatus is >= 200 and <= 299 || IsConfirmedHttpRejectionStatus(httpStatus.Value)))
            {
                document.Dispose();
                throw new ConfirmedProviderRejectionException($"GitHub GraphQL rejected the request: {details}");
            }
            document.Dispose();
            throw ReviewError($"GitHub GraphQL rejected the request: {details}");
        }
        if (!document.RootElement.TryGetProperty("data", out _))
        {
            document.Dispose();
            if (result.ExitCode != 0) EnsureReviewProviderSucceeded(result);
            throw ReviewError("GitHub returned no GraphQL data.");
        }
        if (result.ExitCode != 0)
        {
            document.Dispose();
            EnsureReviewProviderSucceeded(result);
        }
        return document;
    }

    private static PullRequestChangedFile ParseFile(JsonElement file)
    {
        var patch = Text(file, "patch");
        var unavailable = string.IsNullOrEmpty(patch);
        var lines = unavailable ? Array.Empty<PullRequestDiffLine>() : ParsePatch(patch!);
        return new PullRequestChangedFile(
            Text(file, "filename") ?? "(unknown)", Text(file, "previous_filename"), Text(file, "status") ?? "modified",
            Number(file, "additions"), Number(file, "deletions"), lines, unavailable);
    }

    private async Task<ProcessResult> RunReviewCommandAsync(
        IReadOnlyList<string> arguments, string workspace, string? standardInput, CancellationToken cancellationToken)
    {
        if (_reviewCommandExecutor is not null)
        {
            var result = await _reviewCommandExecutor("gh", arguments, workspace, standardInput, cancellationToken).ConfigureAwait(false);
            return new ProcessResult(result.ExitCode, result.StandardOutput, result.StandardError);
        }
        return await RunAsync("gh", arguments, workspace, NetworkTimeout, cancellationToken, standardInput: standardInput).ConfigureAwait(false);
    }

    internal static IReadOnlyList<PullRequestDiffLine> ParsePatch(string patch)
    {
        var lines = new List<PullRequestDiffLine>();
        int? oldLine = null;
        int? newLine = null;
        var inHunk = false;
        foreach (var text in patch.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (text.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunk = TryParseHunkHeader(text, out oldLine, out newLine);
                if (!inHunk) oldLine = newLine = null;
                lines.Add(new PullRequestDiffLine(null, null, text, PullRequestDiffLineKind.Header));
                continue;
            }
            if (text.StartsWith("+", StringComparison.Ordinal) && (inHunk || !text.StartsWith("+++", StringComparison.Ordinal)))
            {
                lines.Add(new PullRequestDiffLine(null, newLine, text, PullRequestDiffLineKind.Addition));
                AdvanceLine(ref newLine);
            }
            else if (text.StartsWith("-", StringComparison.Ordinal) && (inHunk || !text.StartsWith("---", StringComparison.Ordinal)))
            {
                lines.Add(new PullRequestDiffLine(oldLine, null, text, PullRequestDiffLineKind.Deletion));
                AdvanceLine(ref oldLine);
            }
            else if (inHunk && text.StartsWith(" ", StringComparison.Ordinal))
            {
                lines.Add(new PullRequestDiffLine(oldLine, newLine, text, PullRequestDiffLineKind.Context));
                AdvanceLine(ref oldLine);
                AdvanceLine(ref newLine);
            }
            else if (text.Length > 0)
            {
                lines.Add(new PullRequestDiffLine(null, null, text, PullRequestDiffLineKind.Metadata));
            }
        }
        return lines;
    }

    private static bool TryParseHunkHeader(string text, out int? oldStart, out int? newStart)
    {
        oldStart = newStart = null;
        var parts = text.Split(' ', 5);
        return parts.Length >= 4 && parts[0] == "@@" && parts[3] == "@@" &&
            parts[1].StartsWith('-') && parts[2].StartsWith('+') &&
            TryParseRangeStart(parts[1][1..], out oldStart) && TryParseRangeStart(parts[2][1..], out newStart);
    }

    private static bool TryParseRangeStart(string token, out int? start)
    {
        var comma = token.IndexOf(',');
        var value = comma < 0 ? token : token[..comma];
        var count = 1;
        if (comma >= 0 && !int.TryParse(token[(comma + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out count))
        {
            start = null;
            return false;
        }
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < 0 || (parsed == 0 && count > 0) || (long)parsed + count - 1 > int.MaxValue)
        {
            start = null;
            return false;
        }
        start = parsed;
        return true;
    }

    private static void AdvanceLine(ref int? line) => line = line is null or int.MaxValue ? null : line.Value + 1;

    internal static (string Endpoint, string[] Arguments, string Input) BuildSubmitReviewCommand(
        SourceControlRepository repository, int number, string headCommitId, PullRequestReviewEvent reviewEvent,
        string body, IReadOnlyList<PullRequestInlineComment> comments)
    {
        var endpoint = $"repos/{repository.Owner}/{repository.Name}/pulls/{number}/reviews";
        var payload = JsonSerializer.Serialize(new
        {
            commit_id = headCommitId,
            @event = reviewEvent switch
            {
                PullRequestReviewEvent.Approve => "APPROVE",
                PullRequestReviewEvent.RequestChanges => "REQUEST_CHANGES",
                _ => "COMMENT"
            },
            body,
            comments = comments.Select(comment => new
            {
                path = comment.Path,
                line = comment.Line,
                side = comment.Side == PullRequestDiffSide.Left ? "LEFT" : "RIGHT",
                body = comment.Body
            }).ToArray()
        });
        return (endpoint, ["api", "--hostname", repository.Host, "--method", "POST", "--include", "--input", "-", endpoint], payload);
    }

    private sealed record ParsedReview(
        PullRequestDescriptor Descriptor, string Body, string HeadCommitId, string BaseCommitId, string ViewerLogin,
        IReadOnlyList<PullRequestCommit> Commits, IReadOnlyList<PullRequestCheck> Checks, IReadOnlyList<PullRequestDiscussion> Discussions,
        string RepositoryNodeId, string PullRequestNodeId, bool IsTruncated, string? Notice)
    {
        public IReadOnlyList<PullRequestChangedFile> Files { get; init; } = [];
    }

    private static PullRequestReviewSnapshot ParseReviewSnapshot(SourceControlRepository repository, JsonDocument document, int number)
    {
        var parsed = ParseReview(repository, document, number);
        var repo = document.RootElement.GetProperty("data").GetProperty("repository");
        var pr = repo.GetProperty("pullRequest");
        return new PullRequestReviewSnapshot(repository, parsed.Descriptor, parsed.Body, parsed.HeadCommitId, parsed.BaseCommitId,
            parsed.ViewerLogin, parsed.Commits, parsed.Checks, [], parsed.Discussions, parsed.IsTruncated, parsed.Notice,
            CanEditDetails: Boolean(document.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest"), "viewerCanUpdate"),
            CanManageMetadata: CanManageRepository(repo), Advanced: ParseAdvancedState(repo, pr),
            CanReact: Boolean(pr, "viewerCanReact"), Reactions: ParseReactions(pr));
    }

    private static ParsedReview ParseReview(SourceControlRepository repository, JsonDocument document, int number)
    {
        var data = document.RootElement.GetProperty("data");
        var pr = data.GetProperty("repository").GetProperty("pullRequest");
        if (pr.ValueKind == JsonValueKind.Null) throw ReviewError($"Pull request #{number} was not found in {repository.Owner}/{repository.Name}.");
        var head = Text(pr, "headRefOid") ?? throw ReviewError("GitHub returned a pull request without a head commit.");
        var author = NestedText(pr, "author", "login") ?? "Unknown";
        var stateText = Text(pr, "state") ?? "UNKNOWN";
        var state = stateText.ToUpperInvariant() switch { "OPEN" => Boolean(pr, "isDraft") ? PullRequestState.Draft : PullRequestState.Open, "CLOSED" => PullRequestState.Closed, "MERGED" => PullRequestState.Merged, _ => PullRequestState.Unknown };
        var labels = ReadNodes(pr, "labels", "name");
        var reviewers = ReadReviewers(pr);
        var commits = ParseCommits(pr, out var commitsTruncated);
        var checks = ParseChecks(pr, out var checksTruncated);
        var discussions = ParseDiscussions(pr, out var discussionsTruncated, out var commentsTruncated);
        var truncated = commitsTruncated || checksTruncated || discussionsTruncated || commentsTruncated || HasNext(pr, "labels") || HasNext(pr, "reviewRequests") || HasNext(pr, "comments") || HasNext(pr, "reviews");
        var notices = new List<string>();
        if (commitsTruncated) notices.Add("Commits were truncated at 100.");
        if (checksTruncated) notices.Add("Checks were truncated at 100.");
        if (discussionsTruncated) notices.Add("Review threads were truncated at 100.");
        if (commentsTruncated) notices.Add("Thread comments were truncated at 100.");
        if (HasNext(pr, "reviews")) notices.Add("Submitted reviews were truncated at 100.");
        if (HasNext(pr, "labels")) notices.Add("Labels were truncated at 100.");
        if (HasNext(pr, "reviewRequests")) notices.Add("Review requests were truncated at 100.");
        if (HasNext(pr, "comments")) notices.Add("General comments were truncated at 100.");
        var descriptor = new PullRequestDescriptor(repository.Provider, $"{repository.Owner}/{repository.Name}", number.ToString(CultureInfo.InvariantCulture),
            Text(pr, "title") ?? $"Pull request #{number}", Text(pr, "url") ?? repository.WebUrl + "/pull/" + number,
            state, author, Text(pr, "headRefName") ?? string.Empty, Text(pr, "baseRefName") ?? string.Empty,
            Boolean(pr, "isDraft"), labels, reviewers, PullRequestReviewDefaults.GetCheckState(checks, checksTruncated),
            DateTimeOffset.TryParse(Text(pr, "updatedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var updated) ? updated : DateTimeOffset.UtcNow);
        return new ParsedReview(descriptor, Text(pr, "body") ?? string.Empty, head, Text(pr, "baseRefOid") ?? string.Empty,
            NestedText(data, "viewer", "login") ?? string.Empty, commits, checks, discussions,
            Text(data.GetProperty("repository"), "id") ?? string.Empty, Text(pr, "id") ?? string.Empty, truncated,
            notices.Count == 0 ? null : string.Join(" ", notices));
    }

    private static IReadOnlyList<PullRequestCommit> ParseCommits(JsonElement pr, out bool truncated)
    {
        var connection = pr.GetProperty("commits"); truncated = HasNext(connection);
        return Nodes(connection).Take(PullRequestReviewDefaults.MaximumItems).Select(commit =>
        {
            var c = commit.GetProperty("commit");
            return new PullRequestCommit(Text(c, "oid") ?? "", Text(c, "messageHeadline") ?? "", NestedText(c, "author", "user", "login") ?? NestedText(c, "author", "name") ?? "Unknown",
                DateTimeOffset.TryParse(Text(c, "committedDate"), out var created) ? created : DateTimeOffset.UtcNow, Text(c, "url"));
        }).ToArray();
    }

    private static IReadOnlyList<PullRequestCheck> ParseChecks(JsonElement pr, out bool truncated)
    {
        truncated = false;
        if (!pr.TryGetProperty("headCommit", out var commits) || !Nodes(commits).Any()) return [];
        var commit = Nodes(commits).First().GetProperty("commit");
        if (!commit.TryGetProperty("statusCheckRollup", out var rollup) || rollup.ValueKind == JsonValueKind.Null) return [];
        var contexts = rollup.GetProperty("contexts"); truncated = HasNext(contexts);
        return Nodes(contexts).Take(PullRequestReviewDefaults.MaximumItems).Select(check => new PullRequestCheck(
            Text(check, "name") ?? Text(check, "context") ?? "Check", Text(check, "status") ?? Text(check, "state") ?? "UNKNOWN",
            Text(check, "conclusion"), Text(check, "detailsUrl") ?? Text(check, "targetUrl"), Text(check, "id"))).ToArray();
    }

    private static IReadOnlyList<PullRequestDiscussion> ParseDiscussions(JsonElement pr, out bool truncated, out bool commentsTruncated)
    {
        truncated = commentsTruncated = false;
        if (!pr.TryGetProperty("reviewThreads", out var connection)) return [];
        truncated = HasNext(connection);
        var result = new List<PullRequestDiscussion>();
        foreach (var thread in Nodes(connection).Take(PullRequestReviewDefaults.MaximumItems))
        {
            commentsTruncated |= HasNext(thread, "comments");
            result.Add(ParseDiscussion(thread));
        }
        if (pr.TryGetProperty("comments", out var general))
        {
            commentsTruncated |= HasNext(general);
            foreach (var comment in Nodes(general).Take(PullRequestReviewDefaults.MaximumItems))
            {
                var parsed = ParseManagementComment(comment, PullRequestCommentKind.General);
                result.Add(new PullRequestDiscussion(parsed.Id, null, null, null, false, false, false, false, [parsed]));
            }
        }
        if (pr.TryGetProperty("reviews", out var reviews))
        {
            foreach (var review in Nodes(reviews).Take(PullRequestReviewDefaults.MaximumItems))
            {
                var body = Text(review, "body");
                if (string.IsNullOrWhiteSpace(body)) continue;
                var parsed = ParseManagementComment(review, PullRequestCommentKind.Review);
                result.Add(new PullRequestDiscussion(parsed.Id, null, null, null, false, false, false, false, [parsed]));
            }
        }
        return result;
    }

    private static PullRequestDiscussion ParseDiscussion(JsonElement thread)
    {
        var comments = Nodes(thread.GetProperty("comments")).Select(comment => ParseManagementComment(comment, PullRequestCommentKind.Inline)).ToArray();
        var side = Text(thread, "diffSide")?.ToUpperInvariant() switch
        {
            "LEFT" => PullRequestDiffSide.Left, "RIGHT" => PullRequestDiffSide.Right, _ => (PullRequestDiffSide?)null
        };
        return new PullRequestDiscussion(Text(thread, "id") ?? "", Text(thread, "path"),
            NumberNullable(thread, "line") ?? NumberNullable(thread, "originalLine"), side,
            Boolean(thread, "isResolved"), Boolean(thread, "isOutdated"), Boolean(thread, "viewerCanReply"),
            Boolean(thread, "isResolved") ? Boolean(thread, "viewerCanUnresolve") : Boolean(thread, "viewerCanResolve"), comments);
    }

    private static PullRequestDiscussion FindThread(ParsedReview review, string id) => review.Discussions.FirstOrDefault(thread => string.Equals(thread.Id, id, StringComparison.Ordinal))
        ?? throw ReviewError("The review thread does not belong to the selected pull request.");

    private static void ValidateInlineComment(ParsedReview review, PullRequestInlineComment comment)
    {
        if (!Enum.IsDefined(comment.Side)) throw ReviewError("Inline comment side is invalid.");
        var file = review.Files.FirstOrDefault(file => string.Equals(file.Path, comment.Path, StringComparison.Ordinal));
        if (file is null || file.PatchUnavailable) throw ReviewError($"No hosted patch is available for '{comment.Path}'.");
        var valid = file.Lines.Any(line => comment.Side == PullRequestDiffSide.Right ? line.NewLine == comment.Line && line.Kind is PullRequestDiffLineKind.Context or PullRequestDiffLineKind.Addition : line.OldLine == comment.Line && line.Kind is PullRequestDiffLineKind.Context or PullRequestDiffLineKind.Deletion);
        if (!valid) throw ReviewError($"Line {comment.Line} on {comment.Side} is not a real line in '{comment.Path}'.");
    }

    private static void EnsureGitHub(SourceControlRepository repository)
    {
        if (repository.Provider != SourceControlProvider.GitHub) throw new HostOperationException(ProtocolErrorCodes.SourceControlUnavailable, "Pull request review is supported only for GitHub repositories.");
        if (string.IsNullOrWhiteSpace(repository.Host) || !SafeSegment(repository.Owner) || !SafeSegment(repository.Name)) throw ReviewError("The GitHub repository route is invalid.");
    }

    private static int ValidateNumber(string value)
    {
        if (!int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1 || number > MaximumPullRequestNumber)
            throw ReviewError("Pull request number must be a positive decimal number.");
        return number;
    }

    private static HostOperationException ReviewError(string message) => new(ProtocolErrorCodes.SourceControlOperationFailed, message);

    private static void EnsureReviewProviderSucceeded(ProcessResult result)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        var status = FindHttpStatus(result.StandardOutput);
        if (status is not null && IsConfirmedHttpRejectionStatus(status.Value))
        {
            throw new ConfirmedProviderRejectionException(
                $"GitHub rejected the request ({status.Value}). {ProviderErrorDetail(result)}".Trim());
        }

        // A non-zero exit without a definitive 4xx response may mean that the
        // provider accepted the write and the response was lost. Keep the
        // normal exception path so the durable runner records DispatchUncertain.
        EnsureProviderSucceeded(result, SourceControlProvider.GitHub);
    }

    internal static int? FindHttpStatus(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var offset = 0;
        int? status = null;
        while (offset < text.Length)
        {
            while (offset < text.Length && char.IsWhiteSpace(text[offset])) offset++;
            var lineEnd = text.IndexOf('\n', offset);
            if (lineEnd < 0) lineEnd = text.Length;
            var line = text[offset..lineEnd].TrimEnd('\r');
            var match = Regex.Match(line, @"^HTTP/\d(?:\.\d)?\s+(?<status>[1-5]\d{2})(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) break;
            if (int.TryParse(match.Groups["status"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) status = parsed;
            var separator = text.IndexOf("\r\n\r\n", offset, StringComparison.Ordinal);
            var separatorLength = 4;
            if (separator < 0)
            {
                separator = text.IndexOf("\n\n", lineEnd, StringComparison.Ordinal);
                separatorLength = 2;
            }
            if (separator < 0) break;
            offset = separator + separatorLength;
        }
        return status;
    }

    internal static bool IsDefinitiveGraphQlRejection(JsonElement error)
    {
        var type = Text(error, "type") ?? NestedText(error, "extensions", "code");
        return type is not null && type.ToUpperInvariant() is
            "FORBIDDEN" or "UNAUTHORIZED" or "ACCESS_DENIED" or "BAD_USER_INPUT" or
            "VALIDATION_FAILED" or "NOT_FOUND" or "RESOURCE_NOT_FOUND";
    }

    private static bool IsConfirmedHttpRejectionStatus(int status) => status is >= 400 and <= 499 and not 408 and not 429;

    internal static bool IsDefinitiveGraphQlFailure(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0 ||
            !errors.EnumerateArray().All(IsDefinitiveGraphQlRejection))
            return false;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return true;
        return data.ValueKind == JsonValueKind.Object && data.EnumerateObject().All(static property => property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);
    }

    private static string ProviderErrorDetail(ProcessResult result)
    {
        foreach (var output in new[] { ReviewResponseBody(result.StandardOutput), ReviewResponseBody(result.StandardError) })
        {
            if (string.IsNullOrWhiteSpace(output)) continue;
            try
            {
                using var document = JsonDocument.Parse(output);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    return message.GetString()!.Trim();
            }
            catch (JsonException) { }
        }

        var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        return ReviewResponseBody(detail).Trim();
    }

    internal static string ReviewResponseBody(string response)
    {
        if (string.IsNullOrEmpty(response)) return response;
        var offset = 0;
        var foundHeader = false;
        while (offset < response.Length)
        {
            while (offset < response.Length && char.IsWhiteSpace(response[offset])) offset++;
            var lineStart = offset;
            var lineEnd = response.IndexOf('\n', offset);
            if (lineEnd < 0) lineEnd = response.Length;
            var line = response[lineStart..lineEnd].TrimEnd('\r');
            if (!line.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)) break;
            foundHeader = true;
            var separator = response.IndexOf("\r\n\r\n", lineStart, StringComparison.Ordinal);
            var separatorLength = 4;
            if (separator < 0)
            {
                separator = response.IndexOf("\n\n", lineEnd, StringComparison.Ordinal);
                separatorLength = 2;
            }
            if (separator < 0) return string.Empty;
            offset = separator + separatorLength;
        }
        return foundHeader ? response[offset..] : response;
    }

    private static void EnsureJsonResponse(string json, string message)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("message", out var error))
                throw ReviewError(error.GetString() ?? message);
        }
        catch (JsonException exception) { throw ReviewError($"{message} {exception.Message}"); }
    }
    private static SourceControlOperationResult Rejected(string message, PiStation.Protocol.Identifiers.CommandId? operationId) =>
        new(false, message, OperationId: operationId, State: CommandReceiptState.Rejected);
    private static bool SafeSegment(string value) => value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
    private static IEnumerable<JsonElement> Nodes(JsonElement connection) => connection.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array ? nodes.EnumerateArray() : [];
    private static IEnumerable<JsonElement> EnumerateFilePage(JsonElement page) => page.ValueKind == JsonValueKind.Array ? page.EnumerateArray() : [page];
    private static bool HasNext(JsonElement connection) => connection.TryGetProperty("pageInfo", out var info) && Boolean(info, "hasNextPage");
    private static bool HasNext(JsonElement parent, string property) => parent.TryGetProperty(property, out var value) && HasNext(value);
    private static int Number(JsonElement element, string property) => int.TryParse(Text(element, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static int? NumberNullable(JsonElement element, string property) => int.TryParse(Text(element, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static IReadOnlyList<string> ReadNodes(JsonElement parent, string property, string field) => parent.TryGetProperty(property, out var connection) ? Nodes(connection).Select(node => Text(node, field)).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().Take(PullRequestReviewDefaults.MaximumItems).ToArray() : [];
    private static IReadOnlyList<string> ReadReviewers(JsonElement parent) => parent.TryGetProperty("reviewRequests", out var connection) ? Nodes(connection).Select(node =>
        NestedText(node, "requestedReviewer", "login") ??
        (NestedText(node, "requestedReviewer", "slug") is { } slug ? $"{NestedText(node, "requestedReviewer", "organization", "login")}/{slug}" : NestedText(node, "requestedReviewer", "name")))
        .Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().Take(PullRequestReviewDefaults.MaximumItems).ToArray() : [];

    private sealed class ConfirmedProviderRejectionException(string message) : Exception(message);
}
