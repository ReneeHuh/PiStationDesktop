using System.Text.Json;
using System.Globalization;
using PiStation.Protocol;
using PiStation.Protocol.Commands;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Serialization;
using PiStation.Protocol.Streaming;

namespace PiStation.Protocol.Tests;

public sealed class ProtocolSerializationTests
{
    [Fact]
    public void PullRequestManagementDraftRoundTripsExpectedTextAndPendingPayload()
    {
        var request = new ManagePullRequestRequest(new(new(ProjectId.New()), "github.com/owner/repo", "42", "head"),
            PullRequestManagementAction.EditDetails, Title: "New title", Body: "New description", ExpectedTitle: "Old title", ExpectedBody: "Old description");
        var draft = new PullRequestReviewDraft("github.com/owner/repo", "42", "head", "Unsent review", PullRequestReviewEvent.Comment, [],
            CommandId.New(), PendingAction: "Manage", Management: new("New title", "New description", "Old title", "Old description", PendingRequest: request));
        var json = JsonSerializer.Serialize(draft, ProtocolJsonContext.Default.PullRequestReviewDraft);
        var loaded = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.PullRequestReviewDraft);
        Assert.Equal(draft.Management, loaded?.Management);
        Assert.Equal(draft.PendingOperationId, loaded?.PendingOperationId);
        Assert.Equal("Unsent review", loaded?.Body);
    }

    [Fact]
    public void PullRequestCheckoutRoundTripsCapturedWorkspaceRevisionAndModel()
    {
        var request = new CreatePullRequestReviewThreadRequest(
            new(new(ProjectId.New(), ThreadId.New()), "github.com/owner/repo", "42", new string('a', 40)),
            new("provider", "model"), PiThinkingLevel.High);
        var json = JsonSerializer.Serialize(request, ProtocolJsonContext.Default.CreatePullRequestReviewThreadRequest);
        Assert.Equal(request, JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.CreatePullRequestReviewThreadRequest));
    }

    [Fact]
    public void ClosedCommandUnionRoundTripsWithStringIdentifiers()
    {
        var request = new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            EnvironmentId.Parse("environment-1"),
            ClientId.Parse("client-1"),
            CommandId.Parse("command-1"),
            ThreadId.Parse("thread-1"),
            ProjectionEpoch.Parse("epoch-1"),
            null,
            new ThreadStartTurnCommand("hello"));

        var json = JsonSerializer.Serialize(request, ProtocolJsonContext.Default.ExecuteThreadCommandRequest);
        var roundTrip = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ExecuteThreadCommandRequest);

        Assert.NotNull(roundTrip);
        Assert.Equal(request.EnvironmentId, roundTrip.EnvironmentId);
        Assert.IsType<ThreadStartTurnCommand>(roundTrip.Command);
        Assert.Contains("\"environmentId\":\"environment-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"$type\":\"threadStartTurn\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RestartRuntimeCommandRoundTripsThroughTheClosedUnion()
    {
        var request = new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            EnvironmentId.Parse("environment-1"),
            ClientId.Parse("client-1"),
            CommandId.Parse("command-restart"),
            ThreadId.Parse("thread-1"),
            ProjectionEpoch.Parse("epoch-1"),
            null,
            new ThreadRestartRuntimeCommand());

        var json = JsonSerializer.Serialize(request, ProtocolJsonContext.Default.ExecuteThreadCommandRequest);
        var roundTrip = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ExecuteThreadCommandRequest);

        Assert.NotNull(roundTrip);
        Assert.IsType<ThreadRestartRuntimeCommand>(roundTrip.Command);
        Assert.Contains("\"$type\":\"threadRestartRuntime\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposerCompactionAndInboxCommandsRoundTripThroughProtocolTwentyOne()
    {
        var compactRequest = CreateRequest(new ThreadCompactContextCommand("Keep decisions and open questions"));
        var inboxRequest = CreateRequest(new ThreadSetSnoozedCommand(
            7,
            DateTimeOffset.Parse("2026-09-05T12:00:00Z", CultureInfo.InvariantCulture)));
        ThreadEvent @event = new ContextCompactionChangedEvent(new ContextCompactionProjection(
            ContextCompactionState.Completed,
            "manual",
            12_000,
            2_400,
            0.02m,
            "Compacted context",
            null,
            DateTimeOffset.Parse("2026-09-04T12:00:00Z", CultureInfo.InvariantCulture)));

        var compactJson = JsonSerializer.Serialize(compactRequest, ProtocolJsonContext.Default.ExecuteThreadCommandRequest);
        var inboxJson = JsonSerializer.Serialize(inboxRequest, ProtocolJsonContext.Default.ExecuteThreadCommandRequest);
        var eventJson = JsonSerializer.Serialize(@event, ProtocolJsonContext.Default.ThreadEvent);

        Assert.Equal(
            "Keep decisions and open questions",
            Assert.IsType<ThreadCompactContextCommand>(JsonSerializer.Deserialize(
                compactJson,
                ProtocolJsonContext.Default.ExecuteThreadCommandRequest)?.Command).CustomInstructions);
        Assert.Equal(
            7,
            Assert.IsType<ThreadSetSnoozedCommand>(JsonSerializer.Deserialize(
                inboxJson,
                ProtocolJsonContext.Default.ExecuteThreadCommandRequest)?.Command).ExpectedRevision);
        Assert.Equal(
            ContextCompactionState.Completed,
            Assert.IsType<ContextCompactionChangedEvent>(JsonSerializer.Deserialize(
                eventJson,
                ProtocolJsonContext.Default.ThreadEvent)).Compaction.State);
        Assert.Contains("\"$type\":\"threadCompactContext\"", compactJson, StringComparison.Ordinal);
        Assert.Contains("\"$type\":\"contextCompactionChanged\"", eventJson, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckpointCommandAndEventRoundTripThroughClosedUnions()
    {
        var request = new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            EnvironmentId.Parse("environment-1"),
            ClientId.Parse("client-1"),
            CommandId.Parse("command-revert"),
            ThreadId.Parse("thread-1"),
            ProjectionEpoch.Parse("epoch-1"),
            null,
            new ThreadRevertCheckpointCommand(2));
        var checkpoint = new ThreadCheckpoint(
            TurnId.Parse("turn-3"),
            3,
            "refs/pistation/checkpoints/thread/turn/3",
            ThreadCheckpointStatus.Ready,
            [new ThreadCheckpointFile("src/App.cs", 7, 2)],
            "entry-4",
            "entry-6",
            DateTimeOffset.Parse("2026-09-04T12:00:00Z", CultureInfo.InvariantCulture));
        ThreadEvent @event = new CheckpointCapturedEvent(checkpoint);

        var requestJson = JsonSerializer.Serialize(request, ProtocolJsonContext.Default.ExecuteThreadCommandRequest);
        var eventJson = JsonSerializer.Serialize(@event, ProtocolJsonContext.Default.ThreadEvent);
        var restoredRequest = JsonSerializer.Deserialize(
            requestJson,
            ProtocolJsonContext.Default.ExecuteThreadCommandRequest);
        var restoredEvent = JsonSerializer.Deserialize(eventJson, ProtocolJsonContext.Default.ThreadEvent);

        Assert.Equal(2, Assert.IsType<ThreadRevertCheckpointCommand>(restoredRequest?.Command).TurnCount);
        var restoredCheckpoint = Assert.IsType<CheckpointCapturedEvent>(restoredEvent).Checkpoint;
        Assert.Equal(checkpoint.TurnId, restoredCheckpoint.TurnId);
        Assert.Equal(checkpoint.TurnCount, restoredCheckpoint.TurnCount);
        Assert.Equal(checkpoint.CheckpointRef, restoredCheckpoint.CheckpointRef);
        Assert.Equal(checkpoint.Status, restoredCheckpoint.Status);
        Assert.Equal(checkpoint.Files, restoredCheckpoint.Files);
        Assert.Equal(checkpoint.PiEntryIdBeforeTurn, restoredCheckpoint.PiEntryIdBeforeTurn);
        Assert.Equal(checkpoint.PiEntryIdAfterTurn, restoredCheckpoint.PiEntryIdAfterTurn);
        Assert.Contains("\"$type\":\"threadRevertCheckpoint\"", requestJson, StringComparison.Ordinal);
        Assert.Contains("\"$type\":\"checkpointCaptured\"", eventJson, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceGitCommandsRoundTripThroughTheClosedUnion()
    {
        var request = new ExecuteWorkspaceGitCommandRequest(
            ProtocolVersion.Current,
            EnvironmentId.Parse("environment-1"),
            ClientId.Parse("client-1"),
            CommandId.Parse("git-command-1"),
            new WorkspaceTarget(ProjectId.Parse("project-1"), ThreadId.Parse("thread-1")),
            new GitRunActionCommand(
                GitActionKind.CommitPush,
                "Ship it",
                ["src/App.cs", "README.md"]),
            "0123456789abcdef",
            "feature/test",
            "status-token");

        var json = JsonSerializer.Serialize(
            request,
            ProtocolJsonContext.Default.ExecuteWorkspaceGitCommandRequest);
        var restored = JsonSerializer.Deserialize(
            json,
            ProtocolJsonContext.Default.ExecuteWorkspaceGitCommandRequest);

        var command = Assert.IsType<GitRunActionCommand>(restored?.Command);
        Assert.Equal(GitActionKind.CommitPush, command.Action);
        Assert.Equal(["src/App.cs", "README.md"], command.FilePaths);
        Assert.Equal(request.Target, restored.Target);
        Assert.Equal("status-token", restored.ExpectedStatusToken);
        Assert.Contains("\"$type\":\"gitRunAction\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void InteractionCommandsRoundTripThroughTheClosedUnion()
    {
        var request = new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            EnvironmentId.Parse("environment-1"),
            ClientId.Parse("client-1"),
            CommandId.Parse("command-approval"),
            ThreadId.Parse("thread-1"),
            ProjectionEpoch.Parse("epoch-1"),
            TurnId.Parse("turn-1"),
            new ThreadRespondToApprovalCommand(
                InteractionId.Parse("approval-1"),
                ApprovalDecision.Approve));

        var json = JsonSerializer.Serialize(request, ProtocolJsonContext.Default.ExecuteThreadCommandRequest);
        var roundTrip = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ExecuteThreadCommandRequest);

        var command = Assert.IsType<ThreadRespondToApprovalCommand>(roundTrip?.Command);
        Assert.Equal(InteractionId.Parse("approval-1"), command.InteractionId);
        Assert.Equal(ApprovalDecision.Approve, command.Decision);
        Assert.Contains("\"$type\":\"threadRespondToApproval\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PiConfigurationCommandAndCapabilitySnapshotRoundTrip()
    {
        var request = CreateRequest(new ThreadUpdatePiConfigurationCommand(
            3,
            new PiModelSelection("openai", "gpt-5.6"),
            PiThinkingLevel.High,
            null));
        var configuration = new ThreadPiConfiguration(
            request.EnvironmentId,
            request.ThreadId,
            new PiModelSelection("openai", "gpt-5.6"),
            PiThinkingLevel.High,
            null,
            4,
            DateTimeOffset.Parse("2026-09-02T12:00:00Z", CultureInfo.InvariantCulture));
        var snapshot = new ThreadPiConfigurationSnapshot(
            configuration,
            new PiConfigurationCapabilities(
                [new PiModelCapability("openai", "gpt-5.6", "GPT-5.6", true, 200_000)],
                [PiThinkingLevel.Off, PiThinkingLevel.High],
                []),
            configuration.Model,
            PiThinkingLevel.High,
            null);

        var requestJson = JsonSerializer.Serialize(
            request,
            ProtocolJsonContext.Default.ExecuteThreadCommandRequest);
        var snapshotJson = JsonSerializer.Serialize(
            snapshot,
            ProtocolJsonContext.Default.ThreadPiConfigurationSnapshot);
        var restoredRequest = JsonSerializer.Deserialize(
            requestJson,
            ProtocolJsonContext.Default.ExecuteThreadCommandRequest);
        var restoredSnapshot = JsonSerializer.Deserialize(
            snapshotJson,
            ProtocolJsonContext.Default.ThreadPiConfigurationSnapshot);

        var command = Assert.IsType<ThreadUpdatePiConfigurationCommand>(restoredRequest?.Command);
        Assert.Equal(3, command.ExpectedRevision);
        Assert.Equal(configuration.Model, command.Model);
        Assert.Equal(PiThinkingLevel.High, command.ThinkingLevel);
        Assert.NotNull(restoredSnapshot);
        Assert.Equal(configuration, restoredSnapshot.Configuration);
        Assert.Equal("gpt-5.6", Assert.Single(restoredSnapshot.Capabilities.Models).ModelId);
        Assert.Equal(200_000, Assert.Single(restoredSnapshot.Capabilities.Models).ContextWindow);
        Assert.Equal([PiThinkingLevel.Off, PiThinkingLevel.High], restoredSnapshot.Capabilities.ThinkingLevels);
        Assert.Empty(restoredSnapshot.Capabilities.RuntimeModes);
        Assert.Contains("\"$type\":\"threadUpdatePiConfiguration\"", requestJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ThreadLifecycleCommandsAndSearchContractsRoundTrip()
    {
        var rename = CreateRequest(new ThreadRenameCommand(3, "Release plan"));
        var archive = CreateRequest(new ThreadSetArchivedCommand(4, true));
        var pin = CreateRequest(new ThreadSetPinnedCommand(5, true));
        var descriptor = new ThreadDescriptor(
            rename.EnvironmentId,
            rename.ThreadId,
            ProjectId.Parse("project-1"),
            "Release plan",
            "pi-session-1",
            null,
            DateTimeOffset.Parse("2026-09-02T12:00:00Z", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-09-02T12:01:00Z", CultureInfo.InvariantCulture),
            6,
            true,
            true);
        var searchRequest = new SearchThreadsRequest(descriptor.ProjectId, "release", true, 12);
        var searchResult = new SearchThreadsResult([descriptor], true);

        var renameJson = JsonSerializer.Serialize(rename, ProtocolJsonContext.Default.ExecuteThreadCommandRequest);
        var archiveJson = JsonSerializer.Serialize(archive, ProtocolJsonContext.Default.ExecuteThreadCommandRequest);
        var pinJson = JsonSerializer.Serialize(pin, ProtocolJsonContext.Default.ExecuteThreadCommandRequest);
        var searchRequestJson = JsonSerializer.Serialize(
            searchRequest,
            ProtocolJsonContext.Default.SearchThreadsRequest);
        var searchResultJson = JsonSerializer.Serialize(
            searchResult,
            ProtocolJsonContext.Default.SearchThreadsResult);

        Assert.IsType<ThreadRenameCommand>(JsonSerializer.Deserialize(
            renameJson,
            ProtocolJsonContext.Default.ExecuteThreadCommandRequest)?.Command);
        Assert.IsType<ThreadSetArchivedCommand>(JsonSerializer.Deserialize(
            archiveJson,
            ProtocolJsonContext.Default.ExecuteThreadCommandRequest)?.Command);
        Assert.IsType<ThreadSetPinnedCommand>(JsonSerializer.Deserialize(
            pinJson,
            ProtocolJsonContext.Default.ExecuteThreadCommandRequest)?.Command);
        Assert.Equal(searchRequest, JsonSerializer.Deserialize(
            searchRequestJson,
            ProtocolJsonContext.Default.SearchThreadsRequest));
        var restoredResult = JsonSerializer.Deserialize(
            searchResultJson,
            ProtocolJsonContext.Default.SearchThreadsResult);
        var restoredThread = Assert.Single(restoredResult!.Threads);
        Assert.Equal(6, restoredThread.Revision);
        Assert.True(restoredThread.IsArchived);
        Assert.True(restoredThread.IsPinned);
        Assert.True(restoredResult.IsTruncated);
        Assert.Contains("\"$type\":\"threadRename\"", renameJson, StringComparison.Ordinal);
        Assert.Contains("\"$type\":\"threadSetArchived\"", archiveJson, StringComparison.Ordinal);
        Assert.Contains("\"$type\":\"threadSetPinned\"", pinJson, StringComparison.Ordinal);
    }

    [Fact]
    public void DraftCommandAndProjectionRoundTripWithStableIdentity()
    {
        var request = new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            EnvironmentId.Parse("environment-1"),
            ClientId.Parse("client-1"),
            CommandId.Parse("command-draft"),
            ThreadId.Parse("thread-1"),
            null,
            null,
            new ThreadSaveDraftCommand(DraftId.Parse("draft-1"), 2, "unfinished prompt"));

        var json = JsonSerializer.Serialize(request, ProtocolJsonContext.Default.ExecuteThreadCommandRequest);
        var roundTrip = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ExecuteThreadCommandRequest);
        var command = Assert.IsType<ThreadSaveDraftCommand>(roundTrip?.Command);
        var draft = new ThreadDraft(
            request.EnvironmentId,
            request.ThreadId,
            command.DraftId,
            command.Text,
            3,
            DateTimeOffset.Parse("2026-09-01T12:00:00Z", CultureInfo.InvariantCulture),
            []);
        var draftJson = JsonSerializer.Serialize(draft, ProtocolJsonContext.Default.ThreadDraft);
        var draftRoundTrip = JsonSerializer.Deserialize(draftJson, ProtocolJsonContext.Default.ThreadDraft);

        Assert.NotNull(draftRoundTrip);
        Assert.Equal(draft.EnvironmentId, draftRoundTrip.EnvironmentId);
        Assert.Equal(draft.ThreadId, draftRoundTrip.ThreadId);
        Assert.Equal(draft.DraftId, draftRoundTrip.DraftId);
        Assert.Equal(draft.Text, draftRoundTrip.Text);
        Assert.Equal(draft.Revision, draftRoundTrip.Revision);
        Assert.Empty(draftRoundTrip.Attachments);
        Assert.Equal(2, command.ExpectedRevision);
        Assert.Contains("\"$type\":\"threadSaveDraft\"", json, StringComparison.Ordinal);
        Assert.Contains("\"draftId\":\"draft-1\"", draftJson, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentCommandsAndDraftMetadataRoundTrip()
    {
        var attachment = new DraftAttachment(
            EnvironmentId.Parse("environment-1"),
            ThreadId.Parse("thread-1"),
            DraftId.Parse("draft-1"),
            AttachmentId.Parse("attachment-1"),
            "diagram.png",
            "image/png",
            42,
            new string('A', 64),
            "C:\\host\\attachments\\diagram.png",
            DateTimeOffset.Parse("2026-09-01T12:00:00Z", CultureInfo.InvariantCulture));
        var draft = new ThreadDraft(
            attachment.EnvironmentId,
            attachment.ThreadId,
            attachment.DraftId,
            "inspect this",
            4,
            attachment.CreatedUtc,
            [attachment]);
        var request = new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            attachment.EnvironmentId,
            ClientId.Parse("client-1"),
            CommandId.Parse("command-1"),
            attachment.ThreadId,
            null,
            null,
            new ThreadRemoveDraftAttachmentCommand(
                attachment.DraftId,
                attachment.AttachmentId,
                draft.Revision));

        var draftJson = JsonSerializer.Serialize(draft, ProtocolJsonContext.Default.ThreadDraft);
        var requestJson = JsonSerializer.Serialize(request, ProtocolJsonContext.Default.ExecuteThreadCommandRequest);
        var restoredDraft = JsonSerializer.Deserialize(draftJson, ProtocolJsonContext.Default.ThreadDraft);
        var restoredRequest = JsonSerializer.Deserialize(
            requestJson,
            ProtocolJsonContext.Default.ExecuteThreadCommandRequest);

        Assert.NotNull(restoredDraft);
        Assert.Equal(draft.DraftId, restoredDraft.DraftId);
        Assert.Equal(draft.Text, restoredDraft.Text);
        Assert.Equal(draft.Revision, restoredDraft.Revision);
        Assert.Equal(attachment, Assert.Single(restoredDraft!.Attachments));
        Assert.IsType<ThreadRemoveDraftAttachmentCommand>(restoredRequest?.Command);
        Assert.Contains("\"$type\":\"threadRemoveDraftAttachment\"", requestJson, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentTurnAndReceiptLedClearCommandsRoundTrip()
    {
        var attachmentIds = new[]
        {
            AttachmentId.Parse("attachment-1"),
            AttachmentId.Parse("attachment-2"),
        };
        var start = CreateRequest(new ThreadStartTurnCommand(
            "inspect these",
            DraftId.Parse("draft-1"),
            7,
            attachmentIds));
        var clear = CreateRequest(new ThreadClearDraftCommand(
            DraftId.Parse("draft-1"),
            7,
            attachmentIds));

        var startJson = JsonSerializer.Serialize(start, ProtocolJsonContext.Default.ExecuteThreadCommandRequest);
        var clearJson = JsonSerializer.Serialize(clear, ProtocolJsonContext.Default.ExecuteThreadCommandRequest);
        var restoredStart = JsonSerializer.Deserialize(
            startJson,
            ProtocolJsonContext.Default.ExecuteThreadCommandRequest);
        var restoredClear = JsonSerializer.Deserialize(
            clearJson,
            ProtocolJsonContext.Default.ExecuteThreadCommandRequest);

        var startCommand = Assert.IsType<ThreadStartTurnCommand>(restoredStart?.Command);
        Assert.Equal(DraftId.Parse("draft-1"), startCommand.DraftId);
        Assert.Equal(7, startCommand.DraftRevision);
        Assert.Equal(attachmentIds, startCommand.AttachmentIds);
        var clearCommand = Assert.IsType<ThreadClearDraftCommand>(restoredClear?.Command);
        Assert.Equal(7, clearCommand.ExpectedRevision);
        Assert.Equal(attachmentIds, clearCommand.ExpectedAttachmentIds);
        Assert.Contains("\"$type\":\"threadClearDraft\"", clearJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectFileSearchContractsRoundTripWithoutAbsolutePaths()
    {
        var request = new SearchProjectFilesRequest(ProjectId.Parse("project-1"), "program", 12);
        var result = new SearchProjectFilesResult(
            request.ProjectId,
            request.Query,
            [new ProjectFileMatch("src/Program.cs", "Program.cs")],
            IsTruncated: true);

        var requestJson = JsonSerializer.Serialize(request, ProtocolJsonContext.Default.SearchProjectFilesRequest);
        var resultJson = JsonSerializer.Serialize(result, ProtocolJsonContext.Default.SearchProjectFilesResult);
        var restoredRequest = JsonSerializer.Deserialize(
            requestJson,
            ProtocolJsonContext.Default.SearchProjectFilesRequest);
        var restoredResult = JsonSerializer.Deserialize(
            resultJson,
            ProtocolJsonContext.Default.SearchProjectFilesResult);

        Assert.Equal(request, restoredRequest);
        Assert.Equal("src/Program.cs", Assert.Single(restoredResult!.Matches).RelativePath);
        Assert.True(restoredResult.IsTruncated);
        Assert.DoesNotContain(":\\", resultJson, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalSearchContractsRoundTripAcrossProjectsBranchesThreadsAndMessages()
    {
        var projectId = ProjectId.Parse("project-1");
        var threadId = ThreadId.Parse("thread-1");
        var request = new GlobalSearchRequest("release", 25, IncludeArchivedThreads: false);
        var result = new GlobalSearchResult(
            [
                new GlobalSearchItem(
                    GlobalSearchResultKind.Project,
                    projectId,
                    "Release Tools",
                    "Release Tools",
                    "C:\\workspace\\release-tools"),
                new GlobalSearchItem(
                    GlobalSearchResultKind.Branch,
                    projectId,
                    "Release Tools",
                    "release/v2",
                    "Branch in Release Tools",
                    BranchName: "release/v2"),
                new GlobalSearchItem(
                    GlobalSearchResultKind.Thread,
                    projectId,
                    "Release Tools",
                    "Prepare release",
                    "Release Tools • release/v2",
                    threadId,
                    "release/v2"),
                new GlobalSearchItem(
                    GlobalSearchResultKind.Message,
                    projectId,
                    "Release Tools",
                    "Prepare release",
                    "You in Release Tools",
                    threadId,
                    "release/v2",
                    "message-1",
                    MessageRole.User,
                    "Please prepare the release notes."),
            ],
            IsTruncated: true);

        var requestJson = JsonSerializer.Serialize(request, ProtocolJsonContext.Default.GlobalSearchRequest);
        var resultJson = JsonSerializer.Serialize(result, ProtocolJsonContext.Default.GlobalSearchResult);
        var restoredRequest = JsonSerializer.Deserialize(
            requestJson,
            ProtocolJsonContext.Default.GlobalSearchRequest);
        var restoredResult = JsonSerializer.Deserialize(
            resultJson,
            ProtocolJsonContext.Default.GlobalSearchResult);

        Assert.Equal(request, restoredRequest);
        Assert.NotNull(restoredResult);
        Assert.Equal(4, restoredResult.Items.Count);
        Assert.Equal(GlobalSearchResultKind.Project, restoredResult.Items[0].Kind);
        Assert.Equal("release/v2", restoredResult.Items[1].BranchName);
        Assert.Equal(threadId, restoredResult.Items[2].ThreadId);
        Assert.Equal(MessageRole.User, restoredResult.Items[3].MessageRole);
        Assert.Equal("message-1", restoredResult.Items[3].MessageId);
        Assert.True(restoredResult.IsTruncated);
    }

    [Fact]
    public void ProjectFileReadContractsRoundTripWithBoundedPreviewMetadata()
    {
        var request = new ReadProjectFileRequest(ProjectId.Parse("project-1"), "src/Program.cs", 4096);
        var result = new ReadProjectFileResult(
            request.ProjectId,
            request.RelativePath,
            "class Program;",
            ByteLength: 14,
            IsTruncated: false,
            IsBinary: false);

        var requestJson = JsonSerializer.Serialize(request, ProtocolJsonContext.Default.ReadProjectFileRequest);
        var resultJson = JsonSerializer.Serialize(result, ProtocolJsonContext.Default.ReadProjectFileResult);
        var restoredRequest = JsonSerializer.Deserialize(
            requestJson,
            ProtocolJsonContext.Default.ReadProjectFileRequest);
        var restoredResult = JsonSerializer.Deserialize(
            resultJson,
            ProtocolJsonContext.Default.ReadProjectFileResult);

        Assert.Equal(request, restoredRequest);
        Assert.Equal(result, restoredResult);
        Assert.DoesNotContain(":\\", resultJson, StringComparison.Ordinal);
    }

    [Fact]
    public void FullWorkspaceContractsRoundTripWithRelativePathsAndRevisions()
    {
        var projectId = ProjectId.Parse("project-1");
        var threadId = ThreadId.Parse("thread-1");
        var list = new ListProjectEntriesResult(
            projectId,
            [
                new ProjectWorkspaceEntry("src", "src", IsDirectory: true),
                new ProjectWorkspaceEntry("src/Program.cs", "Program.cs", IsDirectory: false, 42),
            ],
            IsTruncated: false);
        var search = new SearchProjectContentsResult(
            projectId,
            "Program",
            [new ProjectContentMatch(
                "src/Program.cs",
                "Program.cs",
                3,
                "class Program;",
                [new ProjectContentMatchRange(6, 13)])],
            IsTruncated: false);
        var save = new SaveProjectFileRequest(
            projectId,
            "src/Program.cs",
            "class Program;",
            "ABC123",
            threadId);

        var listJson = JsonSerializer.Serialize(list, ProtocolJsonContext.Default.ListProjectEntriesResult);
        var searchJson = JsonSerializer.Serialize(search, ProtocolJsonContext.Default.SearchProjectContentsResult);
        var saveJson = JsonSerializer.Serialize(save, ProtocolJsonContext.Default.SaveProjectFileRequest);

        var restoredList = JsonSerializer.Deserialize(
            listJson,
            ProtocolJsonContext.Default.ListProjectEntriesResult);
        var restoredSearch = JsonSerializer.Deserialize(
            searchJson,
            ProtocolJsonContext.Default.SearchProjectContentsResult);
        var restoredSave = JsonSerializer.Deserialize(
            saveJson,
            ProtocolJsonContext.Default.SaveProjectFileRequest);
        Assert.Equal("src/Program.cs", restoredList!.Entries[1].RelativePath);
        Assert.Equal(3, Assert.Single(restoredSearch!.Matches).LineNumber);
        Assert.Equal(save, restoredSave);
        Assert.DoesNotContain(":\\", listJson + searchJson + saveJson, StringComparison.Ordinal);
    }

    [Fact]
    public void GitChangesContractsRoundTripWithoutAbsolutePaths()
    {
        var request = new GetProjectChangesRequest(ProjectId.Parse("project-1"), 50);
        var result = new GetProjectChangesResult(
            request.ProjectId,
            IsRepository: true,
            BranchName: "main",
            UpstreamName: "origin/main",
            AheadCount: 1,
            BehindCount: 2,
            Changes: [new ProjectChange(
                "src/Program.cs",
                "Program.cs",
                null,
                GitFileStatus.None,
                GitFileStatus.Modified)],
            IsTruncated: false);
        var diffRequest = new GetProjectChangeDiffRequest(request.ProjectId, "src/Program.cs", 4096);
        var diffResult = new GetProjectChangeDiffResult(
            request.ProjectId,
            diffRequest.RelativePath,
            "@@ -1 +1 @@\n-old\n+new",
            HasStagedChanges: false,
            HasWorkingTreeChanges: true,
            IsUntracked: false,
            IsTruncated: false);

        var resultJson = JsonSerializer.Serialize(result, ProtocolJsonContext.Default.GetProjectChangesResult);
        var diffJson = JsonSerializer.Serialize(diffResult, ProtocolJsonContext.Default.GetProjectChangeDiffResult);

        var restoredResult = JsonSerializer.Deserialize(resultJson, ProtocolJsonContext.Default.GetProjectChangesResult);
        Assert.NotNull(restoredResult);
        Assert.Equal(result.ProjectId, restoredResult.ProjectId);
        Assert.Equal(result.BranchName, restoredResult.BranchName);
        Assert.Equal(result.UpstreamName, restoredResult.UpstreamName);
        Assert.Equal(result.AheadCount, restoredResult.AheadCount);
        Assert.Equal(result.BehindCount, restoredResult.BehindCount);
        Assert.Equal(result.Changes, restoredResult.Changes);
        Assert.Equal(diffResult, JsonSerializer.Deserialize(diffJson, ProtocolJsonContext.Default.GetProjectChangeDiffResult));
        Assert.DoesNotContain(":\\", resultJson, StringComparison.Ordinal);
        Assert.DoesNotContain(":\\", diffJson, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewDiscoveryContractsRoundTripWithoutProjectPaths()
    {
        var request = new DiscoverProjectPreviewServersRequest(ProjectId.Parse("project-1"));
        var result = new DiscoverProjectPreviewServersResult(
            request.ProjectId,
            DateTimeOffset.Parse("2026-09-04T12:00:00Z", CultureInfo.InvariantCulture),
            [new DiscoveredPreviewServer(
                "http://localhost:5173/",
                "localhost",
                5173,
                "http",
                "node",
                42)],
            IsTruncated: false);

        var requestJson = JsonSerializer.Serialize(
            request,
            ProtocolJsonContext.Default.DiscoverProjectPreviewServersRequest);
        var resultJson = JsonSerializer.Serialize(
            result,
            ProtocolJsonContext.Default.DiscoverProjectPreviewServersResult);

        Assert.Equal(request, JsonSerializer.Deserialize(
            requestJson,
            ProtocolJsonContext.Default.DiscoverProjectPreviewServersRequest));
        var restored = JsonSerializer.Deserialize(
            resultJson,
            ProtocolJsonContext.Default.DiscoverProjectPreviewServersResult);
        Assert.NotNull(restored);
        Assert.Equal(result.ProjectId, restored.ProjectId);
        Assert.Equal("http://localhost:5173/", Assert.Single(restored.Servers).Url);
        Assert.False(restored.IsTruncated);
        Assert.DoesNotContain(":\\", resultJson, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamEnvelopeUnionRoundTrips()
    {
        var projection = new ThreadProjection(
            EnvironmentId.Parse("environment-1"),
            ThreadId.Parse("thread-1"),
            ProjectionEpoch.Parse("epoch-1"),
            new Sequence(4),
            ThreadRuntimeState.Ready,
            null,
            [],
            [],
            "pi-session",
            null,
            null,
            null);
        ThreadEnvelope envelope = new ThreadSnapshotEnvelope(projection);

        var json = JsonSerializer.Serialize(envelope, ProtocolJsonContext.Default.ThreadEnvelope);
        var roundTrip = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ThreadEnvelope);

        var snapshot = Assert.IsType<ThreadSnapshotEnvelope>(roundTrip);
        Assert.Equal(new Sequence(4), snapshot.Sequence);
        Assert.Equal(ThreadRuntimeState.Ready, snapshot.Projection.RuntimeState);
    }

    [Fact]
    public void TimelineItemUnionRoundTripsInProjectionOrder()
    {
        var turnId = TurnId.Parse("turn-1");
        TimelineItem[] timeline =
        [
            new TurnBoundaryTimelineItem("boundary-1", turnId, TurnBoundaryKind.Started),
            new ThinkingTimelineItem("thinking-1", turnId, "message-1", "checking", true),
            new MessageTimelineItem(
                "message-1",
                turnId,
                "message-1",
                MessageRole.Assistant,
                "done",
                true),
            new ToolTimelineItem(
                "tool-1",
                turnId,
                "tool-call-1",
                "read",
                "{\"path\":\"README.md\"}",
                "file contents",
                ToolExecutionState.Completed),
            new StatusTimelineItem(
                "status-1",
                turnId,
                "Pi is working",
                TimelineActivityState.Running),
            new ErrorTimelineItem(
                "error-1",
                turnId,
                new ProtocolError(ProtocolErrorCodes.PiRuntimeCrashed, "Pi stopped")),
            new ApprovalTimelineItem(
                "approval-1",
                turnId,
                InteractionId.Parse("approval-1"),
                "Allow?",
                "Run command",
                null,
                InteractionState.Pending,
                null),
            new QuestionTimelineItem(
                "question-1",
                turnId,
                InteractionId.Parse("question-1"),
                QuestionInputKind.Select,
                "Choose",
                null,
                ["One", "Two"],
                null,
                null,
                InteractionState.Answered,
                "One"),
        ];
        var projection = new ThreadProjection(
            EnvironmentId.Parse("environment-1"),
            ThreadId.Parse("thread-1"),
            ProjectionEpoch.Parse("epoch-1"),
            Sequence.Initial,
            ThreadRuntimeState.Ready,
            null,
            timeline,
            [],
            "pi-session",
            null,
            null,
            null);

        var json = JsonSerializer.Serialize(projection, ProtocolJsonContext.Default.ThreadProjection);
        var roundTrip = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ThreadProjection);

        Assert.NotNull(roundTrip);
        Assert.Equal(
            "{\"path\":\"README.md\"}",
            Assert.IsType<ToolTimelineItem>(roundTrip.Timeline[3]).ArgumentsPreview);
        Assert.Collection(
            roundTrip.Timeline,
            item => Assert.IsType<TurnBoundaryTimelineItem>(item),
            item => Assert.IsType<ThinkingTimelineItem>(item),
            item => Assert.IsType<MessageTimelineItem>(item),
            item => Assert.IsType<ToolTimelineItem>(item),
            item => Assert.IsType<StatusTimelineItem>(item),
            item => Assert.IsType<ErrorTimelineItem>(item),
            item => Assert.IsType<ApprovalTimelineItem>(item),
            item => Assert.IsType<QuestionTimelineItem>(item));
        Assert.Contains("\"$type\":\"turnBoundary\"", json, StringComparison.Ordinal);
        Assert.Contains("\"$type\":\"thinking\"", json, StringComparison.Ordinal);
        Assert.Contains("\"$type\":\"status\"", json, StringComparison.Ordinal);
        Assert.Contains("\"$type\":\"error\"", json, StringComparison.Ordinal);
        Assert.Contains("\"$type\":\"approval\"", json, StringComparison.Ordinal);
        Assert.Contains("\"$type\":\"question\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"messages\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"tools\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TurnMetricsRoundTripWithTheSettledEventAndBoundary()
    {
        var turnId = TurnId.Parse("turn-metrics");
        var metrics = new TurnMetrics(
            1_250,
            new TokenUsage(120, 30, 40, 10, 12, 200),
            200,
            200_000);
        ThreadEvent @event = new TurnSettledEvent(turnId, metrics);
        TimelineItem boundary = new TurnBoundaryTimelineItem(
            "turn-turn-metrics-settled",
            turnId,
            TurnBoundaryKind.Settled,
            metrics);

        var eventJson = JsonSerializer.Serialize(@event, ProtocolJsonContext.Default.ThreadEvent);
        var boundaryJson = JsonSerializer.Serialize(boundary, ProtocolJsonContext.Default.TimelineItem);
        var restoredEvent = Assert.IsType<TurnSettledEvent>(JsonSerializer.Deserialize(
            eventJson,
            ProtocolJsonContext.Default.ThreadEvent));
        var restoredBoundary = Assert.IsType<TurnBoundaryTimelineItem>(JsonSerializer.Deserialize(
            boundaryJson,
            ProtocolJsonContext.Default.TimelineItem));

        Assert.Equal(metrics, restoredEvent.Metrics);
        Assert.Equal(metrics, restoredBoundary.Metrics);
        Assert.Contains("\"contextWindow\":200000", eventJson, StringComparison.Ordinal);
        Assert.Contains("\"totalTokens\":200", boundaryJson, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownRuntimeEventCarriesOnlyBoundedApplicationPreview()
    {
        ThreadEvent @event = new UnknownRuntimeEvent("future_pi_event", "bounded preview");

        var json = JsonSerializer.Serialize(@event, ProtocolJsonContext.Default.ThreadEvent);
        var roundTrip = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ThreadEvent);

        Assert.Equal(@event, Assert.IsType<UnknownRuntimeEvent>(roundTrip));
        Assert.DoesNotContain("rawJson", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActiveTurnQueueCommandsAndProjectionEventsRoundTrip()
    {
        var followUpRequest = CreateRequest(new ThreadQueueFollowUpCommand(
            "Then summarize",
            null,
            null,
            null));
        var deliveryRequest = CreateRequest(new ThreadSetQueueDeliveryModeCommand(
            QueuedMessageKind.FollowUp,
            QueueDeliveryMode.All));
        var interruptRequest = CreateRequest(new ThreadInterruptAgentCommand("workflow-1/agent-0"));
        var queue = new ThreadQueueProjection(
            [new QueuedMessageProjection(QueuedMessageKind.FollowUp, 1, "Then summarize")],
            QueueDeliveryMode.OneAtATime,
            QueueDeliveryMode.All,
            QueueDeliveryState.Queued,
            1,
            new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        var activity = new AgentActivityProjection(
            "workflow-1/agent-0",
            TurnId.Parse("turn-1"),
            "workflow-1",
            AgentActivityKind.Agent,
            AgentActivityState.Running,
            "reviewer",
            "Review the implementation",
            "Reading the diff",
            new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 4, 12, 0, 2, TimeSpan.Zero),
            null,
            3,
            new TokenUsage(100, 25, 10, 0, null, 135),
            "gpt-test",
            "high",
            null,
            null,
            1,
            0,
            true);
        ThreadEvent queueEvent = new QueueStateChangedEvent(queue);
        ThreadEvent activityEvent = new AgentActivityChangedEvent(activity);

        var followUpJson = JsonSerializer.Serialize(
            followUpRequest,
            ProtocolJsonContext.Default.ExecuteThreadCommandRequest);
        var deliveryJson = JsonSerializer.Serialize(
            deliveryRequest,
            ProtocolJsonContext.Default.ExecuteThreadCommandRequest);
        var interruptJson = JsonSerializer.Serialize(
            interruptRequest,
            ProtocolJsonContext.Default.ExecuteThreadCommandRequest);
        var queueJson = JsonSerializer.Serialize(queueEvent, ProtocolJsonContext.Default.ThreadEvent);
        var activityJson = JsonSerializer.Serialize(activityEvent, ProtocolJsonContext.Default.ThreadEvent);

        Assert.IsType<ThreadQueueFollowUpCommand>(JsonSerializer.Deserialize(
            followUpJson,
            ProtocolJsonContext.Default.ExecuteThreadCommandRequest)?.Command);
        Assert.IsType<ThreadSetQueueDeliveryModeCommand>(JsonSerializer.Deserialize(
            deliveryJson,
            ProtocolJsonContext.Default.ExecuteThreadCommandRequest)?.Command);
        Assert.IsType<ThreadInterruptAgentCommand>(JsonSerializer.Deserialize(
            interruptJson,
            ProtocolJsonContext.Default.ExecuteThreadCommandRequest)?.Command);
        var restoredQueue = Assert.IsType<QueueStateChangedEvent>(JsonSerializer.Deserialize(
            queueJson,
            ProtocolJsonContext.Default.ThreadEvent)).Queue;
        Assert.Equal(queue.SteeringMode, restoredQueue.SteeringMode);
        Assert.Equal(queue.FollowUpMode, restoredQueue.FollowUpMode);
        Assert.Equal(queue.DeliveryState, restoredQueue.DeliveryState);
        Assert.Equal(queue.PendingMessageCount, restoredQueue.PendingMessageCount);
        Assert.Equal(queue.Messages.ToArray(), restoredQueue.Messages.ToArray());
        Assert.Equal(activity, Assert.IsType<AgentActivityChangedEvent>(JsonSerializer.Deserialize(
            activityJson,
            ProtocolJsonContext.Default.ThreadEvent)).Activity);
    }

    [Fact]
    public void MissingRequiredCommandFieldIsRejected()
    {
        const string json = """
            {
              "protocolVersion": 4,
              "environmentId": "environment-1",
              "clientId": "client-1",
              "commandId": "command-1",
              "threadId": "thread-1",
              "command": { "$type": "threadStartTurn" }
            }
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ExecuteThreadCommandRequest));
    }

    [Fact]
    public void TerminalEnvelopeRoundTripsWithPolymorphicPayloads()
    {
        var descriptor = new TerminalSessionDescriptor(
            TerminalSessionId.Parse("terminal-1"),
            ProjectId.Parse("project-1"),
            "PowerShell 1",
            TerminalShellKind.PowerShell,
            "PowerShell",
            TerminalSessionState.Running,
            120,
            40,
            null,
            null,
            new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero),
            new Sequence(7));
        TerminalEnvelope envelope = new TerminalSnapshotEnvelope(descriptor, "ready> ");

        var json = JsonSerializer.Serialize(envelope, ProtocolJsonContext.Default.TerminalEnvelope);
        var roundTrip = Assert.IsType<TerminalSnapshotEnvelope>(JsonSerializer.Deserialize(
            json,
            ProtocolJsonContext.Default.TerminalEnvelope));

        Assert.Equal(descriptor, roundTrip.Descriptor);
        Assert.Equal("ready> ", roundTrip.BufferedOutput);
        Assert.Contains("\"$type\":\"snapshot\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AdvancedPullRequestPayloadsRoundTripWithoutLosingSelectedOptions()
    {
        var target = new PullRequestReviewTarget(new(ProjectId.New()), "github.com/owner/repo", "7", new string('a', 40));
        var request = new ManagePullRequestRequest(target, PullRequestManagementAction.Merge, OperationId: CommandId.New(),
            MergeMethod: PullRequestMergeMethod.Squash, UpdateMethod: PullRequestUpdateMethod.Rebase, Reaction: PullRequestReactionContent.Heart, Reacted: false);
        var json = JsonSerializer.Serialize(request, ProtocolJsonContext.Default.ManagePullRequestRequest);
        Assert.Equal(request, JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ManagePullRequestRequest));
        var workflows = new PullRequestWorkflowsResult(target, [new("91", "CI", "Awaiting approval", "https://github.com/owner/repo/actions/runs/91")], 2);
        var roundTrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(workflows, ProtocolJsonContext.Default.PullRequestWorkflowsResult), ProtocolJsonContext.Default.PullRequestWorkflowsResult)!;
        Assert.Equal(workflows.Target, roundTrip.Target);
        Assert.Equal(workflows.NextPage, roundTrip.NextPage);
        Assert.Equal(workflows.Workflows, roundTrip.Workflows);
    }

    private static ExecuteThreadCommandRequest CreateRequest(ThreadCommand command) => new(
        ProtocolVersion.Current,
        EnvironmentId.Parse("environment-1"),
        ClientId.Parse("client-1"),
        CommandId.New(),
        ThreadId.Parse("thread-1"),
        null,
        null,
        command);
}
