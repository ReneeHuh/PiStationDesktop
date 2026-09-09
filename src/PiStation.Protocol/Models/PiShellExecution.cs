using System.Text.Json.Serialization;
using PiStation.Protocol.Identifiers;

namespace PiStation.Protocol.Models;

public enum PiShellExecutionState { Running, CancelRequested, Completed, Failed, Cancelled, Interrupted }

public sealed record PiShellExecution(
    CommandId CommandId, ClientId ClientId, string Command, bool ExcludeFromContext,
    PiShellExecutionState State, string Output, bool Truncated, int? ExitCode,
    string? FullOutputPath, string? Error, DateTimeOffset StartedUtc, DateTimeOffset? CompletedUtc = null)
{
    public const int MaximumCommandLength = 32 * 1024;
    public const int MaximumOutputLength = 64 * 1024;

    [JsonIgnore]
    public bool IsActive => State is PiShellExecutionState.Running or PiShellExecutionState.CancelRequested;
}
