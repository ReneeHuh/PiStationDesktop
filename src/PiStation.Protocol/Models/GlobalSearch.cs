using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Projections;

namespace PiStation.Protocol.Models;

public enum GlobalSearchResultKind
{
    Project,
    Branch,
    Thread,
    Message,
}

public sealed record GlobalSearchRequest(
    string Query,
    int MaximumResults = GlobalSearchDefaults.DefaultMaximumResults,
    bool IncludeArchivedThreads = true);

public sealed record GlobalSearchItem(
    GlobalSearchResultKind Kind,
    ProjectId ProjectId,
    string ProjectName,
    string Title,
    string Description,
    ThreadId? ThreadId = null,
    string? BranchName = null,
    string? MessageId = null,
    MessageRole? MessageRole = null,
    string? Snippet = null,
    DateTimeOffset? UpdatedUtc = null);

public sealed record GlobalSearchResult(
    IReadOnlyList<GlobalSearchItem> Items,
    bool IsTruncated);

public static class GlobalSearchDefaults
{
    public const int MaximumQueryLength = 200;
    public const int DefaultMaximumResults = 50;
    public const int MaximumResults = 100;
    public const int MaximumSnippetLength = 240;
}
