using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PiStation.ClientRuntime;

public enum DiffLineKind { Metadata, File, Hunk, Context, Addition, Deletion }

public sealed record DiffLine(int Offset, int Length, string Text, DiffLineKind Kind, string Path, int? OldLine, int? NewLine);

public static partial class DiffDocument
{
    public static IReadOnlyList<DiffLine> Parse(string text)
    {
        var result = new List<DiffLine>();
        var offset = 0;
        var path = string.Empty;
        int? oldLine = null;
        int? newLine = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var kind = DiffLineKind.Metadata;
            int? left = null, right = null;
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                var separator = line.LastIndexOf(" b/", StringComparison.Ordinal);
                var quotedSeparator = line.LastIndexOf(" \"b/", StringComparison.Ordinal);
                path = separator >= 0 ? DecodePath(line[(separator + 1)..]) : quotedSeparator >= 0 ? DecodePath(line[(quotedSeparator + 1)..]) : line[11..];
                if (path.StartsWith("b/", StringComparison.Ordinal)) path = path[2..];
                kind = DiffLineKind.File;
                oldLine = newLine = null;
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var name = DecodePath(line[4..]);
                if (name != "/dev/null") path = name.StartsWith("b/", StringComparison.Ordinal) ? name[2..] : name;
            }
            else if (HunkPattern().Match(line) is { Success: true } hunk)
            {
                oldLine = int.TryParse(hunk.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var oldStart) ? oldStart : null;
                newLine = int.TryParse(hunk.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var newStart) ? newStart : null;
                kind = DiffLineKind.Hunk;
            }
            else if (oldLine is not null && newLine is not null && line.Length > 0)
            {
                switch (line[0])
                {
                    case '+': kind = DiffLineKind.Addition; right = newLine++; break;
                    case '-': kind = DiffLineKind.Deletion; left = oldLine++; break;
                    case ' ': kind = DiffLineKind.Context; left = oldLine++; right = newLine++; break;
                }
            }
            result.Add(new DiffLine(offset, raw.Length, line, kind, path, left, right));
            offset += raw.Length + 1;
        }
        return result;
    }

    private static string DecodePath(string value)
    {
        if (!value.StartsWith('"') || !value.EndsWith('"')) return value;
        var source = value[1..^1];
        var bytes = new List<byte>();
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] != '\\' || index + 1 == source.Length)
            {
                var length = char.IsHighSurrogate(source[index]) && index + 1 < source.Length ? 2 : 1;
                bytes.AddRange(Encoding.UTF8.GetBytes(source.Substring(index, length)));
                index += length - 1;
                continue;
            }
            var escaped = source[++index];
            if (escaped is >= '0' and <= '7')
            {
                var octal = escaped - '0';
                for (var digit = 1; digit < 3 && index + 1 < source.Length && source[index + 1] is >= '0' and <= '7'; digit++)
                    octal = octal * 8 + source[++index] - '0';
                bytes.Add((byte)octal);
            }
            else bytes.Add((byte)(escaped switch { 't' => '\t', 'n' => '\n', 'r' => '\r', 'b' => '\b', 'f' => '\f', 'v' => '\v', _ => escaped }));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    [GeneratedRegex("^@@ -(\\d+)(?:,\\d+)? \\+(\\d+)(?:,\\d+)? @@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkPattern();
}
