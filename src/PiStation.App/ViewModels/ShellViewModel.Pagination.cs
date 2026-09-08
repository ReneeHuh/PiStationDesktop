using PiStation.ClientRuntime;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    private bool _loadingMorePullRequests;
    private long _pullRequestReadGeneration;

    public async Task RefreshPullRequestsAsync(CancellationToken cancellationToken = default)
    {
        var project = SelectedProject;
        var thread = SelectedThread?.ThreadId;
        var version = Settings.PullRequestQueryVersion;
        var generation = Interlocked.Increment(ref _pullRequestReadGeneration);
        if (project is null)
        {
            RunOnUiThread(() => Settings.ClearSourceControl("Select a project to inspect source-control hosting."));
            return;
        }
        var client = RequireClient();
        try
        {
            var result = await client.ListPullRequestsAsync(new(new(project.ProjectId, thread), Settings.PullRequestStateFilter, Filters: Settings.PullRequestFilters), cancellationToken).ConfigureAwait(false);
            RunOnUiThread(() => { if (Current()) Settings.ApplyPullRequests(result); });
        }
        catch (Exception exception)
        {
            RunOnUiThread(() => { if (Current()) Settings.ClearSourceControl($"Hosting unavailable: {exception.Message}"); });
        }
        bool Current() => generation == _pullRequestReadGeneration && version == Settings.PullRequestQueryVersion &&
            project.ProjectId == SelectedProject?.ProjectId && thread == SelectedThread?.ThreadId && ReferenceEquals(client, _client);
    }

    public async Task LoadMorePullRequestsAsync()
    {
        var project = SelectedProject;
        var thread = SelectedThread?.ThreadId;
        if (project is null || Settings.NextPullRequestOffset is not { } offset || _loadingMorePullRequests) return;
        _loadingMorePullRequests = true;
        var version = Settings.PullRequestQueryVersion;
        var generation = _pullRequestReadGeneration;
        var client = RequireClient();
        try
        {
            var page = await client.ListPullRequestsAsync(new(new(project.ProjectId, thread), Settings.PullRequestStateFilter, offset, Filters: Settings.PullRequestFilters));
            if (generation == _pullRequestReadGeneration && version == Settings.PullRequestQueryVersion && ReferenceEquals(client, _client) && SelectedProject?.ProjectId == project.ProjectId && SelectedThread?.ThreadId == thread)
                Settings.ApplyPullRequests(page, append: true);
        }
        catch (Exception error) { ReportRuntimeError(error); }
        finally { _loadingMorePullRequests = false; }
    }

    private bool _loadingMoreFiles;
    public async Task LoadMoreWorkbenchFilesAsync()
    {
        var project = SelectedProject;
        var thread = SelectedThread?.ThreadId;
        if (project is null || WorkbenchFiles.NextOffset is not { } offset || _loadingMoreFiles) return;
        var query = WorkbenchFiles.SearchQuery;
        var mode = WorkbenchFiles.SearchMode;
        var caseSensitive = WorkbenchFiles.CaseSensitive;
        var wholeWord = WorkbenchFiles.WholeWord;
        var regex = WorkbenchFiles.UseRegularExpression;
        var scanOffset = WorkbenchFiles.NextScanOffset;
        var searchGeneration = _workbenchFileSearchCancellation;
        _loadingMoreFiles = true;
        var truncated = false;
        bool Current() => ReferenceEquals(searchGeneration, _workbenchFileSearchCancellation) && IsCurrentFileSearch(project.ProjectId, thread, query, mode, caseSensitive, wholeWord, regex);
        try
        {
            if (mode == WorkspaceFileSearchMode.Contents)
            {
                var page = await RequireClient().SearchProjectContentsAsync(new(project.ProjectId, query, ContentSearchDefaults.MaximumResults, caseSensitive, wholeWord, regex, thread, offset, scanOffset));
                if (!Current()) return;
                foreach (var match in page.Matches.Where(match => !WorkbenchFiles.ContentMatches.Any(existing => existing.RelativePath == match.RelativePath && existing.LineNumber == match.LineNumber))) WorkbenchFiles.ContentMatches.Add(match);
                WorkbenchFiles.NextOffset = page.NextOffset;
                WorkbenchFiles.NextScanOffset = page.NextScanOffset;
                truncated = page.IsTruncated;
            }
            else if (string.IsNullOrEmpty(query))
            {
                var page = await RequireClient().ListProjectEntriesAsync(new(project.ProjectId, ThreadId: thread, Offset: offset));
                if (!Current()) return;
                WorkbenchFiles.AppendEntries(page.Entries); WorkbenchFiles.NextOffset = page.NextOffset;
                truncated = page.IsTruncated;
            }
            else
            {
                var page = await RequireClient().SearchProjectFilesAsync(new(project.ProjectId, query, FileSearchDefaults.MaximumResults, thread, offset, scanOffset));
                if (!Current()) return;
                foreach (var match in page.Matches.Where(match => !WorkbenchFiles.Files.Any(existing => existing.RelativePath == match.RelativePath))) WorkbenchFiles.Files.Add(match);
                WorkbenchFiles.NextOffset = page.NextOffset;
                WorkbenchFiles.NextScanOffset = page.NextScanOffset;
                truncated = page.IsTruncated;
            }
            WorkbenchFiles.Status = WorkbenchFiles.CanLoadMore ? "More results loaded. Continue loading to see the next page." : truncated ? "The workspace scan limit was reached; narrow the search or increase the host scan limit." : "All results loaded.";
        }
        catch (Exception error) { ReportRuntimeError(error); }
        finally { _loadingMoreFiles = false; }
    }

    public async Task LoadMoreGitRefsAsync()
    {
        var project = SelectedProject;
        var thread = SelectedThread?.ThreadId;
        if (project is null || WorkbenchChanges.NextRefsCursor is not { } cursor || WorkbenchChanges.IsBusy) return;
        WorkbenchChanges.IsBusy = true;
        try
        {
            var page = await RequireClient().ListGitRefsAsync(new(new(project.ProjectId, thread), Cursor: cursor));
            if (SelectedProject?.ProjectId == project.ProjectId && SelectedThread?.ThreadId == thread) WorkbenchChanges.ApplyRefs(page, append: true);
        }
        catch (Exception error) { ReportRuntimeError(error); }
        finally { WorkbenchChanges.IsBusy = false; }
    }
    private int? _threadSearchNextOffset;
    private bool _loadingMoreThreads;
    public bool CanLoadMoreThreads => _threadSearchNextOffset is not null && !_loadingMoreThreads;
    public async Task LoadMoreThreadsAsync()
    {
        var project = SelectedProject;
        if (project is null || _threadSearchNextOffset is not { } offset || _loadingMoreThreads) return;
        var query = ThreadSearchQuery;
        var archived = IsShowingArchivedThreads;
        var version = Volatile.Read(ref _threadSearchVersion);
        _loadingMoreThreads = true; OnPropertyChanged(nameof(CanLoadMoreThreads));
        try
        {
            var result = await RequireClient().SearchThreadsAsync(new(project.ProjectId, query, archived, ThreadLifecycleDefaults.MaximumSearchLimit, offset));
            if (SelectedProject?.ProjectId != project.ProjectId || query != ThreadSearchQuery || archived != IsShowingArchivedThreads || version != Volatile.Read(ref _threadSearchVersion)) return;
            var visible = ThreadInbox.Select(result.Threads, archived ? ThreadInboxShelf.Archived : InboxShelf, DateTimeOffset.UtcNow);
            foreach (var thread in visible.Where(thread => !Threads.Any(existing => existing.ThreadId == thread.ThreadId))) Threads.Add(thread);
            Replace(Threads, SortSidebarThreads(Threads).ToArray());
            _threadSearchNextOffset = result.NextOffset;
            ThreadListStatus = $"{Threads.Count} threads loaded";
        }
        catch (Exception error) { ReportRuntimeError(error); }
        finally { _loadingMoreThreads = false; OnPropertyChanged(nameof(CanLoadMoreThreads)); }
    }
}
