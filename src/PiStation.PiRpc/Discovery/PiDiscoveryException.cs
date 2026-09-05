namespace PiStation.PiRpc.Discovery;

public enum PiDiscoveryFailure
{
    NotFound,
    UnsupportedPiVersion,
    InvalidInstallation,
    MissingEntrypoint,
    NodeNotFound,
    UnsupportedNodeVersion,
}

public sealed class PiDiscoveryException : Exception
{
    public PiDiscoveryException(
        PiDiscoveryFailure failure,
        string message,
        IReadOnlyList<string>? attempts = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        Attempts = attempts ?? [];
    }

    public PiDiscoveryFailure Failure { get; }

    public IReadOnlyList<string> Attempts { get; }
}
