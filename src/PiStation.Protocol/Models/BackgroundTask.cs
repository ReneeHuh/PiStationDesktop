using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Receipts;

namespace PiStation.Protocol.Models;

public sealed record SubmitBackgroundTaskRequest(ThreadId SourceThreadId, DraftId DraftId, long DraftRevision,
    ThreadWorkspaceMode WorkspaceMode, string? BaseBranch = null);

public enum BackgroundTaskState { Preparing, Dispatching, Accepted, Rejected, Uncertain }

public sealed record BackgroundTaskResult(string SubmissionId, ThreadId? TaskThreadId,
    BackgroundTaskState State, string Message, CommandReceipt? Receipt = null);
