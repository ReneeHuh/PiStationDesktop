namespace PiStation.Protocol.Models;

/// <summary>Host-owned metadata retained independently of the composer draft.</summary>
public sealed record SentMessageContent(
    string Id,
    string Text,
    IReadOnlyList<DraftAttachment> Attachments,
    IReadOnlyList<ComposerContext> Citations);

public static class SentMessageReference
{
    private const string Start = "\n\n<pistation_message_ref>";
    private const string End = "</pistation_message_ref>";

    public static string Append(string prompt, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid message reference.", nameof(id));
        return prompt + Start + id + End;
    }

    public static string? Read(string prompt)
    {
        var start = prompt.LastIndexOf(Start, StringComparison.Ordinal);
        if (start < 0) return null;
        start += Start.Length;
        if (prompt.Length < start + 32 + End.Length ||
            !prompt.AsSpan(start + 32).StartsWith(End, StringComparison.Ordinal)) return null;
        var id = prompt.Substring(start, 32);
        return Guid.TryParseExact(id, "N", out _) ? id : null;
    }

    public static string Remove(string prompt)
    {
        if (Read(prompt) is null) return prompt;
        var start = prompt.LastIndexOf(Start, StringComparison.Ordinal);
        return prompt.Remove(start, Start.Length + 32 + End.Length);
    }
}
