using System.Globalization;
using System.Text.Json;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.Host.SourceControl;

public sealed partial class SourceControlHostingService
{
    private sealed record ProviderReviewStep(string Description, Func<CancellationToken, Task<JsonElement>> Execute, bool AllowEmpty = false);

    private async Task<SourceControlOperationResult> WriteProviderReviewAsync(PullRequestReviewTarget target, CommandId? operation,
        object request, CancellationToken token)
    {
        var dispatched = false;
        var completed = 0;
        string? last = null;
        SourceControlRepository? repo = null;
        PullRequestReviewSnapshot? snapshot = null;
        var steps = new List<ProviderReviewStep>();
        try
        {
            if (target?.Workspace is null || string.IsNullOrWhiteSpace(target.HeadCommitId)) throw ReviewError("A repository and inspected head commit are required.");
            var workspace = (await _resolver.ResolveAsync(target.Workspace.ProjectId, target.Workspace.ThreadId, token).ConfigureAwait(false)).WorkspaceRoot;
            repo = await DetectAsync(new(target.Workspace), token).ConfigureAwait(false);
            if (target.Repository != PullRequestReviewDefaults.RepositoryKey(repo)) throw ReviewError("The workspace remote changed. Reload the review before writing.");
            if (!repo.CanWrite || repo.Provider is not (SourceControlProvider.GitLab or SourceControlProvider.AzureDevOps)) throw ReviewError("This provider is not available for review writes.");
            var number = ValidateNumber(target.Number);
            if (repo.Provider == SourceControlProvider.AzureDevOps && request is not ManagePullRequestRequest)
                throw ReviewError("Azure conversation is read-only in PiStation. Use the provider website to submit a review.");
            snapshot = await ReadProviderReviewAsync(repo, number, workspace, null, token).ConfigureAwait(false);
            if (snapshot.HeadCommitId != target.HeadCommitId) throw ReviewError("The pull request head changed. Reload before writing.");
            var detail = await ReadProviderDetailAsync(repo, number, workspace, token).ConfigureAwait(false);
            var endpoint = GitLabRequest(repo, number);
            void GitLabStep(string description, string path, string method, object? payload = null, bool allowEmpty = false) =>
                steps.Add(new(description, ct => GitLabJsonAsync(repo, workspace, path, ct, method, payload), allowEmpty));
            void AzureStep(string description, object payload) =>
                steps.Add(new(description, ct => AzureInvokeAsync(repo, workspace, detail, number, "pullRequests", "PATCH", payload, ct)));

            switch (request)
            {
                case SubmitPullRequestReviewRequest review:
                {
                    if (review.Comments is null || review.Body is null || !snapshot.Capabilities!.Verdicts.Contains(review.Event)) throw ReviewError("Select a review action supported by this provider and account.");
                    if (review.Comments.Count > PullRequestReviewDefaults.MaximumInlineComments || review.Body.Length > PullRequestReviewDefaults.MaximumBodyCharacters ||
                        review.Event == PullRequestReviewEvent.Comment && string.IsNullOrWhiteSpace(review.Body)) throw ReviewError("Enter a review body within the supported review limits.");
                    long size = review.Body.Length;
                    var files = snapshot.Files.ToList();
                    var continuation = snapshot.NextPages?.FirstOrDefault(page => page.Kind == PullRequestReviewPageKind.Files);
                    while (review.Comments.Any(comment => comment is not null && !files.Any(file => file.Path == comment.Path)) && continuation is not null && files.Count < MaximumProviderReviewPages * PullRequestReviewDefaults.MaximumItems)
                    {
                        var page = await ReadProviderReviewAsync(repo, number, workspace, continuation, token).ConfigureAwait(false);
                        files.AddRange(page.Files);
                        continuation = page.NextPages?.FirstOrDefault(item => item.Kind == PullRequestReviewPageKind.Files);
                    }
                    var refs = ProviderObject(detail, "diff_refs");
                    if (review.Comments.Count > 0 && (ProviderText(refs, "head_sha") != target.HeadCommitId ||
                        ProviderText(refs, "base_sha") != snapshot.BaseCommitId || ProviderText(refs, "start_sha").Length == 0))
                        throw ReviewError("The GitLab diff revision is unavailable or changed. Reload before commenting.");
                    foreach (var comment in review.Comments)
                    {
                        if (comment is null || string.IsNullOrWhiteSpace(comment.Path) || comment.Path.Length > 4096 || string.IsNullOrWhiteSpace(comment.Body) ||
                            comment.Body.Length > PullRequestReviewDefaults.MaximumBodyCharacters || comment.Line <= 0 || !Enum.IsDefined(comment.Side))
                            throw ReviewError("Each inline comment needs a valid file, line, side, and body.");
                        size += comment.Body.Length + comment.Path.Length;
                        if (size > MaximumReviewPayloadCharacters) throw ReviewError("The review payload is too large.");
                        var file = files.FirstOrDefault(file => file.Path == comment.Path);
                        var line = file?.Lines.FirstOrDefault(line => comment.Side == PullRequestDiffSide.Left ? line.OldLine == comment.Line : line.NewLine == comment.Line);
                        if (file is null || file.PatchUnavailable || line is null || line.Kind is PullRequestDiffLineKind.Header or PullRequestDiffLineKind.Metadata)
                            throw ReviewError("The inline comment does not match an available line in this revision.");
                        var position = new Dictionary<string, object>
                        {
                            ["base_sha"] = ProviderText(refs, "base_sha"), ["head_sha"] = target.HeadCommitId,
                            ["start_sha"] = ProviderText(refs, "start_sha"), ["position_type"] = "text",
                            ["old_path"] = file.PreviousPath ?? file.Path, ["new_path"] = file.Path,
                        };
                        // GitLab context positions require coordinates on both sides of the comparison.
                        if (line.OldLine is { } old) position["old_line"] = old;
                        if (line.NewLine is { } current) position["new_line"] = current;
                        GitLabStep("Inline comment", endpoint + "/discussions", "POST", new { body = comment.Body, position });
                    }
                    if (!string.IsNullOrWhiteSpace(review.Body)) GitLabStep("Review summary", endpoint + "/notes", "POST", new { body = review.Body });
                    if (review.Event == PullRequestReviewEvent.Approve) GitLabStep("Approval", endpoint + "/approve", "POST", new { sha = target.HeadCommitId });
                    break;
                }
                case ReplyPullRequestThreadRequest reply:
                {
                    ValidateProviderBody(reply.Body);
                    var discussion = await ReadWritableGitLabDiscussionAsync(repo, workspace, endpoint, reply.ThreadId, snapshot, token).ConfigureAwait(false);
                    if (!discussion.CanReply) throw ReviewError("This discussion does not support replies.");
                    GitLabStep("Discussion reply", endpoint + "/discussions/" + Uri.EscapeDataString(reply.ThreadId) + "/notes", "POST", new { body = reply.Body });
                    break;
                }
                case SetPullRequestThreadResolvedRequest resolve:
                {
                    var discussion = await ReadWritableGitLabDiscussionAsync(repo, workspace, endpoint, resolve.ThreadId, snapshot, token).ConfigureAwait(false);
                    if (!discussion.CanResolve) throw ReviewError("This discussion cannot be resolved by the current account.");
                    if (discussion.IsResolved != resolve.IsResolved) GitLabStep("Discussion resolution", endpoint + "/discussions/" + Uri.EscapeDataString(resolve.ThreadId), "PUT", new { resolved = resolve.IsResolved });
                    break;
                }
                case ManagePullRequestRequest management:
                    await BuildProviderManagementStepsAsync(repo, workspace, number, detail, snapshot, management, steps, GitLabStep, AzureStep, token).ConfigureAwait(false);
                    break;
                default: throw ReviewError("This review operation is not supported.");
            }
            foreach (var step in steps)
            {
                // Recheck repository identity and revision before every component of a multi-request review.
                var currentRepo = await DetectAsync(new(target.Workspace), token).ConfigureAwait(false);
                if (PullRequestReviewDefaults.RepositoryKey(currentRepo) != target.Repository) throw ReviewError("The workspace remote changed during this operation.");
                var current = await ReadProviderDetailAsync(repo, number, workspace, token).ConfigureAwait(false);
                if (ProviderHead(repo, current) != target.HeadCommitId || ProviderBase(repo, current) != snapshot.BaseCommitId)
                    throw ReviewError("The pull request revision changed during this operation. Reload before continuing.");
                if (request is ManagePullRequestRequest manage)
                {
                    if (manage.Action == PullRequestManagementAction.EditDetails && (ProviderText(current, "title") != manage.ExpectedTitle || ProviderText(current, "description") != manage.ExpectedBody))
                        throw ReviewError("The title or description changed before dispatch. Compare with your saved edits.");
                    if (manage.Action == PullRequestManagementAction.SetDraft && ProviderText(current, "title") != snapshot.PullRequest.Title)
                        throw ReviewError("The title changed before changing draft status. Reload the review.");
                    if (manage.Action == PullRequestManagementAction.RemoveReviewer && ProviderObject(current, "reviewers").GetRawText() != ProviderObject(detail, "reviewers").GetRawText())
                        throw ReviewError("The reviewer list changed before dispatch. Reload the review.");
                    if (ProviderText(current, repo.Provider == SourceControlProvider.GitLab ? "state" : "status") != ProviderText(detail, repo.Provider == SourceControlProvider.GitLab ? "state" : "status"))
                        throw ReviewError("The pull request state changed before dispatch. Reload the review.");
                    if (ProviderBool(current, repo.Provider == SourceControlProvider.GitLab ? "draft" : "isDraft") != ProviderBool(detail, repo.Provider == SourceControlProvider.GitLab ? "draft" : "isDraft"))
                        throw ReviewError("The draft status changed before dispatch. Reload the review.");
                    if (repo.Provider == SourceControlProvider.GitLab && manage.Action is PullRequestManagementAction.Merge or PullRequestManagementAction.EnableAutoMerge or PullRequestManagementAction.DisableAutoMerge or PullRequestManagementAction.UpdateBranch &&
                        !ProviderBool(ProviderObject(current, "user"), "can_merge"))
                        throw ReviewError("Merge permission changed before dispatch. Reload the review.");
                }
                dispatched = true;
                var response = await step.Execute(token).ConfigureAwait(false);
                if (response.ValueKind != JsonValueKind.Object && !(step.AllowEmpty && response.ValueKind == JsonValueKind.Undefined))
                    throw ReviewError("The provider did not confirm the review change.");
                if (response.ValueKind == JsonValueKind.Object && (ProviderText(response, "merge_error").Length > 0 || ProviderText(response, "mergeFailureMessage").Length > 0 ||
                    ProviderText(response, "id").Length == 0 && ProviderText(response, "pullRequestId").Length == 0 && !ProviderBool(response, "rebase_in_progress")))
                    throw ReviewError("The provider did not confirm the review change.");
                completed++;
                last = step.Description;
            }
            return new(true, steps.Count == 0 ? "The pull request already has the requested state." : "Pull request updated.", repo, snapshot.PullRequest,
                OperationId: operation, ReviewProgress: new(completed, steps.Count, last));
        }
        catch (Exception exception) when (dispatched || exception is not OperationCanceledException)
        {
            return new(false, dispatched
                    ? $"{completed} of {steps.Count} changes confirmed{(last is null ? "" : "; last: " + last)}. The operation did not finish. Inspect the provider and operation history before composing any remaining changes; do not resend the whole review."
                    : exception is PiStation.Host.Errors.HostOperationException ? exception.Message : "The review could not be prepared. Refresh the provider and retry.",
                repo, snapshot?.PullRequest, OperationId: operation,
                State: dispatched ? CommandReceiptState.DispatchUncertain : CommandReceiptState.Rejected,
                ReviewProgress: new(completed, steps.Count, last));
        }
    }

