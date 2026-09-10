using PiStation.Host.Threads;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Streaming;

namespace PiStation.ClientRuntime.Tests;

public sealed class ThreadHistoryTests
{
    [Fact]
    public void PagesReconstructCompletedHistoryInOrderWithoutDuplicates()
    {
        var full = Create(75);
        var recent = ThreadHistoryWindow.Recent(full);
        Assert.Equal(10, recent.Timeline.OfType<TurnBoundaryTimelineItem>().Count());
        var store = new ProjectionStore(full.ThreadId);
        store.Apply(new ThreadSnapshotEnvelope(recent)); store.SetSynchronized(true);
        while (store.Current!.EarlierHistory is { } cursor)
        {
            var page = ThreadHistoryWindow.Read(full, new(full.ThreadId, full.ProjectionEpoch, cursor.BeforeItemId));
            Assert.InRange(page.Items.Count, 1, ThreadHistoryWindow.MaximumPageItems);
            Assert.InRange(page.Items.OfType<TurnBoundaryTimelineItem>().Count(), 1, 20);
            Assert.True(store.TryMergeHistory(page)); Assert.False(store.TryMergeHistory(page));
        }
        Assert.Equal(full.Timeline, store.Current.Timeline);
    }

    [Fact]
    public void PageWaitsForLiveWatermarkAndPreservesNewerMetadataAndContent()
    {
        var full = Create(40);
        var recent = ThreadHistoryWindow.Recent(full);
        var store = new ProjectionStore(full.ThreadId);
        store.Apply(new ThreadSnapshotEnvelope(recent));
        var page = ThreadHistoryWindow.Read(full, new(full.ThreadId, full.ProjectionEpoch, recent.EarlierHistory!.BeforeItemId)) with { Sequence = new(1) };
        Assert.False(store.TryMergeHistory(page)); store.SetSynchronized(true);
        Assert.False(store.TryMergeHistory(page));
        store.Apply(new ThreadEventEnvelope(full.EnvironmentId, full.ThreadId, full.ProjectionEpoch, new(1), new RuntimeStateChangedEvent(ThreadRuntimeState.Running)));
        Assert.True(store.TryMergeHistory(page with { Items = page.Items.Concat(recent.Timeline.Take(1)).ToArray() }));
        Assert.Equal(ThreadRuntimeState.Running, store.Current!.RuntimeState); Assert.Equal(new Sequence(1), store.Current.Sequence);
        Assert.Equal(store.Current.Timeline.Count, store.Current.Timeline.Select(item => item.ItemId).Distinct().Count());
    }

    [Fact]
    public void RestartOrBranchReplacementRejectsOldPagesAndCursors()
    {
        var full = Create(50);
        var recent = ThreadHistoryWindow.Recent(full);
        var request = new ReadThreadHistoryRequest(full.ThreadId, full.ProjectionEpoch, recent.EarlierHistory!.BeforeItemId);
        var page = ThreadHistoryWindow.Read(full, request);
        var store = new ProjectionStore(full.ThreadId); store.Apply(new ThreadSnapshotEnvelope(recent)); store.SetSynchronized(true);
        var replaced = full with { ProjectionEpoch = ProjectionEpoch.New() };
        store.Apply(new ThreadSnapshotEnvelope(ThreadHistoryWindow.Recent(replaced)));
        Assert.False(store.TryMergeHistory(page));
        Assert.Throws<InvalidOperationException>(() => ThreadHistoryWindow.Read(replaced, request));
        Assert.Throws<InvalidOperationException>(() => ThreadHistoryWindow.Read(full, request with { BeforeItemId = "missing" }));
    }

