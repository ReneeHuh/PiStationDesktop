using System.Globalization;
using System.Text.Json;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.Host.SourceControl;

public sealed partial class SourceControlHostingService
{
    private async Task<SourceControlOperationResult> WriteBitbucketAsync(PullRequestReviewTarget target, CommandId? operation, object request, CancellationToken token)
    {
        SourceControlRepository? repo = null;
        var steps = new List<ProviderReviewStep>(); var dispatched = false; var completed = 0; string? last = null;
        try
        {
            repo = await DetectAsync(new(target.Workspace), token).ConfigureAwait(false);
            if (repo.Provider != SourceControlProvider.Bitbucket || !repo.CanWrite || target.Repository != PullRequestReviewDefaults.RepositoryKey(repo))
                throw ReviewError("The Bitbucket repository changed or host credentials are unavailable.");
            var number = ValidateNumber(target.Number); var path = BbPrPath(repo, number);
            var snapshot = await ReadBitbucketReviewAsync(repo, number, null, token).ConfigureAwait(false);
            if (snapshot.HeadCommitId != target.HeadCommitId) throw ReviewError("The inspected head changed. Reload before writing.");
            var original = await BbDetailAsync(repo, number, token).ConfigureAwait(false);
            void Step(string description, string endpoint, string method, object? payload = null, bool empty = false) =>
                steps.Add(new(description, ct => _bitbucket.JsonAsync(endpoint, method, payload, ct), empty));
            switch (request)
            {
                case SubmitPullRequestReviewRequest review:
                {
                    if (review.Comments is null || review.Body is null || !Enum.IsDefined(review.Event) || review.Comments.Count > 50 ||
                        review.Body.Length > PullRequestReviewDefaults.MaximumBodyCharacters || review.Event != PullRequestReviewEvent.Approve && string.IsNullOrWhiteSpace(review.Body))
                        throw ReviewError("Provide a supported review verdict and bounded body/comments.");
                    var files = snapshot.Files.ToList(); var page = snapshot.NextPages?.FirstOrDefault(item => item.Kind == PullRequestReviewPageKind.Files);
                    while (page is not null && review.Comments.Any(comment => comment is not null && !files.Any(file => file.Path == comment.Path)))
                    {
                        var more = await ReadBitbucketReviewAsync(repo, number, page, token).ConfigureAwait(false);
                        files.AddRange(more.Files); page = more.NextPages?.FirstOrDefault(item => item.Kind == PullRequestReviewPageKind.Files);
                    }
                    long size = review.Body.Length;
                    foreach (var comment in review.Comments)
                    {
                        if (comment is null || string.IsNullOrWhiteSpace(comment.Path) || comment.Path.Length > 4096 || comment.Line <= 0 || !Enum.IsDefined(comment.Side))
                            throw ReviewError("Each inline comment requires a valid file, side and line.");
                        ValidateProviderBody(comment.Body);
                        size += comment.Path.Length + comment.Body.Length;
                        if (size > MaximumReviewPayloadCharacters) throw ReviewError("The review payload is too large.");
                        var file = files.FirstOrDefault(file => file.Path == comment.Path);
                        if (file is null || file.PatchUnavailable || !file.Lines.Any(line => line.Kind is PullRequestDiffLineKind.Context or PullRequestDiffLineKind.Addition or PullRequestDiffLineKind.Deletion &&
                            (comment.Side == PullRequestDiffSide.Left ? line.OldLine : line.NewLine) == comment.Line))
                            throw ReviewError("The inline comment does not match an available line in the inspected diff.");
                        var inline = new Dictionary<string, object> { ["path"] = comment.Side == PullRequestDiffSide.Left ? file.PreviousPath ?? file.Path : file.Path,
                            [comment.Side == PullRequestDiffSide.Left ? "from" : "to"] = comment.Line };
                        Step("Inline comment", path + "/comments", "POST", new { content = new { raw = comment.Body }, inline });
                    }
                    if (!string.IsNullOrWhiteSpace(review.Body)) Step("Review summary", path + "/comments", "POST", new { content = new { raw = review.Body } });
                    if (review.Event != PullRequestReviewEvent.Comment)
                        Step(review.Event == PullRequestReviewEvent.Approve ? "Approval" : "Requested changes", path +
                            (review.Event == PullRequestReviewEvent.Approve ? "/approve" : "/request-changes"), "POST");
                    break;
                }
                case ReplyPullRequestThreadRequest reply:
                    ValidateProviderBody(reply.Body);
                    await ReadBitbucketCommentAsync(path, reply.ThreadId, token).ConfigureAwait(false);
                    Step("Reply", path + "/comments", "POST", new { content = new { raw = reply.Body }, parent = new { id = ValidateNumber(reply.ThreadId) } });
                    break;
                case SetPullRequestThreadResolvedRequest resolve:
                {
                    var comment = await ReadBitbucketCommentAsync(path, resolve.ThreadId, token).ConfigureAwait(false);
                    if (ProviderObject(comment, "parent").ValueKind == JsonValueKind.Object) throw ReviewError("Resolve the root discussion rather than a reply.");
                    if ((ProviderObject(comment, "resolution").ValueKind == JsonValueKind.Object) != resolve.IsResolved)
                        Step(resolve.IsResolved ? "Resolve discussion" : "Reopen discussion", path + "/comments/" + resolve.ThreadId + "/resolve", resolve.IsResolved ? "POST" : "DELETE", empty: true);
                    break;
                }
                case ManagePullRequestRequest manage:
                    await ValidateBitbucketManagementAsync(path, original, snapshot.ViewerLogin, manage, token).ConfigureAwait(false);
                    switch (manage.Action)
                    {
                        case PullRequestManagementAction.EditDetails:
                            Step("PR details", path, "PUT", new { title = manage.Title, description = manage.Body }); break;
                        case PullRequestManagementAction.EditComment:
                            Step("Comment edit", path + "/comments/" + manage.ItemId, "PUT", new { content = new { raw = manage.Body } }); break;
                        case PullRequestManagementAction.DeleteComment:
                            Step("Comment deletion", path + "/comments/" + manage.ItemId, "DELETE", empty: true); break;
                        case PullRequestManagementAction.RemoveReviewer:
                            Step("Reviewer removal", path, "PUT", new { reviewers = ProviderArray(ProviderObject(original, "reviewers"))
                                .Where(item => BbIdentity(item) != manage.ItemId).Select(item => new { uuid = BbIdentity(item) }).ToArray() }); break;
                        case PullRequestManagementAction.Merge:
                            Step("Merge submitted", path + "/merge", "POST", new { merge_strategy = BbMergeStrategy(manage.MergeMethod), close_source_branch = false }); break;
                        default: throw ReviewError("This advanced action is unavailable for Bitbucket.");
                    }
                    break;
                case MutatePullRequestRequest basic:
                    switch (basic.Mutation)
                    {
                        case PullRequestMutationKind.Comment:
                            ValidateProviderBody(basic.Value!); Step("Comment", path + "/comments", "POST", new { content = new { raw = basic.Value } }); break;
                        case PullRequestMutationKind.Approve: Step("Approval", path + "/approve", "POST"); break;
                        case PullRequestMutationKind.RequestChanges:
                            ValidateProviderBody(basic.Value!); Step("Review summary", path + "/comments", "POST", new { content = new { raw = basic.Value } });
                            Step("Requested changes", path + "/request-changes", "POST"); break;
                        case PullRequestMutationKind.Merge:
                            Step("Merge submitted", path + "/merge", "POST", new { merge_strategy = "merge_commit", close_source_branch = false }); break;
                        case PullRequestMutationKind.Close: Step("Decline", path + "/decline", "POST"); break;
                        case PullRequestMutationKind.AddReviewer:
                            if (!Guid.TryParse(basic.Value?.Trim('{', '}'), out _)) throw ReviewError("Enter the reviewer's Bitbucket UUID, including braces.");
                            var uuid = "{" + basic.Value!.Trim('{', '}') + "}";
                            var reviewers = ProviderArray(ProviderObject(original, "reviewers")).Select(BbIdentity).Append(uuid).Distinct(StringComparer.Ordinal).Select(id => new { uuid = id }).ToArray();
                            Step("Reviewer request", path, "PUT", new { reviewers }); break;
                        default: throw ReviewError("This action is unavailable for Bitbucket.");
                    }
                    break;
                default: throw ReviewError("Unsupported Bitbucket write.");
            }
            foreach (var step in steps)
            {
                var currentRepo = await DetectAsync(new(target.Workspace), token).ConfigureAwait(false);
                if (PullRequestReviewDefaults.RepositoryKey(currentRepo) != target.Repository || currentRepo.Provider != repo.Provider || !currentRepo.CanWrite)
                    throw ReviewError("The originating repository or credentials changed.");
                var current = await BbDetailAsync(repo, number, token).ConfigureAwait(false);
                if (BbRevision(current, "source") != target.HeadCommitId || BbRevision(current, "destination") != snapshot.BaseCommitId)
                    throw ReviewError("The Bitbucket head or base changed. Inspect the current review before continuing.");
                if (snapshot.ViewerLogin.Length > 0 && BbIdentity(await _bitbucket.JsonAsync("user", token: token).ConfigureAwait(false)) != snapshot.ViewerLogin)
                    throw ReviewError("The Bitbucket account changed. Reload the review.");
                if (request is ManagePullRequestRequest management) await ValidateBitbucketManagementAsync(path, current, snapshot.ViewerLogin, management, token).ConfigureAwait(false);
                if (request is ManagePullRequestRequest { Action: PullRequestManagementAction.RemoveReviewer } &&
                    !ProviderArray(ProviderObject(original, "reviewers")).Select(BbIdentity).Order().SequenceEqual(ProviderArray(ProviderObject(current, "reviewers")).Select(BbIdentity).Order()))
                    throw ReviewError("The reviewer set changed. Reload before editing reviewers.");
                if (request is MutatePullRequestRequest { Mutation: PullRequestMutationKind.AddReviewer } &&
                    !ProviderArray(ProviderObject(original, "reviewers")).Select(BbIdentity).Order().SequenceEqual(ProviderArray(ProviderObject(current, "reviewers")).Select(BbIdentity).Order()))
                    throw ReviewError("The reviewer set changed. Reload before editing reviewers.");
                if (request is ManagePullRequestRequest { Action: PullRequestManagementAction.Merge } || request is MutatePullRequestRequest { Mutation: PullRequestMutationKind.Merge or PullRequestMutationKind.Close })
                    if (ProviderText(current, "state") != "OPEN") throw ReviewError("This pull request is no longer open.");
                token.ThrowIfCancellationRequested(); dispatched = true;
                var response = await step.Execute(token).ConfigureAwait(false);
                if (!step.AllowEmpty && (response.ValueKind != JsonValueKind.Object ||
                    !(ProviderText(response, "id").Length > 0 || ProviderText(response, "state").Length > 0 ||
                      ProviderObject(response, "user").ValueKind == JsonValueKind.Object || ProviderText(response, "task_id").Length > 0 || ProviderText(response, "type") == "task")))
                    throw ReviewError("Bitbucket did not acknowledge the write.");
                completed++; last = step.Description;
            }
            return new(true, steps.Any(step => step.Description == "Merge submitted") ? "Bitbucket accepted the merge request. Refresh to verify the final merge result." : "Bitbucket operation completed.",
                repo, OperationId: operation, ReviewProgress: new(completed, steps.Count, last));
        }
        catch (Exception exception)
        {
            var reason = exception is BitbucketResponseException or PiStation.Host.Errors.HostOperationException ? exception.Message : "The Bitbucket operation could not be confirmed.";
            return new(false, reason + (dispatched ? $" {completed} of {steps.Count} steps were confirmed. Inspect Bitbucket and compose only the remaining work; this operation is not automatically retried." : " No write was dispatched."),
                repo, OperationId: operation, State: dispatched ? CommandReceiptState.DispatchUncertain : CommandReceiptState.Rejected, ReviewProgress: new(completed, steps.Count, last));
        }
    }
    private static string BbMergeStrategy(PullRequestMergeMethod? method) => method switch
    {
        PullRequestMergeMethod.Merge => "merge_commit", PullRequestMergeMethod.Squash => "squash", PullRequestMergeMethod.Rebase => "rebase_fast_forward",
        _ => throw ReviewError("Select a supported merge method.")
    };
    private async Task<JsonElement> ReadBitbucketCommentAsync(string path, string id, CancellationToken token)
    {
        _ = ValidateNumber(id);
        var comment = await _bitbucket.JsonAsync(path + "/comments/" + id, token: token).ConfigureAwait(false);
        if (ProviderText(comment, "id") != id || ProviderBool(comment, "deleted")) throw ReviewError("The comment is unavailable in this pull request.");
        return comment;
    }
    private async Task ValidateBitbucketManagementAsync(string path, JsonElement detail, string viewer, ManagePullRequestRequest request, CancellationToken token)
    {
        switch (request.Action)
        {
            case PullRequestManagementAction.EditDetails:
                if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 256 || request.Body is null || request.Body.Length > PullRequestReviewDefaults.MaximumDescriptionCharacters ||
                    request.ExpectedTitle != ProviderText(detail, "title") || request.ExpectedBody != ProviderText(detail, "description"))
                    throw ReviewError("The title/description changed or the edits are invalid. Reload before saving.");
                break;
            case PullRequestManagementAction.EditComment:
            case PullRequestManagementAction.DeleteComment:
                var comment = await ReadBitbucketCommentAsync(path, request.ItemId ?? "", token).ConfigureAwait(false);
                if (viewer.Length == 0 || viewer != BbIdentity(ProviderObject(comment, "user")) || request.ExpectedBody != ProviderText(ProviderObject(comment, "content"), "raw"))
                    throw ReviewError("Only your own unchanged comment can be edited or deleted.");
                if (request.Action == PullRequestManagementAction.EditComment) ValidateProviderBody(request.Body!);
                break;
            case PullRequestManagementAction.RemoveReviewer:
                if (string.IsNullOrWhiteSpace(request.ItemId) || !ProviderArray(ProviderObject(detail, "reviewers")).Any(item => BbIdentity(item) == request.ItemId))
                    throw ReviewError("The selected reviewer is no longer requested.");
                break;
            case PullRequestManagementAction.Merge: _ = BbMergeStrategy(request.MergeMethod); break;
            default: throw ReviewError("This advanced action is unavailable for Bitbucket.");
        }
    }
}
