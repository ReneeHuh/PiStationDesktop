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

public sealed class PiPlanIntegrationTests
{
    [Fact]
    public async Task PlanApprovalReplayDraftPreservationAndProgressSurviveRestart()
    {
        using var directory = new HostTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var options = directory.CreateOptions("plan-workflow") with { PlanExtensionPath = "fake-plan-extension" };
        ThreadDescriptor thread;
        ExecuteThreadCommandRequest execution;
        await using (var host = await EmbeddedEnvironmentHost.StartAsync(options))
        {
            await using var connection = HostTestConnection.Create(host);
            await connection.StartAsync(timeout.Token);
            var project = await host.Environment.AddProjectAsync(new(directory.CreateDirectory("project")), timeout.Token);
            thread = await host.Environment.CreateThreadAsync(new(project.ProjectId), timeout.Token);
            var client = ClientId.New();
            ExecuteThreadCommandRequest Request(ThreadCommand command) => new(ProtocolVersion.Current, thread.EnvironmentId, client, CommandId.New(), thread.ThreadId, null, null, command);
            async Task Act(ThreadCommand command) => await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", Request(command), timeout.Token);
            var initial = await Snapshot(host.Environment, thread.ThreadId, timeout.Token);
            Assert.Equal("off", initial.Plan!.Mode);
            var draft = await host.Environment.GetThreadDraftAsync(thread.ThreadId, timeout.Token);
            await Act(new ThreadSaveDraftCommand(draft.DraftId, draft.Revision, "Keep this unsent draft"));
            await Act(new ThreadManagePlanCommand("plan", initial.Plan.Revision));
            await Act(new ThreadStartTurnCommand("Create a plan"));
            var ready = await WaitForPlan(host.Environment, thread.ThreadId, "ready", timeout.Token);
            Assert.Equal(2, ready.Plan!.Steps.Count);
            await Act(new ThreadManagePlanCommand("save", ready.Plan.Revision, "Plan:\n1. Edited first step\n2. Verify"));
            ready = await Snapshot(host.Environment, thread.ThreadId, timeout.Token);
            await Assert.ThrowsAsync<HubException>(() => Act(new ThreadManagePlanCommand("save", 0, "Plan:\n1. Stale overwrite")));
            execution = Request(new ThreadManagePlanCommand("execute", ready.Plan!.Revision));
            await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", execution, timeout.Token);
            var paused = await WaitForPlan(host.Environment, thread.ThreadId, "paused", timeout.Token);
            Assert.True(paused.Plan!.Steps[0].Completed);
            Assert.False(paused.Plan.Steps[1].Completed);
            Assert.Equal("Edited first step", paused.Plan.Steps[0].Text);
            await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", execution, timeout.Token);
            Assert.Equal(4, (await Snapshot(host.Environment, thread.ThreadId, timeout.Token)).Messages.Count);
            Assert.Equal("Keep this unsent draft", (await host.Environment.GetThreadDraftAsync(thread.ThreadId, timeout.Token)).Text);
            var other = await host.Environment.CreateThreadAsync(new(project.ProjectId), timeout.Token);
            Assert.Equal("off", (await Snapshot(host.Environment, other.ThreadId, timeout.Token)).Plan!.Mode);
        }
        await using var restarted = await EmbeddedEnvironmentHost.StartAsync(options);
        var restored = await Snapshot(restarted.Environment, thread.ThreadId, timeout.Token);
        Assert.Equal("paused", restored.Plan!.Mode);
        Assert.True(restored.Plan.Steps[0].Completed);
        await restarted.Environment.ExecuteThreadCommandAsync(execution, timeout.Token);
        Assert.Equal(4, (await Snapshot(restarted.Environment, thread.ThreadId, timeout.Token)).Messages.Count);
        Assert.Equal("Keep this unsent draft", (await restarted.Environment.GetThreadDraftAsync(thread.ThreadId, timeout.Token)).Text);
        await restarted.Environment.ExecuteThreadCommandAsync(execution with { CommandId = CommandId.New(), Command = new ThreadManagePlanCommand("execute", restored.Plan.Revision) }, timeout.Token);
        Assert.All((await WaitForPlan(restarted.Environment, thread.ThreadId, "completed", timeout.Token)).Plan!.Steps, step => Assert.True(step.Completed));
    }

    [Fact]
    public async Task RequiredPolicyMissingPreventsRuntimeReadiness()
    {
        using var directory = new HostTestDirectory();
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateOptions() with { PlanExtensionPath = "missing-policy" });
        var project = await host.Environment.AddProjectAsync(new(directory.CreateDirectory("project")));
        var thread = await host.Environment.CreateThreadAsync(new(project.ProjectId));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Snapshot(host.Environment, thread.ThreadId, CancellationToken.None));
        Assert.Contains("management extension is unavailable", error.Message);
        Assert.False(File.Exists(Path.Combine(directory.CreateOptions().SessionRoot, thread.PiSessionId + ".jsonl")));
    }

    private static async Task<ThreadProjection> Snapshot(EnvironmentService environment, ThreadId thread, CancellationToken cancellationToken)
    {
        await using var stream = environment.SubscribeThreadAsync(thread, null, cancellationToken).GetAsyncEnumerator(cancellationToken);
        Assert.True(await stream.MoveNextAsync());
        return Assert.IsType<ThreadSnapshotEnvelope>(stream.Current).Projection;
    }

    private static async Task<ThreadProjection> WaitForPlan(EnvironmentService environment, ThreadId thread, string mode, CancellationToken cancellationToken)
    {
        while (true)
        {
            var snapshot = await Snapshot(environment, thread, cancellationToken);
            if (snapshot.RuntimeState == ThreadRuntimeState.Ready && snapshot.Plan?.Mode == mode) return snapshot;
            await Task.Delay(20, cancellationToken);
        }
    }
}
