using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiStation.FakePi;

internal sealed partial class FakePiServer
{
    private JsonObject? _plan;
    private string PlanPath => Path.Combine(_arguments.SessionDirectory, _arguments.SessionId + ".plan.json");
    private JsonObject ReadPlan()
    {
        if (_plan is not null) return _plan;
        _plan = File.Exists(PlanPath) ? JsonNode.Parse(File.ReadAllText(PlanPath))!.AsObject() : new JsonObject
        {
            ["sessionId"] = _arguments.SessionId, ["revision"] = 0, ["mode"] = "off", ["text"] = "", ["steps"] = new JsonArray(), ["updatedUtc"] = DateTimeOffset.UtcNow,
        };
        if (_plan["mode"]!.ToString() == "executing") { _plan["mode"] = "paused"; SavePlan(); }
        return _plan;
    }
    private void SavePlan()
    {
        _plan!["revision"] = _plan["revision"]!.GetValue<int>() + 1;
        _plan["updatedUtc"] = DateTimeOffset.UtcNow;
        File.WriteAllText(PlanPath, _plan.ToJsonString());
    }
    private Task PublishPlanAsync(CancellationToken cancellationToken) => _writer.WriteAsync(new JsonObject
    {
        ["type"] = "extension_ui_request", ["id"] = Guid.NewGuid().ToString("N"), ["method"] = "setStatus",
        ["statusKey"] = "pistation-plan-state", ["statusText"] = ReadPlan().ToJsonString(),
    }, cancellationToken: cancellationToken);
    private async Task HandlePlanCommandAsync(string promptId, string prompt, CancellationToken cancellationToken)
    {
        var payload = prompt.Split(' ', 2)[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
        var request = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)))!;
        await _writer.WriteAsync(Response(promptId, "prompt"), cancellationToken: cancellationToken).ConfigureAwait(false);
        var plan = ReadPlan();
        string? error = null;
        var action = request["action"]!.ToString();
        if (action != "inspect" && request["expectedRevision"]?.GetValue<int>() != plan["revision"]!.GetValue<int>()) error = "The plan changed. Refresh and review it before retrying.";
        if (error is null && action == "save")
        {
            var text = (request["text"]?.ToString() ?? "").ReplaceLineEndings("\n");
            var steps = new JsonArray();
            foreach (Match match in PlanSteps().Matches(text)) steps.Add(new JsonObject { ["number"] = steps.Count + 1, ["text"] = match.Groups[1].Value, ["completed"] = false });
            if (steps.Count == 0) error = "Use a numbered list with at least one plan step.";
            else { plan["text"] = text; plan["steps"] = steps; plan["mode"] = "ready"; }
        }
        if (error is null && action == "execute")
        {
            if (plan["mode"]!.ToString() is not ("ready" or "paused")) error = "Review a plan before executing.";
            else plan["mode"] = "executing";
        }
        if (error is null && action != "inspect")
        {
            if (action == "plan") plan["mode"] = "planning";
            if (action == "off") plan["mode"] = "off";
            SavePlan();
        }
        if (error is null) await PublishPlanAsync(cancellationToken).ConfigureAwait(false);
        await _writer.WriteAsync(new JsonObject
        {
            ["type"] = "extension_ui_request", ["id"] = Guid.NewGuid().ToString("N"), ["method"] = "setStatus", ["statusKey"] = "pistation-management:" + request["id"],
            ["statusText"] = new JsonObject { ["success"] = error is null, ["error"] = error, ["data"] = plan.DeepClone() }.ToJsonString(),
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
    private async Task HandlePlanPromptAsync(string id, string prompt, CancellationToken cancellationToken)
    {
        await _writer.WriteAsync(Response(id, "prompt"), cancellationToken: cancellationToken).ConfigureAwait(false);
        await _writer.WriteAsync(new JsonObject { ["type"] = "agent_start" }, cancellationToken: cancellationToken).ConfigureAwait(false);
        var plan = ReadPlan();
        string answer;
        if (plan["mode"]!.ToString() == "executing")
        {
            var first = plan["steps"]!.AsArray().First(step => !step!["completed"]!.GetValue<bool>())!;
            first["completed"] = true;
            answer = "Completed the next step.\n[DONE:" + first["number"] + "]";
            plan["mode"] = plan["steps"]!.AsArray().All(step => step!["completed"]!.GetValue<bool>()) ? "completed" : "paused";
        }
        else
        {
            answer = "Plan:\n1. Inspect the project\n2. Implement the change";
            plan["text"] = answer;
            plan["steps"] = new JsonArray(new JsonObject { ["number"] = 1, ["text"] = "Inspect the project", ["completed"] = false }, new JsonObject { ["number"] = 2, ["text"] = "Implement the change", ["completed"] = false });
            plan["mode"] = "ready";
        }
        await _session.AppendTurnAsync(prompt, answer, cancellationToken).ConfigureAwait(false);
        var message = FakeSessionStore.AssistantMessage(answer);
        await _writer.WriteAsync(new JsonObject { ["type"] = "message_start", ["message"] = message.DeepClone() }, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _writer.WriteAsync(new JsonObject { ["type"] = "message_end", ["message"] = message.DeepClone() }, cancellationToken: cancellationToken).ConfigureAwait(false);
        SavePlan();
        await PublishPlanAsync(cancellationToken).ConfigureAwait(false);
        await WriteSettlementAsync(message, cancellationToken).ConfigureAwait(false);
    }
    [GeneratedRegex(@"^\s*\d+[.)]\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex PlanSteps();
}
