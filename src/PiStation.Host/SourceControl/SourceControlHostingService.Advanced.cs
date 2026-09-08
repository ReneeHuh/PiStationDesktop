using System.Globalization;
using System.Text.Json;
using PiStation.Protocol.Models;

namespace PiStation.Host.SourceControl;

public sealed partial class SourceControlHostingService
{
    internal static PullRequestAdvancedState ParseAdvancedState(JsonElement repository, JsonElement pr)
    {
        var methods = new List<PullRequestMergeMethod>();
        if (Boolean(repository, "mergeCommitAllowed")) methods.Add(PullRequestMergeMethod.Merge);
        if (Boolean(repository, "squashMergeAllowed")) methods.Add(PullRequestMergeMethod.Squash);
        if (Boolean(repository, "rebaseMergeAllowed")) methods.Add(PullRequestMergeMethod.Rebase);
        var open = Text(pr, "state") == "OPEN";
        var ready = open && !Boolean(pr, "isDraft");
        var write = CanManageRepository(repository);
        var auto = pr.TryGetProperty("autoMergeRequest", out var value) && value.ValueKind == JsonValueKind.Object;
        return new(methods, ready && write && Text(pr, "mergeable") == "MERGEABLE" && methods.Count > 0,
            ready && Boolean(repository, "autoMergeAllowed") && Boolean(pr, "viewerCanEnableAutoMerge") && !auto && methods.Count > 0,
            open && auto && Boolean(pr, "viewerCanDisableAutoMerge"), open && Boolean(pr, "viewerCanUpdateBranch"),
            Text(pr, "state") == "MERGED" && write, open && write && Boolean(pr, "isCrossRepository"), auto,
            Text(pr, "mergeable") ?? "UNKNOWN", Text(pr, "mergeStateStatus") ?? "UNKNOWN");
    }

    internal static IReadOnlyList<PullRequestReaction> ParseReactions(JsonElement subject)
    {
        if (subject.ValueKind != JsonValueKind.Object || !subject.TryGetProperty("reactionGroups", out var groups) || groups.ValueKind != JsonValueKind.Array) return [];
        var result = new List<PullRequestReaction>();
        foreach (var group in groups.EnumerateArray())
        {
            var content = Enum.GetValues<PullRequestReactionContent>().Cast<PullRequestReactionContent?>()
                .FirstOrDefault(value => ReactionName(value!.Value) == Text(group, "content"));
            if (content is null) continue;
            var count = group.TryGetProperty("reactors", out var reactors) && reactors.ValueKind == JsonValueKind.Object ? Number(reactors, "totalCount") : 0;
            result.Add(new(content.Value, Math.Max(0, count), Boolean(group, "viewerHasReacted")));
        }
        return result.DistinctBy(reaction => reaction.Content).ToArray();
    }

    private static string ReactionName(PullRequestReactionContent content) => content switch
    {
        PullRequestReactionContent.ThumbsUp => "THUMBS_UP", PullRequestReactionContent.ThumbsDown => "THUMBS_DOWN",
        PullRequestReactionContent.Laugh => "LAUGH", PullRequestReactionContent.Hooray => "HOORAY",
        PullRequestReactionContent.Confused => "CONFUSED", PullRequestReactionContent.Heart => "HEART",
        PullRequestReactionContent.Rocket => "ROCKET", PullRequestReactionContent.Eyes => "EYES",
        _ => throw ReviewError("Choose a supported reaction.")
    };

