using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace PiStation.PiRpc.Transport;

/// <summary>Resolves explicit desktop skill mentions against Pi's effective, trusted resource list.</summary>
public static partial class PiSkillPromptExpander
{
    public const int MaximumSkillBytes = 128 * 1024;
    public const int MaximumExpandedCharacters = 256 * 1024;
    private const string Start = "\n\n<pistation_skills>\n";
    private const string End = "\n</pistation_skills>";

    public static IReadOnlyList<string> ReadMentions(string prompt)
    {
        // Fenced/inline code and escaped tokens describe syntax rather than invoking a resource.
        return Tokens().Matches(prompt).Where(static match => match.Groups["name"].Success)
            .Select(static match => "skill:" + match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    public static async Task<string> ExpandAsync(string prompt, IReadOnlyList<PiCommandInfo> commands,
        CancellationToken cancellationToken = default)
    {
        var mentions = ReadMentions(prompt);
        if (mentions.Count == 0) return prompt;
        if (mentions.Count > 16) throw new ArgumentException("A prompt can invoke at most 16 distinct skills.");
        var output = new StringBuilder(prompt).Append(Start);
        foreach (var name in mentions)
        {
            var matches = commands.Where(command => command.Source == "skill" && command.Name == name).ToArray();
            if (matches.Length != 1 || string.IsNullOrWhiteSpace(matches[0].Path))
                throw new InvalidOperationException($"Skill '{name}' is unavailable or ambiguous. Refresh the skill picker and check Pi's resource trust settings.");
            var path = matches[0].Path!;
            if (!Path.IsPathFullyQualified(path))
                throw new InvalidOperationException($"Pi reported a non-absolute path for skill '{name}'.");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumSkillBytes) throw new IOException($"Skill '{name}' exceeds the 128 KiB limit.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            content = Frontmatter().Replace(content, string.Empty, 1).Trim();
            output.Append("<skill name=\"").Append(SecurityElement.Escape(name[6..]))
                .Append("\" location=\"").Append(SecurityElement.Escape(path)).Append("\">\nReferences are relative to ")
                .Append(Path.GetDirectoryName(path)).Append(".\n\n").Append(content).Append("\n</skill>\n");
            if (output.Length > MaximumExpandedCharacters)
                throw new ArgumentException("The prompt and selected skills exceed the 256 KiB character limit.");
        }
        return output.Append(End).ToString();
    }

    public static string RemoveExpansion(string prompt)
    {
        var start = prompt.LastIndexOf(Start, StringComparison.Ordinal);
        return start >= 0 && prompt.EndsWith(End, StringComparison.Ordinal) && ReadMentions(prompt[..start]).Count != 0
            ? prompt[..start] : prompt;
    }

    [GeneratedRegex(@"<pistation_context\b[^>]*>[\s\S]*?</pistation_context>|```[\s\S]*?(?:```|\z)|~~~[\s\S]*?(?:~~~|\z)|`[^`\r\n]*`|\\\$skill:[\w-]+|(?<!\S)\$skill:(?<name>[\w-]+)(?=$|\s|[.,;:!?)\]])", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();

    [GeneratedRegex(@"\A---\s*\r?\n[\s\S]*?\r?\n---(?:\r?\n|\z)", RegexOptions.CultureInvariant)]
    private static partial Regex Frontmatter();
}
