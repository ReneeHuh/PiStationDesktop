using PiStation.Host.Hosting;
using PiStation.Protocol;
using PiStation.Protocol.Commands;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Receipts;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Tests;

public sealed class RuntimeLifecycleTests
{
    [Fact]
    public async Task FirstPromptIsAcknowledgedWhilePiIsStillRunning()
    {
        using var directory = new HostTestDirectory();
        var projectPath = directory.CreateDirectory("project");
        var gates = Path.Combine(projectPath, ".pistation-ui-tool-gates");
        Directory.CreateDirectory(gates);
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateOptions("ui-tool"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var descriptor = host.Environment.GetDescriptor();
        var project = await host.Environment.AddProjectAsync(new AddProjectRequest(projectPath));
        var thread = await host.Environment.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        await using var stream = host.Environment.SubscribeThreadAsync(thread.ThreadId, null, timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await stream.MoveNextAsync());
        var initial = Assert.IsType<ThreadSnapshotEnvelope>(stream.Current);
        try
        {
            var receipt = await host.Environment.ExecuteThreadCommandAsync(new ExecuteThreadCommandRequest(
                ProtocolVersion.Current, descriptor.EnvironmentId, ClientId.New(), CommandId.New(), thread.ThreadId,
                initial.ProjectionEpoch, null, new ThreadStartTurnCommand("Keep the stop action available")), timeout.Token);
            Assert.Equal(CommandReceiptState.Accepted, receipt.State);
            var updated = await host.Environment.GetThreadAsync(thread.ThreadId, timeout.Token);
            Assert.Equal(ThreadTitleKind.Generated, updated.TitleKind);
            Assert.Equal("Keep the stop action available", updated.Title);
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(gates, "continue-tool"), string.Empty);
            await File.WriteAllTextAsync(Path.Combine(gates, "complete-turn"), string.Empty);
        }
    }

    [Fact]
    public async Task MissingPiStillAllowsProjectAndDraftSetupAndInvalidConfigurationDoesNotPersist()
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions() with { PiInstallation = null };
        await using var host = await EmbeddedEnvironmentHost.StartAsync(options);
        Assert.False(host.Environment.GetDescriptor().PiAvailable);
        var project = await host.Environment.AddProjectAsync(new AddProjectRequest(directory.CreateDirectory("project")));
        var thread = await host.Environment.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        Assert.NotNull(await host.Environment.GetThreadDraftAsync(thread.ThreadId));
        var setup = await host.Environment.ConfigurePiRuntimeAsync(new ConfigurePiRuntimeRequest(directory.GetPath("missing-pi.exe")));
        Assert.False(setup.Available);
        Assert.False(File.Exists(Path.Combine(options.CanonicalDataRoot, "pi-executable.txt")));
        Assert.False(host.Environment.GetDescriptor().PiAvailable);
    }

    [Fact]
    public async Task IdleRuntimeStopsAndNextPromptRestartsItWithoutSettlingTheTask()
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions() with { IdleRuntimeTimeout = TimeSpan.FromMilliseconds(400), IdleRuntimeSweepInterval = TimeSpan.FromMilliseconds(100) };
        await using var host = await EmbeddedEnvironmentHost.StartAsync(options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var descriptor = host.Environment.GetDescriptor();
        var project = await host.Environment.AddProjectAsync(new AddProjectRequest(directory.CreateDirectory("project")));
        var thread = await host.Environment.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        await using var stream = host.Environment.SubscribeThreadAsync(thread.ThreadId, null, timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await stream.MoveNextAsync());
        var initial = Assert.IsType<ThreadSnapshotEnvelope>(stream.Current);
        var stopped = false;
        while (await stream.MoveNextAsync())
        {
            if (stream.Current is ThreadEventEnvelope { Event: RuntimeStateChangedEvent { State: ThreadRuntimeState.Stopped } }) { stopped = true; break; }
        }
        Assert.True(stopped);
        var receipt = await host.Environment.ExecuteThreadCommandAsync(new ExecuteThreadCommandRequest(
            ProtocolVersion.Current, descriptor.EnvironmentId, ClientId.New(), CommandId.New(), thread.ThreadId,
            initial.ProjectionEpoch, null, new ThreadStartTurnCommand("Resume this task")), timeout.Token);
        Assert.True(receipt.State is CommandReceiptState.Accepted or CommandReceiptState.Completed);
        var completed = false;
        while (await stream.MoveNextAsync())
        {
            if (stream.Current is ThreadEventEnvelope { Event: TurnSettledEvent }) { completed = true; break; }
        }
        Assert.True(completed);
        Assert.False((await host.Environment.GetThreadAsync(thread.ThreadId)).IsSettled);
    }
}
