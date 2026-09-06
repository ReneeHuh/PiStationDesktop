using System.Text.Json;
using PiStation.Protocol.Serialization;

namespace PiStation.Protocol.Models;

public sealed record PiPlanStep(int Number, string Text, bool Completed);

public sealed record PiPlanState(string SessionId, long Revision, string Mode, string Text,
    IReadOnlyList<PiPlanStep> Steps, DateTimeOffset UpdatedUtc)
{
    public static PiPlanState Parse(string json)
    {
        if (json.Length > 512 * 1024) throw new JsonException("Plan state exceeds its size limit.");
        var state = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.PiPlanState)
            ?? throw new JsonException("Plan state is empty.");
        if (string.IsNullOrWhiteSpace(state.SessionId) || state.Revision < 0 ||
            state.Mode is not ("off" or "planning" or "ready" or "executing" or "paused" or "completed") ||
            state.Text is null || state.Text.Length > 32 * 1024 || state.Steps is null || state.Steps.Count > 100 ||
            state.Steps.Where((step, index) => step is null || step.Number != index + 1 || step.Text is null || step.Text.Length > 2000).Any())
            throw new JsonException("Pi returned invalid plan state.");
        return state;
    }
}