    private static (string Query, object Variables) BuildAdvancedMutation(ManagePullRequestRequest request,
        JsonElement repository, JsonElement pr, JsonElement comment)
    {
        var state = ParseAdvancedState(repository, pr);
        var id = Text(pr, "id")!;
        var head = request.Target.HeadCommitId;
        switch (request.Action)
        {
            case PullRequestManagementAction.Merge:
            case PullRequestManagementAction.EnableAutoMerge:
                var merge = request.Action == PullRequestManagementAction.Merge;
                if (!(merge ? state.CanMerge : state.CanEnableAutoMerge)) throw ReviewError("This pull request cannot use the selected merge action. Refresh its status and permissions.");
                if (request.MergeMethod is not { } method || !state.MergeMethods.Contains(method)) throw ReviewError("Choose a merge method allowed by this repository.");
                return (merge
                    ? "mutation($id:ID!, $head:GitObjectID!, $method:PullRequestMergeMethod!){ change:mergePullRequest(input:{pullRequestId:$id expectedHeadOid:$head mergeMethod:$method}) { pullRequest { id } } }"
                    : "mutation($id:ID!, $method:PullRequestMergeMethod!){ change:enablePullRequestAutoMerge(input:{pullRequestId:$id mergeMethod:$method}) { pullRequest { id } } }",
                    merge ? new { id, head, method = method.ToString().ToUpperInvariant() } : (object)new { id, method = method.ToString().ToUpperInvariant() });
            case PullRequestManagementAction.DisableAutoMerge:
                if (!state.CanDisableAutoMerge) throw ReviewError("Automatic merge is unavailable or you cannot disable it.");
                return ("mutation($id:ID!){ change:disablePullRequestAutoMerge(input:{pullRequestId:$id}) { pullRequest { id } } }", new { id });
            case PullRequestManagementAction.UpdateBranch:
                if (!state.CanUpdateBranch || request.UpdateMethod is not { } update || !Enum.IsDefined(update)) throw ReviewError("This branch cannot be updated with the selected method.");
                return ("mutation($id:ID!, $head:GitObjectID!, $method:PullRequestBranchUpdateMethod!){ change:updatePullRequestBranch(input:{pullRequestId:$id expectedHeadOid:$head updateMethod:$method}) { pullRequest { id } } }",
                    new { id, head, method = update.ToString().ToUpperInvariant() });
            case PullRequestManagementAction.Revert:
                if (!state.CanRevert) throw ReviewError("Only a merged pull request in a repository you can write to can be reverted.");
                return ("mutation($id:ID!){ change:revertPullRequest(input:{pullRequestId:$id draft:true}) { revertPullRequest { id url } } }", new { id });
            case PullRequestManagementAction.ApproveWorkflow:
                if (!state.CanApproveWorkflows) throw ReviewError("Workflow approval requires an open fork pull request and repository write access.");
                return (string.Empty, new { });
            case PullRequestManagementAction.SetReaction:
                var subject = request.ItemId is null ? pr : comment;
                if (subject.ValueKind != JsonValueKind.Object || !Boolean(subject, "viewerCanReact") ||
                    request.ItemId is not null && (Text(subject, "id") != request.ItemId || ManagementCommentPullRequestId(subject) != id))
                    throw ReviewError("Select a pull request or comment you can react to on this pull request.");
                if (request.Reaction is not { } reaction || !Enum.IsDefined(reaction) || request.Reacted is null) throw ReviewError("Choose a reaction and its desired state.");
                var active = ParseReactions(subject).Any(value => value.Content == reaction && value.ViewerHasReacted);
                if (active == request.Reacted) return (string.Empty, new { });
                var mutation = request.Reacted == true ? "addReaction" : "removeReaction";
                return ($"mutation($id:ID!, $content:ReactionContent!){{ change:{mutation}(input:{{subjectId:$id content:$content}}) {{ clientMutationId }} }}",
                    new { id = Text(subject, "id"), content = ReactionName(reaction) });
            default: throw ReviewError("Unsupported pull request action.");
        }
    }

    public async Task<PullRequestWorkflowsResult> GetPullRequestWorkflowsAsync(GetPullRequestWorkflowsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request?.Target?.Workspace is null || request.Page is < 1 or > 10) throw ReviewError("Choose a pull request and a workflow page from 1–10.");
        var target = request.Target;
        var workspace = await _resolver.ResolveAsync(target.Workspace.ProjectId, target.Workspace.ThreadId, cancellationToken).ConfigureAwait(false);
        var repository = await DetectAsync(new(target.Workspace), cancellationToken).ConfigureAwait(false);
        EnsureGitHub(repository);
        if (target.Repository != PullRequestReviewDefaults.RepositoryKey(repository)) throw ReviewError("The repository changed. Reload this pull request.");
        var number = ValidateNumber(target.Number);
        var currentPr = await CheckHeadAsync().ConfigureAwait(false);
        var result = await RunReviewCommandAsync(["api", "--hostname", repository.Host, "--method", "GET", "--include",
            $"repos/{repository.Owner}/{repository.Name}/actions/runs?event=pull_request&status=action_required&head_sha={Uri.EscapeDataString(target.HeadCommitId)}&per_page=100&page={request.Page.ToString(CultureInfo.InvariantCulture)}"],
            workspace.WorkspaceRoot, null, cancellationToken).ConfigureAwait(false);
        EnsureReviewProviderSucceeded(result);
        using var document = JsonDocument.Parse(ReviewResponseBody(result.StandardOutput));
        var runs = document.RootElement.GetProperty("workflow_runs");
        if (runs.ValueKind != JsonValueKind.Array || runs.GetArrayLength() > 100) throw ReviewError("GitHub returned an invalid workflow page.");
        var workflows = new List<PullRequestWorkflow>();
        var uniqueHead = runs.EnumerateArray().Any(WorkflowHasNoAssociation) &&
            await HasUniqueWorkflowHeadAsync(repository, workspace.WorkspaceRoot, target, currentPr, cancellationToken).ConfigureAwait(false);
        foreach (var run in runs.EnumerateArray())
        {
            // Fork runs with no unambiguous PR association are intentionally not offered for approval.
            if (!WorkflowMatches(run, target, currentPr, uniqueHead) || !WorkflowNeedsApproval(run) || !ValidWorkflowId(Text(run, "id"))) continue;
            workflows.Add(new(Text(run, "id")!, Text(run, "name") ?? "Workflow", "Awaiting approval", Text(run, "html_url") ?? ""));
        }
        await CheckHeadAsync().ConfigureAwait(false);
        return new(target, workflows, request.Page < 10 && runs.GetArrayLength() == 100 ? request.Page + 1 : null);

