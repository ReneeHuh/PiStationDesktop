using System.Text.Json;
using PiStation.ClientRuntime;
using PiStation.Protocol.Models;

namespace PiStation.HostingVerification;

internal static class LiveReadVerification
{
    public static async Task RunAsync(EnvironmentClient client, WorkspaceTarget workspace, string repository, string number, string root, CancellationToken token)
    {
        var report = new LiveVerificationReport(Path.Combine(root, "verification.json"), repository);
        var review = await client.GetPullRequestReviewAsync(new(workspace, number), token);
        Require(review.PullRequest.Number == number && string.Equals(review.PullRequest.Repository, repository, StringComparison.OrdinalIgnoreCase), "The provider returned another pull request.");
        report.Record("read-pull-request", "passed", $"PR #{number}: {review.Files.Count} initial files, {review.Discussions.Count} discussions, {review.Checks.Count} checks.");
        var target = new PullRequestReviewTarget(workspace, PullRequestReviewDefaults.RepositoryKey(review.Repository), number, review.HeadCommitId);
        await report.CheckAsync("all-review-pages", async () =>
        {
            var queue = new Queue<PullRequestReviewContinuation>(review.NextPages ?? []);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var paths = review.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
            while (queue.TryDequeue(out var continuation))
            {
                Require(seen.Count < 100 && seen.Add(JsonSerializer.Serialize(continuation)), "Review pagination repeated or exceeded 100 continuation pages.");
                var page = await client.GetPullRequestReviewAsync(new(workspace, number, continuation), token);
                Require(page.HeadCommitId == review.HeadCommitId && page.BaseCommitId == review.BaseCommitId, "The review revision changed during verification.");
                foreach (var file in page.Files) paths.Add(file.Path); // A long patch may legitimately continue on another page.
                foreach (var next in page.NextPages ?? []) queue.Enqueue(next);
            }
            report.Record("review-page-count", "observed", $"{seen.Count} continuation pages; {paths.Count} unique file paths.");
        });
        await report.CheckAsync("pull-request-list", async () =>
        {
            var first = await client.ListPullRequestsAsync(new(workspace), token);
            Require(first.PullRequests.All(pr => string.Equals(pr.Repository, repository, StringComparison.OrdinalIgnoreCase)), "A list result escaped the selected repository.");
            if (first.NextOffset is not { } offset)
            {
                Require(first.PullRequests.Any(pr => pr.Number == number), "The all-state list omitted the selected PR.");
                report.Record("list-second-page", "not-exercised", "This repository returned only one page.");
                return;
            }
            var next = await client.ListPullRequestsAsync(new(workspace, Offset: offset), token);
            Require(next.PullRequests.Count > 0 && !first.PullRequests.Select(pr => pr.Number).Intersect(next.PullRequests.Select(pr => pr.Number), StringComparer.Ordinal).Any(), "The next list page was empty or repeated an earlier PR.");
            report.Record("list-second-page", "passed", $"{first.PullRequests.Count} first-page and {next.PullRequests.Count} second-page PRs without duplicates.");
        });
        await report.CheckAsync("author-draft-and-viewer-filters", async () =>
        {
            var state = review.PullRequest.State == PullRequestState.Draft ? PullRequestState.Open : review.PullRequest.State;
            var filtered = await client.ListPullRequestsAsync(new(workspace, state, SourceBranch: review.PullRequest.SourceBranch,
                Filters: new(Author: review.PullRequest.Author, Draft: review.PullRequest.IsDraft ? PullRequestDraftFilter.Only : PullRequestDraftFilter.Hide)), token);
            Require(filtered.PullRequests.Any(pr => pr.Number == number), "The selected PR was absent from its author, branch, state, and draft filters.");
            var authored = await client.ListPullRequestsAsync(new(workspace, Filters: new(Involvement: PullRequestInvolvement.Authored)), token);
            Require(authored.PullRequests.All(pr => string.Equals(pr.Author, review.ViewerLogin, StringComparison.OrdinalIgnoreCase)), "The authored filter returned a different author.");
        });
        if (review.Advanced?.CanApproveWorkflows == true)
            await report.CheckAsync("workflow-pages", async () =>
            {
                var page = 1;
                do
                {
                    var workflows = await client.GetPullRequestWorkflowsAsync(new(target, page), token);
                    Require(workflows.Target == target && (workflows.NextPage is null || workflows.NextPage > page), "Workflow pagination did not advance or changed target.");
                    page = workflows.NextPage ?? 0;
                } while (page != 0);
            });
        else report.Record("workflow-pages", "not-exercised", "This PR/account does not expose workflow approval.");
        report.Record("read-only-completed", "passed", "No GitHub mutations were sent by this mode.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
