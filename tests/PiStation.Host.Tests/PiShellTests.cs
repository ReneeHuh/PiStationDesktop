using PiStation.Host.Hosting;
using PiStation.Host.Errors;
using PiStation.Host.Persistence;
using PiStation.Protocol;
using PiStation.Protocol.Commands;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Receipts;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Tests;

public sealed class PiShellTests
{
    [Fact]
    public async Task UnfinishedDurableExecutionIsInterruptedWithoutLaunchingPiOrReplayingTheCommand()
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions();
        ThreadId threadId;
        var execution = new PiShellExecution(CommandId.New(), ClientId.New(), "do not replay", true,
            PiShellExecutionState.Running, "last saved output", false, null, null, null, DateTimeOffset.UtcNow);
        await using (var host = await EmbeddedEnvironmentHost.StartAsync(options))
        {
            var project = await host.Environment.AddProjectAsync(new(directory.CreateDirectory("project")));
            threadId = (await host.Environment.CreateThreadAsync(new(project.ProjectId))).ThreadId;
            // The database left behind by an abrupt shutdown, with no confirmed result.
            await new HostDatabase(options).SavePiShellAsync(threadId, execution);
        }
        await using var restored = await EmbeddedEnvironmentHost.StartAsync(options with { PiInstallation = null });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var stream = restored.Environment.SubscribeThreadPassiveAsync(threadId, null, timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await stream.MoveNextAsync());
        var projection = Assert.IsType<ThreadSnapshotEnvelope>(stream.Current).Projection;
        Assert.Equal(ThreadRuntimeState.Stopped, projection.RuntimeState);
        Assert.Equal(PiShellExecutionState.Interrupted, projection.ShellExecution!.State);
        Assert.Equal(execution.Output, projection.ShellExecution.Output);
        Assert.Contains("not run again", projection.ShellExecution.Error);
        Assert.Equal(execution.CommandId, projection.ShellExecution.CommandId);
        Assert.Empty(Directory.GetFiles(directory.Path, "command-log.jsonl", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task StreamCancelAndRetryPreserveDraftAndNeverStartAnAgentTurn()
    {
        using var directory = new HostTestDirectory();
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateOptions());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var project = await host.Environment.AddProjectAsync(new(directory.CreateDirectory("project")));
        var thread = await host.Environment.CreateThreadAsync(new(project.ProjectId));
        await using var stream = host.Environment.SubscribeThreadAsync(thread.ThreadId, null, timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await stream.MoveNextAsync());
        var initial = Assert.IsType<ThreadSnapshotEnvelope>(stream.Current);
        var client = ClientId.New();
        ExecuteThreadCommandRequest Request(ThreadCommand command) => new(ProtocolVersion.Current,
            host.Environment.GetDescriptor().EnvironmentId, client, CommandId.New(), thread.ThreadId, initial.ProjectionEpoch, null, command);
        var draft = await host.Environment.GetThreadDraftAsync(thread.ThreadId);
        var save = Request(new ThreadSaveDraftCommand(draft.DraftId, draft.Revision, "Preserve my prompt"));
        Assert.Equal(CommandReceiptState.Completed, (await host.Environment.ExecuteThreadCommandAsync(save, timeout.Token)).State);
        var saved = await host.Environment.GetThreadDraftAsync(thread.ThreadId);
        var run = Request(new ThreadRunPiShellCommand("wait", true));
        Assert.Equal(CommandReceiptState.Accepted, (await host.Environment.ExecuteThreadCommandAsync(run, timeout.Token)).State);
        var live = await ReadShellAsync(stream, shell => shell.Output.Length > 0);
        Assert.Equal("started 😀\n", live.Output);
        Assert.True(live.ExcludeFromContext);
        Assert.Equal(CommandReceiptState.Accepted, (await host.Environment.ExecuteThreadCommandAsync(run, timeout.Token)).State);
        await Assert.ThrowsAsync<HostOperationException>(() => host.Environment.ExecuteThreadCommandAsync(Request(new ThreadStartTurnCommand("must not start")), timeout.Token));
        await Assert.ThrowsAsync<HostOperationException>(() => host.Environment.ExecuteThreadCommandAsync(Request(new ThreadStopTurnCommand()), timeout.Token));
        await Assert.ThrowsAsync<HostOperationException>(() => host.Environment.ExecuteThreadCommandAsync(Request(new ThreadCancelPiShellCommand(CommandId.New())), timeout.Token));
        Assert.Equal(CommandReceiptState.Completed, (await host.Environment.ExecuteThreadCommandAsync(Request(new ThreadCancelPiShellCommand(run.CommandId)), timeout.Token)).State);
        var final = await ReadShellAsync(stream, shell => !shell.IsActive);
        Assert.Equal(PiShellExecutionState.Cancelled, final.State);
        Assert.Null(final.ExitCode);
        // Replaying the accepted request returns its receipt, never starts bash again.
        var receipt = await host.Environment.ExecuteThreadCommandAsync(run, timeout.Token);
        Assert.Equal(CommandReceiptState.Completed, receipt.State);
        var after = await host.Environment.GetThreadDraftAsync(thread.ThreadId);
        Assert.Equal(saved.Text, after.Text);
        Assert.Equal(saved.Revision, after.Revision);
        await using var fresh = host.Environment.SubscribeThreadAsync(thread.ThreadId, null, timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await fresh.MoveNextAsync());
        var snapshot = Assert.IsType<ThreadSnapshotEnvelope>(fresh.Current).Projection;
        Assert.Null(snapshot.CurrentTurnId);
        Assert.Equal("entry-0001", snapshot.LastEntryId);
        Assert.DoesNotContain(snapshot.Timeline, item => item is TurnBoundaryTimelineItem);
        var logs = Directory.GetFiles(directory.Path, "command-log.jsonl", SearchOption.AllDirectories);
        var commands = string.Join('\n', await File.ReadAllLinesAsync(Assert.Single(logs), timeout.Token));
        Assert.Equal(1, commands.Split("\"type\":\"bash\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("\"excludeFromContext\":true", commands);
    }

    [Theory]
    [InlineData("ok", PiShellExecutionState.Completed, 0)]
    [InlineData("fail", PiShellExecutionState.Failed, 7)]
    [InlineData("large", PiShellExecutionState.Completed, 0)]
    [InlineData("reject", PiShellExecutionState.Failed, null)]
    [InlineData("crash", PiShellExecutionState.Interrupted, null)]
    public async Task ResultsSurviveHostRestart(string command, PiShellExecutionState expected, int? exitCode)
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        ThreadId threadId;
        PiShellExecution final;
        await using (var host = await EmbeddedEnvironmentHost.StartAsync(options))
        {
            var project = await host.Environment.AddProjectAsync(new(directory.CreateDirectory("project")));
            threadId = (await host.Environment.CreateThreadAsync(new(project.ProjectId))).ThreadId;
            await using var stream = host.Environment.SubscribeThreadAsync(threadId, null, timeout.Token).GetAsyncEnumerator(timeout.Token);
            Assert.True(await stream.MoveNextAsync());
            var initial = Assert.IsType<ThreadSnapshotEnvelope>(stream.Current);
            var request = new ExecuteThreadCommandRequest(ProtocolVersion.Current, host.Environment.GetDescriptor().EnvironmentId,
                ClientId.New(), CommandId.New(), threadId, initial.ProjectionEpoch, null, new ThreadRunPiShellCommand(command));
            await host.Environment.ExecuteThreadCommandAsync(request, timeout.Token);
            final = await ReadShellAsync(stream, shell => !shell.IsActive);
            Assert.Equal(expected, final.State);
            Assert.Equal(exitCode, final.ExitCode);
            Assert.DoesNotContain("wrong-command", final.Output);
            if (command == "large")
            {
                Assert.True(final.Truncated);
                Assert.Equal(PiShellExecution.MaximumOutputLength, final.Output.Length);
                Assert.EndsWith("last line", final.Output);
                Assert.NotNull(final.FullOutputPath);
            }
        }
        await using var restored = await EmbeddedEnvironmentHost.StartAsync(options);
        await using var restoredStream = restored.Environment.SubscribeThreadAsync(threadId, null, timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await restoredStream.MoveNextAsync());
        var projection = Assert.IsType<ThreadSnapshotEnvelope>(restoredStream.Current).Projection;
        Assert.Equal(final, projection.ShellExecution);
        Assert.Null(projection.CurrentTurnId);
        if (command is "ok" or "fail" or "large")
            Assert.Contains(projection.Timeline, item => item is MessageTimelineItem { Role: MessageRole.System } message && message.Text.Contains("Pi shell:", StringComparison.Ordinal));
    }

    private static async Task<PiShellExecution> ReadShellAsync(IAsyncEnumerator<ThreadEnvelope> stream, Func<PiShellExecution, bool> predicate)
    {
        while (await stream.MoveNextAsync())
        {
            Assert.False(stream.Current is ThreadEventEnvelope { Event: TurnStartedEvent });
            if (stream.Current is ThreadEventEnvelope { Event: PiShellChangedEvent changed } && predicate(changed.Execution))
                return changed.Execution;
        }
        throw new InvalidOperationException("Shell result was not received.");
    }
}
