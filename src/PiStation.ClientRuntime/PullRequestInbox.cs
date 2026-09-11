using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

public sealed record PullRequestInboxSource(string Id, string Name,
    Func<CancellationToken, Task<IReadOnlyList<ProjectDescriptor>>> ListProjects,
    Func<DetectSourceControlRequest, CancellationToken, Task<SourceControlRepository>> Detect,
    Func<ListPullRequestsRequest, CancellationToken, Task<ListPullRequestsResult>> List);

public sealed record PullRequestInboxQuery(PullRequestState? State = null, PullRequestListFilters? Filters = null,
    SourceControlProvider? Provider = null, string? Environment = null, string Repository = "");

public sealed record PullRequestInboxRow(string SourceId, string Environment, ProjectDescriptor Project,
    SourceControlRepository Repository, PullRequestDescriptor PullRequest)
{
    public WorkspaceTarget Target => new(Project.ProjectId, null);
    public string Key => string.Join('\n', SourceId, Project.ProjectId.Value, PullRequestReviewDefaults.RepositoryKey(Repository), PullRequest.Number);
    public string DisplayText => $"{Environment} · {Project.DisplayName} · {Repository.Host}/{Repository.Owner}/{Repository.Name} #{PullRequest.Number} · {PullRequest.Title} ({PullRequest.State})";
}

public sealed record PullRequestInboxRepository(PullRequestInboxSource Source, ProjectDescriptor Project,
    SourceControlRepository? Repository, IReadOnlyList<PullRequestInboxRow> Rows, int? NextOffset = 0,
    string? Error = null, string? Notice = null, int Pages = 0);

public sealed record PullRequestInboxPage(PullRequestInboxQuery Query, IReadOnlyList<PullRequestInboxRepository> Repositories,
    IReadOnlyList<string> DiscoveryErrors)
{
    public IReadOnlyList<PullRequestInboxRow> Rows => Repositories.SelectMany(repository => repository.Rows)
        .DistinctBy(row => row.Key).OrderByDescending(row => row.PullRequest.UpdatedUtc).ThenBy(row => row.Key, StringComparer.Ordinal).ToArray();
    public bool HasMore => Repositories.Any(repository => repository.NextOffset is not null && repository.Error is null);
    public bool HasFailures => DiscoveryErrors.Count > 0 || Repositories.Any(repository => repository.Error is not null);
    public string Summary => $"{Rows.Count} matching pull requests across {Repositories.Count} project repositories. " +
        (HasMore ? "More provider pages are available. " : "") +
        string.Join(Environment.NewLine, DiscoveryErrors.Concat(Repositories.SelectMany(repository =>
            new[] { repository.Error, repository.Notice }.Where(message => !string.IsNullOrEmpty(message))
                .Select(message => $"{repository.Source.Name} / {repository.Project.DisplayName}: {message}"))));
}

/// <summary>Immutable inbox pages keep concurrent refreshes and independent repository continuations separate.</summary>
public static class PullRequestInbox
{
    public const int MaximumRepositories = 100;
    public const int MaximumPages = 10;
    private const int Concurrency = 4;

