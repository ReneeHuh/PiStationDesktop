using PiStation.Protocol.Models;

namespace PiStation.Host.SourceControl;

public sealed partial class SourceControlHostingService
{
    internal static (string FileName, string[] Arguments) BuildFilteredListCommand(SourceControlRepository repository, ListPullRequestsRequest request, string? viewer = null)
    {
        var filters = request.Filters ?? new();
        ValidatePullRequestFilters(filters);
        if (request.State is { } state && !Enum.IsDefined(state)) throw ReviewError("Choose a valid pull request state.");
        var (tool, arguments) = BuildListCommand(repository.Provider, request.State, request.Offset, request.SourceBranch);
        if (repository.Provider != SourceControlProvider.GitHub)
        {
            if (HasPullRequestFilters(filters)) throw ReviewError("These advanced PR filters are currently supported for GitHub. Clear them to browse this provider.");
            if (repository.Provider == SourceControlProvider.GitLab)
                return (tool, [.. arguments, "--repo", $"{repository.Host}/{repository.Owner}/{repository.Name}"]);
            if (repository.Provider == SourceControlProvider.AzureDevOps)
            {
                var location = AzureReviewLocation(repository);
                return (tool, [.. arguments, "--organization", location.Organization, "--project", location.Project,
                    "--repository", Uri.UnescapeDataString(repository.Name), "--detect", "false", "--only-show-errors"]);
            }
            return (tool, arguments);
        }
        var qualifiers = new List<string>();
        if ((filters.Involvement != PullRequestInvolvement.All || filters.Author?.Trim() == "@me") && string.IsNullOrWhiteSpace(viewer))
            throw ReviewError("Sign in to GitHub before filtering by your account.");
        if (request.State == PullRequestState.Closed) qualifiers.Add("is:unmerged");
        if (request.State == PullRequestState.Draft) qualifiers.Add("draft:true");
        if (!string.IsNullOrWhiteSpace(filters.Query)) qualifiers.Add(SearchPhrase(filters.Query.Trim()));
        if (filters.Involvement == PullRequestInvolvement.Authored) qualifiers.Add("author:" + SearchPhrase(viewer!));
        if (filters.Involvement == PullRequestInvolvement.ReviewRequested) qualifiers.Add("review-requested:" + SearchPhrase(viewer!));
        if (!string.IsNullOrWhiteSpace(filters.Author)) qualifiers.Add("author:" + SearchPhrase(filters.Author.Trim() == "@me" ? viewer! : filters.Author.Trim()));
        if (filters.Draft != PullRequestDraftFilter.Any) qualifiers.Add(filters.Draft == PullRequestDraftFilter.Only ? "draft:true" : "draft:false");
        if (filters.Review != PullRequestReviewFilter.Any) qualifiers.Add("review:" + (filters.Review switch {
            PullRequestReviewFilter.Approved => "approved", PullRequestReviewFilter.ChangesRequested => "changes_requested", PullRequestReviewFilter.ReviewRequired => "required", _ => "none" }));
        if (filters.Checks != PullRequestChecksFilter.Any) qualifiers.Add("status:" + (filters.Checks switch {
            PullRequestChecksFilter.Passing => "success", PullRequestChecksFilter.Failing => "failure", _ => "pending" }));
        foreach (var group in filters.LabelGroups ?? []) qualifiers.Add("label:" + string.Join(',', group.Select(label => SearchPhrase(label.Trim()))));
        foreach (var label in filters.ExcludedLabels ?? []) qualifiers.Add("-label:" + SearchPhrase(label.Trim()));
        var result = arguments.ToList();
        result.AddRange(["--repo", $"{repository.Host}/{repository.Owner}/{repository.Name}"]);
        // Keep the existing search-free listing when no narrowing was requested: it also works before GitHub's search index catches up.
        if (qualifiers.Count > 0)
        {
            if (request.Offset >= 1000) throw ReviewError("GitHub search returns up to 1000 results. Narrow the filters to continue.");
            result[result.IndexOf("--limit") + 1] = Math.Min(1000, request.Offset + 101).ToString(System.Globalization.CultureInfo.InvariantCulture);
            result.AddRange(["--search", string.Join(' ', qualifiers) + " sort:updated-desc"]);
        }
        return (tool, result.ToArray());
    }

    private static string SearchPhrase(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    private static bool HasPullRequestFilters(PullRequestListFilters filters) => !string.IsNullOrWhiteSpace(filters.Query) || filters.Involvement != PullRequestInvolvement.All ||
        filters.Draft != PullRequestDraftFilter.Any || filters.Review != PullRequestReviewFilter.Any || filters.Checks != PullRequestChecksFilter.Any ||
        !string.IsNullOrWhiteSpace(filters.Author) || filters.LabelGroups?.Count > 0 || filters.ExcludedLabels?.Count > 0;

    private static void ValidatePullRequestFilters(PullRequestListFilters filters)
    {
        if (filters.Query is null || filters.Query.Length > 512 || filters.Query.Any(char.IsControl) ||
            !Enum.IsDefined(filters.Involvement) || !Enum.IsDefined(filters.Draft) || !Enum.IsDefined(filters.Review) || !Enum.IsDefined(filters.Checks) ||
            filters.Author is { } author && (author.Length > 100 || author.Any(char.IsControl)) ||
            filters.LabelGroups?.Count > 10 || filters.ExcludedLabels?.Count > 20)
            throw ReviewError("The pull request filters contain an invalid or oversized value.");
        foreach (var group in filters.LabelGroups ?? [])
        {
            if (group is null || group.Count is < 1 or > 10 || group.Any(InvalidLabel)) throw ReviewError("Use up to ten label groups with one to ten label names each.");
        }
        if (filters.ExcludedLabels?.Any(InvalidLabel) == true) throw ReviewError("An excluded label is invalid.");
        static bool InvalidLabel(string? label) => string.IsNullOrWhiteSpace(label) || label.Length > 100 || label.Any(char.IsControl);
    }
}
