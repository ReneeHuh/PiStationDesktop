using PiStation.Protocol.Identifiers;

namespace PiStation.ClientRuntime;

public sealed class WorkspaceCommandDispatchUncertainException : Exception
{
    public WorkspaceCommandDispatchUncertainException(
        CommandId commandId,
        ProjectId projectId,
        ThreadId? threadId,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        CommandId = commandId;
        ProjectId = projectId;
        ThreadId = threadId;
    }

    public CommandId CommandId { get; }

    public ProjectId ProjectId { get; }

    public ThreadId? ThreadId { get; }
}
