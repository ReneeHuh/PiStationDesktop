using System.Globalization;
using System.Text.Json;
using PiStation.Protocol.Models;

namespace PiStation.Host.SourceControl;

public sealed partial class SourceControlHostingService
{
    private const int MaximumProviderReviewPages = 30;
    private static string ProviderText(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object ? Text(value, name) ?? "" : "";
    private static JsonElement ProviderObject(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) ? item : default;
    private static bool ProviderBool(JsonElement value, string name) => ProviderObject(value, name).ValueKind == JsonValueKind.True;
    private static JsonElement[] ProviderArray(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
    private static DateTimeOffset ProviderDate(JsonElement value, string name) => DateTimeOffset.TryParse(ProviderText(value, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : DateTimeOffset.UnixEpoch;
    private static string GitLabProject(SourceControlRepository repo) => "projects/" + Uri.EscapeDataString(repo.Owner + "/" + repo.Name);
    private static string GitLabRequest(SourceControlRepository repo, int number) => GitLabProject(repo) + "/merge_requests/" + number.ToString(CultureInfo.InvariantCulture);

    private async Task<bool> IsAdditionalReviewProviderAsync(WorkspaceTarget workspace, CancellationToken token) =>
        (await DetectAsync(new(workspace), token).ConfigureAwait(false)).Provider is SourceControlProvider.GitLab or SourceControlProvider.AzureDevOps or SourceControlProvider.Bitbucket;

    private async Task<JsonElement> ProviderJsonAsync(string tool, IReadOnlyList<string> args, string workspace, string? input, CancellationToken token)
    {
        var result = _reviewCommandExecutor is null
            ? await RunAsync(tool, args, workspace, NetworkTimeout, token, standardInput: input).ConfigureAwait(false)
            : await ExecuteFixtureAsync().ConfigureAwait(false);
        // Never put untrusted provider output (which may contain credentials) in receipts or UI errors.
        if (result.ExitCode != 0) throw ReviewError("The hosting provider could not confirm the request. Check authentication, permissions, and the provider website.");
        if (result.StandardOutput.Length >= MaximumStandardOutputCharacters) throw ReviewError("The provider response exceeded the review size limit.");
        if (string.IsNullOrWhiteSpace(result.StandardOutput)) return default;
        try { using var json = JsonDocument.Parse(ReviewResponseBody(result.StandardOutput)); return json.RootElement.Clone(); }
        catch (JsonException) { throw ReviewError("The hosting provider returned an invalid response."); }

        async Task<ProcessResult> ExecuteFixtureAsync()
        {
            var value = await _reviewCommandExecutor!(tool, args, workspace, input, token).ConfigureAwait(false);
            return new(value.ExitCode, value.StandardOutput, value.StandardError);
        }
    }

    private Task<JsonElement> GitLabJsonAsync(SourceControlRepository repo, string workspace, string path, CancellationToken token, string method = "GET", object? payload = null) =>
        ProviderJsonAsync("glab", payload is null
            ? ["api", "--hostname", repo.Host, "--method", method, path]
            : ["api", "--hostname", repo.Host, "--method", method, "--input", "-", path],
            workspace, payload is null ? null : JsonSerializer.Serialize(payload), token);

    private static (string Organization, string Project) AzureReviewLocation(SourceControlRepository repo)
    {
        if (!Uri.TryCreate(repo.WebUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0)
            throw ReviewError("Select a supported HTTPS Azure repository.");
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) && parts.Length == 4 && parts[2] == "_git")
            return ("https://dev.azure.com/" + parts[0], Uri.UnescapeDataString(parts[1]));
        if (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase) && parts.Length == 3 && parts[1] == "_git")
            return ("https://" + uri.Host, Uri.UnescapeDataString(parts[0]));
        throw ReviewError("The Azure repository organization and project could not be identified.");
    }

    private async Task<JsonElement> ReadProviderDetailAsync(SourceControlRepository repo, int number, string workspace, CancellationToken token)
    {
        JsonElement detail;
        if (repo.Provider == SourceControlProvider.GitLab)
        {
            detail = await GitLabJsonAsync(repo, workspace, GitLabRequest(repo, number), token).ConfigureAwait(false);
            if (ProviderText(detail, "iid") != number.ToString(CultureInfo.InvariantCulture) ||
                ProviderText(detail, "web_url").TrimEnd('/') != repo.WebUrl.TrimEnd('/') + "/-/merge_requests/" + number.ToString(CultureInfo.InvariantCulture))
                throw ReviewError("GitLab returned a different merge request. Reload the repository.");
        }
        else
        {
            var location = AzureReviewLocation(repo);
            detail = await ProviderJsonAsync("az", ["repos", "pr", "show", "--id", number.ToString(CultureInfo.InvariantCulture),
                "--organization", location.Organization, "--detect", "false", "--output", "json", "--only-show-errors"], workspace, null, token).ConfigureAwait(false);
            var repository = ProviderObject(detail, "repository");
            if (ProviderText(detail, "pullRequestId") != number.ToString(CultureInfo.InvariantCulture) ||
                !string.Equals(ProviderText(repository, "name"), Uri.UnescapeDataString(repo.Name), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ProviderText(ProviderObject(repository, "project"), "name"), location.Project, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParse(ProviderText(repository, "id"), out _))
                throw ReviewError("Azure returned a different pull request repository. Reload the repository.");
            var remote = ProviderText(repository, "remoteUrl");
            if (remote.Length > 0 && Uri.TryCreate(remote, UriKind.Absolute, out var remoteUri) &&
                PullRequestReviewDefaults.HostingAuthority(repo.Provider, new UriBuilder(remoteUri) { UserName = "", Password = "" }.Uri.AbsoluteUri) != PullRequestReviewDefaults.HostingAuthority(repo.Provider, repo.WebUrl))
                throw ReviewError("Azure returned a repository in another organization.");
        }
        if (ProviderHead(repo, detail).Length == 0) throw ReviewError("The provider has not supplied a reviewable head commit. Retry after the diff is prepared.");
        return detail;
    }

    private static string ProviderHead(SourceControlRepository repo, JsonElement detail) => repo.Provider == SourceControlProvider.GitLab
        ? ProviderText(detail, "sha") : ProviderText(ProviderObject(detail, "lastMergeSourceCommit"), "commitId");
    private static string ProviderBase(SourceControlRepository repo, JsonElement detail) => repo.Provider == SourceControlProvider.GitLab
        ? ProviderText(ProviderObject(detail, "diff_refs"), "base_sha") : ProviderText(ProviderObject(detail, "lastMergeTargetCommit"), "commitId");

    private async Task<PullRequestReviewSnapshot> ReadProviderReviewAsync(SourceControlRepository repo, int number, string workspace,
        PullRequestReviewContinuation? page, CancellationToken token)
    {
        var detail = await ReadProviderDetailAsync(repo, number, workspace, token).ConfigureAwait(false);
        var head = ProviderHead(repo, detail);
        var baseId = ProviderBase(repo, detail);
        var pageNumber = 1;
        if (page is not null && (page.Repository != PullRequestReviewDefaults.RepositoryKey(repo) || page.Number != number.ToString(CultureInfo.InvariantCulture) ||
            page.HeadCommitId != head || page.BaseCommitId != baseId || page.ParentId is not null ||
            !int.TryParse(page.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out pageNumber) || pageNumber is < 2 or > MaximumProviderReviewPages ||
            repo.Provider != SourceControlProvider.GitLab || page.Kind is not (PullRequestReviewPageKind.Files or PullRequestReviewPageKind.Commits or PullRequestReviewPageKind.Checks or PullRequestReviewPageKind.Threads)))
            throw ReviewError("The review page no longer matches this repository and revision. Reload the review.");
        var descriptor = ParsePullRequest(repo, detail) with { Reviewers = ProviderArray(ProviderObject(detail, "reviewers")).Select(item => repo.Provider == SourceControlProvider.GitLab
                ? ProviderText(item, "username") : ProviderText(item, "uniqueName").Length > 0 ? ProviderText(item, "uniqueName") : ProviderText(item, "id")).ToArray(),
            Url = repo.Provider == SourceControlProvider.GitLab
            ? ProviderText(detail, "web_url") : repo.WebUrl.TrimEnd('/') + "/pullrequest/" + number.ToString(CultureInfo.InvariantCulture) };
        var notices = new List<string>();
        var next = new List<PullRequestReviewContinuation>();
        var files = new List<PullRequestChangedFile>();
        var commits = new List<PullRequestCommit>();
        var checks = new List<PullRequestCheck>();
        var discussions = new List<PullRequestDiscussion>();
        var reactions = new List<PullRequestReaction>();
        string viewer = "";
        JsonElement project = default;
        if (repo.Provider == SourceControlProvider.GitLab)
        {
            project = await GitLabJsonAsync(repo, workspace, GitLabProject(repo), token).ConfigureAwait(false);
            if (ProviderText(project, "id") != ProviderText(detail, "project_id")) throw ReviewError("The merge request does not belong to the selected project.");
            try { viewer = ProviderText(await GitLabJsonAsync(repo, workspace, "user", token).ConfigureAwait(false), "username"); }
            catch (Exception ex) when (ex is not OperationCanceledException) { notices.Add("Viewer identity unavailable; review writes are disabled."); }
            foreach (var kind in new[] { PullRequestReviewPageKind.Files, PullRequestReviewPageKind.Commits, PullRequestReviewPageKind.Checks, PullRequestReviewPageKind.Threads })
            {
                if (page is not null && page.Kind != kind) continue;
                var suffix = kind switch { PullRequestReviewPageKind.Files => "diffs", PullRequestReviewPageKind.Commits => "commits", PullRequestReviewPageKind.Checks => "pipelines", _ => "discussions" };
                try
                {
                    var data = await GitLabJsonAsync(repo, workspace, $"{GitLabRequest(repo, number)}/{suffix}?per_page=100&page={pageNumber.ToString(CultureInfo.InvariantCulture)}", token).ConfigureAwait(false);
                    if (data.ValueKind != JsonValueKind.Array) throw ReviewError("Invalid review page.");
                    var items = ProviderArray(data);
                    if (items.Length >= 100)
                    {
                        if (pageNumber < MaximumProviderReviewPages) next.Add(new(kind, (pageNumber + 1).ToString(CultureInfo.InvariantCulture), PullRequestReviewDefaults.RepositoryKey(repo), descriptor.Number, head, BaseCommitId: baseId));
                        else notices.Add($"{kind} reached the page limit. Continue on the provider website.");
                    }
                    foreach (var item in items.Take(100))
                        switch (kind)
                        {
                            case PullRequestReviewPageKind.Files: files.Add(ParseGitLabFile(item)); break;
                            case PullRequestReviewPageKind.Commits:
                                commits.Add(new(ProviderText(item, "id"), ProviderText(item, "title"), ProviderText(item, "author_name"), ProviderDate(item, "created_at"), ProviderText(item, "web_url"))); break;
                            case PullRequestReviewPageKind.Checks:
                                if (ProviderText(item, "sha") != head && ProviderText(item, "id") != ProviderText(ProviderObject(detail, "head_pipeline"), "id")) break;
                                var status = ProviderText(item, "status");
                                checks.Add(new("Pipeline " + ProviderText(item, "id"), status, status is "success" or "failed" or "canceled" or "skipped" ? status : null, ProviderText(item, "web_url"), ProviderText(item, "id"))); break;
                            case PullRequestReviewPageKind.Threads: discussions.Add(ParseGitLabDiscussion(item, viewer, descriptor.Url, head, notices)); break;
                        }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && page is null) { notices.Add($"{kind} could not be loaded. Refresh or use the provider website."); }
            }
            var noteIds = discussions.SelectMany(item => item.Comments).Where(comment => comment.Author.Length > 0).Select(comment => comment.Id).Distinct().Take(100).ToArray();
            var noteReactions = new Dictionary<string, IReadOnlyList<PullRequestReaction>>(StringComparer.Ordinal);
            try
            {
                var awards = await ReadGitLabReviewReactionsAsync(repo, workspace, number, noteIds, viewer, token).ConfigureAwait(false);
                reactions.AddRange(awards.Reactions);
                noteReactions = awards.Notes;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { notices.Add("Reactions could not be loaded."); }
            for (var index = 0; index < discussions.Count; index++) discussions[index] = discussions[index] with
            {
                Comments = discussions[index].Comments.Select(comment => noteReactions.TryGetValue(comment.Id, out var awards)
                    ? comment with { CanReact = viewer.Length > 0, Reactions = awards } : comment).ToArray()
            };
            if (discussions.SelectMany(item => item.Comments).Any(comment => !noteReactions.ContainsKey(comment.Id)))
                notices.Add("Some comment reactions are unavailable; reactions are limited to 100 comments per page.");
        }
        else
        {
            notices.Add("Azure diffs and comment changes are available on the provider website. Conversation is read-only here.");
            try
            {
                var data = await AzureInvokeAsync(repo, workspace, detail, number, "pullRequestThreads", "GET", null, token).ConfigureAwait(false);
                if (ProviderObject(data, "value").ValueKind != JsonValueKind.Array) throw ReviewError("Invalid Azure conversation response.");
                var items = ProviderArray(ProviderObject(data, "value"));
                if (items.Length >= 100) notices.Add("Conversation is limited to 100 threads. Continue on the provider website.");
                foreach (var item in items.Take(100))
                {
                    var all = ProviderArray(ProviderObject(item, "comments"));
                    if (all.Length > 100) notices.Add("A conversation thread has additional comments on the provider website.");
                    var comments = all.Take(100).Select(note => new PullRequestReviewComment(ProviderText(item, "id") + "/" + ProviderText(note, "id"),
                        ProviderText(ProviderObject(note, "author"), "displayName"), ProviderText(note, "content"), ProviderDate(note, "publishedDate"), descriptor.Url)).ToArray();
                    discussions.Add(new(ProviderText(item, "id"), null, null, null, ProviderText(item, "status") is "fixed" or "closed", false, false, false, comments));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { notices.Add("Conversation could not be loaded. Refresh or use the provider website."); }
        }
        // Disallow a mixed snapshot when the head or comparison changed during the parallel provider reads.
        var current = await ReadProviderDetailAsync(repo, number, workspace, token).ConfigureAwait(false);
        if (head != ProviderHead(repo, current) || baseId != ProviderBase(repo, current)) throw ReviewError("The pull request revision changed while loading. Reload the review.");
        var lineCount = 0;
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            if (lineCount + file.Lines.Count > PullRequestReviewDefaults.MaximumDiffLines)
            { files[index] = file with { Lines = [], PatchUnavailable = true }; notices.Add("Some patches exceed the review size limit. Open the provider website for those files."); }
            else lineCount += file.Lines.Count;
        }
        var canWrite = repo.CanWrite && (repo.Provider == SourceControlProvider.AzureDevOps || viewer.Length > 0);
        descriptor = descriptor with { Checks = PullRequestReviewDefaults.GetCheckState(checks, next.Any(item => item.Kind == PullRequestReviewPageKind.Checks)) };
        return new(repo, descriptor, ProviderText(detail, "description"), head, baseId, viewer,
            commits, checks, files, discussions, notices.Count > 0 || next.Count > 0, string.Join(" ", notices.Distinct()), next,
            CanEditDetails: canWrite, CanManageMetadata: canWrite, Advanced: ParseProviderAdvanced(repo, detail, project, canWrite),
            CanReact: canWrite && repo.Provider == SourceControlProvider.GitLab && !notices.Contains("Reactions could not be loaded."), Reactions: reactions,
            Capabilities: HostingCapabilities.Review(repo.Provider) with { Verdicts = canWrite ? HostingCapabilities.Review(repo.Provider).Verdicts : [] });
    }

    private static PullRequestChangedFile ParseGitLabFile(JsonElement item)
    {
        var path = ProviderText(item, "new_path");
        var oldPath = ProviderText(item, "old_path");
        var patch = ProviderText(item, "diff");
        var unavailable = patch.Length == 0 || ProviderBool(item, "too_large") || ProviderBool(item, "collapsed");
        var lines = unavailable ? [] : ParsePatch(patch);
        return new(path, oldPath != path ? oldPath : null, ProviderBool(item, "new_file") ? "added" : ProviderBool(item, "deleted_file") ? "removed" : oldPath != path ? "renamed" : "modified",
            lines.Count(line => line.Kind == PullRequestDiffLineKind.Addition), lines.Count(line => line.Kind == PullRequestDiffLineKind.Deletion), lines, unavailable);
    }

    private static PullRequestDiscussion ParseGitLabDiscussion(JsonElement item, string viewer, string url, string head, List<string> notices)
    {
        var all = ProviderArray(ProviderObject(item, "notes"));
        if (all.Length > 100) notices.Add("A discussion has more than 100 comments. Continue on the provider website.");
        var first = all.FirstOrDefault();
        var position = ProviderObject(first, "position");
        var hasNew = int.TryParse(ProviderText(position, "new_line"), out var newLine);
        var hasOld = int.TryParse(ProviderText(position, "old_line"), out var oldLine);
        var comments = all.Take(100).Select(note => new PullRequestReviewComment(ProviderText(note, "id"),
            ProviderText(ProviderObject(note, "author"), "username"), ProviderText(note, "body"), ProviderDate(note, "created_at"), url + "#note_" + ProviderText(note, "id"),
            CanEdit: viewer.Length > 0 && ProviderText(ProviderObject(note, "author"), "username") == viewer && !ProviderBool(note, "system"),
            CanDelete: viewer.Length > 0 && ProviderText(ProviderObject(note, "author"), "username") == viewer && !ProviderBool(note, "system"),
            Kind: position.ValueKind == JsonValueKind.Object ? PullRequestCommentKind.Inline : PullRequestCommentKind.General)).ToArray();
        return new(ProviderText(item, "id"), position.ValueKind == JsonValueKind.Object ? ProviderText(position, "new_path") : null,
            hasNew ? newLine : hasOld ? oldLine : null, hasNew ? PullRequestDiffSide.Right : hasOld ? PullRequestDiffSide.Left : null,
            all.Any(note => ProviderBool(note, "resolvable")) && all.Where(note => ProviderBool(note, "resolvable")).All(note => ProviderBool(note, "resolved")),
            position.ValueKind == JsonValueKind.Object && ProviderText(position, "head_sha") != head,
            viewer.Length > 0 && !ProviderBool(item, "individual_note"), viewer.Length > 0 && all.Any(note => ProviderBool(note, "resolvable")), comments);
    }

    private static PullRequestAdvancedState ParseProviderAdvanced(SourceControlRepository repo, JsonElement detail, JsonElement project, bool canWrite)
    {
        var gitlab = repo.Provider == SourceControlProvider.GitLab;
        var open = ProviderText(detail, gitlab ? "state" : "status") == (gitlab ? "opened" : "active");
        var draft = ProviderBool(detail, gitlab ? "draft" : "isDraft") || gitlab && ProviderBool(detail, "work_in_progress");
        var auto = gitlab ? ProviderBool(detail, "merge_when_pipeline_succeeds") || ProviderBool(detail, "auto_merge_enabled") : ProviderText(ProviderObject(detail, "autoCompleteSetBy"), "id").Length > 0;
        var mergeability = ProviderText(detail, gitlab ? "detailed_merge_status" : "mergeStatus");
        var mergePermission = canWrite && (!gitlab || ProviderBool(ProviderObject(detail, "user"), "can_merge"));
        var methods = new List<PullRequestMergeMethod>();
        if (!gitlab) methods.AddRange([PullRequestMergeMethod.Merge, PullRequestMergeMethod.Squash]);
        else
        {
            var method = ProviderText(project, "merge_method");
            var squash = ProviderText(project, "squash_option");
            if (method == "merge" && squash != "always") methods.Add(PullRequestMergeMethod.Merge);
            if (method is "ff" or "rebase_merge" && squash != "always") methods.Add(PullRequestMergeMethod.Rebase);
            if (method is "merge" or "rebase_merge" or "ff" && squash is "always" or "default_on" or "default_off") methods.Add(PullRequestMergeMethod.Squash);
        }
        return new(methods, open && !draft && mergePermission && methods.Count > 0 && mergeability is "mergeable" or "succeeded",
            open && !draft && mergePermission && !auto && methods.Count > 0, open && mergePermission && auto,
            gitlab && open && mergePermission && !ProviderBool(detail, "rebase_in_progress"), false, false, auto, mergeability.Length > 0 ? mergeability : "Unknown",
            gitlab && ProviderText(detail, "diverged_commits_count") == "0" ? "Up to date" : "Unknown",
            gitlab ? [PullRequestUpdateMethod.Rebase] : []);
    }

    private async Task<JsonElement> AzureInvokeAsync(SourceControlRepository repo, string workspace, JsonElement detail, int number, string resource, string method, object? payload, CancellationToken token)
    {
        var location = AzureReviewLocation(repo);
        var args = new List<string> { "devops", "invoke", "--organization", location.Organization, "--area", "git", "--resource", resource,
            "--route-parameters", "project=" + location.Project, "repositoryId=" + ProviderText(ProviderObject(detail, "repository"), "id"),
            "pullRequestId=" + number.ToString(CultureInfo.InvariantCulture), "--http-method", method, "--api-version", "7.1", "--output", "json", "--only-show-errors" };
        string? temporary = null;
        try
        {
            if (payload is not null)
            {
                temporary = Path.Combine(Path.GetTempPath(), "pistation-review-" + Guid.NewGuid().ToString("N") + ".json");
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(payload), token).ConfigureAwait(false);
                args.AddRange(["--in-file", temporary, "--encoding", "utf-8"]);
            }
            return await ProviderJsonAsync("az", args, workspace, null, token).ConfigureAwait(false);
        }
        finally { if (temporary is not null) File.Delete(temporary); }
    }

    // Like T3, read comment awards together through GraphQL instead of launching one CLI process per comment.
    private async Task<(IReadOnlyList<PullRequestReaction> Reactions, Dictionary<string, IReadOnlyList<PullRequestReaction>> Notes)> ReadGitLabReviewReactionsAsync(
        SourceControlRepository repo, string workspace, int number, string[] noteIds, string viewer, CancellationToken token)
    {
        const string query = """
            query($fullPath:ID!, $iid:String!, $cursor:String) {
              currentUser { username }
              project(fullPath:$fullPath) { mergeRequest(iid:$iid) {
                awardEmoji(first:100) { pageInfo { hasNextPage } nodes { name user { username } } }
                notes(first:100, after:$cursor) {
                  pageInfo { hasNextPage endCursor }
                  nodes { id awardEmoji(first:100) { pageInfo { hasNextPage } nodes { name user { username } } } }
                }
              } }
            }
            """;
        var notes = new Dictionary<string, IReadOnlyList<PullRequestReaction>>(StringComparer.Ordinal);
        IReadOnlyList<PullRequestReaction> reactions = [];
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var response = await GitLabJsonAsync(repo, workspace, "graphql", token, "POST", new { query,
                variables = new { fullPath = repo.Owner + "/" + repo.Name, iid = number.ToString(CultureInfo.InvariantCulture), cursor } }).ConfigureAwait(false);
            if (ProviderArray(ProviderObject(response, "errors")).Length > 0) throw ReviewError("GitLab reactions are unavailable.");
            var data = ProviderObject(response, "data");
            if (ProviderText(ProviderObject(data, "currentUser"), "username") != viewer) throw ReviewError("The GitLab account changed during the read.");
            var mr = ProviderObject(ProviderObject(data, "project"), "mergeRequest");
            if (page == 0) reactions = ParseGitLabAwards(ProviderObject(mr, "awardEmoji"), viewer);
            var connection = ProviderObject(mr, "notes");
            if (ProviderObject(connection, "nodes").ValueKind != JsonValueKind.Array) throw ReviewError("GitLab returned an invalid reactions page.");
            foreach (var note in ProviderArray(ProviderObject(connection, "nodes")).Take(100))
            {
                var id = ProviderText(note, "id").Split('/')[^1];
                if (!noteIds.Contains(id)) continue;
                var awards = ProviderObject(note, "awardEmoji");
                if (!ProviderBool(ProviderObject(awards, "pageInfo"), "hasNextPage")) notes[id] = ParseGitLabAwards(awards, viewer);
            }
            var info = ProviderObject(connection, "pageInfo");
            if (noteIds.All(notes.ContainsKey) || !ProviderBool(info, "hasNextPage")) break;
            var next = ProviderText(info, "endCursor");
            if (next.Length is 0 or > 2048 || next == cursor) throw ReviewError("GitLab reactions pagination did not advance.");
            cursor = next;
        }
        return (reactions, notes);
    }

    private static PullRequestReaction[] ParseGitLabAwards(JsonElement connection, string viewer)
    {
        var nodes = ProviderObject(connection, "nodes");
        if (nodes.ValueKind != JsonValueKind.Array || nodes.GetArrayLength() > 100 || ProviderBool(ProviderObject(connection, "pageInfo"), "hasNextPage"))
            throw ReviewError("The reaction list is incomplete. Use the provider website.");
        return ProviderArray(nodes).GroupBy(item => ProviderText(item, "name")).Select(group =>
            Enum.GetValues<PullRequestReactionContent>().Where(content => GitLabReactionName(content) == group.Key)
                .Select(content => new PullRequestReaction(content, group.Count(), group.Any(item => ProviderText(ProviderObject(item, "user"), "username") == viewer))))
            .SelectMany(group => group).ToArray();
    }

    private static string GitLabReactionName(PullRequestReactionContent content) => content switch
    {
        PullRequestReactionContent.ThumbsUp => "thumbsup", PullRequestReactionContent.ThumbsDown => "thumbsdown", PullRequestReactionContent.Laugh => "laughing",
        PullRequestReactionContent.Hooray => "tada", PullRequestReactionContent.Confused => "confused", PullRequestReactionContent.Heart => "heart",
        PullRequestReactionContent.Rocket => "rocket", PullRequestReactionContent.Eyes => "eyes", _ => throw ReviewError("Select a supported reaction.")
    };
}
