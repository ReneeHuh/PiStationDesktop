using System.Text.Json;
using PiStation.Protocol.Serialization;

namespace PiStation.Protocol.Models;

public sealed record PiAgentPreset(string Name, string Description, string SystemPrompt, IReadOnlyList<string> Tools, string? Model = null);
public sealed record PiAgentTask(string Agent, string Task);
public sealed record PiAgentWorkflow(string Mode, IReadOnlyList<PiAgentTask> Tasks, string? ResumeId = null);
public sealed record PiAgentSetup(string SessionId, bool Available, bool Enabled, string Revision, IReadOnlyList<PiAgentPreset> Presets, string Message)
{
    public static PiAgentSetup Parse(string json)
    {
        if (json.Length > 256 * 1024) throw new JsonException("Agent setup exceeds its size limit.");
        var value = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.PiAgentSetup) ?? throw new JsonException("Agent setup is empty.");
        if (string.IsNullOrWhiteSpace(value.SessionId) || value.Revision is null || value.Revision.Length > 128 ||
            value.Message is null || value.Message.Length > 4000 || value.Presets is null || value.Presets.Count > 16 ||
            value.Presets.Any(p => p is null || p.Name is null || p.Name.Length > 40 || p.Description is null || p.Description.Length > 240 ||
                p.SystemPrompt is null || p.SystemPrompt.Length > 8192 || p.Tools is null || p.Tools.Count > 8 || p.Tools.Any(t => t is null || t.Length > 32) || p.Model?.Length > 240))
            throw new JsonException("Pi returned invalid agent setup.");
        return value;
    }
}
