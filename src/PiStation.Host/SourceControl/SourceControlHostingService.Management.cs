using System.Globalization;
using System.Text.Json;
using PiStation.Protocol.Models;

namespace PiStation.Host.SourceControl;

public sealed partial class SourceControlHostingService
{
    private const string ManagementQuery = """
        query($owner:String!, $name:String!, $number:Int!) {
          repository(owner:$owner, name:$name) {
            viewerPermission mergeCommitAllowed squashMergeAllowed rebaseMergeAllowed autoMergeAllowed
            pullRequest(number:$number) {
              id number title body state isDraft headRefOid viewerCanUpdate
              headRefName headRepository { nameWithOwner owner { login } }
              mergeable mergeStateStatus isCrossRepository viewerCanUpdateBranch viewerCanEnableAutoMerge viewerCanDisableAutoMerge
              autoMergeRequest { enabledAt } viewerCanReact reactionGroups { content viewerHasReacted reactors { totalCount } }
            }
          }
        }
        """;
    private const string ManagementCommentQuery = """
        query($id:ID!) { node(id:$id) {
          __typename
          ... on IssueComment { id body viewerDidAuthor viewerCanUpdate viewerCanDelete issuePullRequest:pullRequest { id } viewerCanReact reactionGroups { content viewerHasReacted reactors { totalCount } } }
          ... on PullRequestReviewComment { id body viewerDidAuthor viewerCanUpdate viewerCanDelete pullRequest { id } viewerCanReact reactionGroups { content viewerHasReacted reactors { totalCount } } }
          ... on PullRequestReview { id body state viewerDidAuthor viewerCanUpdate viewerCanDelete pullRequest { id } viewerCanReact reactionGroups { content viewerHasReacted reactors { totalCount } } }
        } }
        """;