    [Fact]
    public void ItemBoundHandlesHugeTurnsAndMutableInteractionsStayVisible()
    {
        var full = Create(1000);
        var messages = full.Timeline.OfType<MessageTimelineItem>().Cast<TimelineItem>().ToArray();
        Assert.Equal(400, ThreadHistoryWindow.Recent(full with { Timeline = messages }).Timeline.Count);
        var mutable = messages.ToArray(); mutable[1] = ((MessageTimelineItem)mutable[1]) with { IsComplete = false };
        var window = ThreadHistoryWindow.Recent(full with { Timeline = mutable });
        Assert.Equal(mutable[1], window.Timeline[0]); Assert.Equal(1, window.EarlierHistory!.RemainingItems);
    }

    [Fact]
    public void RecoverySnapshotRetainsLoadedHistoryOnlyWhenEpochAndWindowOverlap()
    {
        var full = Create(50);
        var store = new ProjectionStore(full.ThreadId); store.Apply(new ThreadSnapshotEnvelope(full)); store.SetSynchronized(true);
        store.Apply(new ThreadSnapshotEnvelope(ThreadHistoryWindow.Recent(full with { Sequence = new(1) })));
        Assert.Equal(full.Timeline, store.Current!.Timeline); Assert.Null(store.Current.EarlierHistory);
        store.Apply(new ThreadSnapshotEnvelope(ThreadHistoryWindow.Recent(full with { ProjectionEpoch = ProjectionEpoch.New() })));
        Assert.Equal(20, store.Current.Timeline.Count); Assert.NotNull(store.Current.EarlierHistory);
    }

    [Fact]
    public void LongSessionHydrationUsesLinearAllocationAndRetainsThinkingOrder()
    {
        var entries = Enumerable.Range(0, 4000).Select(i => System.Text.Json.JsonSerializer.SerializeToElement(new {
            type = "message", id = $"m{i}", message = new { role = i % 2 == 0 ? "user" : "assistant",
                content = new[] { new { type = "thinking", thinking = "reasoning", text = "" }, new { type = "text", thinking = "", text = $"Message {i}" } } } })).ToArray();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var projection = ThreadProjectionReducer.Hydrate(Create(0), entries, "m3999", null);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 80 * 1024 * 1024, $"Hydration allocated {allocated:N0} bytes for 4,000 messages.");
        Assert.Equal(4000, projection.Timeline.OfType<MessageTimelineItem>().Count());
        var assistant = projection.Timeline.ToList().FindIndex(item => item.ItemId == "message-m1");
        Assert.Equal("thinking-m1", projection.Timeline[assistant - 1].ItemId);
    }

    [Fact]
    public void RecoveryDoesNotRetainAnOldPendingItemThatCompletedDuringAGap()
    {
        var full = Create(50);
        var pending = full.Timeline.ToArray(); pending[1] = ((MessageTimelineItem)pending[1]) with { IsComplete = false };
        var store = new ProjectionStore(full.ThreadId);
        store.Apply(new ThreadSnapshotEnvelope(ThreadHistoryWindow.Recent(full with { Timeline = pending })));
        Assert.Contains(store.Current!.Timeline, ThreadHistoryRules.IsMutable);
        store.Apply(new ThreadSnapshotEnvelope(ThreadHistoryWindow.Recent(full with { Sequence = new(1) })));
        Assert.DoesNotContain(store.Current!.Timeline, ThreadHistoryRules.IsMutable);
        Assert.Equal(20, store.Current.Timeline.Count);
    }

    private static ThreadProjection Create(int turns)
    {
        var timeline = new List<TimelineItem>();
        for (var i = 0; i < turns; i++)
        {
            var turn = TurnId.New();
            timeline.Add(new TurnBoundaryTimelineItem($"turn-{i}", turn, TurnBoundaryKind.Started));
            timeline.Add(new MessageTimelineItem($"message-{i}", turn, i.ToString(System.Globalization.CultureInfo.InvariantCulture), MessageRole.User, $"Question {i}", true));
        }
        return new(EnvironmentId.New(), ThreadId.New(), ProjectionEpoch.New(), Sequence.Initial, ThreadRuntimeState.Ready, null, timeline, [], "session", null, null, null);
    }
}
