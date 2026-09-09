using PiStation.Protocol.Models;

namespace PiStation.App.Composition;

internal static class ComposerPresentation
{
    public static IEnumerable<ComposerCommandDescriptor> Suggestions(IEnumerable<ComposerCommandDescriptor> commands,
        char prefix, string query, bool showSkills) => commands
        .Where(command => prefix == '$' ? command.Source == ComposerCommandSource.Skill
            : showSkills || command.Source != ComposerCommandSource.Skill)
        .Where(command => command.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            command.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
        .Take(12);

    public static bool ShouldCollapse(bool containsFocus, bool readingHistory, bool collapseOnBlur,
        bool collapseOnScroll, bool hasRecoveryConflict, bool hasOpenPopup = false) =>
        !hasRecoveryConflict && !hasOpenPopup &&
        ((!containsFocus && collapseOnBlur) || (collapseOnScroll && readingHistory));

    public static string Summary(string text, int attachmentCount, int contextCount)
    {
        var preview = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (preview.Length > 120) preview = preview[..120] + "…";
        var counts = new List<string>();
        if (attachmentCount > 0) counts.Add($"{attachmentCount} attachment(s)");
        if (contextCount > 0) counts.Add($"{contextCount} context item(s)");
        if (preview.Length == 0) preview = "Write a message…";
        return counts.Count == 0 ? preview : $"{preview} · {string.Join(" · ", counts)}";
    }
}
