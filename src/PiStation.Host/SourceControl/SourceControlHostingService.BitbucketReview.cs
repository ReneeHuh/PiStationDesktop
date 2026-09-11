using System.Globalization;
using System.Text.Json;
using PiStation.Protocol.Models;

namespace PiStation.Host.SourceControl;

public sealed partial class SourceControlHostingService
{
    private async Task<PullRequestReviewSnapshot> ReadBitbucketReviewAsync(SourceControlRepository repo, int number,
        PullRequestReviewContinuation? page, CancellationToken token)
    {
        var detail = await BbDetailAsync(repo, number, token).ConfigureAwait(false);
        var head = BbRevision(detail, "source"); var baseline = BbRevision(detail, "destination");
        var endpoint = BbPrPath(repo, number);
        var depth = 1;
        string Section(PullRequestReviewPageKind kind) => endpoint + (kind switch { PullRequestReviewPageKind.Files => "/diffstat",
            PullRequestReviewPageKind.Commits => "/commits", PullRequestReviewPageKind.Checks => "/statuses", PullRequestReviewPageKind.Threads => "/comments",
            _ => throw ReviewError("Unsupported Bitbucket review page.") });
        string? continuation = null;
        if (page is not null)
        {
            var parts = page.Cursor.Split('|', 2);
            if (page.Repository != PullRequestReviewDefaults.RepositoryKey(repo) || page.Number != number.ToString(CultureInfo.InvariantCulture) ||
                page.HeadCommitId != head || page.BaseCommitId != baseline || page.ParentId is not null || parts.Length != 2 ||
                !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out depth) || depth is < 2 or > 30 || parts[1].Length > 4096)
                throw ReviewError("The Bitbucket page no longer matches the inspected revision.");
            var uri = BitbucketCloudClient.Resolve(parts[1]);
            if (uri.AbsolutePath != BitbucketCloudClient.Resolve(Section(page.Kind)).AbsolutePath) throw ReviewError("Invalid Bitbucket page destination.");
            continuation = parts[1];
        }
        var viewer = "";
        if (_bitbucket.IsConfigured)
        {
            try { viewer = BbIdentity(await _bitbucket.JsonAsync("user", token: token).ConfigureAwait(false)); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { }
        }
        var files = new List<PullRequestChangedFile>(); var commits = new List<PullRequestCommit>();
        var checks = new List<PullRequestCheck>(); var discussions = new List<PullRequestDiscussion>();
        var nextPages = new List<PullRequestReviewContinuation>(); var notices = new List<string>();
        var kinds = page is null ? new[] { PullRequestReviewPageKind.Files, PullRequestReviewPageKind.Commits, PullRequestReviewPageKind.Checks, PullRequestReviewPageKind.Threads } : [page.Kind];
        foreach (var kind in kinds)
        {
            try
            {
                var section = Section(kind);
                var raw = await _bitbucket.JsonAsync(continuation ?? section + "?pagelen=100", token: token).ConfigureAwait(false);
                var values = BbValues(raw); var next = BbNext(raw, section);
                if (next is not null && depth < 30) nextPages.Add(new(kind, (depth + 1).ToString(CultureInfo.InvariantCulture) + "|" + next,
                    PullRequestReviewDefaults.RepositoryKey(repo), number.ToString(CultureInfo.InvariantCulture), head, BaseCommitId: baseline));
                else if (next is not null) notices.Add($"{kind} reached the 30-page limit; use the provider website for the remainder.");
                switch (kind)
                {
                    case PullRequestReviewPageKind.Files:
                    {
                        string patch;
                        try { patch = await _bitbucket.RequestAsync(endpoint + "/diff", token: token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception) { patch = ""; notices.Add("Hosted patch unavailable or oversized; use the provider website."); }
                        var sections = new List<string>(); var current = new System.Text.StringBuilder();
                        foreach (var line in patch.Split('\n'))
                        {
                            if (line.StartsWith("diff --git ", StringComparison.Ordinal) && current.Length > 0) { sections.Add(current.ToString()); current.Clear(); }
                            current.Append(line.TrimEnd('\r')).Append('\n');
                        }
                        if (current.Length > 0) sections.Add(current.ToString());
                        var remaining = PullRequestReviewDefaults.MaximumDiffLines;
                        foreach (var item in values)
                        {
                            var path = ProviderText(ProviderObject(item, "new"), "path"); var old = ProviderText(ProviderObject(item, "old"), "path");
                            if (path.Length == 0) path = old;
                            if (path.Length == 0) continue;
                            var content = sections.FirstOrDefault(section => section.Split('\n').TakeWhile(line => !line.StartsWith("@@ ", StringComparison.Ordinal)).Any(line => line == "+++ b/" + path ||
                                ProviderText(item, "status") == "removed" && line == "--- a/" + path));
                            var parsed = content is null || !content.Contains("@@ ", StringComparison.Ordinal) ? [] : ParsePatch(content);
                            var unavailable = parsed.Count == 0 || parsed.Count > remaining;
                            if (!unavailable) remaining -= parsed.Count;
                            files.Add(new(path, old.Length > 0 && old != path ? old : null, ProviderText(item, "status"),
                                BbInt(item, "lines_added"), BbInt(item, "lines_removed"), unavailable ? [] : parsed, unavailable));
                        }
                        break;
                    }
                    case PullRequestReviewPageKind.Commits:
                        commits.AddRange(values.Select(item => new PullRequestCommit(ProviderText(item, "hash"), ProviderText(item, "message").Split('\n')[0],
                            BbActor(ProviderObject(ProviderObject(item, "author"), "user")), ProviderDate(item, "date"), BbLink(item, "html")))); break;
                    case PullRequestReviewPageKind.Checks:
                        checks.AddRange(values.Select(item => new PullRequestCheck(ProviderText(item, "name"), ProviderText(item, "state"),
                            ProviderText(item, "state") switch { "SUCCESSFUL" => "SUCCESS", "FAILED" => "FAILURE", "STOPPED" => "CANCELLED", _ => null },
                            ProviderText(item, "url"), ProviderText(item, "key")))); break;
                    case PullRequestReviewPageKind.Threads:
                        foreach (var group in values.GroupBy(item => ProviderText(ProviderObject(item, "parent"), "id") is { Length: > 0 } parent ? parent : ProviderText(item, "id")))
                        {
                            var root = group.FirstOrDefault(item => ProviderText(item, "id") == group.Key);
                            var inline = ProviderObject(root, "inline");
                            var line = BbInt(inline, "to"); var side = PullRequestDiffSide.Right;
                            if (line == 0) { line = BbInt(inline, "from"); side = PullRequestDiffSide.Left; }
                            var comments = group.Where(item => !ProviderBool(item, "deleted")).Select(item => new PullRequestReviewComment(
                                ProviderText(item, "id"), BbActor(ProviderObject(item, "user")), ProviderText(ProviderObject(item, "content"), "raw"), ProviderDate(item, "created_on"),
                                BbLink(item, "html"), repo.CanWrite && viewer.Length > 0 && viewer == BbIdentity(ProviderObject(item, "user")),
                                repo.CanWrite && viewer.Length > 0 && viewer == BbIdentity(ProviderObject(item, "user")),
                                ProviderObject(item, "inline").ValueKind == JsonValueKind.Object ? PullRequestCommentKind.Inline : PullRequestCommentKind.General)).ToArray();
                            discussions.Add(new(group.Key, ProviderText(inline, "path") is { Length: > 0 } p ? p : null, line > 0 ? line : null, line > 0 ? side : null,
                                ProviderObject(root, "resolution").ValueKind == JsonValueKind.Object, ProviderBool(inline, "outdated"), repo.CanWrite,
                                repo.CanWrite && root.ValueKind == JsonValueKind.Object && !ProviderBool(root, "deleted"), comments));
                        }
                        break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { notices.Add($"{kind} could not be loaded. Refresh or open the provider website."); }
        }
        var fresh = await BbDetailAsync(repo, number, token).ConfigureAwait(false);
        if (BbRevision(fresh, "source") != head || BbRevision(fresh, "destination") != baseline) throw ReviewError("The Bitbucket revision changed while loading. Refresh the review.");
        var descriptor = ParseBitbucketPr(repo, detail) with { Checks = PullRequestReviewDefaults.GetCheckState(checks, nextPages.Any(p => p.Kind == PullRequestReviewPageKind.Checks)) };
        var open = descriptor.State == PullRequestState.Open;
        return new(repo, descriptor, ProviderText(detail, "description"), head, baseline, viewer, commits, checks, files, discussions,
            notices.Count > 0 || nextPages.Count > 0, notices.Count == 0 ? null : string.Join(" ", notices), nextPages,
            repo.CanWrite, repo.CanWrite, new(Enum.GetValues<PullRequestMergeMethod>(), repo.CanWrite && open, false, false, false, false, false, false,
                "Provider checks permissions and merge rules when submitted.", "", []), Capabilities: HostingCapabilities.Review(repo.Provider));
    }
    private static int BbInt(JsonElement value, string name) => int.TryParse(ProviderText(value, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;
}
