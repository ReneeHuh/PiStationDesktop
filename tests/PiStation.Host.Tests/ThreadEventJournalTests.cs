using System.Text.Json;
using PiStation.Host.Threads;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Tests;

public sealed class ThreadEventJournalTests
{
    [Fact]
    public async Task CurrentCursorReceivesSynchronizationWithoutWaitingForNewActivity()
    {
        var projection = ThreadProjectionReducer.Create(EnvironmentId.New(), ThreadId.New(), "session");
        var journal = new ThreadEventJournal(projection, Options(eventLimit: 4));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = journal.SubscribeAsync(new(projection.ProjectionEpoch, projection.Sequence), timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(projection.Sequence, Assert.IsType<ThreadSynchronizedEnvelope>(reader.Current).Sequence);
    }
    [Fact]
    public void HydrationReconstructsOrderedTurnAndThinkingItems()
    {
        var projection = ThreadProjectionReducer.Create(
            EnvironmentId.New(),
            ThreadId.New(),
            "session");
        var entries = JsonSerializer.Deserialize<JsonElement[]>("""
            [
              {
                "id": "user-entry",
                "type": "message",
                "timestamp": "2026-09-02T12:00:00.000Z",
                "message": { "role": "user", "content": "question" }
              },
              {
                "id": "assistant-entry",
                "type": "message",
                "timestamp": "2026-09-02T12:00:01.250Z",
                "message": {
                  "role": "assistant",
                  "content": [
                    { "type": "thinking", "thinking": "checking" },
                    { "type": "text", "text": "answer" }
                  ],
                  "usage": {
                    "input": 120,
                    "output": 30,
                    "cacheRead": 40,
                    "cacheWrite": 10,
                    "reasoning": 12,
                    "totalTokens": 200
                  }
                }
              }
            ]
            """)!;

        var hydrated = ThreadProjectionReducer.Hydrate(
            projection,
            entries,
            "assistant-entry",
            "session.jsonl",
            200_000);

        Assert.Collection(
            hydrated.Timeline,
            item => Assert.Equal(TurnBoundaryKind.Started, Assert.IsType<TurnBoundaryTimelineItem>(item).Boundary),
            item => Assert.Equal(MessageRole.User, Assert.IsType<MessageTimelineItem>(item).Role),
            item => Assert.Equal("checking", Assert.IsType<ThinkingTimelineItem>(item).Text),
            item => Assert.Equal("answer", Assert.IsType<MessageTimelineItem>(item).Text),
            item => Assert.Equal(TurnBoundaryKind.Settled, Assert.IsType<TurnBoundaryTimelineItem>(item).Boundary));
        Assert.Equal(2, hydrated.Messages.Count);
        Assert.Equal("checking", hydrated.Messages[^1].Thinking);
        var metrics = Assert.IsType<TurnMetrics>(
            hydrated.Timeline.OfType<TurnBoundaryTimelineItem>().Last().Metrics);
        Assert.Equal(1_250, metrics.ElapsedMilliseconds);
        Assert.Equal(200, metrics.Usage?.TotalTokens);
        Assert.Equal(200, metrics.ContextTokens);
        Assert.Equal(200_000, metrics.ContextWindow);
    }

    [Fact]
    public void HydrationAggregatesTurnUsageButUsesTheLatestResponseForContext()
    {
        var projection = ThreadProjectionReducer.Create(EnvironmentId.New(), ThreadId.New(), "session");
        var entries = JsonSerializer.Deserialize<JsonElement[]>("""
            [
              {
                "id": "user-entry",
                "type": "message",
                "timestamp": "2026-09-02T12:00:00.000Z",
                "message": { "role": "user", "content": "question" }
              },
              {
                "id": "assistant-entry-1",
                "type": "message",
                "timestamp": "2026-09-02T12:00:01.000Z",
                "message": {
                  "role": "assistant",
                  "content": "first",
                  "usage": {
                    "input": 80, "output": 20, "cacheRead": 0, "cacheWrite": 0, "totalTokens": 100
                  }
                }
              },
              {
                "id": "assistant-entry-2",
                "type": "message",
                "timestamp": "2026-09-02T12:00:02.000Z",
                "message": {
                  "role": "assistant",
                  "content": "second",
                  "usage": {
                    "input": 120, "output": 30, "cacheRead": 0, "cacheWrite": 0, "totalTokens": 150
                  }
                }
              }
            ]
            """)!;

        var hydrated = ThreadProjectionReducer.Hydrate(
            projection,
            entries,
            "assistant-entry-2",
            "session.jsonl",
            1_000);

        var metrics = Assert.IsType<TurnMetrics>(
            hydrated.Timeline.OfType<TurnBoundaryTimelineItem>().Last().Metrics);
        Assert.Equal(2_000, metrics.ElapsedMilliseconds);
        Assert.Equal(250, metrics.Usage?.TotalTokens);
        Assert.Equal(150, metrics.ContextTokens);
        Assert.Equal(1_000, metrics.ContextWindow);
    }

    [Fact]
    public async Task RetainedCursorGetsOnlyMissingEvents()
    {
        var projection = ThreadProjectionReducer.Create(
            EnvironmentId.New(),
            ThreadId.New(),
            "session");
        var journal = new ThreadEventJournal(projection, Options(eventLimit: 4));
        journal.Commit(new RuntimeStateChangedEvent(ThreadRuntimeState.Starting));
        var cursorEnvelope = journal.Commit(new RuntimeStateChangedEvent(ThreadRuntimeState.Hydrating));
        var expected = journal.Commit(new RuntimeStateChangedEvent(ThreadRuntimeState.Ready));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var subscription = journal.SubscribeAsync(
                new ThreadCursor(cursorEnvelope.ProjectionEpoch, cursorEnvelope.Sequence),
                cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Assert.True(await subscription.MoveNextAsync());
        var actual = Assert.IsType<ThreadEventEnvelope>(subscription.Current);
        Assert.Equal(expected.Sequence, actual.Sequence);
    }

    [Fact]
    public async Task ExpiredCursorGetsOneAuthoritativeSnapshot()
    {
        var projection = ThreadProjectionReducer.Create(
            EnvironmentId.New(),
            ThreadId.New(),
            "session");
        var journal = new ThreadEventJournal(projection, Options(eventLimit: 2));
        var staleCursor = new ThreadCursor(projection.ProjectionEpoch, Sequence.Initial);
        journal.Commit(new RuntimeStateChangedEvent(ThreadRuntimeState.Starting));
        journal.Commit(new RuntimeStateChangedEvent(ThreadRuntimeState.Hydrating));
        journal.Commit(new RuntimeStateChangedEvent(ThreadRuntimeState.Ready));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var subscription = journal.SubscribeAsync(staleCursor, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Assert.True(await subscription.MoveNextAsync());
        var snapshot = Assert.IsType<ThreadSnapshotEnvelope>(subscription.Current);
        Assert.Equal(journal.Projection.Sequence, snapshot.Sequence);
        Assert.Equal(ThreadRuntimeState.Ready, snapshot.Projection.RuntimeState);
    }

    private static HostOptions Options(int eventLimit) => new()
    {
        ApplicationDataRoot = Path.GetTempPath(),
        JournalEventLimit = eventLimit,
        JournalByteLimit = 1024 * 1024,
        SubscriberCapacity = 8,
    };
}
