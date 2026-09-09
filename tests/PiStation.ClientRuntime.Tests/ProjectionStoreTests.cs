using System.Globalization;
using PiStation.Host.Threads;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Streaming;

namespace PiStation.ClientRuntime.Tests;

public sealed class ProjectionStoreTests
{
    [Fact]
    public void PiShellOutputReplaysWithoutChangingTurnOrAppendingDuplicateOutput()
    {
        var initial = CreateProjection(EnvironmentId.New(), ThreadId.New(), ProjectionEpoch.New());
        var store = new ProjectionStore(initial.ThreadId);
        store.Apply(new ThreadSnapshotEnvelope(initial));
        var shell = new PiShellExecution(CommandId.New(), ClientId.New(), "echo hello", false,
            PiShellExecutionState.Running, "hello", false, null, null, null, DateTimeOffset.UtcNow);
        var envelope = new ThreadEventEnvelope(initial.EnvironmentId, initial.ThreadId, initial.ProjectionEpoch, new Sequence(1), new PiShellChangedEvent(shell, "shell-entry"));
        Assert.Equal(ProjectionApplyResult.Applied, store.Apply(envelope));
        Assert.Equal(ProjectionApplyResult.Ignored, store.Apply(envelope));
        Assert.Equal(shell, store.Current!.ShellExecution);
        Assert.Equal("shell-entry", store.Current.LastEntryId);
        Assert.Null(store.Current.CurrentTurnId);
        Assert.Empty(store.Current.Timeline);
        var completed = shell with { State = PiShellExecutionState.Completed, ExitCode = 0, CompletedUtc = DateTimeOffset.UtcNow };
        var restored = initial with { ProjectionEpoch = ProjectionEpoch.New(), ShellExecution = completed };
        store.Apply(new ThreadSnapshotEnvelope(restored));
        Assert.Equal(completed, store.Current.ShellExecution);
        Assert.Equal(ThreadProjectionReducer.Apply(initial, new PiShellChangedEvent(completed)).ShellExecution, store.Current.ShellExecution);
    }

    [Fact]
    public void AppliesOrderedEventsAndIgnoresDuplicates()
    {
        var environmentId = EnvironmentId.New();
        var threadId = ThreadId.New();
        var epoch = ProjectionEpoch.New();
        var store = new ProjectionStore(threadId);
        var snapshot = CreateProjection(environmentId, threadId, epoch);

        Assert.Equal(
            ProjectionApplyResult.Applied,
            store.Apply(new ThreadSnapshotEnvelope(snapshot)));
        var first = new ThreadEventEnvelope(
            environmentId,
            threadId,
            epoch,
            new Sequence(1),
            new RuntimeStateChangedEvent(ThreadRuntimeState.Running));
        Assert.Equal(ProjectionApplyResult.Applied, store.Apply(first));
        Assert.Equal(ProjectionApplyResult.Ignored, store.Apply(first));
        Assert.Equal(ThreadRuntimeState.Running, store.Current?.RuntimeState);
        Assert.Equal(new Sequence(1), store.Current?.Sequence);
    }

    [Fact]
    public void RequestsResyncForGapOrChangedEpoch()
    {
        var environmentId = EnvironmentId.New();
        var threadId = ThreadId.New();
        var epoch = ProjectionEpoch.New();
        var store = new ProjectionStore(threadId);
        store.Apply(new ThreadSnapshotEnvelope(CreateProjection(environmentId, threadId, epoch)));

        var gap = new ThreadEventEnvelope(
            environmentId,
            threadId,
            epoch,
            new Sequence(2),
            new RuntimeStateChangedEvent(ThreadRuntimeState.Running));
        var changedEpoch = gap with
        {
            ProjectionEpoch = ProjectionEpoch.New(),
            Sequence = new Sequence(1),
        };

        Assert.Equal(ProjectionApplyResult.ResyncRequired, store.Apply(gap));
        Assert.Equal(ProjectionApplyResult.ResyncRequired, store.Apply(changedEpoch));
        Assert.Equal(Sequence.Initial, store.Current?.Sequence);
    }

