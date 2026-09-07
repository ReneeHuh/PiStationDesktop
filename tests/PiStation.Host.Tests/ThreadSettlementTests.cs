using PiStation.Host.Persistence;
using PiStation.Host.Projects;
using PiStation.Host.Threads;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;

namespace PiStation.Host.Tests;

public sealed class ThreadSettlementTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-07T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static ThreadDescriptor Thread() => new(EnvironmentId.New(), ThreadId.New(), ProjectId.New(), "Task", "session", null, Now.AddDays(-10), Now);

    [Fact]
    public void InactivityUsesTaskActivityNotMetadataAndNeverGuessesEmptyHistory()
    {
        var thread = Thread();
        Assert.True(ThreadSettlementPolicy.ShouldSettle(thread, new(Now.AddDays(-4), Now.AddDays(-4), false), new(), Now));
        Assert.False(ThreadSettlementPolicy.ShouldSettle(thread, new(Now.AddDays(-3), Now.AddDays(-3), false), new(), Now));
        Assert.False(ThreadSettlementPolicy.ShouldSettle(thread, new(null, null, false), new(), Now));
        Assert.False(ThreadSettlementPolicy.ShouldSettle(thread, new(Now.AddDays(-4), Now.AddDays(-4), false), new(null), Now));
    }

    [Theory]
    [InlineData(PullRequestState.Open, false)]
    [InlineData(PullRequestState.Merged, true)]
    [InlineData(PullRequestState.Closed, true)]
    public void PullRequestRulesRespectResumedWorkAndSettings(PullRequestState state, bool expected)
    {
        var pr = new PullRequestDescriptor(SourceControlProvider.GitHub, "owner/repo", "1", "PR", "https://github.com/owner/repo/pull/1", state,
            "author", "feature", "main", false, [], [], PullRequestCheckState.Passed, Now.AddHours(-1), Now.AddHours(-1));
        Assert.Equal(expected, ThreadSettlementPolicy.ShouldSettle(Thread(), new(Now.AddHours(-2), Now.AddHours(-2), false), new(null), Now, pr));
        Assert.False(ThreadSettlementPolicy.ShouldSettle(Thread(), new(Now.AddMinutes(-10), Now.AddMinutes(-10), false), new(null), Now, pr));
        Assert.False(ThreadSettlementPolicy.ShouldSettle(Thread(), new(Now.AddHours(-2), Now.AddHours(-2), false), new(null), Now, pr with { ClosedOrMergedUtc = null }));
        Assert.False(ThreadSettlementPolicy.ShouldSettle(Thread(), new(Now.AddHours(-2), Now.AddHours(-2), false), new(null, false, false), Now, pr));
        Assert.True(ThreadSettlementPolicy.ShouldSettle(Thread(), new(Now.AddDays(-4), Now.AddDays(-4), false), new(), Now, pr with { State = PullRequestState.Open }));
    }

    [Theory]
    [InlineData(ThreadRuntimeState.Starting)]
    [InlineData(ThreadRuntimeState.Hydrating)]
    [InlineData(ThreadRuntimeState.Running)]
    [InlineData(ThreadRuntimeState.Stopping)]
    public void ActiveRuntimesAreProtected(ThreadRuntimeState state)
    {
        var projection = ThreadProjectionReducer.Create(EnvironmentId.New(), ThreadId.New(), "session", null) with { RuntimeState = state };
        Assert.True(ThreadSettlementPolicy.HasLiveWork(projection));
        projection = projection with { RuntimeState = ThreadRuntimeState.Ready, Queue = null, Timeline = [new ApprovalTimelineItem("approval", null, InteractionId.New(), "Approve", "Question", null, InteractionState.Pending, null)] };
        Assert.True(ThreadSettlementPolicy.HasLiveWork(projection));
        projection = projection with { Timeline = [new QuestionTimelineItem("question", null, InteractionId.New(), QuestionInputKind.Input, "Question", null, [], null, null, InteractionState.Pending, null)] };
        Assert.True(ThreadSettlementPolicy.HasLiveWork(projection));
        Assert.False(ThreadSettlementPolicy.ShouldSettle(Thread() with { RuntimeState = state }, new(Now.AddDays(-5), Now.AddDays(-5), false), new(), Now));
    }

    [Fact]
    public void SnoozeManualOverrideAndQueuedPromptsAreProtected()
    {
        var old = new SettlementActivity(Now.AddDays(-5), Now.AddDays(-5), false);
        Assert.False(ThreadSettlementPolicy.ShouldSettle(Thread() with { SnoozedUntilUtc = Now.AddHours(1) }, old, new(), Now));
        Assert.False(ThreadSettlementPolicy.ShouldSettle(Thread(), old with { Protected = true }, new(), Now));
        Assert.False(ThreadSettlementPolicy.ShouldSettle(Thread(), old with { LastUserActivity = Now.AddMinutes(-1) }, new(), Now));
        Assert.False(ThreadSettlementPolicy.ShouldSettle(Thread() with { NeedsAttention = true }, old, new(), Now));
        var projection = ThreadProjectionReducer.Create(EnvironmentId.New(), ThreadId.New(), "session", null) with
        { Queue = new([], QueueDeliveryMode.OneAtATime, QueueDeliveryMode.OneAtATime, QueueDeliveryState.Queued, 1, Now) };
        Assert.True(ThreadSettlementPolicy.HasLiveWork(projection));
    }

    [Fact]
    public async Task HostSweepPersistsSettlementWithoutAnOpenClientAndSettingsCanDisableIt()
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions();
        await using var host = await PiStation.Host.Hosting.EmbeddedEnvironmentHost.StartAsync(options);
        var db = new HostDatabase(options);
        await db.InitializeAsync();
        var project = await host.Environment.AddProjectAsync(new(directory.CreateDirectory("host-sweep")));
        var thread = await host.Environment.CreateThreadAsync(new(project.ProjectId));
        await db.RecordSettlementActivityAsync(thread.ThreadId, true, DateTimeOffset.UtcNow.AddDays(-5));
        await host.Environment.SaveSettlementSettingsAsync(new(null, false, false));
        Assert.Equal(0, await host.Environment.SweepThreadSettlementAsync(DateTimeOffset.UtcNow));
        await host.Environment.SaveSettlementSettingsAsync(new(3, false, false));
        Assert.Equal(1, await host.Environment.SweepThreadSettlementAsync(DateTimeOffset.UtcNow));
        Assert.True((await host.Environment.GetThreadAsync(thread.ThreadId)).IsSettled);
        Assert.Equal(0, await host.Environment.SweepThreadSettlementAsync(DateTimeOffset.UtcNow));
        await host.Environment.SaveSettlementSettingsAsync(new(null, false, false));
        Assert.True((await host.Environment.GetThreadAsync(thread.ThreadId)).IsSettled);
    }

    [Fact]
    public async Task SettlementIsRevisionGuardedUnpinsAndManualOverrideSurvivesRestartUntilNewWork()
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions();
        var db = new HostDatabase(options);
        await db.InitializeAsync();
        var projects = new ProjectService(db);
        var project = await projects.AddAsync(new(directory.CreateDirectory("settlement")));
        var thread = await projects.CreateThreadAsync(new(project.ProjectId));
        await db.ApplyThreadBulkOperationAsync(new(project.ProjectId, [thread.ThreadId], ThreadBulkOperation.Pin));
        Assert.True((await db.GetThreadAsync(thread.ThreadId))!.IsPinned);
        await db.RecordSettlementActivityAsync(thread.ThreadId, true, Now.AddDays(-5));
        var before = (await db.GetThreadAsync(thread.ThreadId))!;
        await db.RecordSettlementActivityAsync(thread.ThreadId, true, Now);
        Assert.False(await db.TryAutoSettleAsync(thread.ThreadId, before.Revision));
        var current = (await db.GetThreadAsync(thread.ThreadId))!;
        Assert.True(await db.TryAutoSettleAsync(thread.ThreadId, current.Revision));
        current = (await db.GetThreadAsync(thread.ThreadId))!;
        Assert.True((await db.EnrichThreadDescriptorAsync(current)).IsSettled);
        Assert.False(current.IsPinned);
        Assert.Equal(before.UpdatedUtc, current.UpdatedUtc);
        await db.UpdateThreadInboxAsync(thread.ThreadId, current.Revision, isSettled: false);
        var restarted = new HostDatabase(options);
        await restarted.InitializeAsync();
        Assert.True((await restarted.GetSettlementActivityAsync(thread.ThreadId)).Protected);
        current = (await restarted.GetThreadAsync(thread.ThreadId))!;
        Assert.False(await restarted.TryAutoSettleAsync(thread.ThreadId, current.Revision));
        await restarted.RecordSettlementActivityAsync(thread.ThreadId, true, Now);
        Assert.False((await restarted.GetSettlementActivityAsync(thread.ThreadId)).Protected);
        await restarted.RecordSettlementPlanAsync(thread.ThreadId, new("session", 2, "ready", "Review plan", [], Now));
        await restarted.RecordSettlementPlanAsync(thread.ThreadId, new("session", 1, "off", "", [], Now));
        Assert.True((await db.GetSettlementActivityAsync(thread.ThreadId)).Protected);
        await restarted.RecordSettlementPlanAsync(thread.ThreadId, new("session", 3, "completed", "Done", [], Now));
        Assert.False((await db.GetSettlementActivityAsync(thread.ThreadId)).Protected);
        await restarted.SaveSettlementSettingsAsync(new(null, false, true));
        Assert.Equal(new SettlementSettings(null, false, true), await db.GetSettlementSettingsAsync());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.SaveSettlementSettingsAsync(new(0)));
    }
}
