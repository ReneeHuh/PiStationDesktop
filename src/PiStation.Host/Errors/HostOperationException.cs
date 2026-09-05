namespace PiStation.Host.Errors;

public sealed class HostOperationException : Exception
{
    public HostOperationException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}