    [Fact]
    public void ReplacesAccumulatedToolOutput()
    {
        var environmentId = EnvironmentId.New();
        var threadId = ThreadId.New();
        var epoch = ProjectionEpoch.New();
        var store = new ProjectionStore(threadId);
        store.Apply(new ThreadSnapshotEnvelope(CreateProjection(environmentId, threadId, epoch)));
        store.Apply(new ThreadEventEnvelope(
            environmentId,
            threadId,
            epoch,
            new Sequence(1),
            new ToolStartedEvent(new ToolProjection(
                "tool-1",
                "read",
                "{\"path\":\"a\"}",
                "a",
                ToolExecutionState.Running))));
        store.Apply(new ThreadEventEnvelope(
            environmentId,
            threadId,
            epoch,
            new Sequence(2),
            new ToolOutputReplacedEvent("tool-1", "all output")));

        Assert.NotNull(store.Current);
        var tool = Assert.Single(store.Current.Tools);
        Assert.Equal("{\"path\":\"a\"}", tool.ArgumentsPreview);
        Assert.Equal("all output", tool.OutputPreview);
        Assert.IsType<ToolTimelineItem>(Assert.Single(store.Current.Timeline));
    }

    [Fact]
    public void BuildsOneOrderedTimelineForTurnMessagesThinkingToolsAndBoundaries()
    {
        var environmentId = EnvironmentId.New();
        var threadId = ThreadId.New();
        var epoch = ProjectionEpoch.New();
        var turnId = TurnId.New();
        var store = new ProjectionStore(threadId);
        var hostProjection = CreateProjection(environmentId, threadId, epoch);
        store.Apply(new ThreadSnapshotEnvelope(hostProjection));

        ThreadEvent[] events =
        [
            new TurnStartedEvent(turnId, "inspect the file"),
            new MessageStartedEvent(new MessageProjection(
                "assistant-1",
                MessageRole.Assistant,
                string.Empty,
                string.Empty,
                false)),
            new ContentDeltaEvent("assistant-1", 0, ContentKind.Thinking, "checking"),
            new ToolStartedEvent(new ToolProjection(
                "tool-1",
                "read",
                "{\"path\":\"README.md\"}",
                string.Empty,
                ToolExecutionState.Running)),
            new ContentDeltaEvent("assistant-1", 1, ContentKind.Text, "finished"),
            new CheckpointCapturedEvent(new ThreadCheckpoint(
                turnId,
                1,
                "refs/pistation/checkpoints/thread/turn/1",
                ThreadCheckpointStatus.Ready,
                [new ThreadCheckpointFile("README.md", 1, 1)],
                null,
                "entry-2",
                DateTimeOffset.Parse("2026-09-04T12:00:00Z", CultureInfo.InvariantCulture))),
            new TurnSettledEvent(
                turnId,
                new TurnMetrics(
                    1_250,
                    new TokenUsage(120, 30, 40, 10, 12, 200),
                    200,
                    200_000)),
        ];
        for (var index = 0; index < events.Length; index++)
        {
            var sequence = new Sequence(index + 1);
            store.Apply(new ThreadEventEnvelope(
                environmentId,
                threadId,
                epoch,
                sequence,
                events[index]));
            hostProjection = ThreadProjectionReducer.Apply(hostProjection, events[index]) with
            {
                Sequence = sequence,
            };
        }

        Assert.NotNull(store.Current);
        Assert.Equal(hostProjection.Timeline, store.Current.Timeline);
        Assert.Collection(
            store.Current.Timeline,
            item => Assert.Equal(TurnBoundaryKind.Started, Assert.IsType<TurnBoundaryTimelineItem>(item).Boundary),
            item => Assert.Equal(MessageRole.User, Assert.IsType<MessageTimelineItem>(item).Role),
            item => Assert.Equal("checking", Assert.IsType<ThinkingTimelineItem>(item).Text),
            item => Assert.Equal("finished", Assert.IsType<MessageTimelineItem>(item).Text),
            item => Assert.Equal("read", Assert.IsType<ToolTimelineItem>(item).ToolName),
            item => Assert.Equal(TurnBoundaryKind.Settled, Assert.IsType<TurnBoundaryTimelineItem>(item).Boundary));
        var metrics = Assert.IsType<TurnMetrics>(
            store.Current.Timeline.OfType<TurnBoundaryTimelineItem>().Last().Metrics);
        Assert.Equal(1_250, metrics.ElapsedMilliseconds);
        Assert.Equal(200, metrics.Usage?.TotalTokens);
        Assert.Equal(200_000, metrics.ContextWindow);
        Assert.Equal("entry-2", store.Current.LastEntryId);
        Assert.Equal(1, Assert.Single(store.Current.Checkpoints).TurnCount);
    }

