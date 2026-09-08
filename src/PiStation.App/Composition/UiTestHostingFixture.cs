#if DEBUG
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiStation.App.Composition;

// Explicit Debug-only fixture: provider commands terminate here and never invoke a CLI or network.
internal sealed class UiTestHostingFixture
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly JsonObject _document;
    private JsonObject PullRequest => _document["data"]!["repository"]!["pullRequest"]!.AsObject();

    public UiTestHostingFixture(string path)
    {
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new ArgumentException("The hosting fixture exceeds 2 MiB.", nameof(path));
        _path = path;
        _document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        _ = PullRequest["id"]!.GetValue<string>();
    }

    public Task<(int ExitCode, string StandardOutput, string StandardError)> ExecuteAsync(string tool, IReadOnlyList<string> arguments,
        string workspace, string? input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(Execute(tool, arguments, input));
    }

    private (int, string, string) Execute(string tool, IReadOnlyList<string> args, string? input)
    {
        if (tool != "gh" || args.Count == 0) return (1, "", "Only GitHub is supported by this UI fixture.");
        if (args[0] == "auth") return (0, "", "");
        if (args.Count > 1 && args[0] == "pr" && args[1] is "list" or "view")
        {
            var row = PullRequest.DeepClone().AsObject();
            row["labels"] = PullRequest["labels"]?["nodes"]?.DeepClone() ?? new JsonArray();
            row["reviewRequests"] = new JsonArray();
            row["statusCheckRollup"] = new JsonArray();
            return Success(args[1] == "list" ? new JsonArray(row) : row);
        }
        var payload = input is null ? null : JsonNode.Parse(input);
        var query = payload?["query"]?.GetValue<string>();
        if (query is not null)
        {
            if (query.Contains("PullRequestListCheckStates", StringComparison.Ordinal))
            {
                var repository = new JsonObject();
                foreach (Match field in Regex.Matches(query, @"(pr\d+):pullRequest\(number:(\d+)\)"))
                    repository[field.Groups[1].Value] = field.Groups[2].Value == PullRequest["number"]!.ToString()
                        ? new JsonObject { ["number"] = PullRequest["number"]!.DeepClone(), ["commits"] = new JsonObject { ["nodes"] = new JsonArray(new JsonObject { ["commit"] = new JsonObject { ["statusCheckRollup"] = new JsonObject { ["state"] = "FAILURE" } } }) } }
                        : null;
                return Success(new JsonObject { ["data"] = new JsonObject { ["repository"] = repository } });
            }
            var variables = payload!["variables"];
            if (query.TrimStart().StartsWith("mutation", StringComparison.Ordinal)) return Mutate(query, variables!);
            if (query.Contains("node(id:$id)", StringComparison.Ordinal))
            {
                var subject = FindSubject(variables!["id"]!.GetValue<string>())?.DeepClone();
                if (subject?["__typename"]?.ToString() == "IssueComment" && query.Contains("issuePullRequest:pullRequest", StringComparison.Ordinal))
                {
                    subject["issuePullRequest"] = subject["pullRequest"]?.DeepClone();
                    subject.AsObject().Remove("pullRequest");
                }
                return Success(new JsonObject { ["data"] = new JsonObject { ["node"] = subject } });
            }
            return Success(new JsonObject { ["data"] = _document["data"]!.DeepClone() });
        }
        var endpoint = args[^1];
        if (args.Contains("DELETE"))
        {
            if (endpoint.Contains("/labels/", StringComparison.Ordinal))
            {
                var labels = PullRequest["labels"]!["nodes"]!.AsArray();
                var label = Uri.UnescapeDataString(endpoint[(endpoint.LastIndexOf('/') + 1)..]);
                var match = labels.FirstOrDefault(item => item?["name"]?.GetValue<string>() == label);
                if (match is not null) labels.Remove(match);
            }
            else if (endpoint.EndsWith("/requested_reviewers", StringComparison.Ordinal)) PullRequest["reviewRequests"]!["nodes"] = new JsonArray();
            else return Rejected();
            Save(); return Success(new JsonObject());
        }
        if (endpoint.Contains("/actions/runs", StringComparison.Ordinal))
        {
            var workflows = _document["workflows"]!.AsArray();
            if (endpoint.Contains('?')) return Success(new JsonObject { ["workflow_runs"] = new JsonArray(workflows.Where(run => run?["conclusion"]?.ToString() == "action_required").Select(run => run!.DeepClone()).ToArray()) });
            var id = endpoint.Split('/').Last(part => part.All(char.IsAsciiDigit));
            var selected = workflows.FirstOrDefault(run => run?["id"]?.ToString() == id);
            if (selected is null) return Rejected();
            if (endpoint.EndsWith("/approve", StringComparison.Ordinal))
            {
                selected["status"] = "queued"; selected["conclusion"] = null; Save();
                return (0, "HTTP/2.0 201 Created\r\n\r\n", "");
            }
            return Success(selected.DeepClone());
        }
        if (endpoint.Contains("/files", StringComparison.Ordinal)) return Success(_document["files"]!.DeepClone());
        return Rejected();
    }

    private (int, string, string) Mutate(string query, JsonNode variables)
    {
        var pr = PullRequest;
        var id = variables["id"]!.GetValue<string>();
        var subject = FindSubject(id);
        if (subject is null) return Rejected();
        if (query.Contains("change:updatePullRequest(", StringComparison.Ordinal)) { pr["title"] = variables["title"]!.DeepClone(); pr["body"] = variables["body"]!.DeepClone(); }
        else if (query.Contains("change:convertPullRequestToDraft", StringComparison.Ordinal)) pr["isDraft"] = true;
        else if (query.Contains("change:markPullRequestReadyForReview", StringComparison.Ordinal)) pr["isDraft"] = false;
        else if (query.Contains("change:enablePullRequestAutoMerge", StringComparison.Ordinal))
        { pr["autoMergeRequest"] = new JsonObject { ["enabledAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) }; }
        else if (query.Contains("change:disablePullRequestAutoMerge", StringComparison.Ordinal)) pr["autoMergeRequest"] = null;
        else if (query.Contains("change:mergePullRequest", StringComparison.Ordinal)) pr["state"] = "MERGED";
        else if (query.Contains("change:updatePullRequestBranch", StringComparison.Ordinal)) pr["headRefOid"] = new string('c', 40);
        else if (query.Contains("change:revertPullRequest", StringComparison.Ordinal))
            return Success(new JsonObject { ["data"] = new JsonObject { ["change"] = new JsonObject { ["revertPullRequest"] = new JsonObject { ["id"] = "REVERT", ["url"] = "https://github.com/pistation-fixture/review/pull/8" } } } });
        else if (query.Contains("change:addReaction", StringComparison.Ordinal) || query.Contains("change:removeReaction", StringComparison.Ordinal))
        {
            var adding = query.Contains("change:addReaction", StringComparison.Ordinal);
            var content = variables["content"]!.GetValue<string>();
            var groups = subject["reactionGroups"]?.AsArray() ?? new JsonArray();
            if (subject["reactionGroups"] is null) subject["reactionGroups"] = groups;
            var reaction = groups.FirstOrDefault(group => group?["content"]?.ToString() == content);
            if (reaction is null) { reaction = new JsonObject { ["content"] = content, ["viewerHasReacted"] = false, ["reactors"] = new JsonObject { ["totalCount"] = 0 } }; groups.Add(reaction); }
            if (reaction["viewerHasReacted"]!.GetValue<bool>() != adding)
                reaction["reactors"]!["totalCount"] = Math.Max(0, reaction["reactors"]!["totalCount"]!.GetValue<int>() + (adding ? 1 : -1));
            reaction["viewerHasReacted"] = adding;
        }
        else if (query.Contains("change:updateIssueComment", StringComparison.Ordinal) || query.Contains("change:updatePullRequestReviewComment", StringComparison.Ordinal) || query.Contains("change:updatePullRequestReview(", StringComparison.Ordinal)) subject["body"] = variables["body"]!.DeepClone();
        else if (query.Contains("change:delete", StringComparison.Ordinal) && subject.Parent is JsonArray parent) parent.Remove(subject);
        else return Rejected();
        Save();
        return Success(new JsonObject { ["data"] = new JsonObject { ["change"] = new JsonObject { ["clientMutationId"] = null, ["pullRequest"] = new JsonObject { ["id"] = pr["id"]!.DeepClone() } } } });
    }

    private JsonObject? FindSubject(string id) => Walk(PullRequest).FirstOrDefault(node => node["id"]?.ToString() == id);
    private static IEnumerable<JsonObject> Walk(JsonNode node)
    {
        if (node is JsonObject obj) { yield return obj; foreach (var value in obj.Select(pair => pair.Value).OfType<JsonNode>()) foreach (var child in Walk(value)) yield return child; }
        else if (node is JsonArray array) foreach (var value in array.OfType<JsonNode>()) foreach (var child in Walk(value)) yield return child;
    }
    private void Save()
    {
        PullRequest["updatedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        File.WriteAllText(_path, _document.ToJsonString());
    }
    private static (int, string, string) Success(JsonNode node) => (0, node.ToJsonString(), "");
    private static (int, string, string) Rejected() => (1, "HTTP/2.0 422 Unprocessable Entity\r\n\r\n{\"message\":\"This command is unavailable in the local UI fixture.\"}", "");
}
#endif
