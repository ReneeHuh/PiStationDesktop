using System.Globalization;
using System.Text;
using System.Text.Json;
using PiStation.ClientRuntime;
using PiStation.Host;
using PiStation.Host.Hosting;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.HostingVerification;

internal static class LiveWriteVerification
{
    private static readonly bool[] ToggleStates = [true, false];
    private static readonly string[] DeletedBodies = ["Edited Generated general comment", "Edited Generated inline comment", "Generated reply"];
    private static readonly string[] RequiredContexts = ["pistation-verification"];
    public static async Task RunAsync(string directory, string repository, bool resume = false)
    {
        LiveGitHub.ValidateNewRepository(repository);
        var root = Path.GetFullPath(directory);
        if (!resume && Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new ArgumentException("Use a new empty verification directory.");
        var projectPath = Path.Combine(root, "project");
        Directory.CreateDirectory(projectPath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        var token = timeout.Token;
        var github = new LiveGitHub(projectPath, token);
        var report = new LiveVerificationReport(Path.Combine(root, "verification.json"), repository, resume);
        var viewer = (await github.ApiAsync("GET", "user")).GetProperty("login").GetString()!;
        if (!string.Equals(repository.Split('/')[0], viewer, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The verification repository must belong to the signed-in user.");
        // Creation fails when the name already exists. All subsequent writes affect only this new private repository.
        if (!resume) await github.CliAsync("repo", "create", repository, "--private", "--description", LiveGitHub.Description);
        var repo = await github.ApiAsync("GET", "repos/" + repository);
        Require(repo.GetProperty("private").GetBoolean() && repo.GetProperty("owner").GetProperty("login").GetString() == viewer && repo.GetProperty("description").GetString() == LiveGitHub.Description,
            "GitHub did not confirm a private repository owned by the signed-in user.");
        if (!report.WasPassed("isolated-repository")) report.Record("isolated-repository", "passed", repo.GetProperty("html_url").GetString()!);
        var endpoint = "repos/" + repository;
        if (!resume)
        {
            await github.GitAsync("init", "--quiet", "--initial-branch=main");
            await github.GitAsync("remote", "add", "origin", "https://github.com/" + repository + ".git");
            await PutFile("fixture.txt", "base line\nsecond line\n", "main");
            var baseSha = await BranchSha("main");
            var baseCommit = await github.ApiAsync("GET", endpoint + "/git/commits/" + baseSha);
            var files = Enumerable.Range(1, 101).Select(index => new { path = $"fixture/{index:D3}.txt", mode = "100644", type = "blob", content = $"Generated test file {index}.\n" }).ToList();
            files.Add(new { path = "fixture.txt", mode = "100644", type = "blob", content = "base line\nupdated line\n" });
            var tree = await github.ApiAsync("POST", endpoint + "/git/trees", new { base_tree = baseCommit.GetProperty("tree").GetProperty("sha").GetString(), tree = files });
            var commit = await github.ApiAsync("POST", endpoint + "/git/commits", new { message = "Generated verification changes", tree = tree.GetProperty("sha").GetString(), parents = new[] { baseSha } });
            var head = commit.GetProperty("sha").GetString()!;
            await github.ApiAsync("POST", endpoint + "/git/refs", new { @ref = "refs/heads/verification-changes", sha = head });
            await PutFile("base-progress.txt", "Base progressed before the pull request.\n", "main");
            await SetStatus(head, "success");
        }
        else Require(await github.GitAsync("remote", "get-url", "origin") == "https://github.com/" + repository + ".git", "The saved workspace remote changed.");

        await using var host = await EmbeddedEnvironmentHost.StartAsync(new HostOptions { ApplicationDataRoot = Path.Combine(root, "data"), EnvironmentName = "Live GitHub verification" }, cancellationToken: token);
        await using var client = new EnvironmentClient(new() { HubAddress = host.HubAddress, BearerCredential = host.BearerCredential });
        await client.ConnectAsync(token);
        var project = resume ? (await client.ListProjectsAsync(token)).Single() : await client.AddProjectAsync(new(projectPath, "Generated live GitHub verification"), token);
        Require(string.Equals(project.CanonicalPath, projectPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "The saved project points to another workspace.");
        if (resume) Require((await client.ListHostingOperationsAsync(token)).All(operation => operation.State is CommandReceiptState.Completed or CommandReceiptState.Rejected),
            "A previous write has an unresolved receipt. Inspect it before continuing; writes are never automatically retried.");
        var workspace = new WorkspaceTarget(project.ProjectId);
        if (!resume)
        {
            var created = await client.CreatePullRequestAsync(new(workspace, "Generated verification pull request", "Generated test content for Pi Station.",
                "verification-changes", "main", OperationId: CommandId.New()), token);
            Require(created.Succeeded, created.Message);
        }
        var prs = await github.ApiAsync("GET", endpoint + "/pulls?head=" + viewer + ":verification-changes&state=all");
        var number = prs[0].GetProperty("number").GetInt32().ToString(CultureInfo.InvariantCulture);
        if (!report.WasPassed("create-pull-request")) report.Record("create-pull-request", "passed", $"https://github.com/{repository}/pull/{number}");
        PullRequestReviewTarget Target(PullRequestReviewSnapshot value) => new(workspace, "github.com/" + repository, number, value.HeadCommitId);
        Task<PullRequestReviewSnapshot> Read() => client.GetPullRequestReviewAsync(new(workspace, number), token);
        var snapshot = await Read();

        await report.CheckAsync("read-files-checks-and-pagination", async () =>
        {
            var paths = snapshot.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
            var pending = new Queue<PullRequestReviewContinuation>(snapshot.NextPages ?? []);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (pending.TryDequeue(out var page))
            {
                Require(seen.Add(JsonSerializer.Serialize(page)), "A continuation repeated.");
                var next = await client.GetPullRequestReviewAsync(new(workspace, number, page), token);
                foreach (var file in next.Files) Require(paths.Add(file.Path), "A file appeared twice across pages.");
                foreach (var continuation in next.NextPages ?? []) pending.Enqueue(continuation);
            }
            Require(paths.Count == 102 && seen.Count > 0, "Expected all 102 changed files across multiple pages.");
            Require(snapshot.Checks.Any(check => check.Name == "pistation-verification"), "The real commit status was not displayed.");
        });
        await report.CheckAsync("edit-details-and-replay", async () =>
        {
            var current = await Read();
            await Manage(new(Target(current), PullRequestManagementAction.EditDetails, Title: "Verified title", Body: "Verified description",
                ExpectedTitle: current.PullRequest.Title, ExpectedBody: current.Body));
            await Eventually(value => value.PullRequest.Title == "Verified title" && value.Body == "Verified description");
        });
        foreach (var draft in ToggleStates)
            await report.CheckAsync(draft ? "convert-to-draft" : "mark-ready", async () =>
            {
                var current = await Read();
                await Manage(new(Target(current), PullRequestManagementAction.SetDraft, IsDraft: draft, ExpectedIsDraft: current.PullRequest.IsDraft));
                await Eventually(value => value.PullRequest.IsDraft == draft);
            });

        await report.CheckAsync("label-add-filter-remove", async () =>
        {
            await github.ApiAsync("POST", endpoint + "/labels", new { name = "verification/ui", color = "336699" });
            RequireSuccess(await client.MutatePullRequestAsync(new(workspace, number, PullRequestMutationKind.AddLabel, "verification/ui", CommandId.New()), token));
            await Eventually(value => value.PullRequest.Labels.Contains("verification/ui"));
            var filtered = await client.ListPullRequestsAsync(new(workspace, PullRequestState.Open,
                Filters: new(Involvement: PullRequestInvolvement.Authored, Author: "@me", LabelGroups: [["verification/ui"]])), token);
            Require(filtered.PullRequests.Any(pr => pr.Number == number), "The authored and label filter did not return the fixture.");
            await Manage(new(Target(await Read()), PullRequestManagementAction.RemoveLabel, ItemId: "verification/ui"));
            await Eventually(value => !value.PullRequest.Labels.Contains("verification/ui"));
        });
        await report.CheckAsync("general-inline-and-review-comments", async () =>
        {
            RequireSuccess(await client.MutatePullRequestAsync(new(workspace, number, PullRequestMutationKind.Comment, "Generated general comment", CommandId.New()), token));
            RequireSuccess(await client.SubmitPullRequestReviewAsync(new(Target(await Read()), PullRequestReviewEvent.Comment, "Generated review",
                [new("fixture.txt", 2, PullRequestDiffSide.Right, "Generated inline comment")], CommandId.New()), token));
            await Eventually(value => value.Discussions.SelectMany(thread => thread.Comments).Any(comment => comment.Body == "Generated inline comment"));
        });
        foreach (var (kind, original) in new[] { (PullRequestCommentKind.General, "Generated general comment"), (PullRequestCommentKind.Inline, "Generated inline comment"), (PullRequestCommentKind.Review, "Generated review") })
        {
            await report.CheckAsync("edit-and-react-" + kind, async () =>
            {
                var current = await Read();
                var comment = current.Discussions.SelectMany(thread => thread.Comments).Single(comment => comment.Kind == kind && comment.Body == original);
                await Manage(new(Target(current), PullRequestManagementAction.EditComment, ItemId: comment.Id, Body: "Edited " + original, ExpectedBody: original));
                await Eventually(value => value.Discussions.SelectMany(thread => thread.Comments).Any(item => item.Id == comment.Id && item.Body == "Edited " + original));
                foreach (var reacted in ToggleStates)
                {
                    await Manage(new(Target(await Read()), PullRequestManagementAction.SetReaction, ItemId: comment.Id, Reaction: PullRequestReactionContent.Eyes, Reacted: reacted));
                    await Eventually(value => HasReaction(value.Discussions.SelectMany(thread => thread.Comments).Single(item => item.Id == comment.Id).Reactions, PullRequestReactionContent.Eyes) == reacted);
                }
            });
        }
        await report.CheckAsync("reply-resolve-reopen-thread", async () =>
        {
            var current = await Read();
            var thread = current.Discussions.Single(thread => thread.Path == "fixture.txt");
            RequireSuccess(await client.ReplyPullRequestThreadAsync(new(Target(current), thread.Id, "Generated reply", CommandId.New()), token));
            await Eventually(value => value.Discussions.Single(item => item.Id == thread.Id).Comments.Any(comment => comment.Body == "Generated reply"));
            foreach (var resolved in ToggleStates)
            {
                RequireSuccess(await client.SetPullRequestThreadResolvedAsync(new(Target(await Read()), thread.Id, resolved, CommandId.New()), token));
                await Eventually(value => value.Discussions.Single(item => item.Id == thread.Id).IsResolved == resolved);
            }
        });
        await report.CheckAsync("all-pull-request-reactions", async () =>
        {
            foreach (var reaction in Enum.GetValues<PullRequestReactionContent>())
                foreach (var reacted in ToggleStates)
                {
                    await Manage(new(Target(await Read()), PullRequestManagementAction.SetReaction, Reaction: reaction, Reacted: reacted));
                    await Eventually(value => HasReaction(value.Reactions, reaction) == reacted);
                }
        });
        await report.CheckAsync("delete-own-comments", async () =>
        {
            foreach (var original in DeletedBodies)
            {
                var current = await Read();
                var comment = current.Discussions.SelectMany(thread => thread.Comments).Single(comment => comment.Body == original);
                await Manage(new(Target(current), PullRequestManagementAction.DeleteComment, ItemId: comment.Id, ExpectedBody: comment.Body));
                await Eventually(value => value.Discussions.SelectMany(thread => thread.Comments).All(item => item.Id != comment.Id));
            }
        });
        await github.ApiAsync("PATCH", endpoint, new { allow_update_branch = true });
        foreach (var method in Enum.GetValues<PullRequestUpdateMethod>())
            await report.CheckAsync("update-branch-" + method, async () =>
            {
                if (method == PullRequestUpdateMethod.Rebase) await PutFile("base-rebase.txt", "Base progressed again.\n", "main");
                var current = await Read();
                await Manage(new(Target(current), PullRequestManagementAction.UpdateBranch, UpdateMethod: method));
                await Eventually(value => value.HeadCommitId != current.HeadCommitId);
                var stale = await client.ManagePullRequestAsync(new(Target(current), PullRequestManagementAction.SetReaction,
                    Reaction: PullRequestReactionContent.Heart, Reacted: true, OperationId: CommandId.New()), token);
                Require(!stale.Succeeded && stale.State == CommandReceiptState.Rejected, "A stale-head write was not rejected.");
            });

        var protectedBranch = false;
        try
        {
            await github.ApiAsync("PATCH", endpoint, new { allow_auto_merge = true });
            await github.ApiAsync("PUT", endpoint + "/branches/main/protection", new { required_status_checks = new { strict = false, contexts = RequiredContexts },
                enforce_admins = true, required_pull_request_reviews = (object?)null, restrictions = (object?)null });
            protectedBranch = true;
        }
        catch (InvalidOperationException exception) { report.Record("auto-merge-prerequisite", "blocked", exception.Message); }
        if (protectedBranch)
            await report.CheckAsync("enable-disable-auto-merge", async () =>
            {
                await SetStatus((await Read()).HeadCommitId, "pending");
                var current = await Eventually(value => value.Advanced?.CanEnableAutoMerge == true);
                await Manage(new(Target(current), PullRequestManagementAction.EnableAutoMerge, MergeMethod: PullRequestMergeMethod.Squash));
                await Eventually(value => value.Advanced?.AutoMergeEnabled == true);
                await Manage(new(Target(await Read()), PullRequestManagementAction.DisableAutoMerge));
                await Eventually(value => value.Advanced?.AutoMergeEnabled == false);
            });
        report.Record("remove-requested-reviewer", "blocked", "Requires a second consenting account with a pending review request; no other users were contacted.");
        report.Record("approve-fork-workflow", "blocked", "Requires an actual fork workflow awaiting approval; generated fixtures do not execute Actions.");

        await report.CheckAsync("squash-merge-and-draft-revert", async () =>
        {
            await SetStatus((await Read()).HeadCommitId, "success");
            var current = await Eventually(value => value.Advanced?.CanMerge == true && value.PullRequest.Checks == PullRequestCheckState.Passed);
            await Manage(new(Target(current), PullRequestManagementAction.Merge, MergeMethod: PullRequestMergeMethod.Squash));
            await Eventually(value => value.PullRequest.State == PullRequestState.Merged);
            var result = await Manage(new(Target(await Read()), PullRequestManagementAction.Revert));
            var reverts = await github.ApiAsync("GET", endpoint + "/pulls?state=open");
            Require(reverts.EnumerateArray().Any(pr => pr.GetProperty("draft").GetBoolean() && result.Message.Contains(pr.GetProperty("html_url").GetString()!, StringComparison.Ordinal)),
                "GitHub did not expose the created draft revert pull request.");
        });
        foreach (var method in new[] { PullRequestMergeMethod.Merge, PullRequestMergeMethod.Rebase })
            await report.CheckAsync("merge-method-" + method, async () =>
            {
                var branch = "verification-" + method.ToString().ToLowerInvariant();
                await github.ApiAsync("POST", endpoint + "/git/refs", new { @ref = "refs/heads/" + branch, sha = await BranchSha("main") });
                await PutFile(branch + ".txt", "Generated merge-method fixture.\n", branch);
                await SetStatus(await BranchSha(branch), "success");
                RequireSuccess(await client.CreatePullRequestAsync(new(workspace, "Verify " + method, "Generated merge-method fixture.", branch, "main", OperationId: CommandId.New()), token));
                var matches = await github.ApiAsync("GET", endpoint + "/pulls?head=" + viewer + ":" + branch + "&state=open");
                number = matches[0].GetProperty("number").GetInt32().ToString(CultureInfo.InvariantCulture);
                var current = await Eventually(value => value.Advanced?.CanMerge == true && value.PullRequest.Checks == PullRequestCheckState.Passed);
                await Manage(new(Target(current), PullRequestManagementAction.Merge, MergeMethod: method));
                await Eventually(value => value.PullRequest.State == PullRequestState.Merged);
            });
        report.Record("native-interactive-verification", "blocked", "Run the populated native fixture with an accessible Windows desktop; live API checks do not verify rendering.");

        async Task<SourceControlOperationResult> Manage(ManagePullRequestRequest request)
        {
            request = request with { OperationId = CommandId.New() };
            var result = await client.ManagePullRequestAsync(request, token);
            RequireSuccess(result);
            var replay = await client.ManagePullRequestAsync(request, token);
            Require(JsonSerializer.Serialize(result) == JsonSerializer.Serialize(replay), "Receipt replay changed the result.");
            return result;
        }
        async Task<PullRequestReviewSnapshot> Eventually(Func<PullRequestReviewSnapshot, bool> predicate)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var current = await Read();
                if (predicate(current)) return current;
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
            throw new InvalidDataException("The expected GitHub state did not appear after ten reads. No write was retried.");
        }
        async Task PutFile(string path, string content, string branch) => _ = await github.ApiAsync("PUT", endpoint + "/contents/" + path,
            new { message = "Generated verification fixture", content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)), branch });
        async Task<string> BranchSha(string branch) => (await github.ApiAsync("GET", endpoint + "/git/ref/heads/" + branch)).GetProperty("object").GetProperty("sha").GetString()!;
        async Task SetStatus(string sha, string state) => _ = await github.ApiAsync("POST", endpoint + "/statuses/" + sha,
            new { state, context = "pistation-verification", description = "Generated verification status; no Actions executed." });
    }

    private static bool HasReaction(IReadOnlyList<PullRequestReaction>? reactions, PullRequestReactionContent content) => reactions?.Any(reaction => reaction.Content == content && reaction.ViewerHasReacted) == true;
    private static void RequireSuccess(SourceControlOperationResult result) => Require(result.Succeeded, result.Message);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