    public static async Task<PullRequestInboxPage> StartAsync(IReadOnlyList<PullRequestInboxSource> sources,
        PullRequestInboxQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var discovered = await MapAsync(sources.Where(source => query.Environment is null || source.Id == query.Environment).ToArray(), async source =>
        {
            try { return (Source: source, Projects: await source.ListProjects(cancellationToken).ConfigureAwait(false), Error: (string?)null); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { return (Source: source, Projects: (IReadOnlyList<ProjectDescriptor>)[], Error: $"{source.Name}: project discovery failed. Refresh to retry."); }
        }, cancellationToken).ConfigureAwait(false);
        var repositories = discovered.SelectMany(result => result.Projects.DistinctBy(project => project.ProjectId)
            .Select(project => new PullRequestInboxRepository(result.Source, project, null, []))).ToArray();
        var errors = discovered.Where(result => result.Error is not null).Select(result => result.Error!).ToList();
        if (repositories.Length > MaximumRepositories) errors.Add($"Showing the first {MaximumRepositories} project repositories. Select an environment to narrow the inbox.");
        return await ContinueAsync(new(query, repositories.Take(MaximumRepositories).ToArray(), errors), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public static async Task<PullRequestInboxPage> ContinueAsync(PullRequestInboxPage previous, bool retryFailures = false,
        CancellationToken cancellationToken = default)
    {
        var repositories = await MapAsync(previous.Repositories, async held =>
        {
            if (held.NextOffset is null || (retryFailures ? held.Error is null : held.Error is not null)) return held;
            try
            {
                var target = new WorkspaceTarget(held.Project.ProjectId, null);
                var repository = await held.Source.Detect(new(target), cancellationToken).ConfigureAwait(false);
                if (held.Repository is not null && (repository.Provider != held.Repository.Provider ||
                    PullRequestReviewDefaults.RepositoryKey(repository) != PullRequestReviewDefaults.RepositoryKey(held.Repository)))
                    return held with { Error = "The repository changed. Refresh before continuing.", NextOffset = null };
                if (previous.Query.Provider is { } provider && repository.Provider != provider ||
                    !repository.WebUrl.Contains(previous.Query.Repository.Trim(), StringComparison.OrdinalIgnoreCase))
                    return held with { Repository = repository, NextOffset = null, Error = null };
                var filters = previous.Query.Filters ?? new();
                var limitation = FilterLimitation(repository.Provider, filters);
                if (limitation is not null) return held with { Repository = repository, Error = limitation };
                var serverFilters = repository.Provider == SourceControlProvider.GitHub ? filters : null;
                var page = await held.Source.List(new(target, previous.Query.State, held.NextOffset.Value, Filters: serverFilters), cancellationToken).ConfigureAwait(false);
                if (page.Repository.Provider != repository.Provider || PullRequestReviewDefaults.RepositoryKey(page.Repository) != PullRequestReviewDefaults.RepositoryKey(repository))
                    return held with { Error = "The repository changed while loading. Refresh before continuing.", NextOffset = null };
                if (page.NextOffset is { } next && (next <= held.NextOffset || next % 100 != 0))
                    return held with { Error = "The provider returned an invalid continuation. Refresh to retry.", NextOffset = null };
                var incoming = page.PullRequests.Where(pr => pr.Provider == repository.Provider && pr.Repository == $"{repository.Owner}/{repository.Name}")
                    .Where(pr => repository.Provider == SourceControlProvider.GitHub || Matches(pr, previous.Query.State, filters))
                    .Select(pr => new PullRequestInboxRow(held.Source.Id, held.Source.Name, held.Project, repository, pr));
                var rows = held.Rows.Concat(incoming).GroupBy(row => row.Key).Select(group => group.Last()).ToArray();
                var bounded = held.Pages + 1 >= MaximumPages && page.NextOffset is not null;
                var notice = repository.Provider == SourceControlProvider.GitHub ? page.Notice :
                    "Filters apply to loaded pages. Load more to search later pages. " + page.Notice;
                if (bounded) notice += $" Reached {MaximumPages} pages for this repository. Narrow the filters or open the provider website.";
                return held with { Repository = repository, Rows = rows, NextOffset = bounded ? null : page.NextOffset, Error = null, Notice = notice, Pages = held.Pages + 1 };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { return held with { Error = "Could not load this repository. Check its connection/authentication, then retry failed repositories." }; }
        }, cancellationToken).ConfigureAwait(false);
        return previous with { Repositories = repositories };
    }

    public static string? FilterLimitation(SourceControlProvider provider, PullRequestListFilters filters)
    {
        if (provider is not (SourceControlProvider.GitHub or SourceControlProvider.GitLab or SourceControlProvider.AzureDevOps or SourceControlProvider.Bitbucket)) return "Pull request browsing is unavailable for this provider.";
        if (provider == SourceControlProvider.GitHub) return null;
        if (provider == SourceControlProvider.AzureDevOps && (filters.LabelGroups?.Count > 0 || filters.ExcludedLabels?.Count > 0))
            return "PR label filtering is unavailable for Azure. Clear label filters to include this repository.";
        if (provider == SourceControlProvider.Bitbucket && (filters.Draft != PullRequestDraftFilter.Any || filters.LabelGroups?.Count > 0 || filters.ExcludedLabels?.Count > 0))
            return "PR labels and draft transitions are unavailable for Bitbucket. Clear those filters to include this repository.";
        if (filters.Involvement != PullRequestInvolvement.All || filters.Author?.Trim() == "@me" ||
            filters.Review != PullRequestReviewFilter.Any || filters.Checks != PullRequestChecksFilter.Any)
            return "Account involvement, @me, review-decision and check filters require GitHub. Clear those filters to include this repository.";
        return null;
    }

    internal static bool Matches(PullRequestDescriptor pr, PullRequestState? state, PullRequestListFilters filters) =>
        (state is null || pr.State == state || state == PullRequestState.Open && pr.State == PullRequestState.Draft) &&
        (filters.Draft == PullRequestDraftFilter.Any || pr.IsDraft == (filters.Draft == PullRequestDraftFilter.Only)) &&
        (string.IsNullOrWhiteSpace(filters.Query) || $"{pr.Number} {pr.Title} {pr.SourceBranch} {pr.TargetBranch}".Contains(filters.Query.Trim(), StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(filters.Author) || string.Equals(pr.Author, filters.Author.Trim(), StringComparison.OrdinalIgnoreCase)) &&
        (filters.LabelGroups ?? []).All(group => group.Any(label => pr.Labels.Contains(label.Trim(), StringComparer.OrdinalIgnoreCase))) &&
        !(filters.ExcludedLabels ?? []).Any(label => pr.Labels.Contains(label.Trim(), StringComparer.OrdinalIgnoreCase));

    private static async Task<TOut[]> MapAsync<TIn, TOut>(IReadOnlyList<TIn> items, Func<TIn, Task<TOut>> read, CancellationToken token)
    {
        using var gate = new SemaphoreSlim(Concurrency);
        return await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try { token.ThrowIfCancellationRequested(); return await read(item).ConfigureAwait(false); }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
    }
}
