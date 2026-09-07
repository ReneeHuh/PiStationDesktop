using System.Text.Json;
using PiStation.Host.Threads;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Serialization;
using PiStation.Protocol.Streaming;

namespace PiStation.ClientRuntime.Tests;

public sealed class PiAgentProjectionTests
{
    [Fact]
    public void NativeChildControlsTranscriptAndSessionScopedSetupSurviveSerializationAndReconnect()
    {
        var projection = ThreadProjectionReducer.Create(EnvironmentId.New(), ThreadId.New(), "session");
        var store = new ProjectionStore(projection.ThreadId);
        store.Apply(new ThreadSnapshotEnvelope(projection));
        var setup = new PiAgentSetup("session", true, true, "revision", [new("scout", "Inspect", "Read files", ["read"])], "Ready");
        var activity = new AgentActivityProjection("call:agent:0", null, "call", AgentActivityKind.Agent, AgentActivityState.Interrupted,
            "scout", "Read", "Interrupted", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            1, null, "offline/model", "off", null, "Stopped", null, 0, false, Guid.NewGuid().ToString("N"), "toolResult:\nChild evidence", true);
        foreach (var change in new ThreadEvent[] { new PiAgentSetupChangedEvent(setup), new AgentActivityChangedEvent(activity), new PiAgentSetupChangedEvent(setup with { SessionId = "foreign", Enabled = false }) })
        {
            projection = ThreadProjectionReducer.Apply(projection, change) with { Sequence = projection.Sequence.Next() };
            var envelope = new ThreadEventEnvelope(projection.EnvironmentId, projection.ThreadId, projection.ProjectionEpoch, projection.Sequence, change);
            store.Apply(JsonSerializer.Deserialize(JsonSerializer.Serialize<ThreadEnvelope>(envelope, ProtocolJsonContext.Default.ThreadEnvelope), ProtocolJsonContext.Default.ThreadEnvelope)!);
        }
        Assert.True(store.Current!.AgentSetup!.Enabled);
        Assert.Equal(activity, Assert.Single(store.Current.AgentActivities!));
        var restored = new ProjectionStore(projection.ThreadId);
        restored.Apply(JsonSerializer.Deserialize(JsonSerializer.Serialize<ThreadEnvelope>(new ThreadSnapshotEnvelope(projection), ProtocolJsonContext.Default.ThreadEnvelope), ProtocolJsonContext.Default.ThreadEnvelope)!);
        Assert.Equal(activity.ControlId, Assert.Single(restored.Current!.AgentActivities!).ControlId);
        Assert.True(Assert.Single(restored.Current.AgentActivities!).CanResume);
        Assert.Throws<JsonException>(() => PiAgentSetup.Parse("""{"sessionId":"","available":true,"enabled":true,"revision":"0","presets":[],"message":""}"""));
    }
}
