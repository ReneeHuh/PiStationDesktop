using System.Security.Cryptography;
using System.Text;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;

namespace PiStation.ClientRuntime;

public static class CitationSourceResolver
{
    public static string Fingerprint(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string? Resolve(ComposerContext citation, IEnumerable<MessageTimelineItem> messages)
    {
        var candidates = messages.Where(message => message.Role == MessageRole.Assistant).ToArray();
        bool Matches(MessageTimelineItem message) => citation.SourceTextSha256 is null ||
            string.Equals(Fingerprint(message.Text), citation.SourceTextSha256, StringComparison.OrdinalIgnoreCase);
        var exact = candidates.FirstOrDefault(message =>
            (message.ItemId == citation.MessageId || message.MessageId == citation.MessageId) && Matches(message));
        if (exact is not null) return exact.ItemId;
        if (citation.SourceTextSha256 is null) return null;
        var matching = candidates.Where(Matches).Take(2).ToArray();
        // Never guess between duplicate answers or navigate by quoted text alone.
        return matching.Length == 1 ? matching[0].ItemId : null;
    }
}
