using PiStation.Host.Threads;
using PiStation.Protocol;
using PiStation.Protocol.Commands;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Tests;

public sealed class PassiveRemoteViewingTests
{
    [Fact]
    public async Task PassiveConfigurationAndSubscriptionDoNotLaunchPi()
    {
        using var directory = new HostTestDirectory();
        await using var environment = await EnvironmentService.CreateAsync(
            directory.CreateOptions(),
            new ThrowingProcessFactory());
        var project = await environment.AddProjectAsync(new AddProjectRequest(directory.CreateDirectory("project")));
        var thread = await environment.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));

        var configuration = await environment.GetThreadPiConfigurationPassiveAsync(thread.ThreadId);
        Assert.Equal(thread.ThreadId, configuration.Configuration.ThreadId);
        await using var subscription = environment.SubscribeThreadPassiveAsync(thread.ThreadId, null).GetAsyncEnumerator();
        Assert.True(await subscription.MoveNextAsync());
        Assert.IsType<ThreadSnapshotEnvelope>(subscription.Current);
    }

    [Fact]
    public async Task PassiveSubscriptionRemainsLiveForLaterLocalTurn()
    {
        using var directory = new HostTestDirectory();
        await using var environment = await EnvironmentService.CreateAsync(directory.CreateOptions());
        var project = await environment.AddProjectAsync(new AddProjectRequest(directory.CreateDirectory("project")));
        var thread = await environment.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var subscription = environment.SubscribeThreadPassiveAsync(thread.ThreadId, null)
            .GetAsyncEnumerator(timeout.Token);
        Assert.True(await subscription.MoveNextAsync());

        var clientId = ClientId.New();
        var commandId = CommandId.New();
        await environment.ExecuteThreadCommandAsync(new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            environment.EnvironmentId,
            clientId,
            commandId,
            thread.ThreadId,
            null,
            null,
            new ThreadStartTurnCommand("observe this turn")));
        while ((await environment.GetCommandReceiptAsync(clientId, commandId, timeout.Token))?.State != CommandReceiptState.Completed)
        {
            await Task.Delay(20, timeout.Token);
        }

        var observed = false;
        while (await subscription.MoveNextAsync())
        {
            var envelope = subscription.Current;
            if (envelope is ThreadEventEnvelope { Event: TurnStartedEvent started } &&
                started.Prompt.Contains("observe this turn", StringComparison.Ordinal))
            {
                observed = true;
                break;
            }
        }

        Assert.True(observed);
    }

    [Fact]
    public async Task PassiveViewerHydratesPersistedHistoryWithoutStartingPi()
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions();
        var projectPath = directory.CreateDirectory("project");
        ThreadDescriptor thread;
        await using (var first = await EnvironmentService.CreateAsync(options))
        {
            var project = await first.AddProjectAsync(new AddProjectRequest(projectPath));
            thread = await first.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
            var clientId = ClientId.New();
            var commandId = CommandId.New();
            await first.ExecuteThreadCommandAsync(new ExecuteThreadCommandRequest(
                ProtocolVersion.Current,
                first.EnvironmentId,
                clientId,
                commandId,
                thread.ThreadId,
                null,
                null,
                new ThreadStartTurnCommand("persist this history")));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while ((await first.GetCommandReceiptAsync(clientId, commandId, timeout.Token))?.State !=
                   CommandReceiptState.Completed)
            {
                await Task.Delay(20, timeout.Token);
            }
        }

        await using var passive = await EnvironmentService.CreateAsync(options, new ThrowingProcessFactory());
        await using var subscription = passive.SubscribeThreadPassiveAsync(thread.ThreadId, null).GetAsyncEnumerator();
        Assert.True(await subscription.MoveNextAsync());
        var snapshot = Assert.IsType<ThreadSnapshotEnvelope>(subscription.Current);
        Assert.Contains(
            snapshot.Projection.Timeline.OfType<PiStation.Protocol.Projections.MessageTimelineItem>(),
            message => message.Text.Contains("persist this history", StringComparison.Ordinal));
    }

    private sealed class ThrowingProcessFactory : IPiProcessFactory
    {
        public Task<PiStation.PiRpc.Process.PiProcess> StartAsync(
            ProjectDescriptor project,
            PiStation.Host.Persistence.HostThreadRecord thread,
            CancellationToken cancellationToken = default) =>
            throw new Xunit.Sdk.XunitException("Passive viewing attempted to launch Pi.");
    }
}
