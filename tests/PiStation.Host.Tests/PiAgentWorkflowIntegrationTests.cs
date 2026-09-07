using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using PiStation.Host.Hosting;
using PiStation.Protocol;
using PiStation.Protocol.Commands;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Receipts;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Tests;

public sealed class PiAgentWorkflowIntegrationTests
{
    [Fact]
    public async Task MaximumUnicodePresetAndEightTaskWorkflowFitTheAuthenticatedTransport()
    {
        using var directory = new HostTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateOptions("agent-workflow") with { AgentExtensionPath = "fake-agent-extension" });
        await using var connection = HostTestConnection.Create(host);
        await connection.StartAsync(timeout.Token);
        var project = await host.Environment.AddProjectAsync(new(directory.CreateDirectory("project")), timeout.Token);
        var thread = await host.Environment.CreateThreadAsync(new(project.ProjectId), timeout.Token);
        var client = ClientId.New();
        ExecuteThreadCommandRequest Request(ThreadCommand command) => new(ProtocolVersion.Current, thread.EnvironmentId, client, CommandId.New(), thread.ThreadId, null, null, command);
        var setup = (await Snapshot(host.Environment, thread.ThreadId, timeout.Token)).AgentSetup!;
        var text = new string('語', 8192);
        await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", Request(new ThreadManageAgentsCommand("save", setup.Revision, new("unicode", "Unicode instructions", text, ["read"]))), timeout.Token);
        var workflow = new PiAgentWorkflow("parallel", Enumerable.Range(0, 8).Select(_ => new PiAgentTask("unicode", text)).ToArray());
        await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", Request(new ThreadRunAgentWorkflowCommand(workflow)), timeout.Token);
        var completed = await WaitFor(host.Environment, thread.ThreadId, p => p.RuntimeState == ThreadRuntimeState.Ready && p.Messages.Count == 2, timeout.Token);
        Assert.Equal(8, completed.AgentActivities!.Count(a => a.Kind == AgentActivityKind.Agent && a.State == AgentActivityState.Completed));
        Assert.DoesNotContain("PISTATION_AGENT_WORKFLOW", completed.Messages[0].Text);
    }

    [Fact]
    public async Task PresetsTargetedStopReplayAndChildHistoryPersistWithoutConsumingDrafts()
    {
        using var directory = new HostTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var options = directory.CreateOptions("agent-workflow") with { AgentExtensionPath = "fake-agent-extension" };
        ThreadDescriptor thread;
        ExecuteThreadCommandRequest execution;
        string controlId;
        await using (var host = await EmbeddedEnvironmentHost.StartAsync(options))
        {
            await using var connection = HostTestConnection.Create(host);
            await connection.StartAsync(timeout.Token);
            var project = await host.Environment.AddProjectAsync(new(directory.CreateDirectory("project")), timeout.Token);
            thread = await host.Environment.CreateThreadAsync(new(project.ProjectId), timeout.Token);
            var client = ClientId.New();
            ExecuteThreadCommandRequest Request(ThreadCommand command) => new(ProtocolVersion.Current, thread.EnvironmentId, client, CommandId.New(), thread.ThreadId, null, null, command);
            async Task Act(ThreadCommand command) => await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", Request(command), timeout.Token);
            var snapshot = await Snapshot(host.Environment, thread.ThreadId, timeout.Token);
            Assert.True(snapshot.AgentSetup!.Enabled);
            var preset = new PiAgentPreset("custom", "Custom read-only agent", "Inspect the requested code.", ["read", "grep"]);
            await Act(new ThreadManageAgentsCommand("save", snapshot.AgentSetup.Revision, preset));
            await Assert.ThrowsAsync<HubException>(() => Act(new ThreadManageAgentsCommand("disable", snapshot.AgentSetup.Revision)));
            snapshot = await Snapshot(host.Environment, thread.ThreadId, timeout.Token);
            Assert.Contains(snapshot.AgentSetup!.Presets, p => p.Name == "custom");
            var draft = await host.Environment.GetThreadDraftAsync(thread.ThreadId, timeout.Token);
            await Act(new ThreadSaveDraftCommand(draft.DraftId, draft.Revision, "Preserve the unsent draft"));
            execution = Request(new ThreadRunAgentWorkflowCommand(new("parallel", [new("custom", "WAIT ONE"), new("scout", "WAIT TWO")])));
            await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", execution, timeout.Token);
            snapshot = await WaitFor(host.Environment, thread.ThreadId, p => p.AgentActivities?.Count(a => a.Kind == AgentActivityKind.Agent && a.State == AgentActivityState.Running) == 2, timeout.Token);
            var children = snapshot.AgentActivities!.Where(a => a.Kind == AgentActivityKind.Agent).ToArray();
            Assert.All(children, child => { Assert.True(child.CanInterrupt); Assert.Contains("Child session evidence", child.Transcript); });
            var stopReceipt = await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", Request(new ThreadInterruptAgentCommand(children[0].ActivityId)), timeout.Token);
            Assert.Equal(CommandReceiptState.Completed, stopReceipt.State);
            snapshot = await WaitFor(host.Environment, thread.ThreadId, p => p.AgentActivities!.Any(a => a.ActivityId == children[0].ActivityId && a.State == AgentActivityState.Interrupted), timeout.Token);
            Assert.Equal(ThreadRuntimeState.Running, snapshot.RuntimeState);
            Assert.Equal(AgentActivityState.Running, snapshot.AgentActivities!.Single(a => a.ActivityId == children[1].ActivityId).State);
            await Act(new ThreadStopTurnCommand());
            snapshot = await WaitFor(host.Environment, thread.ThreadId, p => p.RuntimeState == ThreadRuntimeState.Ready, timeout.Token);
            Assert.Equal("0 completed • 2 interrupted", snapshot.AgentActivities!.Single(a => a.Kind == AgentActivityKind.Workflow).CurrentActivity);
            Assert.All(snapshot.AgentActivities!.Where(a => a.Kind == AgentActivityKind.Agent), a => Assert.Equal("Child interrupted", a.FailureSummary));
            controlId = snapshot.AgentActivities!.Single(a => a.ActivityId == children[0].ActivityId).ControlId!;
            Assert.Equal("Preserve the unsent draft", (await host.Environment.GetThreadDraftAsync(thread.ThreadId, timeout.Token)).Text);
            await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", execution, timeout.Token);
            Assert.Equal(2, (await Snapshot(host.Environment, thread.ThreadId, timeout.Token)).Messages.Count);
            var other = await host.Environment.CreateThreadAsync(new(project.ProjectId), timeout.Token);
            Assert.Empty((await Snapshot(host.Environment, other.ThreadId, timeout.Token)).AgentActivities!);
        }
        await using var restarted = await EmbeddedEnvironmentHost.StartAsync(options);
        var restored = await Snapshot(restarted.Environment, thread.ThreadId, timeout.Token);
        Assert.Contains(restored.AgentSetup!.Presets, p => p.Name == "custom");
        Assert.Contains(restored.AgentActivities!, a => a.ControlId == controlId && a.CanResume && a.Transcript!.Contains("Child session evidence"));
        await restarted.Environment.ExecuteThreadCommandAsync(execution with { CommandId = CommandId.New(), Command = new ThreadRunAgentWorkflowCommand(new("single", [new("custom", "CONTINUE")], controlId)) }, timeout.Token);
        var continued = await WaitFor(restarted.Environment, thread.ThreadId, p => p.RuntimeState == ThreadRuntimeState.Ready && p.Messages.Count == 4, timeout.Token);
        Assert.Contains(continued.AgentActivities!, a => a.Transcript?.Contains("Remembered the previous child result.") == true);
    }

    private static async Task<ThreadProjection> Snapshot(EnvironmentService environment, ThreadId thread, CancellationToken cancellationToken)
    {
        await using var stream = environment.SubscribeThreadAsync(thread, null, cancellationToken).GetAsyncEnumerator(cancellationToken);
        Assert.True(await stream.MoveNextAsync());
        return Assert.IsType<ThreadSnapshotEnvelope>(stream.Current).Projection;
    }
    private static async Task<ThreadProjection> WaitFor(EnvironmentService environment, ThreadId thread, Func<ThreadProjection, bool> condition, CancellationToken cancellationToken)
    {
        while (true)
        {
            var snapshot = await Snapshot(environment, thread, cancellationToken);
            if (condition(snapshot)) return snapshot;
            await Task.Delay(20, cancellationToken);
        }
    }
}
