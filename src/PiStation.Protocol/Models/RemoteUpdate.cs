using PiStation.Protocol.Identifiers;

namespace PiStation.Protocol.Models;

// This maintenance contract is versioned independently of workspace RPCs.
public sealed record RemoteUpdateMaintenanceDescriptor(int ApiVersion, EnvironmentId EnvironmentId, RemoteUpdateDescriptor Update)
{
    public const int CurrentApiVersion = 1;
}

public enum RemoteUpdateState { Uploading, Ready, WaitingForIdle, Restarting, Succeeded, Failed, Canceled }
public sealed record RemoteUpdateDescriptor(string HostKind, string CurrentVersion, bool Supported, bool Enabled, string PackageKind, string Trust, bool HasActiveWork);
public sealed record PrepareRemoteUpdateRequest(Guid RequestId, string FileName, long Length, string Sha256);
public sealed record CommitRemoteUpdateRequest(Guid RequestId, bool InterruptActiveWork = false);
public sealed record RemoteUpdateReceipt(Guid RequestId, RemoteUpdateState State, string? TargetVersion = null,
    long ReceivedBytes = 0, string? Message = null, DateTimeOffset? UpdatedAt = null);
