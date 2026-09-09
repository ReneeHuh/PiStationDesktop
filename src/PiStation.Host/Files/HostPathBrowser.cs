using PiStation.Protocol.Models;

namespace PiStation.Host.Files;

internal static class HostPathBrowser
{
    public static HostPathPage Browse(BrowseHostPathRequest request, CancellationToken cancellationToken)
    {
        const int pageSize = 200;
        if (request.Offset is < 0 or > 100_000) throw new ArgumentException("Choose an offset from 0 through 100000.");
        if (string.IsNullOrWhiteSpace(request.Path))
            return new(null, null, Directory.GetLogicalDrives().Select(path => new HostPathEntry(path, path, true)).ToArray(), null);
        if (!Path.IsPathFullyQualified(request.Path) || request.Path.Length > 4096 || request.Path.Any(char.IsControl))
            throw new ArgumentException("Enter an absolute path on the host.");
        var root = Path.GetFullPath(request.Path);
        var entries = new List<HostPathEntry>();
        var index = 0;
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", new EnumerationOptions
        { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System, RecurseSubdirectories = false }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index++ < request.Offset) continue;
            entries.Add(new(entry.Name, entry.FullName, (entry.Attributes & FileAttributes.Directory) != 0));
            if (entries.Count > pageSize) break;
        }
        var next = entries.Count > pageSize && request.Offset + pageSize <= 100_000 ? request.Offset + pageSize : (int?)null;
        return new(root, Directory.GetParent(root)?.FullName, entries.Take(pageSize).ToArray(), next);
    }
}