    public async Task<SourceControlOperationResult> ManagePullRequestAsync(ManagePullRequestRequest request,
        CancellationToken cancellationToken = default)
    {
        var dispatched = false;
        try
        {
            if (request?.Target?.Workspace is null) throw ReviewError("A pull request workspace and revision are required.");
            if (await IsAdditionalReviewProviderAsync(request.Target.Workspace, cancellationToken).ConfigureAwait(false))
                return await WriteProviderReviewAsync(request.Target, request.OperationId, request, cancellationToken).ConfigureAwait(false);
            var target = request.Target;
            var workspace = await _resolver.ResolveAsync(target.Workspace.ProjectId, target.Workspace.ThreadId, cancellationToken).ConfigureAwait(false);
            var repository = await DetectAsync(new(target.Workspace), cancellationToken).ConfigureAwait(false);
            EnsureGitHub(repository);
            if (target.Repository != PullRequestReviewDefaults.RepositoryKey(repository))
                throw ReviewError("The repository changed. Reload this pull request before editing it.");
            var number = ValidateNumber(target.Number);
            JsonElement comment = default;
            if (request.Action is PullRequestManagementAction.EditComment or PullRequestManagementAction.DeleteComment ||
                request.Action == PullRequestManagementAction.SetReaction && request.ItemId is not null)
            {
                if (string.IsNullOrWhiteSpace(request.ItemId) || request.ItemId.Length > 256) throw ReviewError("Select a comment to edit or delete.");
                using var response = await ExecuteGraphQlAsync(repository, workspace.WorkspaceRoot, ManagementCommentQuery,
                    new { id = request.ItemId }, cancellationToken).ConfigureAwait(false);
                comment = response.RootElement.GetProperty("data").GetProperty("node").Clone();
            }
            JsonElement workflow = default;
            var uniqueWorkflowHead = false;
            if (request.Action == PullRequestManagementAction.ApproveWorkflow)
            {
                workflow = await ReadWorkflowAsync(repository, workspace.WorkspaceRoot, request.ItemId, cancellationToken).ConfigureAwait(false);
                if (WorkflowHasNoAssociation(workflow))
                {
                    using var scope = await ExecuteGraphQlAsync(repository, workspace.WorkspaceRoot, ManagementQuery,
                        new { owner = repository.Owner, name = repository.Name, number }, cancellationToken).ConfigureAwait(false);
                    uniqueWorkflowHead = await HasUniqueWorkflowHeadAsync(repository, workspace.WorkspaceRoot, target,
                        scope.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest"), cancellationToken).ConfigureAwait(false);
                }
            }
            // Re-read immediately before dispatch. Expected text/state prevents overwriting an edit already made elsewhere.
            using var current = await ExecuteGraphQlAsync(repository, workspace.WorkspaceRoot, ManagementQuery,
                new { owner = repository.Owner, name = repository.Name, number }, cancellationToken).ConfigureAwait(false);
            var repo = current.RootElement.GetProperty("data").GetProperty("repository");
            var mutation = BuildManagementMutation(request, repo, comment);
            if (request.Action == PullRequestManagementAction.SetReaction && mutation.Query.Length == 0)
                return new(true, "The reaction already has the requested state.", repository, OperationId: request.OperationId);
            if (request.Action == PullRequestManagementAction.ApproveWorkflow)
            {
                ValidateWorkflow(workflow, target, repo.GetProperty("pullRequest"), uniqueWorkflowHead);
                if (!WorkflowNeedsApproval(workflow)) throw ReviewError("This workflow is no longer waiting for approval. Refresh the workflow list.");
                dispatched = true;
                var response = await RunReviewCommandAsync(["api", "--hostname", repository.Host, "--method", "POST", "--include",
                    $"repos/{repository.Owner}/{repository.Name}/actions/runs/{request.ItemId}/approve"], workspace.WorkspaceRoot, null, cancellationToken).ConfigureAwait(false);
                EnsureReviewProviderSucceeded(response);
                if (FindHttpStatus(response.StandardOutput) != 201) throw ReviewError("GitHub did not confirm workflow approval. Refresh operation history before retrying.");
            }
            else if (request.Action is PullRequestManagementAction.RemoveLabel or PullRequestManagementAction.RemoveReviewer)
            {
                var command = BuildRemovalCommand(repository, number, request);
                dispatched = true;
                var response = await RunReviewCommandAsync(command.Arguments, workspace.WorkspaceRoot, command.Input, cancellationToken).ConfigureAwait(false);
                EnsureReviewProviderSucceeded(response);
                EnsureJsonResponse(ReviewResponseBody(response.StandardOutput), "GitHub returned an invalid removal response.");
            }
            else
            {
                dispatched = true;
                using var response = await ExecuteGraphQlAsync(repository, workspace.WorkspaceRoot, mutation.Query,
                    mutation.Variables, cancellationToken).ConfigureAwait(false);
                if (!response.RootElement.GetProperty("data").TryGetProperty("change", out var changed) || changed.ValueKind != JsonValueKind.Object)
                    throw ReviewError("GitHub did not confirm the change. Refresh operation history before retrying.");
                if (request.Action == PullRequestManagementAction.Revert)
                {
                    var url = NestedText(changed, "revertPullRequest", "url");
                    if (string.IsNullOrEmpty(url)) throw ReviewError("GitHub did not return the revert pull request. Check operation history before retrying.");
                    return new(true, "Created revert pull request: " + url, repository, OperationId: request.OperationId);
                }
            }
            return new(true, "Pull request updated.", repository, OperationId: request.OperationId);
        }
        catch (ConfirmedProviderRejectionException exception) { return Rejected(exception.Message, request?.OperationId); }
        catch (Exception exception) when (!dispatched && exception is not OperationCanceledException)
        { return Rejected(exception.Message, request?.OperationId); }
    }

    internal static (string Query, object Variables) BuildManagementMutation(ManagePullRequestRequest request,
        JsonElement repository, JsonElement comment = default)
    {
        if (!Enum.IsDefined(request.Action) || repository.ValueKind != JsonValueKind.Object ||
            !repository.TryGetProperty("pullRequest", out var pr) || pr.ValueKind != JsonValueKind.Object)
            throw ReviewError("The pull request or action is unavailable.");
        if (Text(pr, "number") != request.Target.Number ||
            !string.Equals(Text(pr, "headRefOid"), request.Target.HeadCommitId, StringComparison.OrdinalIgnoreCase))
            throw ReviewError("The pull request head changed. Reload before writing.");
        var id = Text(pr, "id");
        if (string.IsNullOrEmpty(id)) throw ReviewError("The pull request has no provider identity.");
        switch (request.Action)
        {
            case PullRequestManagementAction.EditDetails:
                if (!Boolean(pr, "viewerCanUpdate")) throw ReviewError("You do not have permission to edit this pull request.");
                if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 256 || request.Title.Any(char.IsControl) ||
                    request.Body is null || request.Body.Length > PullRequestReviewDefaults.MaximumDescriptionCharacters)
                    throw ReviewError("Enter a title of 1–256 characters and a description of at most 65536 characters.");
                if (request.ExpectedTitle != Text(pr, "title") || request.ExpectedBody != Text(pr, "body"))
                    throw ReviewError("The pull request title or description changed elsewhere. Your edits are saved; compare them with the latest details before retrying.");
                return ("mutation($id:ID!, $title:String!, $body:String!){ change:updatePullRequest(input:{pullRequestId:$id title:$title body:$body}) { pullRequest { id } } }",
                    new { id, title = request.Title, body = request.Body });
            case PullRequestManagementAction.SetDraft:
                if (!Boolean(pr, "viewerCanUpdate") || Text(pr, "state") != "OPEN") throw ReviewError("Only an open pull request you can edit can change draft status.");
                if (request.IsDraft is null || request.ExpectedIsDraft is null || request.ExpectedIsDraft != Boolean(pr, "isDraft"))
                    throw ReviewError("The draft status changed. Refresh before changing it again.");
                return (request.IsDraft == true
                    ? "mutation($id:ID!){ change:convertPullRequestToDraft(input:{pullRequestId:$id}) { pullRequest { id } } }"
                    : "mutation($id:ID!){ change:markPullRequestReadyForReview(input:{pullRequestId:$id}) { pullRequest { id } } }", new { id });
            case PullRequestManagementAction.EditComment:
            case PullRequestManagementAction.DeleteComment:
                if (comment.ValueKind != JsonValueKind.Object || Text(comment, "id") != request.ItemId ||
                    ManagementCommentPullRequestId(comment) != id || !Boolean(comment, "viewerDidAuthor"))
                    throw ReviewError("Select your own comment on this pull request.");
                if (request.ExpectedBody is null || request.ExpectedBody != Text(comment, "body"))
                    throw ReviewError("The comment changed elsewhere. Your edits are saved; reload the comment before retrying.");
                var deleting = request.Action == PullRequestManagementAction.DeleteComment;
                if (!Boolean(comment, deleting ? "viewerCanDelete" : "viewerCanUpdate")) throw ReviewError("You do not have permission to change this comment.");
                if (!deleting && (string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > PullRequestReviewDefaults.MaximumBodyCharacters))
                    throw ReviewError("A comment must contain 1–32768 characters.");
                var type = Text(comment, "__typename");
                var (name, idField) = (type, deleting) switch
                {
                    ("IssueComment", false) => ("updateIssueComment", "id"),
                    ("IssueComment", true) => ("deleteIssueComment", "id"),
                    ("PullRequestReviewComment", false) => ("updatePullRequestReviewComment", "pullRequestReviewCommentId"),
                    ("PullRequestReviewComment", true) => ("deletePullRequestReviewComment", "id"),
                    ("PullRequestReview", false) => ("updatePullRequestReview", "pullRequestReviewId"),
                    ("PullRequestReview", true) when Text(comment, "state") == "PENDING" => ("deletePullRequestReview", "pullRequestReviewId"),
                    _ => throw ReviewError("Submitted reviews cannot be deleted; edit their text instead."),
                };
                return (deleting
                    ? $"mutation($id:ID!){{ change:{name}(input:{{{idField}:$id}}) {{ clientMutationId }} }}"
                    : $"mutation($id:ID!, $body:String!){{ change:{name}(input:{{{idField}:$id body:$body}}) {{ clientMutationId }} }}",
                    deleting ? new { id = request.ItemId } : (object)new { id = request.ItemId, body = request.Body });
            case PullRequestManagementAction.RemoveLabel:
            case PullRequestManagementAction.RemoveReviewer:
                if (!CanManageRepository(repository)) throw ReviewError("Repository write permission is required to remove labels or reviewers.");
                if (string.IsNullOrWhiteSpace(request.ItemId) || request.ItemId.Length > 256 || request.ItemId.Any(char.IsControl))
                    throw ReviewError("Select a label or reviewer to remove.");
                return (string.Empty, new { });
            default: return BuildAdvancedMutation(request, repository, pr, comment);
        }
    }

