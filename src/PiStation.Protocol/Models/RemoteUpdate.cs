namespace PiStation.Protocol.Models;

public enum RemoteUpdateState { Uploading, Ready, WaitingForIdle, Restarting, Succeeded, Failed, Canceled }
public sealed record RemoteUpdateDescriptor(string HostKind, string CurrentVersion, bool Supported, bool Enabled, string PackageKind, string Trust, bool HasActiveWork);
public sealed record PrepareRemoteUpdateRequest(Guid RequestId, string FileName, long Length, string Sha256);
public sealed record CommitRemoteUpdateRequest(Guid RequestId, bool InterruptActiveWork = false);
public sealed record RemoteUpdateReceipt(Guid RequestId, RemoteUpdateState State, string? TargetVersion = null,
    long ReceivedBytes = 0, string? Message = null, DateTimeOffset? UpdatedAt = null);
