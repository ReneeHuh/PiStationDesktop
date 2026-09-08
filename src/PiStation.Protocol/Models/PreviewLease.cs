using PiStation.Protocol.Identifiers;

namespace PiStation.Protocol.Models;

public sealed record OpenPreviewRequest(ProjectId ProjectId, Uri Address);
public sealed record PreviewLease(string Id, Uri Address, DateTimeOffset ExpiresAt);
