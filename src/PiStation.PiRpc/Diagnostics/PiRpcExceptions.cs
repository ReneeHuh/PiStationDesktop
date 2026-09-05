namespace PiStation.PiRpc.Diagnostics;

public class PiRpcException : Exception
{
    public PiRpcException(string message)
        : base(message)
    {
    }

    public PiRpcException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public class PiRpcConnectionException : PiRpcException
{
    public PiRpcConnectionException(string message)
        : base(message)
    {
    }

    public PiRpcConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class PiRpcCommandException : PiRpcException
{
    public PiRpcCommandException(string command, string message)
        : base($"Pi command '{command}' failed: {message}")
    {
        Command = command;
    }

    public string Command { get; }
}

public sealed class PiRpcTimeoutException : TimeoutException
{
    public PiRpcTimeoutException(string command, TimeSpan timeout)
        : base($"Timed out after {timeout} waiting for Pi command '{command}'.")
    {
        Command = command;
        Timeout = timeout;
    }

    public string Command { get; }

    public TimeSpan Timeout { get; }
}

public sealed class JsonlProtocolException : PiRpcConnectionException
{
    public JsonlProtocolException(string message)
        : base(message)
    {
    }

    public JsonlProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
