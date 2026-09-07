using System.Text;
using System.Text.Json.Nodes;
using System.Globalization;

namespace PiStation.FakePi;

internal sealed partial class FakePiServer
{
    private static readonly string[] FixtureAgentNames = ["scout", "planner", "reviewer", "worker"];
    private JsonObject? _agentWorkflow;
    private JsonObject? _preparedAgentWorkflow;
    private JsonArray _agentResults = [];
    private string _agentCall = string.Empty, _agentPrompt = string.Empty;
    private string AgentSettingsPath => Environment.GetEnvironmentVariable("PISTATION_AGENT_SETTINGS") ?? Path.Combine(_arguments.SessionDirectory, "agent-presets.json");
    private JsonObject ReadAgentSettings()
    {
        var settings = File.Exists(AgentSettingsPath) ? JsonNode.Parse(File.ReadAllText(AgentSettingsPath))!.AsObject() : new JsonObject
        {
            ["enabled"] = true, ["revision"] = "0", ["presets"] = new JsonArray(FixtureAgentNames.Select(name => (JsonNode)new JsonObject
            { ["name"] = name, ["description"] = "Fixture " + name, ["systemPrompt"] = "Complete the delegated task.", ["tools"] = new JsonArray("read", "grep", "find", "ls"), ["model"] = null }).ToArray()),
        };
        settings["sessionId"] = _arguments.SessionId; settings["available"] = true; settings["message"] = "Bundled integration fixture.";
        return settings;
    }
    private async Task HandleAgentsCommandAsync(string id, string prompt, CancellationToken cancellationToken)
    {
        var payload = prompt.Split(' ', 2)[1].Replace('-', '+').Replace('_', '/');
        var request = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '='))))!;
        await _writer.WriteAsync(Response(id, "prompt"), cancellationToken: cancellationToken);
        var setup = ReadAgentSettings();
        var action = request["action"]!.ToString();
        string? error = null;
        if (action == "stop")
        {
            var child = _agentResults.FirstOrDefault(child => child!["controlId"]!.ToString() == request["controlId"]?.ToString() && child["status"]!.ToString() == "running");
            if (child is null) error = "This child is no longer running.";
            else { child["status"] = "interrupted"; child["stopReason"] = "aborted"; child["exitCode"] = 1; child["canResume"] = true; }
        }
        else if (action == "prepare") _preparedAgentWorkflow = request["workflow"]!.DeepClone().AsObject();
        else if (action != "inspect")
        {
            if (request["expectedRevision"]?.ToString() != setup["revision"]!.ToString()) error = "Agent settings changed. Refresh before saving.";
            else
            {
                if (action is "enable" or "disable") setup["enabled"] = action == "enable";
                if (action is "save" or "delete")
                {
                    var presets = setup["presets"]!.AsArray();
                    var previous = presets.FirstOrDefault(p => p!["name"]!.ToString() == request["preset"]!["name"]!.ToString());
                    if (previous is not null) presets.Remove(previous);
                    if (action == "save") presets.Add(request["preset"]!.DeepClone());
                }
                setup["revision"] = (int.Parse(setup["revision"]!.ToString(), CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
                Directory.CreateDirectory(Path.GetDirectoryName(AgentSettingsPath)!);
                File.WriteAllText(AgentSettingsPath, setup.ToJsonString());
            }
        }
        await _writer.WriteAsync(new JsonObject
        {
            ["type"] = "extension_ui_request", ["id"] = Guid.NewGuid().ToString("N"), ["method"] = "setStatus", ["statusKey"] = "pistation-management:" + request["id"],
            ["statusText"] = new JsonObject { ["success"] = error is null, ["error"] = error, ["data"] = setup }.ToJsonString(),
        }, cancellationToken: cancellationToken);
        if (action == "stop" && error is null)
        {
            await PublishAgentResultsAsync(false, cancellationToken);
            if (_agentResults.All(child => child!["status"]!.ToString() != "running")) await FinishAgentWorkflowAsync(cancellationToken);
        }
    }
    private async Task HandleAgentPromptAsync(string id, string prompt, CancellationToken cancellationToken)
    {
        const string marker = "PISTATION_AGENT_WORKFLOW\n";
        var start = prompt.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0 && _preparedAgentWorkflow is null) { await HandlePromptAsync(id, prompt, null, cancellationToken); return; }
        _agentWorkflow = _preparedAgentWorkflow ?? JsonNode.Parse(prompt[(start + marker.Length)..])!.AsObject();
        _preparedAgentWorkflow = null;
        _agentPrompt = prompt; _agentCall = Guid.NewGuid().ToString("N"); _isStreaming = true;
        _agentResults = new JsonArray(_agentWorkflow["tasks"]!.AsArray().Select((task, index) => (JsonNode)new JsonObject
        {
            ["agent"] = task!["agent"]!.ToString(), ["task"] = task["task"]!.ToString(), ["controlId"] = Guid.NewGuid().ToString("N"),
            ["status"] = task["task"]!.ToString().Contains("WAIT", StringComparison.Ordinal) ? "running" : "completed",
            ["exitCode"] = task["task"]!.ToString().Contains("WAIT", StringComparison.Ordinal) ? -1 : 0,
            ["canResume"] = true, ["toolCount"] = 1, ["model"] = "fake/fake-standard", ["step"] = _agentWorkflow["mode"]!.ToString() == "chain" ? index + 1 : null,
            ["currentActivity"] = task["task"]!.ToString().Contains("WAIT", StringComparison.Ordinal) ? "Waiting for fixture completion" : "Completed",
            ["transcript"] = "user:\n" + task["task"] + "\n\ntoolResult:\nChild session evidence\n\nassistant:\n" + (_agentWorkflow["resumeId"] is null ? "Completed delegated task." : "Remembered the previous child result."),
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Completed delegated task." }) }),
            ["usage"] = new JsonObject { ["input"] = 10, ["output"] = 5 },
        }).ToArray());
        await _writer.WriteAsync(Response(id, "prompt"), cancellationToken: cancellationToken);
        await _writer.WriteAsync(new JsonObject { ["type"] = "agent_start" }, cancellationToken: cancellationToken);
        await _writer.WriteAsync(new JsonObject { ["type"] = "tool_execution_start", ["toolCallId"] = _agentCall, ["toolName"] = "pistation_subagent", ["args"] = _agentWorkflow.DeepClone() }, cancellationToken: cancellationToken);
        await PublishAgentResultsAsync(false, cancellationToken);
        if (_agentResults.All(child => child!["status"]!.ToString() != "running")) await FinishAgentWorkflowAsync(cancellationToken);
    }
    private Task PublishAgentResultsAsync(bool final, CancellationToken cancellationToken) => _writer.WriteAsync(new JsonObject
    {
        ["type"] = final ? "tool_execution_end" : "tool_execution_update", ["toolCallId"] = _agentCall, ["toolName"] = "pistation_subagent", ["args"] = _agentWorkflow!.DeepClone(), ["isError"] = final && _agentResults.Any(child => child!["status"]!.ToString() != "completed"),
        [final ? "result" : "partialResult"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Workflow results" }),
            ["details"] = new JsonObject { ["integration"] = "pistation", ["mode"] = _agentWorkflow["mode"]!.ToString(), ["results"] = _agentResults.DeepClone() } },
    }, cancellationToken: cancellationToken);
    private async Task FinishAgentWorkflowAsync(CancellationToken cancellationToken)
    {
        await PublishAgentResultsAsync(true, cancellationToken);
        _isStreaming = false;
        const string answer = "Agent workflow finished.";
        await _session.AppendTurnAsync(_agentPrompt, answer, cancellationToken);
        var message = FakeSessionStore.AssistantMessage(answer);
        await _writer.WriteAsync(new JsonObject { ["type"] = "message_start", ["message"] = message.DeepClone() }, cancellationToken: cancellationToken);
        await _writer.WriteAsync(new JsonObject { ["type"] = "message_end", ["message"] = message.DeepClone() }, cancellationToken: cancellationToken);
        await WriteSettlementAsync(message, cancellationToken);
    }
}
