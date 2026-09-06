using PiStation.Protocol.Identifiers;

namespace PiStation.Protocol.Models;

public sealed record PiSessionCandidate(string Path, string Title, string ProjectDirectory, string Revision, int EntryCount, DateTimeOffset ModifiedUtc);
public sealed record BrowsePiSessionsRequest(string? Directory = null);
public sealed record PiSessionBrowserResult(string Directory, IReadOnlyList<PiSessionCandidate> Sessions, bool IsTruncated, int SkippedFiles);
public sealed record PiSessionTreeEntry(string Id, string? ParentId, int Depth, string Kind, string Preview, bool IsActiveBranch, bool CanFork);
public sealed record PiSessionSnapshot(ThreadId ThreadId, string Path, string Revision, string? LeafId,
    IReadOnlyList<PiSessionTreeEntry> Entries, int TotalEntries, int ActiveMessageCount, PiModelSelection? Model,
    string? ThinkingLevel, long? TotalTokens, decimal? Cost, bool IsTruncated);
public sealed record CopyPiSessionRequest(Guid OperationId, ProjectId ProjectId, string? SourcePath = null,
    ThreadId? SourceThreadId = null, string? EntryId = null, string? ExpectedRevision = null, string? Title = null);
public enum PiSessionExportFormat { Jsonl, Html }
public sealed record ExportPiSessionRequest(ThreadId ThreadId, string DestinationPath, PiSessionExportFormat Format);
public sealed record PiSessionExportResult(string Path, long Bytes);
