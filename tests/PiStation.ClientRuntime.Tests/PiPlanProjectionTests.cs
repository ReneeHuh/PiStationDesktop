using System.Text.Json;
using PiStation.Host.Threads;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;
using PiStation.Protocol.Streaming;

namespace PiStation.ClientRuntime.Tests;

public sealed class PiPlanProjectionTests
{
    [Fact]
    public void PlanEventsRemainThreadScopedOrderedAndRecoverable()
    {
        var projection = ThreadProjectionReducer.Create(EnvironmentId.New(), ThreadId.New(), "session");
        var store = new ProjectionStore(projection.ThreadId);
        store.Apply(new ThreadSnapshotEnvelope(projection));
        var plan = new PiPlanState("session", 3, "paused", "Plan:\n1. Inspect", [new(1, "Inspect", false)], DateTimeOffset.UnixEpoch);
        foreach (var state in new[] { plan, plan with { Revision = 1, Mode = "off" }, plan with { SessionId = "other", Revision = 4 } })
        {
            var change = new PiPlanChangedEvent(state);
            projection = ThreadProjectionReducer.Apply(projection, change) with { Sequence = projection.Sequence.Next() };
            var envelope = new ThreadEventEnvelope(projection.EnvironmentId, projection.ThreadId, projection.ProjectionEpoch, projection.Sequence, change);
            var json = JsonSerializer.Serialize<ThreadEnvelope>(envelope, ProtocolJsonContext.Default.ThreadEnvelope);
            store.Apply(JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ThreadEnvelope)!);
            Assert.Equal(JsonSerializer.Serialize(projection.Plan), JsonSerializer.Serialize(store.Current!.Plan));
        }
        Assert.Equal("paused", store.Current!.Plan!.Mode);
        Assert.Equal(3, store.Current.Plan.Revision);
        var reconnect = new ProjectionStore(projection.ThreadId);
        reconnect.Apply(new ThreadSnapshotEnvelope(projection));
        Assert.Equal(plan, reconnect.Current!.Plan);
        Assert.Throws<JsonException>(() => PiPlanState.Parse("{\"sessionId\":\"session\",\"revision\":-1,\"mode\":\"off\",\"text\":\"\",\"steps\":[],\"updatedUtc\":\"2026-09-06T00:00:00Z\"}"));
    }
}