        async Task<JsonElement> CheckHeadAsync()
        {
            using var current = await ExecuteGraphQlAsync(repository, workspace.WorkspaceRoot, ManagementQuery,
                new { owner = repository.Owner, name = repository.Name, number }, cancellationToken).ConfigureAwait(false);
            var pr = current.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest");
            if (Text(pr, "number") != target.Number || !string.Equals(Text(pr, "headRefOid"), target.HeadCommitId, StringComparison.OrdinalIgnoreCase))
                throw ReviewError("The pull request head changed. Reload before approving workflows.");
            return pr.Clone();
        }
    }

    private async Task<JsonElement> ReadWorkflowAsync(SourceControlRepository repository, string root, string? id, CancellationToken cancellationToken)
    {
        if (!ValidWorkflowId(id)) throw ReviewError("Select a valid workflow run.");
        var result = await RunReviewCommandAsync(["api", "--hostname", repository.Host, "--method", "GET", "--include",
            $"repos/{repository.Owner}/{repository.Name}/actions/runs/{id}"], root, null, cancellationToken).ConfigureAwait(false);
        EnsureReviewProviderSucceeded(result);
        using var document = JsonDocument.Parse(ReviewResponseBody(result.StandardOutput));
        if (Text(document.RootElement, "id") != id) throw ReviewError("GitHub returned another workflow run.");
        return document.RootElement.Clone();
    }

    private static bool ValidWorkflowId(string? id) => id is { Length: > 0 and <= 20 } && id.All(char.IsAsciiDigit) &&
        ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0;
    private static bool WorkflowNeedsApproval(JsonElement run) => Text(run, "conclusion") == "action_required" || Text(run, "status") == "action_required";
    private async Task<bool> HasUniqueWorkflowHeadAsync(SourceControlRepository repository, string root, PullRequestReviewTarget target, JsonElement pr, CancellationToken token)
    {
        var owner = NestedText(pr, "headRepository", "owner", "login");
        var branch = Text(pr, "headRefName");
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(branch) || string.IsNullOrEmpty(NestedText(pr, "headRepository", "nameWithOwner"))) return false;
        var response = await RunReviewCommandAsync(["api", "--hostname", repository.Host, "--method", "GET", "--include",
            $"repos/{repository.Owner}/{repository.Name}/pulls?state=open&head={Uri.EscapeDataString(owner + ":" + branch)}&per_page=100"], root, null, token).ConfigureAwait(false);
        EnsureReviewProviderSucceeded(response);
        using var document = JsonDocument.Parse(ReviewResponseBody(response.StandardOutput));
        var requests = document.RootElement;
        return requests.ValueKind == JsonValueKind.Array && requests.GetArrayLength() == 1 && Text(requests[0], "number") == target.Number &&
            string.Equals(NestedText(requests[0], "head", "sha"), target.HeadCommitId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(NestedText(requests[0], "head", "repo", "full_name"), NestedText(pr, "headRepository", "nameWithOwner"), StringComparison.OrdinalIgnoreCase) &&
            NestedText(requests[0], "head", "ref") == branch;
    }

    private static bool WorkflowHasNoAssociation(JsonElement run) => run.ValueKind == JsonValueKind.Object &&
        run.TryGetProperty("pull_requests", out var requests) && requests.ValueKind == JsonValueKind.Array && requests.GetArrayLength() == 0;

    private static bool WorkflowMatches(JsonElement run, PullRequestReviewTarget target, JsonElement pr, bool uniqueHead)
    {
        if (run.ValueKind != JsonValueKind.Object || Text(run, "event") != "pull_request" ||
            !string.Equals(Text(run, "head_sha"), target.HeadCommitId, StringComparison.OrdinalIgnoreCase) ||
            !run.TryGetProperty("pull_requests", out var requests) || requests.ValueKind != JsonValueKind.Array) return false;
        if (requests.GetArrayLength() == 1) return Text(requests[0], "number") == target.Number &&
            string.Equals(NestedText(requests[0], "head", "sha"), target.HeadCommitId, StringComparison.OrdinalIgnoreCase);
        return requests.GetArrayLength() == 0 && uniqueHead && Text(run, "head_branch") == Text(pr, "headRefName") &&
            string.Equals(NestedText(run, "head_repository", "full_name"), NestedText(pr, "headRepository", "nameWithOwner"), StringComparison.OrdinalIgnoreCase);
    }
    private static void ValidateWorkflow(JsonElement run, PullRequestReviewTarget target, JsonElement pr, bool uniqueHead)
    {
        if (!WorkflowMatches(run, target, pr, uniqueHead)) throw ReviewError("The workflow is not unambiguously associated with this pull request revision. Refresh before approving it.");
    }
}
