using System.Text.RegularExpressions;

namespace PiStation.ClientRuntime;

public sealed partial record WorkspaceLink(string RelativePath, int? Line)
{
    public static WorkspaceLink? Parse(string value, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var path = Uri.UnescapeDataString(value.Trim());
            var match = LineSuffix().Match(path);
            int? line = match.Success && int.TryParse(match.Groups[1].Value, out var parsed) && parsed > 0 ? parsed : null;
            if (match.Success) path = path[..match.Index];
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile) return null;
            if (uri?.IsFile == true) path = uri.LocalPath;
            var root = Path.GetFullPath(workspaceRoot);
            var full = Path.GetFullPath(path, root);
            var relative = Path.GetRelativePath(root, full);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) return null;
            return new WorkspaceLink(relative.Replace('\\', '/'), line);
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException or NotSupportedException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"(?:#L|:)(\d+)(?:(?:-L?\d+)|(?::\d+))?$")]
    private static partial Regex LineSuffix();
}
