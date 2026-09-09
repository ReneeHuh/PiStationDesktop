using System.Security.Cryptography;
using System.Text;

namespace PiStation.App.Composition;

internal static class BrowserProfilePaths
{
    internal static string ProfileDirectory(string profileRoot, string profileId) =>
        Path.Combine(profileRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profileId))));

    internal static IReadOnlyList<string> ExistingProfileDirectories(string dataRoot, string currentProfileRoot, string profileId)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(dataRoot, "browser-profiles"), currentProfileRoot,
        };
        var remoteRoot = Path.Combine(dataRoot, "remote-environments");
        if (Directory.Exists(remoteRoot))
            foreach (var environment in Directory.EnumerateDirectories(remoteRoot))
                if (Path.GetFileName(environment) is { Length: 64 } name && name.All(Uri.IsHexDigit))
                    roots.Add(Path.Combine(environment, "browser-profiles"));
        return roots.Select(root => ProfileDirectory(root, profileId)).Where(Directory.Exists).ToArray();
    }
}
