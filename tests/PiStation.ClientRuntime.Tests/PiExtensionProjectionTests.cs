using System.Text.Json;
using PiStation.Host.Threads;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Serialization;
using PiStation.Protocol.Streaming;

namespace PiStation.ClientRuntime.Tests;

public sealed class PiExtensionProjectionTests
{
    [Fact]
    public void HostAndClientAgreeOnUpdatesRemovalReconnectAndRuntimeRestart()
    {
        var thread = ThreadId.New();
        var projection = ThreadProjectionReducer.Create(EnvironmentId.New(), thread, "session");
        var store = new ProjectionStore(thread);
        store.Apply(new ThreadSnapshotEnvelope(projection));
        void Apply(ThreadEvent change)
        {
            projection = ThreadProjectionReducer.Apply(projection, change) with { Sequence = projection.Sequence.Next() };
            var envelope = new ThreadEventEnvelope(projection.EnvironmentId, thread, projection.ProjectionEpoch, projection.Sequence, change);
            var json = JsonSerializer.Serialize<ThreadEnvelope>(envelope, ProtocolJsonContext.Default.ThreadEnvelope);
            store.Apply(JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ThreadEnvelope)!);
            Assert.Equal(JsonSerializer.Serialize(projection.ExtensionUi), JsonSerializer.Serialize(store.Current!.ExtensionUi));
        }
        void Update(string id, string method, string? key = null, string? text = null, string[]? lines = null) =>
            Apply(new PiExtensionUiChangedEvent(new(id, method, DateTimeOffset.UnixEpoch, key, text, lines, "belowEditor")));
        Update("1", "setStatus", "build", "Running");
        Update("2", "setStatus", "build", "Done");
        Assert.Equal("Done", Assert.Single(store.Current!.ExtensionUi!.Statuses).Text);
        Update("3", "setWidget", "plan", lines: ["Step 1"]);
        Update("4", "set_editor_text", text: "A suggested prompt");
        Update("5", "notify", text: "Notification");
        var reconnect = new ProjectionStore(thread);
        reconnect.Apply(new ThreadSnapshotEnvelope(projection));
        Assert.Equal("A suggested prompt", reconnect.Current!.ExtensionUi!.EditorSuggestion!.Text);
        Assert.Equal("belowEditor", Assert.Single(reconnect.Current.ExtensionUi.Widgets).Placement);
        Update("6", "setStatus", "build");
        Update("7", "setWidget", "plan");
        Assert.Empty(store.Current!.ExtensionUi!.Statuses);
        Assert.Empty(store.Current.ExtensionUi.Widgets);
        Apply(new RuntimeStateChangedEvent(ThreadRuntimeState.Starting));
        Assert.Null(store.Current!.ExtensionUi!.EditorSuggestion);
        Assert.Empty(store.Current.ExtensionUi.Notifications);
        Assert.Throws<ArgumentException>(() => new ProjectionStore(ThreadId.New()).Apply(new ThreadSnapshotEnvelope(projection)));
    }
}
