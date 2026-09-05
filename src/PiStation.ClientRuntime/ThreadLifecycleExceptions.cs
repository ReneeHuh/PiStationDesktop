using PiStation.Protocol.Identifiers;

namespace PiStation.ClientRuntime;

public class ThreadLifecycleException : Exception
{
    public ThreadLifecycleException(
        string errorCode,
        CommandId commandId,
        ThreadId threadId,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        CommandId = commandId;
        ThreadId = threadId;
    }

    public string ErrorCode { get; }

    public CommandId CommandId { get; }

    public ThreadId ThreadId { get; }
}

public sealed class ThreadLifecycleConflictException(
    string errorCode,
    CommandId commandId,
    ThreadId threadId,
    string message,
    Exception? innerException = null)
    : ThreadLifecycleException(errorCode, commandId, threadId, message, innerException);

public sealed class ThreadLifecycleInvalidException(
    string errorCode,
    CommandId commandId,
    ThreadId threadId,
    string message,
    Exception? innerException = null)
    : ThreadLifecycleException(errorCode, commandId, threadId, message, innerException);

public sealed class ThreadLifecycleNotFoundException(
    string errorCode,
    CommandId commandId,
    ThreadId threadId,
    string message,
    Exception? innerException = null)
    : ThreadLifecycleException(errorCode, commandId, threadId, message, innerException);

public sealed class ThreadSearchException(
    string errorCode,
    ProjectId projectId,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string ErrorCode { get; } = errorCode;

    public ProjectId ProjectId { get; } = projectId;
}
