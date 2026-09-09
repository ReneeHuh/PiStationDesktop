namespace PiStation.Protocol.Models;

public sealed record BrowseHostPathRequest(string? Path = null, int Offset = 0);
public sealed record HostPathEntry(string Name, string Path, bool IsDirectory)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => IsDirectory ? Name + " (folder)" : Name;
}
public sealed record HostPathPage(string? Path, string? ParentPath, IReadOnlyList<HostPathEntry> Entries, int? NextOffset);

public sealed record ProjectIconUpload(string FileName, byte[] Content);

public static class ProjectIconLimits
{
    public const int MaximumBytes = 512 * 1024;
}
