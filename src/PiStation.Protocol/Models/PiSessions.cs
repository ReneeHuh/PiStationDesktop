using PiStation.Protocol.Identifiers;

namespace PiStation.Protocol.Models;

public sealed record PiSessionCandidate(string Path, string Title, string ProjectDirectory, string Revision, int EntryCount, DateTimeOffset ModifiedUtc);
public sealed record BrowsePiSessionsRequest(string? Directory = null, int Offset = 0);
public sealed record PiSessionBrowserResult(string Directory, IReadOnlyList<PiSessionCandidate> Sessions, bool IsTruncated, int SkippedFiles, int? NextOffset = null);
public sealed record PiSessionPageRequest(ThreadId ThreadId, int Offset = 0, int Limit = 1000, string? ExpectedRevision = null);
public sealed record PiSessionTreeEntry(string Id, string? ParentId, int Depth, string Kind, string Preview, bool IsActiveBranch, bool CanFork);
public sealed record PiSessionSnapshot(ThreadId ThreadId, string Path, string Revision, string? LeafId,
    IReadOnlyList<PiSessionTreeEntry> Entries, int TotalEntries, int ActiveMessageCount, PiModelSelection? Model,
    string? ThinkingLevel, long? TotalTokens, decimal? Cost, bool IsTruncated, int? NextOffset = null);
public sealed record CopyPiSessionRequest(Guid OperationId, ProjectId ProjectId, string? SourcePath = null,
    ThreadId? SourceThreadId = null, string? EntryId = null, string? ExpectedRevision = null, string? Title = null);
public enum PiSessionExportFormat { Jsonl, Html, Bundle }
public sealed record ExportPiSessionRequest(ThreadId ThreadId, string DestinationPath, PiSessionExportFormat Format);
public sealed record PiSessionExportResult(string Path, long Bytes);
public sealed record NavigatePiSessionRequest(Guid OperationId, ThreadId ThreadId, string EntryId, string ExpectedRevision,
    bool Summarize = false, string? CustomInstructions = null, bool ReplaceInstructions = false);
public sealed record NavigatePiSessionResult(PiSessionSnapshot Snapshot, bool Cancelled, string? EditorText = null);
public sealed record CancelPiSessionNavigationRequest(ThreadId ThreadId, Guid OperationId);