    private static void ValidateProviderBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > PullRequestReviewDefaults.MaximumBodyCharacters) throw ReviewError("Enter a comment of 1–32768 characters.");
    }

    private async Task<PullRequestDiscussion> ReadWritableGitLabDiscussionAsync(SourceControlRepository repo, string workspace, string endpoint,
        string id, PullRequestReviewSnapshot snapshot, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(character => !char.IsAsciiLetterOrDigit(character))) throw ReviewError("Select a valid discussion in this merge request.");
        var data = await GitLabJsonAsync(repo, workspace, endpoint + "/discussions/" + Uri.EscapeDataString(id), token).ConfigureAwait(false);
        if (ProviderText(data, "id") != id) throw ReviewError("The discussion does not belong to this merge request.");
        return ParseGitLabDiscussion(data, snapshot.ViewerLogin, snapshot.PullRequest.Url, snapshot.HeadCommitId, []);
    }

    private async Task BuildProviderManagementStepsAsync(SourceControlRepository repo, string workspace, int number, JsonElement detail,
        PullRequestReviewSnapshot snapshot, ManagePullRequestRequest request, List<ProviderReviewStep> steps,
        Action<string, string, string, object?, bool> gitlabStep, Action<string, object> azureStep, CancellationToken token)
    {
        var gitlab = repo.Provider == SourceControlProvider.GitLab;
        var endpoint = GitLabRequest(repo, number);
        var state = snapshot.Advanced!;
        switch (request.Action)
        {
            case PullRequestManagementAction.EditDetails:
                if (!snapshot.CanEditDetails || string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 256 || request.Title.Any(char.IsControl) ||
                    request.Body is null || request.Body.Length > PullRequestReviewDefaults.MaximumDescriptionCharacters)
                    throw ReviewError("Enter a valid title and description you have permission to edit.");
                if (request.ExpectedTitle != snapshot.PullRequest.Title || request.ExpectedBody != snapshot.Body) throw ReviewError("The title or description changed elsewhere. Compare your saved edits with the latest details.");
                if (gitlab) gitlabStep("Title and description", endpoint, "PUT", new { title = request.Title, description = request.Body }, false);
                else azureStep("Title and description", new { title = request.Title, description = request.Body });
                break;
            case PullRequestManagementAction.SetDraft:
                if (!snapshot.CanEditDetails || snapshot.PullRequest.State is not (PullRequestState.Open or PullRequestState.Draft) || request.IsDraft is null || request.ExpectedIsDraft != snapshot.PullRequest.IsDraft)
                    throw ReviewError("The draft status changed or cannot be edited. Refresh the pull request.");
                if (gitlab)
                {
                    var title = snapshot.PullRequest.Title;
                    if (request.IsDraft == true && !snapshot.PullRequest.IsDraft) title = "Draft: " + title;
                    else if (request.IsDraft == false) title = System.Text.RegularExpressions.Regex.Replace(title, @"^(?:(?:Draft|WIP):\s*|\[(?:Draft|WIP)\]\s*|\((?:Draft|WIP)\)\s*)+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                    gitlabStep("Draft status", endpoint, "PUT", new { title }, false);
                }
                else azureStep("Draft status", new { isDraft = request.IsDraft.Value });
                break;
            case PullRequestManagementAction.EditComment:
            case PullRequestManagementAction.DeleteComment:
                if (!gitlab || string.IsNullOrWhiteSpace(snapshot.ViewerLogin) || !long.TryParse(request.ItemId, NumberStyles.None, CultureInfo.InvariantCulture, out var noteId) || noteId <= 0)
                    throw ReviewError("Select a supported comment to change.");
                var note = await GitLabJsonAsync(repo, workspace, endpoint + "/notes/" + request.ItemId, token).ConfigureAwait(false);
                if (ProviderText(note, "id") != request.ItemId || ProviderText(ProviderObject(note, "author"), "username") != snapshot.ViewerLogin || ProviderBool(note, "system"))
                    throw ReviewError("Only your own comments on this merge request can be changed.");
                if (request.ExpectedBody != ProviderText(note, "body")) throw ReviewError("The comment changed elsewhere. Compare it with your saved edits.");
                if (request.Action == PullRequestManagementAction.EditComment)
                { ValidateProviderBody(request.Body); gitlabStep("Comment edit", endpoint + "/notes/" + request.ItemId, "PUT", new { body = request.Body }, false); }
                else gitlabStep("Comment deletion", endpoint + "/notes/" + request.ItemId, "DELETE", null, true);
                break;
            case PullRequestManagementAction.RemoveLabel:
                if (!gitlab || !snapshot.CanManageMetadata || !snapshot.PullRequest.Labels.Contains(request.ItemId) || request.ItemId!.Contains(',')) throw ReviewError("Select a label on this merge request.");
                gitlabStep("Label removal", endpoint, "PUT", new { remove_labels = request.ItemId }, false);
                break;
            case PullRequestManagementAction.RemoveReviewer:
                if (!snapshot.CanManageMetadata || !snapshot.PullRequest.Reviewers.Contains(request.ItemId)) throw ReviewError("Select a reviewer on this pull request.");
                if (gitlab)
                {
                    var ids = ProviderArray(ProviderObject(detail, "reviewers")).Where(item => ProviderText(item, "username") != request.ItemId)
                        .Select(item => long.Parse(ProviderText(item, "id"), CultureInfo.InvariantCulture)).ToArray();
                    gitlabStep("Reviewer removal", endpoint, "PUT", new { reviewer_ids = ids }, false);
                }
                else
                {
                    var reviewer = ProviderArray(ProviderObject(detail, "reviewers")).FirstOrDefault(item => ProviderText(item, "id") == request.ItemId || ProviderText(item, "uniqueName") == request.ItemId);
                    var id = ProviderText(reviewer, "id");
                    if (!Guid.TryParse(id, out _)) throw ReviewError("The reviewer identity is unavailable.");
                    var location = AzureReviewLocation(repo);
                    steps.Add(new("Reviewer removal", ct => ProviderJsonAsync("az", ["repos", "pr", "reviewer", "remove", "--id", request.Target.Number,
                        "--reviewers", id, "--organization", location.Organization, "--detect", "false", "--output", "json", "--only-show-errors"], workspace, null, ct), true));
                }
                break;
            case PullRequestManagementAction.Merge:
            case PullRequestManagementAction.EnableAutoMerge:
            case PullRequestManagementAction.DisableAutoMerge:
                var merge = request.Action == PullRequestManagementAction.Merge;
                var enable = request.Action == PullRequestManagementAction.EnableAutoMerge;
                if (!(merge ? state.CanMerge : enable ? state.CanEnableAutoMerge : state.CanDisableAutoMerge)) throw ReviewError("This merge action is unavailable for the current state or account.");
                if ((merge || enable) && (request.MergeMethod is null || !state.MergeMethods.Contains(request.MergeMethod.Value))) throw ReviewError("Choose a merge method allowed by this repository.");
                if (gitlab)
                {
                    if (!merge && !enable) gitlabStep("Disable automatic merge", endpoint + "/cancel_merge_when_pipeline_succeeds", "POST", null, false);
                    else gitlabStep(merge ? "Merge" : "Enable automatic merge", endpoint + "/merge", "PUT",
                        new { sha = request.Target.HeadCommitId, squash = request.MergeMethod == PullRequestMergeMethod.Squash, auto_merge = enable }, false);
                }
                else if (merge) azureStep("Merge", new { status = "completed", lastMergeSourceCommit = new { commitId = request.Target.HeadCommitId },
                    completionOptions = new { mergeStrategy = request.MergeMethod == PullRequestMergeMethod.Squash ? "squash" : "noFastForward", bypassPolicy = false } });
                else
                {
                    var location = AzureReviewLocation(repo);
                    var args = new List<string> { "repos", "pr", "update", "--id", request.Target.Number, "--organization", location.Organization, "--detect", "false",
                        "--auto-complete", enable ? "true" : "false", "--output", "json", "--only-show-errors" };
                    if (enable) args.AddRange(["--squash", request.MergeMethod == PullRequestMergeMethod.Squash ? "true" : "false"]);
                    steps.Add(new(enable ? "Enable automatic completion" : "Disable automatic completion", ct => ProviderJsonAsync("az", args, workspace, null, ct)));
                }
                break;
            case PullRequestManagementAction.UpdateBranch:
                if (!gitlab || !state.CanUpdateBranch || request.UpdateMethod != PullRequestUpdateMethod.Rebase) throw ReviewError("GitLab branch updates support rebase only.");
                gitlabStep("Branch rebase requested", endpoint + "/rebase", "PUT", new { skip_ci = false }, false);
                break;
            case PullRequestManagementAction.SetReaction:
                if (!gitlab || !snapshot.CanReact || request.Reaction is null || request.Reacted is null) throw ReviewError("Reactions are unavailable for this provider or account.");
                var awardsEndpoint = endpoint;
                if (request.ItemId is not null)
                {
                    if (!long.TryParse(request.ItemId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0) throw ReviewError("Select a valid comment.");
                    var subject = await GitLabJsonAsync(repo, workspace, endpoint + "/notes/" + request.ItemId, token).ConfigureAwait(false);
                    if (ProviderText(subject, "id") != request.ItemId || ProviderBool(subject, "system")) throw ReviewError("Select a comment on this merge request.");
                    awardsEndpoint += "/notes/" + request.ItemId;
                }
                var name = GitLabReactionName(request.Reaction.Value);
                var awards = await GitLabJsonAsync(repo, workspace, awardsEndpoint + "/award_emoji?per_page=100", token).ConfigureAwait(false);
                if (awards.ValueKind != JsonValueKind.Array || awards.GetArrayLength() >= 100) throw ReviewError("The reaction list is incomplete. Use the provider website.");
                var award = ProviderArray(awards).FirstOrDefault(item => ProviderText(item, "name") == name && ProviderText(ProviderObject(item, "user"), "username") == snapshot.ViewerLogin);
                if (request.Reacted == true && award.ValueKind == JsonValueKind.Undefined) gitlabStep("Reaction added", awardsEndpoint + "/award_emoji", "POST", new { name }, false);
                else if (request.Reacted == false && award.ValueKind != JsonValueKind.Undefined)
                {
                    var awardId = ProviderText(award, "id");
                    if (!long.TryParse(awardId, NumberStyles.None, CultureInfo.InvariantCulture, out _)) throw ReviewError("The reaction identity is invalid.");
                    gitlabStep("Reaction removed", awardsEndpoint + "/award_emoji/" + awardId, "DELETE", null, true);
                }
                break;
            default: throw ReviewError("This provider does not support the selected advanced action.");
        }
    }
}