    [Fact]
    public void AddsStatusAndErrorActivitiesToTheActiveTurn()
    {
        var environmentId = EnvironmentId.New();
        var threadId = ThreadId.New();
        var epoch = ProjectionEpoch.New();
        var turnId = TurnId.New();
        var store = new ProjectionStore(threadId);
        store.Apply(new ThreadSnapshotEnvelope(CreateProjection(environmentId, threadId, epoch)));
        store.Apply(new ThreadEventEnvelope(
            environmentId,
            threadId,
            epoch,
            new Sequence(1),
            new TurnStartedEvent(turnId, "run")));
        store.Apply(new ThreadEventEnvelope(
            environmentId,
            threadId,
            epoch,
            new Sequence(2),
            new RuntimeStateChangedEvent(ThreadRuntimeState.Running)));
        var error = new ProtocolError(ProtocolErrorCodes.PiRuntimeCrashed, "Pi stopped", true);
        store.Apply(new ThreadEventEnvelope(
            environmentId,
            threadId,
            epoch,
            new Sequence(3),
            new RuntimeFailedEvent(error)));

        Assert.NotNull(store.Current);
        var status = Assert.Single(store.Current.Timeline.OfType<StatusTimelineItem>());
        Assert.Equal(TimelineActivityState.Failed, status.State);
        var failure = Assert.Single(store.Current.Timeline.OfType<ErrorTimelineItem>());
        Assert.Equal(error, failure.Error);
        Assert.Equal(turnId, failure.TurnId);
    }

    [Fact]
    public void ProjectsPendingAndResolvedInteractionsIdenticallyToTheHost()
    {
        var environmentId = EnvironmentId.New();
        var threadId = ThreadId.New();
        var epoch = ProjectionEpoch.New();
        var turnId = TurnId.New();
        var interactionId = InteractionId.Parse("approval-1");
        var store = new ProjectionStore(threadId);
        var hostProjection = CreateProjection(environmentId, threadId, epoch);
        store.Apply(new ThreadSnapshotEnvelope(hostProjection));
        ThreadEvent[] events =
        [
            new TurnStartedEvent(turnId, "run"),
            new ApprovalRequestedEvent(interactionId, "Allow?", "Run it", null),
            new InteractionResolvedEvent(
                interactionId,
                InteractionState.Approved,
                ApprovalDecision.Approve),
        ];

        for (var index = 0; index < events.Length; index++)
        {
            var sequence = new Sequence(index + 1);
            store.Apply(new ThreadEventEnvelope(
                environmentId,
                threadId,
                epoch,
                sequence,
                events[index]));
            hostProjection = ThreadProjectionReducer.Apply(hostProjection, events[index]) with
            {
                Sequence = sequence,
            };
        }

        Assert.NotNull(store.Current);
        Assert.Equal(hostProjection.Timeline, store.Current.Timeline);
        var approval = Assert.Single(store.Current.Timeline.OfType<ApprovalTimelineItem>());
        Assert.Equal(InteractionState.Approved, approval.State);
        Assert.Equal(ApprovalDecision.Approve, approval.Decision);
    }

    private static ThreadProjection CreateProjection(
        EnvironmentId environmentId,
        ThreadId threadId,
        ProjectionEpoch epoch) => new(
        environmentId,
        threadId,
        epoch,
        Sequence.Initial,
        ThreadRuntimeState.Ready,
        null,
        [],
        [],
        "session",
        null,
        null,
        null);
}
