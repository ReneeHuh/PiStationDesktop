using PiStation.Protocol.Identifiers;

namespace PiStation.ClientRuntime;

public sealed class CommandDispatchUncertainException : Exception
{
    public CommandDispatchUncertainException(
        CommandId commandId,
        ThreadId threadId,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        CommandId = commandId;
        ThreadId = threadId;
    }

    public CommandId CommandId { get; }

    public ThreadId ThreadId { get; }
}
