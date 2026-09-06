using System.Text.Json;
using PiStation.Host.Threads;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Projections;

namespace PiStation.Host.Tests;

public sealed class PiAgentActivityProjectorTests
{
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
                  "messages": [],
                  "stderr": "review command failed",
                  "usage": { "input": 20, "output": 0, "cacheRead": 0, "cacheWrite": 0 }
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
        Assert.NotNull(failed.CompletedUtc);
    }
}
