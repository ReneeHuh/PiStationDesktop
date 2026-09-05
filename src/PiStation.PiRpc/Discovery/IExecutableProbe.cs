namespace PiStation.PiRpc.Discovery;

public interface IExecutableProbe
{
    Task<string> GetVersionOutputAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}
