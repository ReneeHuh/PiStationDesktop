using System.Text.RegularExpressions;

namespace PiStation.ClientRuntime;

/// <summary>An explicit absolute file reference, separate from workspace-relative editable links.</summary>
public sealed partial record ArtifactLink(string AbsolutePath, int? Line)
{
    public static ArtifactLink? Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            // Strip authored line syntax before decoding so an escaped # in a filename stays a filename.
            var path = value.Trim();
            var suffix = LineSuffix().Match(path);
            int? line = suffix.Success && int.TryParse(suffix.Groups[1].Value, out var number) && number > 0 ? number : null;
            if (suffix.Success) path = path[..suffix.Index];
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
            {
                if (!uri.IsFile || uri.IsUnc) return null;
                path = uri.LocalPath;
            }
            else path = Uri.UnescapeDataString(path);
            if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.Any(char.IsControl)) return null;
            return new(Path.GetFullPath(path), line);
        }
        catch (Exception error) when (error is ArgumentException or UriFormatException or NotSupportedException) { return null; }
    }

    [GeneratedRegex(@"(?:#L|:)(\d+)(?:(?:-L?\d+)|(?::\d+))?$")]
    private static partial Regex LineSuffix();
}
