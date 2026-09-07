using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;
using PiStation.Protocol.Projections;

namespace PiStation.ClientRuntime;

public interface IEnvironmentClient : IAsyncDisposable
{
    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    EnvironmentConnectionState ConnectionState { get; }

    EnvironmentDescriptor? Descriptor { get; }

    PiConfigurationStore PiConfigurations { get; }

    ThreadMetadataStore ThreadMetadata { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectDescriptor>> ListProjectsAsync(CancellationToken cancellationToken = default);

    Task<ProjectDescriptor> AddProjectAsync(
        AddProjectRequest request,
        CancellationToken cancellationToken = default);

    Task RemoveProjectAsync(RemoveProjectRequest request, CancellationToken cancellationToken = default);

    Task<ProjectDescriptor> UpdateProjectDefaultsAsync(
        UpdateProjectDefaultsRequest request,
        CancellationToken cancellationToken = default);

    Task<ProjectDescriptor> SetProjectScriptsTrustAsync(
        SetProjectScriptsTrustRequest request,
        CancellationToken cancellationToken = default);

    Task<ProjectSetupScriptResult> RunProjectSetupScriptAsync(
        RunProjectSetupScriptRequest request,
        CancellationToken cancellationToken = default);

    Task<ProjectSetupScriptResult> RunProjectScriptAsync(
        RunProjectScriptRequest request,
        CancellationToken cancellationToken = default);

    Task<ComposerDiscoveryResult> GetComposerDiscoveryAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PromptStash>> ListPromptStashesAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default);

    Task<PromptStash> SavePromptStashAsync(
        SavePromptStashRequest request,
        CancellationToken cancellationToken = default);

    Task DeletePromptStashAsync(
        DeletePromptStashRequest request,
        CancellationToken cancellationToken = default);

    Task<ThreadDraft> RestorePromptStashAsync(ThreadId threadId, DraftId draftId, long expectedRevision, string stashId, CancellationToken cancellationToken = default);

    Task<SourceControlRepository> DetectSourceControlAsync(
        DetectSourceControlRequest request,
        CancellationToken cancellationToken = default);

    Task<PullRequestReviewSnapshot> GetPullRequestReviewAsync(GetPullRequestReviewRequest request, CancellationToken cancellationToken = default);

    Task<SourceControlOperationResult> SubmitPullRequestReviewAsync(SubmitPullRequestReviewRequest request, CancellationToken cancellationToken = default);

    Task<SourceControlOperationResult> ReplyPullRequestThreadAsync(ReplyPullRequestThreadRequest request, CancellationToken cancellationToken = default);

    Task<SourceControlOperationResult> SetPullRequestThreadResolvedAsync(SetPullRequestThreadResolvedRequest request, CancellationToken cancellationToken = default);

    Task<ListPullRequestsResult> ListPullRequestsAsync(
        ListPullRequestsRequest request,
        CancellationToken cancellationToken = default);

    Task<SourceControlOperationResult> CloneHostedRepositoryAsync(
        CloneHostedRepositoryRequest request,
        CancellationToken cancellationToken = default);

    Task<SourceControlOperationResult> PublishHostedRepositoryAsync(
        PublishHostedRepositoryRequest request,
        CancellationToken cancellationToken = default);

    Task<SourceControlOperationResult> CreatePullRequestAsync(
        CreatePullRequestRequest request,
        CancellationToken cancellationToken = default);

    Task<SourceControlOperationResult> MutatePullRequestAsync(
        MutatePullRequestRequest request,
        CancellationToken cancellationToken = default);

    Task<GeneratedSourceControlText> GenerateSourceControlTextAsync(
        GenerateSourceControlTextRequest request,
        CancellationToken cancellationToken = default);

    Task<DiagnosticsSnapshot> GetDiagnosticsAsync(CancellationToken cancellationToken = default);

