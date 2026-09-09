using PiStation.Protocol.Identifiers;

namespace PiStation.Protocol.Models;

public sealed record UpdateProjectIconsRequest(
    IReadOnlyList<ProjectId> ProjectIds,
    string? Icon = null,
    ProjectIconUpload? UploadedIcon = null);
