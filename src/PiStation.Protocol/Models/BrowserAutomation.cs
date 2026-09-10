using System.Text.Json;
using System.Text.Json.Serialization;
using PiStation.Protocol.Identifiers;

namespace PiStation.Protocol.Models;

public enum BrowserAutomationAccess { Off, Inspect, Interact }
public sealed record OpenBrowserAutomationRequest(ThreadId ThreadId, BrowserAutomationAccess Access);
public sealed record BrowserAutomationLease(string Id);
public sealed record BrowserAutomationRequest(string Id, string Operation, JsonElement Input, DateTimeOffset CreatedUtc, string? ControllerId = null);
public sealed record BrowserAutomationPoll(BrowserAutomationRequest? Request, string? ActiveRequestId);
public sealed record BrowserAutomationResult(bool Success, JsonElement? Data = null, string? Error = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] byte[]? ScreenshotPng = null);
public sealed record BrowserRecordingArtifact(string Id, string Path, string MimeType, long SizeBytes, string Sha256,
    DateTimeOffset CreatedUtc);
public sealed record BrowserRecordingChunk(string RequestId, long Offset, byte[] Content, bool Final);

public static class BrowserAutomationLimits
{
    public const int MaximumRequestBytes = 64 * 1024;
    public const int MaximumDataBytes = 256 * 1024;
    public const int MaximumScreenshotBytes = 4 * 1024 * 1024;
    public const int MaximumEvaluationBytes = 64_000;
    public const int MaximumExpressionCharacters = 32_000;
    public const int MaximumRecordingBytes = 64 * 1024 * 1024;
    public const int RecordingChunkBytes = 64 * 1024;
    public static TimeSpan RequestLifetime(string operation) => TimeSpan.FromSeconds(operation == "recording_stop" ? 120 : 30);
    public static bool RequiresInteraction(string operation) => operation is "recording_start" or "recording_stop" or "evaluate" or "open" or "resize" or "set_appearance" or "navigate" or "click" or "type" or "press_key" or "scroll";
    public static bool IsOperation(string operation) => operation is "status" or "snapshot" or "screenshot" or "wait" || RequiresInteraction(operation);
}
