using PiStation.Protocol.Identifiers;

namespace PiStation.ClientRuntime;

public class PiConfigurationException : Exception
{
    public PiConfigurationException(
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

public sealed class PiConfigurationConflictException(
    string errorCode,
    CommandId commandId,
    ThreadId threadId,
    string message,
    Exception? innerException = null)
    : PiConfigurationException(errorCode, commandId, threadId, message, innerException);

public sealed class PiConfigurationUnsupportedException(
    string errorCode,
    CommandId commandId,
    ThreadId threadId,
    string message,
    Exception? innerException = null)
    : PiConfigurationException(errorCode, commandId, threadId, message, innerException);

public sealed class PiConfigurationInvalidException(
    string errorCode,
    CommandId commandId,
    ThreadId threadId,
    string message,
    Exception? innerException = null)
    : PiConfigurationException(errorCode, commandId, threadId, message, innerException);
