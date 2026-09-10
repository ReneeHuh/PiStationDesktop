using System.Text.Json;
using PiStation.Host.Threads;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Projections;

namespace PiStation.Host.Tests;

public sealed class PiAgentActivityProjectorTests
{
    [Fact]
    public void ExternalAdapterHandleEnablesTargetedStopWithoutClaimingBundledResume()
    {
        var id = Guid.NewGuid().ToString("N");
        using var result = JsonDocument.Parse("""{"details":{"integration":"pistation-external-v1","mode":"single","results":[{"agent":"external","status":"running","controlId":"CONTROL_ID","canResume":true}]}}""".Replace("CONTROL_ID", id, StringComparison.Ordinal));
        var projected = PiAgentActivityProjector.Update("external-tool", result.RootElement, [], false, false, null, DateTimeOffset.UnixEpoch);
        var child = Assert.Single(projected);
        Assert.Equal(id, child.ControlId);
        Assert.True(child.CanInterrupt);
        Assert.False(child.CanResume);
    }

    [Fact]
    public void RejectedNativeChainFinishesPendingChildrenWithoutInventingResults()
    {
        using var args = JsonDocument.Parse("""{"mode":"chain","tasks":[{"agent":"scout","task":"Read"},{"agent":"missing","task":"Never"}]}""");
        var started = PiAgentActivityProjector.Start("native-rejected", "pistation_subagent", args.RootElement, null, DateTimeOffset.UnixEpoch);
        Assert.Equal(3, started.Count);
        using var error = JsonDocument.Parse("""{"content":[{"type":"text","text":"Unknown agent preset"}]}""");
        var result = PiAgentActivityProjector.Update("native-rejected", error.RootElement, started, true, true, null, DateTimeOffset.UnixEpoch.AddSeconds(1));
        Assert.Equal(AgentActivityState.Failed, result[0].State);
        Assert.All(result.Skip(1), child => { Assert.Equal(AgentActivityState.Interrupted, child.State); Assert.False(child.CanInterrupt); Assert.Null(child.ResultSummary); });
    }

    [Fact]
    public void ChainedWorkflowKeepsPendingStepsWhileStreamingAndProjectsFinalUsage()
    {
        var startedUtc = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var turnId = TurnId.Parse("turn-agent-test");
        using var argumentsDocument = JsonDocument.Parse("""
            {
              "mode": "chain",
              "chain": [
                { "agent": "scout", "task": "Inspect the code" },
                { "agent": "reviewer", "task": "Review the result" }
              ]
            }
            """);
        var started = PiAgentActivityProjector.Start(
            "call-1",
            "subagent",
            argumentsDocument.RootElement,
            turnId,
            startedUtc);
        using var partialDocument = JsonDocument.Parse("""
            {
              "content": [{ "type": "text", "text": "Step one finished" }],
              "details": {
                "mode": "chain",
                "results": [{
                  "agent": "scout",
                  "task": "Inspect the code",
                  "exitCode": 0,
                  "messages": [{
                    "role": "assistant",
                    "content": [{ "type": "text", "text": "Found the relevant files" }]
                  }],
                  "usage": { "input": 100, "output": 25, "cacheRead": 10, "cacheWrite": 0 },
                  "model": "fake-standard",
                  "step": 1
                }]
              }
            }
            """);

        var partial = PiAgentActivityProjector.Update(
            "call-1",
            partialDocument.RootElement,
            started,
            isFinal: false,
            isError: false,
            turnId,
            startedUtc.AddSeconds(2));
        var workflow = Assert.Single(partial, static activity => activity.Kind == AgentActivityKind.Workflow);
        var firstAgent = Assert.Single(partial, static activity => activity.Kind == AgentActivityKind.Agent);

        Assert.Equal(3, started.Count);
        Assert.True(started[0].CanInterrupt);
        Assert.All(started.Skip(1), static activity => Assert.False(activity.CanInterrupt));
        Assert.Equal(AgentActivityState.Waiting, workflow.State);
        Assert.Contains("1 active", workflow.CurrentActivity, StringComparison.Ordinal);
        Assert.Equal(AgentActivityState.Completed, firstAgent.State);
        Assert.Equal(135, firstAgent.Usage?.TotalTokens);
        Assert.Equal("Found the relevant files", firstAgent.ResultSummary);
        Assert.Equal("fake-standard", firstAgent.Model);
    }

    [Fact]
    public void FailedAgentCapturesFailureAndCannotBeInterrupted()
    {
        var startedUtc = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        using var argumentsDocument = JsonDocument.Parse("""
            { "agent": "reviewer", "task": "Review the change" }
            """);
        var started = PiAgentActivityProjector.Start(
            "call-2",
            "subagent",
            argumentsDocument.RootElement,
            TurnId.Parse("turn-agent-failure"),
            startedUtc);
        using var resultDocument = JsonDocument.Parse("""
            {
              "content": [{ "type": "text", "text": "Agent failed" }],
              "details": {
                "mode": "single",
                "results": [{
                  "agent": "reviewer",
                  "task": "Review the change",
                  "exitCode": 1,
                  "step": null,
                  "toolCount": null,
                  "messages": [],
                  "stderr": "review command failed",
                  "usage": { "input": 20, "output": 0, "cacheRead": null, "cacheWrite": 0 }
                }]
              }
            }
            """);

        var failed = Assert.Single(PiAgentActivityProjector.Update(
            "call-2",
            resultDocument.RootElement,
            started,
            isFinal: true,
            isError: true,
            TurnId.Parse("turn-agent-failure"),
            startedUtc.AddSeconds(1)));

        Assert.Equal(AgentActivityState.Failed, failed.State);
        Assert.Equal("review command failed", failed.FailureSummary);
        Assert.False(failed.CanInterrupt);
        Assert.Null(failed.Step);
        Assert.Equal(20, failed.Usage?.TotalTokens);
        Assert.NotNull(failed.CompletedUtc);
    }
}
