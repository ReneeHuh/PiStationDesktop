using PiStation.Protocol.Identifiers;

namespace PiStation.Protocol.Models;

public sealed record SearchProjectFilesRequest(
    ProjectId ProjectId,
    string Query,
    int MaximumResults = FileSearchDefaults.DefaultMaximumResults,
    ThreadId? ThreadId = null,
    int Offset = 0,
    int ScanOffset = 0);

public sealed record ProjectFileMatch(
    string RelativePath,
    string FileName);

public sealed record SearchProjectFilesResult(
    ProjectId ProjectId,
    string Query,
    IReadOnlyList<ProjectFileMatch> Matches,
    bool IsTruncated,
    int? NextOffset = null,
    int NextScanOffset = 0);

public sealed record ListProjectEntriesRequest(
    ProjectId ProjectId,
    int MaximumResults = WorkspaceEntryDefaults.DefaultMaximumResults,
    ThreadId? ThreadId = null,
    int Offset = 0);

public sealed record ProjectWorkspaceEntry(
    string RelativePath,
    string Name,
    bool IsDirectory,
    long ByteLength = 0);

public sealed record ListProjectEntriesResult(
    ProjectId ProjectId,
    IReadOnlyList<ProjectWorkspaceEntry> Entries,
    bool IsTruncated,
    int? NextOffset = null);

public sealed record SearchProjectContentsRequest(
    ProjectId ProjectId,
    string Query,
    int MaximumResults = ContentSearchDefaults.DefaultMaximumResults,
    bool CaseSensitive = false,
    bool WholeWord = false,
    bool UseRegularExpression = false,
    ThreadId? ThreadId = null,
    int Offset = 0,
    int ScanOffset = 0);

public sealed record ProjectContentMatchRange(int Start, int End);

public sealed record ProjectContentMatch(
    string RelativePath,
    string FileName,
    int LineNumber,
    string LineContent,
    IReadOnlyList<ProjectContentMatchRange> MatchRanges);

public sealed record SearchProjectContentsResult(
    ProjectId ProjectId,
    string Query,
    IReadOnlyList<ProjectContentMatch> Matches,
    bool IsTruncated,
    int? NextOffset = null,
    int NextScanOffset = 0);

public sealed record ReadProjectFileRequest(
    ProjectId ProjectId,
    string RelativePath,
    int MaximumBytes = FileReadDefaults.DefaultMaximumBytes,
    ThreadId? ThreadId = null);

public sealed record ReadProjectFileResult(
    ProjectId ProjectId,
    string RelativePath,
    string Content,
    long ByteLength,
    bool IsTruncated,
    bool IsBinary,
    string Revision = "",
    string MediaType = "text/plain");

public sealed record ReadProjectFileAssetRequest(
    ProjectId ProjectId,
    string RelativePath,
    int MaximumBytes = FileAssetDefaults.DefaultMaximumBytes,
    ThreadId? ThreadId = null);

public sealed record ReadProjectFileAssetResult(
    ProjectId ProjectId,
    string RelativePath,
    byte[] Content,
    long ByteLength,
    string MediaType,
    string Revision);

public sealed record SaveProjectFileRequest(
    ProjectId ProjectId,
    string RelativePath,
    string Content,
    string ExpectedRevision,
    ThreadId? ThreadId = null);

public sealed record SaveProjectFileResult(
    ProjectId ProjectId,
    string RelativePath,
    long ByteLength,
    string Revision);

public sealed record OpenProjectFileInEditorRequest(
    ProjectId ProjectId,
    string RelativePath,
    int? LineNumber = null,
    int? ColumnNumber = null,
    ThreadId? ThreadId = null);

public sealed record OpenProjectFileInEditorResult(
    ProjectId ProjectId,
    string RelativePath,
    string AbsolutePath,
    string EditorName);

public static class FileSearchDefaults
{
    public const int DefaultMaximumResults = 25;
    public const int MaximumResults = 100;
    public const int MaximumQueryLength = 200;
}

public static class WorkspaceEntryDefaults
{
    public const int DefaultMaximumResults = 20_000;
    public const int MaximumResults = 50_000;
}

public static class ContentSearchDefaults
{
    public const int DefaultMaximumResults = 200;
    public const int MaximumResults = 500;
    public const int MaximumQueryLength = 256;
    public const int MaximumFileBytes = 1024 * 1024;
    public const int MaximumLineCharacters = 4 * 1024;
}

public static class FileReadDefaults
{
    public const int DefaultMaximumBytes = 1024 * 1024;
    public const int MaximumBytes = 1024 * 1024;
    public const int MaximumRelativePathLength = 1024;
    public const int MaximumWriteBytes = 1024 * 1024;
}

public static class FileAssetDefaults
{
    public const int DefaultMaximumBytes = 64 * 1024 * 1024;
    public const int MaximumBytes = 64 * 1024 * 1024;
}
