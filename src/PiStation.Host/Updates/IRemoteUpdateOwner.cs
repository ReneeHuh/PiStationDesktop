namespace PiStation.Host.Updates;

public interface IRemoteUpdateOwner
{
    string Kind { get; }
    string CurrentVersion { get; }
    string PackageKind { get; }
    string Trust { get; }
    Task<string> ValidateAsync(string packagePath, string runtimeDirectory, CancellationToken cancellationToken);
    // Once accepted, the owner completes activation independently of the requesting network connection.
    Task ActivateAsync(StagedRemoteUpdate update, CancellationToken cancellationToken);
}

public sealed record StagedRemoteUpdate(Guid RequestId, string DataRoot, string PackagePath,
    string RuntimeDirectory, string TargetVersion, string ReceiptPath, bool InterruptActiveWork);