    Task<PiRuntimeSetupResult> ConfigurePiRuntimeAsync(ConfigurePiRuntimeRequest request, CancellationToken cancellationToken = default);
    Task<PiResourcesSnapshot> ManagePiResourcesAsync(ManagePiResourcesRequest request, CancellationToken cancellationToken = default);
    Task<PiSessionBrowserResult> BrowsePiSessionsAsync(BrowsePiSessionsRequest request, CancellationToken cancellationToken = default);
    Task<PiSessionSnapshot> InspectPiSessionAsync(ThreadId threadId, CancellationToken cancellationToken = default);
    Task<ThreadDescriptor> CopyPiSessionAsync(CopyPiSessionRequest request, CancellationToken cancellationToken = default);
    Task<PiSessionExportResult> ExportPiSessionAsync(ExportPiSessionRequest request, CancellationToken cancellationToken = default);
    Task<PiSetupTerminalResult> StartPiSetupAsync(StartPiSetupRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HostingOperation>> ListHostingOperationsAsync(CancellationToken cancellationToken = default);

    Task<ExportDiagnosticsResult> ExportDiagnosticsAsync(
        ExportDiagnosticsRequest request,
        CancellationToken cancellationToken = default);

    Task<SearchProjectFilesResult> SearchProjectFilesAsync(
        SearchProjectFilesRequest request,
        CancellationToken cancellationToken = default);

    Task<ListProjectEntriesResult> ListProjectEntriesAsync(
        ListProjectEntriesRequest request,
        CancellationToken cancellationToken = default);

    Task<SearchProjectContentsResult> SearchProjectContentsAsync(
        SearchProjectContentsRequest request,
        CancellationToken cancellationToken = default);

    Task<ReadProjectFileResult> ReadProjectFileAsync(
        ReadProjectFileRequest request,
        CancellationToken cancellationToken = default);

    Task<ReadProjectFileAssetResult> ReadProjectFileAssetAsync(
        ReadProjectFileAssetRequest request,
        CancellationToken cancellationToken = default);

    Task<SaveProjectFileResult> SaveProjectFileAsync(
        SaveProjectFileRequest request,
        CancellationToken cancellationToken = default);

    Task<OpenProjectFileInEditorResult> OpenProjectFileInEditorAsync(
        OpenProjectFileInEditorRequest request,
        CancellationToken cancellationToken = default);

    Task<GetProjectChangesResult> GetProjectChangesAsync(
        GetProjectChangesRequest request,
        CancellationToken cancellationToken = default);

    Task<GetProjectChangeDiffResult> GetProjectChangeDiffAsync(
        GetProjectChangeDiffRequest request,
        CancellationToken cancellationToken = default);

    Task<ListGitRefsResult> ListGitRefsAsync(
        ListGitRefsRequest request,
        CancellationToken cancellationToken = default);

    Task<ListGitWorktreesResult> ListGitWorktreesAsync(
        ListGitWorktreesRequest request,
        CancellationToken cancellationToken = default);

    Task<ExecuteWorkspaceGitCommandResult> ExecuteWorkspaceGitCommandAsync(
        WorkspaceTarget target,
        WorkspaceGitCommand command,
        string? expectedHeadSha = null,
        string? expectedBranchName = null,
        string? expectedStatusToken = null,
        CommandId? commandId = null,
        CancellationToken cancellationToken = default);

    Task<ExecuteWorkspaceGitCommandResult?> GetWorkspaceGitCommandResultAsync(
        CommandId commandId,
        CancellationToken cancellationToken = default);

    Task<GetThreadCheckpointDiffResult> GetThreadCheckpointDiffAsync(
        GetThreadCheckpointDiffRequest request,
        CancellationToken cancellationToken = default);

    Task<DiscoverProjectPreviewServersResult> DiscoverProjectPreviewServersAsync(
        DiscoverProjectPreviewServersRequest request,
        CancellationToken cancellationToken = default);

    Task<TerminalSessionDescriptor> StartTerminalSessionAsync(
        StartTerminalSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TerminalSessionDescriptor>> ListTerminalSessionsAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default);

    Task WriteTerminalInputAsync(
        WriteTerminalInputRequest request,
        CancellationToken cancellationToken = default);

    Task<TerminalSessionDescriptor> ResizeTerminalSessionAsync(
        ResizeTerminalSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<TerminalSessionDescriptor> StopTerminalSessionAsync(
        StopTerminalSessionRequest request,
        CancellationToken cancellationToken = default);

    Task CloseTerminalSessionAsync(
        CloseTerminalSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ThreadDescriptor>> ListThreadsAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default);

    Task<ThreadDescriptor> GetThreadAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default);

    Task<SearchThreadsResult> SearchThreadsAsync(
        SearchThreadsRequest request,
        CancellationToken cancellationToken = default);

    Task<GlobalSearchResult> SearchGlobalAsync(
        GlobalSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<ThreadDescriptor> CreateThreadAsync(
        CreateThreadRequest request,
        CancellationToken cancellationToken = default);

    Task DeleteThreadAsync(DeleteThreadRequest request, CancellationToken cancellationToken = default);

    Task<ApplyThreadBulkOperationResult> ApplyThreadBulkOperationAsync(
        ApplyThreadBulkOperationRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ThreadDescriptor>> SetThreadPinnedOrderAsync(
        SetThreadPinnedOrderRequest request,
        CancellationToken cancellationToken = default);

    Task<ThreadDescriptor> LinkThreadPullRequestAsync(
        LinkThreadPullRequestRequest request,
        CancellationToken cancellationToken = default);

    Task<ThreadLifecycleUpdateResult> RenameThreadAsync(
        ThreadId threadId,
        long expectedRevision,
        string title,
        CancellationToken cancellationToken = default);

    Task<ThreadLifecycleUpdateResult> SetThreadArchivedAsync(
        ThreadId threadId,
        long expectedRevision,
        bool isArchived,
        CancellationToken cancellationToken = default);

    Task<ThreadLifecycleUpdateResult> SetThreadPinnedAsync(
        ThreadId threadId,
        long expectedRevision,
        bool isPinned,
        CancellationToken cancellationToken = default);

    Task<ThreadLifecycleUpdateResult> SetThreadSettledAsync(
        ThreadId threadId,
        long expectedRevision,
        bool isSettled,
        CancellationToken cancellationToken = default);

    Task<ThreadLifecycleUpdateResult> SetThreadSnoozedAsync(
        ThreadId threadId,
        long expectedRevision,
        DateTimeOffset? snoozedUntilUtc,
        CancellationToken cancellationToken = default);

    Task<ThreadLifecycleUpdateResult> SetThreadPinnedOrderAsync(
        ThreadId threadId,
        long expectedRevision,
        long pinnedOrder,
        CancellationToken cancellationToken = default);

    Task<ThreadLifecycleUpdateResult> RegenerateThreadTitleAsync(
        ThreadId threadId,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<CommandReceipt> CompactThreadContextAsync(
        ThreadId threadId,
        string? customInstructions = null,
        ProjectionEpoch? expectedProjectionEpoch = null,
        CancellationToken cancellationToken = default);

    Task<ThreadDraft> GetThreadDraftAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default);

    Task<ThreadPiConfigurationSnapshot> GetThreadPiConfigurationAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default);

    Task<ThreadPiConfigurationUpdateResult> UpdateThreadPiConfigurationAsync(
        ThreadId threadId,
        long expectedRevision,
        PiModelSelection? model,
        PiThinkingLevel? thinkingLevel,
        string? runtimeModeId,
        CancellationToken cancellationToken = default);

    Task<ThreadDraftSaveResult> SaveThreadDraftAsync(
        ThreadId threadId, DraftId draftId, long expectedRevision, string text,
        CancellationToken cancellationToken = default);

    Task<ThreadDraftSaveResult> SaveThreadDraftAsync(
        ThreadId threadId,
        DraftId draftId,
        long expectedRevision,
        string text,
        IReadOnlyList<ComposerContext>? context,
        CancellationToken cancellationToken = default);

    Task<DraftAttachmentUploadResult> UploadDraftAttachmentAsync(
        ThreadId threadId,
        DraftId draftId,
        long expectedRevision,
        string fileName,
        string? mediaType,
        Stream content,
        long byteLength,
        CancellationToken cancellationToken = default);

    Task<DraftAttachmentRemoveResult> RemoveDraftAttachmentAsync(
        ThreadId threadId,
        DraftId draftId,
        AttachmentId attachmentId,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<ThreadDraftClearResult> ClearThreadDraftAsync(
        ThreadId threadId,
        DraftId draftId,
        long expectedRevision,
        IReadOnlyList<AttachmentId> expectedAttachmentIds,
        CancellationToken cancellationToken = default);

    Task<CommandReceipt> ManagePlanAsync(ThreadId threadId, string action, long expectedRevision, string? text = null,
        CancellationToken cancellationToken = default);

    Task<CommandReceipt> StartTurnAsync(
        ThreadId threadId,
        string prompt,
        ProjectionEpoch? expectedProjectionEpoch = null,
        DraftId? draftId = null,
        long? draftRevision = null,
        IReadOnlyList<AttachmentId>? attachmentIds = null,
        CancellationToken cancellationToken = default);

    Task<CommandReceipt> QueueSteeringAsync(
        ThreadId threadId,
        string prompt,
        ProjectionEpoch? expectedProjectionEpoch = null,
        TurnId? expectedTurnId = null,
        DraftId? draftId = null,
        long? draftRevision = null,
        IReadOnlyList<AttachmentId>? attachmentIds = null,
        CancellationToken cancellationToken = default);

    Task<CommandReceipt> QueueFollowUpAsync(
        ThreadId threadId,
        string prompt,
        ProjectionEpoch? expectedProjectionEpoch = null,
        TurnId? expectedTurnId = null,
        DraftId? draftId = null,
        long? draftRevision = null,
        IReadOnlyList<AttachmentId>? attachmentIds = null,
        CancellationToken cancellationToken = default);

    Task<CommandReceipt> ClearTurnQueueAsync(
        ThreadId threadId,
        ProjectionEpoch? expectedProjectionEpoch = null,
        TurnId? expectedTurnId = null,
        CancellationToken cancellationToken = default);

    Task<CommandReceipt> RefreshTurnQueueAsync(
        ThreadId threadId,
        ProjectionEpoch? expectedProjectionEpoch = null,
        CancellationToken cancellationToken = default);

    Task<CommandReceipt> SetQueueDeliveryModeAsync(
        ThreadId threadId,
        QueuedMessageKind kind,
        QueueDeliveryMode mode,
        ProjectionEpoch? expectedProjectionEpoch = null,
        CancellationToken cancellationToken = default);

    Task<CommandReceipt> StopTurnAsync(
        ThreadId threadId,
        ProjectionEpoch? expectedProjectionEpoch = null,
        TurnId? expectedTurnId = null,
        CancellationToken cancellationToken = default);

    Task<CommandReceipt> InterruptAgentAsync(
        ThreadId threadId,
        string activityId,
        ProjectionEpoch? expectedProjectionEpoch = null,
        TurnId? expectedTurnId = null,
        CancellationToken cancellationToken = default);

    Task<CommandReceipt> RestartThreadAsync(
        ThreadId threadId,
        ProjectionEpoch? expectedProjectionEpoch = null,
        CancellationToken cancellationToken = default);

    Task<CommandReceipt> RevertThreadToCheckpointAsync(
        ThreadId threadId,
        int turnCount,
        ProjectionEpoch? expectedProjectionEpoch = null,
        CancellationToken cancellationToken = default);

    Task<CommandReceipt> RespondToApprovalAsync(
        ThreadId threadId,
        InteractionId interactionId,
        ApprovalDecision decision,
        ProjectionEpoch? expectedProjectionEpoch = null,
        TurnId? expectedTurnId = null,
        CancellationToken cancellationToken = default);

    Task<CommandReceipt> AnswerQuestionAsync(
        ThreadId threadId,
        InteractionId interactionId,
        string answer,
        ProjectionEpoch? expectedProjectionEpoch = null,
        TurnId? expectedTurnId = null,
        CancellationToken cancellationToken = default);

    Task<CommandReceipt> CancelInteractionAsync(
        ThreadId threadId,
        InteractionId interactionId,
        ProjectionEpoch? expectedProjectionEpoch = null,
        TurnId? expectedTurnId = null,
        CancellationToken cancellationToken = default);

    Task<CommandReceipt?> GetCommandReceiptAsync(
        CommandId commandId,
        CancellationToken cancellationToken = default);

    ThreadSubscription SubscribeThread(ThreadId threadId);

    TerminalSubscription SubscribeTerminal(TerminalSessionId terminalSessionId);
}
