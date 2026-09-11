using System.Globalization;
using System.Text.Json;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.Host.SourceControl;

public sealed partial class SourceControlHostingService
{
    private static string BbRepositoryPath(SourceControlRepository repo)
    {
        static bool Segment(string value) => value.Length is > 0 and <= 128 && value is not "." and not ".." &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
        if (repo.Provider != SourceControlProvider.Bitbucket || repo.Host != "bitbucket.org" || !Segment(repo.Owner) || !Segment(repo.Name))
            throw ReviewError("Only Bitbucket Cloud repositories at bitbucket.org are supported.");
        return "repositories/" + Uri.EscapeDataString(repo.Owner) + "/" + Uri.EscapeDataString(repo.Name);
    }
    private static string BbPrPath(SourceControlRepository repo, int number) => BbRepositoryPath(repo) + "/pullrequests/" + number.ToString(CultureInfo.InvariantCulture);
    private static string BbIdentity(JsonElement actor) => ProviderText(actor, "uuid");
    private static string BbActor(JsonElement actor) => ProviderText(actor, "nickname") is { Length: > 0 } nickname ? nickname :
        ProviderText(actor, "display_name") is { Length: > 0 } display ? display : BbIdentity(actor);
    private static string BbLink(JsonElement value, string kind) => ProviderText(ProviderObject(ProviderObject(value, "links"), kind), "href");
    private static string BbRevision(JsonElement detail, string side) => ProviderText(ProviderObject(ProviderObject(detail, side), "commit"), "hash");

