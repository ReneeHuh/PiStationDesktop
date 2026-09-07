namespace PiStation.Host.Hosting;

internal static class HostDataPaths
{
    // Keep this identity in sync with the desktop Package.appxmanifest. Older MSIX builds
    // virtualized the default directory into LocalCache; reuse it in place, never silently
    // create an empty environment or copy a live SQLite database during an upgrade.
    private const string DesktopPackageIdentity = "584BC26F-2CB5-42F0-A9E5-6DB195B0890E";

    public static string ResolveDefaultRoot(string localApplicationData)
    {
        var normal = Path.Combine(localApplicationData, "PiStationDesktop");
        var candidates = new List<string> { normal };
        var packages = Path.Combine(localApplicationData, "Packages");
        if (Directory.Exists(packages))
        {
            candidates.AddRange(Directory.EnumerateDirectories(packages, DesktopPackageIdentity + "_*")
                .Select(package => Path.Combine(package, "LocalCache", "Local", "PiStationDesktop")));
        }
        var existing = candidates.Where(root => File.Exists(Path.Combine(root, "host.db")))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return existing.Length switch
        {
            0 => normal,
            1 => existing[0],
            _ => throw new InvalidOperationException("Multiple PiStation desktop data directories exist. Start the desktop and SSH host with the same explicit --data-root; no data was moved or merged."),
        };
    }
}