    internal static (string[] Arguments, string? Input) BuildRemovalCommand(SourceControlRepository repository, int number, ManagePullRequestRequest request)
    {
        var endpoint = $"repos/{repository.Owner}/{repository.Name}";
        string? input = null;
        if (request.Action == PullRequestManagementAction.RemoveLabel)
            endpoint += $"/issues/{number.ToString(CultureInfo.InvariantCulture)}/labels/{Uri.EscapeDataString(request.ItemId!)}";
        else
        {
            endpoint += $"/pulls/{number.ToString(CultureInfo.InvariantCulture)}/requested_reviewers";
            var parts = request.ItemId!.Split('/');
            if (parts.Length > 2 || parts.Any(string.IsNullOrWhiteSpace) || parts.Length == 2 &&
                !string.Equals(parts[0], repository.Owner, StringComparison.OrdinalIgnoreCase)) throw ReviewError("The selected reviewer is invalid for this repository.");
            input = JsonSerializer.Serialize(new { reviewers = parts.Length == 1 ? parts : [], team_reviewers = parts.Length == 2 ? new[] { parts[1] } : [] });
        }
        return (input is null
            ? ["api", "--hostname", repository.Host, "--method", "DELETE", "--include", endpoint]
            : ["api", "--hostname", repository.Host, "--method", "DELETE", "--include", "--input", "-", endpoint], input);
    }

    private static bool CanManageRepository(JsonElement repository) => Text(repository, "viewerPermission") is "ADMIN" or "MAINTAIN" or "WRITE";
    private static string? ManagementCommentPullRequestId(JsonElement comment) =>
        NestedText(comment, Text(comment, "__typename") == "IssueComment" ? "issuePullRequest" : "pullRequest", "id");

    internal static PullRequestReviewComment ParseManagementComment(JsonElement comment, PullRequestCommentKind kind) => new(
        Text(comment, "id") ?? "", NestedText(comment, "author", "login") ?? "Unknown", Text(comment, "body") ?? "",
        DateTimeOffset.TryParse(Text(comment, kind == PullRequestCommentKind.Review ? "submittedAt" : "createdAt"), out var created) ? created : DateTimeOffset.UtcNow,
        Text(comment, "url"), Boolean(comment, "viewerDidAuthor") && Boolean(comment, "viewerCanUpdate"),
        Boolean(comment, "viewerDidAuthor") && Boolean(comment, "viewerCanDelete") && (kind != PullRequestCommentKind.Review || Text(comment, "state") == "PENDING"), kind,
        Boolean(comment, "viewerCanReact"), ParseReactions(comment));
}
