using PiStation.Protocol.Identifiers;

namespace PiStation.Protocol.Models;

public static class TerminalDefaults
{
    public const int DefaultColumns = 100;
    public const int DefaultRows = 30;
    public const int MinimumColumns = 20;
    public const int MaximumColumns = 500;
    public const int MinimumRows = 5;
    public const int MaximumRows = 200;
    public const int MaximumInputCharacters = 16 * 1024;
}

public enum TerminalShellKind
{
    PowerShell,
    CommandPrompt,
}

public enum TerminalSessionState
{
    Running,
    Exited,
    Failed,
    Interrupted,
}

public sealed record StartTerminalSessionRequest(
    ProjectId ProjectId,
    TerminalShellKind ShellKind = TerminalShellKind.PowerShell,
    int Columns = TerminalDefaults.DefaultColumns,
    int Rows = TerminalDefaults.DefaultRows,
    ThreadId? ThreadId = null);

public sealed record WriteTerminalInputRequest(TerminalSessionId TerminalSessionId, string Data);

public sealed record ResizeTerminalSessionRequest(
    TerminalSessionId TerminalSessionId,
    int Columns,
    int Rows);

public sealed record StopTerminalSessionRequest(TerminalSessionId TerminalSessionId);

public sealed record CloseTerminalSessionRequest(TerminalSessionId TerminalSessionId);
public sealed record ClearTerminalHistoryRequest(TerminalSessionId TerminalSessionId);

public sealed record TerminalSessionDescriptor(
    TerminalSessionId TerminalSessionId,
    ProjectId ProjectId,
    string Name,
    TerminalShellKind ShellKind,
    string ShellDisplayName,
    TerminalSessionState State,
    int Columns,
    int Rows,
    int? ExitCode,
    string? ErrorMessage,
    DateTimeOffset CreatedUtc,
    Sequence Sequence,
    ThreadId? ThreadId = null,
    string? WorkspacePath = null,
    string Epoch = "",
    bool? HasRunningSubprocess = null,
    string? ForegroundCommand = null);