    private async Task<SourceControlRepository> DetectBitbucketAsync(SourceControlRepository repo, CancellationToken token)
    {
        var metadata = await _bitbucket.JsonAsync(BbRepositoryPath(repo), token: token).ConfigureAwait(false);
        if (!string.Equals(ProviderText(metadata, "full_name"), repo.Owner + "/" + repo.Name, StringComparison.OrdinalIgnoreCase))
            throw ReviewError("Bitbucket returned another repository.");
        var web = "https://bitbucket.org/" + repo.Owner + "/" + repo.Name;
        return repo with { WebUrl = web, RemoteUrl = web + ".git", DefaultBranch = ProviderText(ProviderObject(metadata, "mainbranch"), "name") is { Length: > 0 } branch ? branch : "main",
            CanWrite = _bitbucket.IsConfigured };
    }
    private async Task<JsonElement> BbDetailAsync(SourceControlRepository repo, int number, CancellationToken token)
    {
        var detail = await _bitbucket.JsonAsync(BbPrPath(repo, number), token: token).ConfigureAwait(false);
        var destination = ProviderObject(ProviderObject(detail, "destination"), "repository");
        if (ProviderText(detail, "id") != number.ToString(CultureInfo.InvariantCulture) ||
            !string.Equals(ProviderText(destination, "full_name"), repo.Owner + "/" + repo.Name, StringComparison.OrdinalIgnoreCase) ||
            BbRevision(detail, "source").Length == 0 || BbRevision(detail, "destination").Length == 0)
            throw ReviewError("Bitbucket did not identify the requested pull request and revisions.");
        return detail;
    }
    private static PullRequestDescriptor ParseBitbucketPr(SourceControlRepository repo, JsonElement item)
    {
        var number = ProviderText(item, "id");
        _ = ValidateNumber(number);
        var state = ProviderText(item, "state") switch { "OPEN" => PullRequestState.Open, "MERGED" => PullRequestState.Merged,
            "DECLINED" or "SUPERSEDED" => PullRequestState.Closed, _ => PullRequestState.Unknown };
        return new(repo.Provider, repo.Owner + "/" + repo.Name, number, ProviderText(item, "title"), repo.WebUrl + "/pull-requests/" + number,
            state, BbActor(ProviderObject(item, "author")), ProviderText(ProviderObject(ProviderObject(item, "source"), "branch"), "name"),
            ProviderText(ProviderObject(ProviderObject(item, "destination"), "branch"), "name"), false, [],
            ProviderArray(ProviderObject(item, "reviewers")).Select(BbIdentity).Where(id => id.Length > 0).ToArray(),
            PullRequestCheckState.Unknown, ProviderDate(item, "updated_on"), state is PullRequestState.Closed or PullRequestState.Merged ? ProviderDate(item, "updated_on") : null);
    }
    private async Task<ListPullRequestsResult> ListBitbucketAsync(SourceControlRepository repo, ListPullRequestsRequest request, CancellationToken token)
    {
        var filters = request.Filters ?? new();
        ValidatePullRequestFilters(filters);
        if (request.State == PullRequestState.Draft || filters.Draft != PullRequestDraftFilter.Any || filters.Involvement != PullRequestInvolvement.All ||
            filters.Review != PullRequestReviewFilter.Any || filters.Checks != PullRequestChecksFilter.Any || !string.IsNullOrWhiteSpace(filters.Author) ||
            filters.LabelGroups?.Count > 0 || filters.ExcludedLabels?.Count > 0)
            throw ReviewError("This Bitbucket list supports state, source branch and title search. Use the inbox for loaded-page author filters; labels and drafts are unavailable.");
        var states = request.State switch { null => "state=OPEN&state=MERGED&state=DECLINED&state=SUPERSEDED", PullRequestState.Closed => "state=DECLINED&state=SUPERSEDED",
            PullRequestState.Merged => "state=MERGED", PullRequestState.Open => "state=OPEN", _ => throw ReviewError("Select a supported pull request state.") };
        var path = BbRepositoryPath(repo) + "/pullrequests";
        var terms = new List<string>();
        if (!string.IsNullOrWhiteSpace(filters.Query)) terms.Add("title ~ " + JsonSerializer.Serialize(filters.Query.Trim()));
        if (!string.IsNullOrWhiteSpace(request.SourceBranch)) terms.Add("source.branch.name = " + JsonSerializer.Serialize(request.SourceBranch));
        var result = await _bitbucket.JsonAsync(path + "?pagelen=100&page=" + (request.Offset / 100 + 1).ToString(CultureInfo.InvariantCulture) + "&sort=-updated_on&" + states +
            (terms.Count == 0 ? "" : "&q=" + Uri.EscapeDataString(string.Join(" AND ", terms))), token: token).ConfigureAwait(false);
        var values = BbValues(result);
        var next = BbNext(result, path);
        return new(repo, values.Select(item => ParseBitbucketPr(repo, item)).ToArray(), next is null ? null : request.Offset + 100);
    }
    private static JsonElement[] BbValues(JsonElement value)
    {
        if (ProviderObject(value, "values").ValueKind != JsonValueKind.Array) throw ReviewError("Bitbucket returned an invalid collection.");
        var values = ProviderArray(ProviderObject(value, "values"));
        if (values.Length > 100) throw ReviewError("Bitbucket returned an oversized page.");
        return values;
    }
    private static string? BbNext(JsonElement result, string endpoint)
    {
        var next = ProviderText(result, "next");
        if (next.Length == 0) return null;
        var expected = BitbucketCloudClient.Resolve(endpoint);
        if (next.Length > 4096 || !Uri.TryCreate(next, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Authority != expected.Authority ||
            uri.AbsolutePath != expected.AbsolutePath || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw ReviewError("Bitbucket returned an invalid next-page destination.");
        return uri.PathAndQuery["/2.0/".Length..];
    }
    private async Task<SourceControlOperationResult> CreateBitbucketPrAsync(SourceControlRepository repo, string workspace, CreatePullRequestRequest request, CancellationToken token)
    {
        if (request.IsDraft) return Rejected("Draft creation is unavailable for Bitbucket. Clear Create as draft first.", request.OperationId);
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 256 || request.Body is null || request.Body.Length > PullRequestReviewDefaults.MaximumDescriptionCharacters)
            return Rejected("Enter a bounded pull request title and description.", request.OperationId);
        var source = request.SourceBranch ?? await CurrentBranchAsync(workspace, token).ConfigureAwait(false);
        var destination = request.TargetBranch ?? repo.DefaultBranch;
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination) || source.Length > 1024 || destination.Length > 1024 || source.Any(char.IsControl) || destination.Any(char.IsControl))
            return Rejected("Valid source and target branches are required.", request.OperationId);
        try
        {
            var result = await _bitbucket.JsonAsync(BbRepositoryPath(repo) + "/pullrequests", "POST", new { title = request.Title, description = request.Body,
                source = new { branch = new { name = source } }, destination = new { branch = new { name = destination } }, close_source_branch = false }, token).ConfigureAwait(false);
            var pr = ParseBitbucketPr(repo, result);
            return new(true, "Created " + pr.Url, repo);
        }
        catch (Exception) { return new(false, "Bitbucket PR creation could not be confirmed. Inspect the repository before creating again.", repo, State: CommandReceiptState.DispatchUncertain); }
    }

    private async Task<string> PublishBitbucketRepositoryAsync(PublishHostedRepositoryRequest request, CancellationToken token)
    {
        var repo = new SourceControlRepository(SourceControlProvider.Bitbucket, "bitbucket.org", request.Owner, request.RepositoryName,
            "https://bitbucket.org/" + request.Owner + "/" + request.RepositoryName, "", "main", true);
        var item = await _bitbucket.JsonAsync(BbRepositoryPath(repo), request.ResumeExisting ? "GET" : "POST",
            request.ResumeExisting ? null : new { scm = "git", is_private = request.IsPrivate }, token).ConfigureAwait(false);
        if (!string.Equals(ProviderText(item, "full_name"), request.Owner + "/" + request.RepositoryName, StringComparison.OrdinalIgnoreCase))
            throw new PublicationFailure("Bitbucket returned a different repository. No remote was changed and nothing was pushed.");
        return JsonSerializer.Serialize(new { clone_url = repo.WebUrl + ".git", default_branch = ProviderText(ProviderObject(item, "mainbranch"), "name") });
    }
}
