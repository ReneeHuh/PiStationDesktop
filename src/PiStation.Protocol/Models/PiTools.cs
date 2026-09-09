namespace PiStation.Protocol.Models;

public enum PiToolSelectionMode { PiDefault, Allowlist, None }

public sealed record PiToolSelection(PiToolSelectionMode Mode = PiToolSelectionMode.PiDefault,
    IReadOnlyList<string>? Allowed = null, IReadOnlyList<string>? Excluded = null);

public sealed record PiToolDescriptor(string Name, string Description, string Source, bool Active);
public sealed record PiToolInventory(IReadOnlyList<PiToolDescriptor> Tools, PiToolSelection? Selection = null,
    bool Truncated = false);

public static class PiToolSelectionRules
{
    public const int MaximumNames = 128;
    private static readonly string[] Readers = ["read", "grep", "find", "ls"];
    private static readonly string[] ManagedFlags =
        ["--tools", "-t", "--exclude-tools", "-xt", "--no-tools", "-nt", "--no-builtin-tools", "-nbt"];

    public static PiToolSelection Normalize(PiToolSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!Enum.IsDefined(selection.Mode)) throw new ArgumentException("Choose a supported tool selection mode.");
        return selection with { Allowed = Names(selection.Allowed), Excluded = Names(selection.Excluded) };
    }

    private static string[] Names(IReadOnlyList<string>? values)
    {
        if (values?.Count > MaximumNames) throw new ArgumentException("Configure at most 128 tool names per list.");
        var names = new List<string>();
        foreach (var value in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
                value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')))
                throw new ArgumentException("Tool names must be 1–128 ASCII letters, digits, underscores, hyphens, or dots; use exact names without wildcards.");
            if (!names.Contains(value, StringComparer.Ordinal)) names.Add(value);
        }
        return names.ToArray();
    }

    public static bool IsManaged(PiToolSelection? selection) => selection is not null &&
        (selection.Mode != PiToolSelectionMode.PiDefault || selection.Excluded?.Count > 0);

    public static void ValidateArguments(PiToolSelection? selection, IEnumerable<string> arguments)
    {
        if (IsManaged(selection) && arguments.Any(argument => ManagedFlags.Contains(argument.Split('=')[0], StringComparer.Ordinal)))
            throw new ArgumentException("Remove tool-selection flags from advanced launch arguments before using the dedicated tool controls. Pi defaults with no exclusions preserves legacy flags.");
    }

    public static IReadOnlyList<string> LaunchArguments(PiToolSelection? selection)
    {
        if (selection is null) return [];
        selection = Normalize(selection);
        var arguments = new List<string>();
        if (selection.Mode == PiToolSelectionMode.None ||
            selection.Mode == PiToolSelectionMode.Allowlist && selection.Allowed!.Count == 0)
            arguments.Add("--no-tools");
        else if (selection.Mode == PiToolSelectionMode.Allowlist)
            arguments.AddRange(["--tools", string.Join(',', WithPlanningAliases(selection.Allowed!))]);
        if (selection.Excluded!.Count > 0)
            arguments.AddRange(["--exclude-tools", string.Join(',', WithPlanningAliases(selection.Excluded))]);
        return arguments;
    }

    // Dedicated plan readers must not bypass exclusions or disappear merely
    // because the equivalent read/search builtin was explicitly allowed.
    private static IEnumerable<string> WithPlanningAliases(IEnumerable<string> names) => names
        .SelectMany(name => Readers.Contains(name, StringComparer.Ordinal) ? new[] { name, "pistation_plan_" + name } : [name])
        .Distinct(StringComparer.Ordinal);
}
