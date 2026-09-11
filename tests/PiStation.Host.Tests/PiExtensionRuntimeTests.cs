using PiStation.Host.Hosting;
using PiStation.Host.Errors;
using PiStation.Protocol;
using PiStation.Protocol.Commands;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Receipts;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Tests;

public sealed class PiExtensionRuntimeTests
{
    [Fact]
    public async Task ExtensionCommandsSettleAndIdleRestartRecreatesRuntimeAndRefreshesUi()
    {
        using var directory = new HostTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateOptions("extension-ui"));
        var project = await host.Environment.AddProjectAsync(new AddProjectRequest(directory.CreateDirectory("project")));
        var thread = await host.Environment.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        await using var stream = host.Environment.SubscribeThreadAsync(thread.ThreadId, null, timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await stream.MoveNextAsync());
        var initial = Assert.IsType<ThreadSnapshotEnvelope>(stream.Current).Projection;
        var receipt = await host.Environment.ExecuteThreadCommandAsync(new(ProtocolVersion.Current,
            host.Environment.GetDescriptor().EnvironmentId, ClientId.New(), CommandId.New(), thread.ThreadId,
            initial.ProjectionEpoch, null, new ThreadStartTurnCommand("/review")), timeout.Token);
        Assert.True(receipt.State is CommandReceiptState.Accepted or CommandReceiptState.Completed);
        var sawUi = initial.ExtensionUi?.EditorSuggestion is not null;
        var sawComponentClose = false;
        while (await stream.MoveNextAsync())
        {
            if (stream.Current is ThreadEventEnvelope { Event: QuestionRequestedEvent { InteractionId.Value: "component-question" } component })
                Assert.Equal(QuestionInputKind.Component, component.InputKind);
            if (stream.Current is ThreadEventEnvelope { Event: PiExtensionUiChangedEvent { Update.Method: "set_editor_text" } }) sawUi = true;
            if (stream.Current is ThreadEventEnvelope { Event: InteractionResolvedEvent { State: InteractionState.Canceled } resolved })
            {
                Assert.Equal("component-question", resolved.InteractionId.Value);
                sawComponentClose = true;
            }
            if (stream.Current is ThreadEventEnvelope { Event: TurnSettledEvent }) break;
        }
        Assert.True(sawUi);
        Assert.True(sawComponentClose);
        var restart = await host.Environment.ExecuteThreadCommandAsync(new(ProtocolVersion.Current,
            host.Environment.GetDescriptor().EnvironmentId, ClientId.New(), CommandId.New(), thread.ThreadId,
            initial.ProjectionEpoch, null, new ThreadRestartRuntimeCommand()), timeout.Token);
        Assert.Equal(CommandReceiptState.Completed, restart.State);
        var changedEpoch = false;
        while (await stream.MoveNextAsync())
        {
            if (stream.Current is ThreadSnapshotEnvelope snapshot && snapshot.ProjectionEpoch != initial.ProjectionEpoch)
            {
                changedEpoch = true;
                break;
            }
        }
        Assert.True(changedEpoch);
    }

    [Fact]
    public async Task InvalidSkillDoesNotCreateAPhantomTurn()
    {
        using var directory = new HostTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateOptions());
        var project = await host.Environment.AddProjectAsync(new AddProjectRequest(directory.CreateDirectory("project")));
        var thread = await host.Environment.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        await using var stream = host.Environment.SubscribeThreadAsync(thread.ThreadId, null, timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await stream.MoveNextAsync());
        var initial = Assert.IsType<ThreadSnapshotEnvelope>(stream.Current).Projection;
        var error = await Assert.ThrowsAsync<HostOperationException>(() => host.Environment.ExecuteThreadCommandAsync(new(ProtocolVersion.Current,
            host.Environment.GetDescriptor().EnvironmentId, ClientId.New(), CommandId.New(), thread.ThreadId,
            initial.ProjectionEpoch, null, new ThreadStartTurnCommand("$skill:missing")), timeout.Token));
        Assert.Contains("unavailable", error.Message);
        await using var refreshed = host.Environment.SubscribeThreadAsync(thread.ThreadId, null, timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await refreshed.MoveNextAsync());
        var projection = Assert.IsType<ThreadSnapshotEnvelope>(refreshed.Current).Projection;
        Assert.Null(projection.CurrentTurnId);
        Assert.Empty(projection.Timeline.OfType<TurnBoundaryTimelineItem>());
        Assert.Equal(ThreadRuntimeState.Ready, projection.RuntimeState);
    }
}
