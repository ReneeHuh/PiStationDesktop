using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed class PiModelOptionViewModel(PiModelCapability capability)
{
    public string ProviderId { get; } = capability.ProviderId;

    public string ModelId { get; } = capability.ModelId;

    public string DisplayName { get; } = capability.DisplayName;

    public bool SupportsReasoning { get; } = capability.SupportsReasoning;

    public PiModelSelection Selection => new(ProviderId, ModelId);
}

public sealed class PiThinkingLevelOptionViewModel(PiThinkingLevel value)
{
    public PiThinkingLevel Value { get; } = value;

    public string DisplayName { get; } = value switch
    {
        PiThinkingLevel.Off => "Off",
        PiThinkingLevel.Minimal => "Minimal",
        PiThinkingLevel.Low => "Low",
        PiThinkingLevel.Medium => "Medium",
        PiThinkingLevel.High => "High",
        PiThinkingLevel.XHigh => "Extra high",
        PiThinkingLevel.Max => "Maximum",
        _ => value.ToString(),
    };
}
