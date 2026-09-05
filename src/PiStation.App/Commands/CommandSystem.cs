using Windows.System;

namespace PiStation.App.Commands;

public static class CommandContextKeys
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "connected",
        "projectOpen",
        "threadOpen",
        "threadReady",
        "turnRunning",
        "rightPanelOpen",
        "changesOpen",
        "filesOpen",
        "terminalOpen",
        "previewOpen",
        "terminalFocus",
        "previewFocus",
        "composerFocus",
        "editorFocus",
        "paletteOpen",
    };
}

public sealed class CommandContext(IReadOnlyDictionary<string, bool>? values = null)
{
    private readonly IReadOnlyDictionary<string, bool> _values =
        values ?? new Dictionary<string, bool>(StringComparer.Ordinal);

    public bool this[string key] => _values.TryGetValue(key, out var value) && value;
}

public abstract record WhenNode
{
    internal abstract bool Evaluate(CommandContext context);

    internal abstract void CollectIdentifiers(ISet<string> identifiers);

    private sealed record ConstantNode(bool Value) : WhenNode
    {
        internal override bool Evaluate(CommandContext context) => Value;

        internal override void CollectIdentifiers(ISet<string> identifiers)
        {
        }
    }

    private sealed record IdentifierNode(string Name) : WhenNode
    {
        internal override bool Evaluate(CommandContext context) => context[Name];

        internal override void CollectIdentifiers(ISet<string> identifiers) => identifiers.Add(Name);
    }

    private sealed record NotNode(WhenNode Node) : WhenNode
    {
        internal override bool Evaluate(CommandContext context) => !Node.Evaluate(context);

        internal override void CollectIdentifiers(ISet<string> identifiers) => Node.CollectIdentifiers(identifiers);
    }

    private sealed record AndNode(WhenNode Left, WhenNode Right) : WhenNode
    {
        internal override bool Evaluate(CommandContext context) => Left.Evaluate(context) && Right.Evaluate(context);

        internal override void CollectIdentifiers(ISet<string> identifiers)
        {
            Left.CollectIdentifiers(identifiers);
            Right.CollectIdentifiers(identifiers);
        }
    }

    private sealed record OrNode(WhenNode Left, WhenNode Right) : WhenNode
    {
        internal override bool Evaluate(CommandContext context) => Left.Evaluate(context) || Right.Evaluate(context);

        internal override void CollectIdentifiers(ISet<string> identifiers)
        {
            Left.CollectIdentifiers(identifiers);
            Right.CollectIdentifiers(identifiers);
        }
    }

    public static WhenNode True { get; } = new ConstantNode(true);

    internal static WhenNode Identifier(string name) => new IdentifierNode(name);

    internal static WhenNode Not(WhenNode node) => new NotNode(node);

    internal static WhenNode And(WhenNode left, WhenNode right) => new AndNode(left, right);

    internal static WhenNode Or(WhenNode left, WhenNode right) => new OrNode(left, right);
}

public static class WhenExpression
{
    private static readonly string[] PanelKeys =
        ["changesOpen", "filesOpen", "terminalOpen", "previewOpen"];
    private static readonly string[] FocusKeys =
        ["composerFocus", "editorFocus", "terminalFocus", "previewFocus"];

    public static bool TryParse(string? value, out WhenNode expression, out string error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            expression = WhenNode.True;
            error = string.Empty;
            return true;
        }

        try
        {
            var parser = new Parser(value);
            expression = parser.Parse();
            var identifiers = new HashSet<string>(StringComparer.Ordinal);
            expression.CollectIdentifiers(identifiers);
            var unknown = identifiers.FirstOrDefault(identifier => !CommandContextKeys.All.Contains(identifier));
            if (unknown is not null)
            {
                error = $"Unknown context key '{unknown}'.";
                expression = WhenNode.True;
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (FormatException exception)
        {
            expression = WhenNode.True;
            error = exception.Message;
            return false;
        }
    }

    public static bool Overlaps(WhenNode left, WhenNode right)
    {
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        left.CollectIdentifiers(identifiers);
        right.CollectIdentifiers(identifiers);
        if (identifiers.Overlaps(PanelKeys))
        {
            identifiers.Add("rightPanelOpen");
            identifiers.Add("projectOpen");
        }

        if (identifiers.Contains("terminalFocus"))
        {
            identifiers.Add("terminalOpen");
            identifiers.Add("rightPanelOpen");
            identifiers.Add("projectOpen");
        }

        if (identifiers.Contains("previewFocus"))
        {
            identifiers.Add("previewOpen");
            identifiers.Add("rightPanelOpen");
            identifiers.Add("projectOpen");
        }

        if (identifiers.Contains("editorFocus"))
        {
            identifiers.Add("filesOpen");
            identifiers.Add("rightPanelOpen");
            identifiers.Add("projectOpen");
        }

        if (identifiers.Contains("threadOpen") || identifiers.Contains("threadReady") ||
            identifiers.Contains("turnRunning") || identifiers.Contains("composerFocus"))
        {
            identifiers.Add("threadOpen");
            identifiers.Add("projectOpen");
        }

        var keys = identifiers.ToArray();
        if (keys.Length > 16)
        {
            return true;
        }

        var values = new Dictionary<string, bool>(StringComparer.Ordinal);
        var combinations = 1 << keys.Length;
        for (var mask = 0; mask < combinations; mask++)
        {
            for (var index = 0; index < keys.Length; index++)
            {
                values[keys[index]] = (mask & (1 << index)) != 0;
            }

            var context = new CommandContext(values);
            if (IsPossibleContext(context) && left.Evaluate(context) && right.Evaluate(context))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPossibleContext(CommandContext context)
    {
        var openPanels = PanelKeys.Count(key => context[key]);
        var focusedSurfaces = FocusKeys.Count(key => context[key]);
        if (openPanels > 1 || focusedSurfaces > 1 ||
            (context["threadOpen"] && !context["projectOpen"]) ||
            (context["threadReady"] && !context["threadOpen"]) ||
            (context["turnRunning"] && !context["threadOpen"]) ||
            (context["threadReady"] && context["turnRunning"]) ||
            (openPanels > 0 && (!context["rightPanelOpen"] || !context["projectOpen"])) ||
            (context["composerFocus"] && !context["threadOpen"]) ||
            (context["editorFocus"] && !context["filesOpen"]) ||
            (context["terminalFocus"] && !context["terminalOpen"]) ||
            (context["previewFocus"] && !context["previewOpen"]))
        {
            return false;
        }

        return true;
    }

    private sealed class Parser(string source)
    {
        private readonly string _source = source;
        private int _position;

        public WhenNode Parse()
        {
            var result = ParseOr();
            SkipWhitespace();
            if (_position != _source.Length)
            {
                throw Error("Unexpected token");
            }

            return result;
        }

        private WhenNode ParseOr()
        {
            var left = ParseAnd();
            while (TryConsume("||"))
            {
                left = WhenNode.Or(left, ParseAnd());
            }

            return left;
        }

        private WhenNode ParseAnd()
        {
            var left = ParseUnary();
            while (TryConsume("&&"))
            {
                left = WhenNode.And(left, ParseUnary());
            }

            return left;
        }

        private WhenNode ParseUnary()
        {
            SkipWhitespace();
            if (TryConsume("!"))
            {
                return WhenNode.Not(ParseUnary());
            }

            if (TryConsume("("))
            {
                var nested = ParseOr();
                if (!TryConsume(")"))
                {
                    throw Error("Expected ')'");
                }

                return nested;
            }

            return ParseIdentifier();
        }

        private WhenNode ParseIdentifier()
        {
            SkipWhitespace();
            var start = _position;
            while (_position < _source.Length &&
                   (char.IsLetterOrDigit(_source[_position]) || _source[_position] == '_'))
            {
                _position++;
            }

            if (start == _position)
            {
                throw Error("Expected a context key");
            }

            var name = _source[start.._position];
            return name switch
            {
                "true" => WhenNode.True,
                "false" => WhenNode.Not(WhenNode.True),
                _ => WhenNode.Identifier(name),
            };
        }

        private bool TryConsume(string token)
        {
            SkipWhitespace();
            if (!_source.AsSpan(_position).StartsWith(token, StringComparison.Ordinal))
            {
                return false;
            }

            _position += token.Length;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_position < _source.Length && char.IsWhiteSpace(_source[_position]))
            {
                _position++;
            }
        }

        private FormatException Error(string message) =>
            new($"{message} at position {_position + 1}.");
    }
}

public readonly record struct KeyGesture(VirtualKey Key, VirtualKeyModifiers Modifiers)
{
    public static bool TryParse(string? value, out KeyGesture gesture, out string error)
    {
        gesture = default;
        var parts = (value ?? string.Empty)
            .Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "Enter a shortcut such as Ctrl+K.";
            return false;
        }

        var modifiers = VirtualKeyModifiers.None;
        VirtualKey? key = null;
        foreach (var part in parts)
        {
            var modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" or "mod" => VirtualKeyModifiers.Control,
                "shift" => VirtualKeyModifiers.Shift,
                "alt" => VirtualKeyModifiers.Menu,
                "win" or "meta" => VirtualKeyModifiers.Windows,
                _ => VirtualKeyModifiers.None,
            };
            if (modifier != VirtualKeyModifiers.None)
            {
                if (modifiers.HasFlag(modifier))
                {
                    error = $"Shortcut modifier '{part}' is repeated.";
                    return false;
                }

                modifiers |= modifier;
                continue;
            }

            if (key is not null || !TryParseKey(part, out var parsedKey))
            {
                error = $"'{part}' is not a supported shortcut key.";
                return false;
            }

            key = parsedKey;
        }

        if (key is null)
        {
            error = "The shortcut is missing a key.";
            return false;
        }

        if (modifiers == VirtualKeyModifiers.None &&
            key is >= VirtualKey.A and <= VirtualKey.Z or >= VirtualKey.Number0 and <= VirtualKey.Number9)
        {
            error = "Letter and number shortcuts require Ctrl, Alt, or Win.";
            return false;
        }

        gesture = new KeyGesture(key.Value, modifiers);
        error = string.Empty;
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(VirtualKeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(VirtualKeyModifiers.Menu)) parts.Add("Alt");
        if (Modifiers.HasFlag(VirtualKeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(VirtualKeyModifiers.Windows)) parts.Add("Win");
        parts.Add(FormatKey(Key));
        return string.Join('+', parts);
    }

    private static bool TryParseKey(string value, out VirtualKey key)
    {
        var normalized = value.Trim();
        if (normalized.Length == 1)
        {
            var character = char.ToUpperInvariant(normalized[0]);
            if (character is >= 'A' and <= 'Z')
            {
                key = (VirtualKey)character;
                return true;
            }

            if (character is >= '0' and <= '9')
            {
                key = (VirtualKey)((int)VirtualKey.Number0 + (character - '0'));
                return true;
            }
        }

        var alias = normalized.ToLowerInvariant() switch
        {
            "esc" => "Escape",
            "space" => "Space",
            "enter" => "Enter",
            "tab" => "Tab",
            "backspace" => "Back",
            "delete" => "Delete",
            "left" => "Left",
            "right" => "Right",
            "up" => "Up",
            "down" => "Down",
            "pageup" => "PageUp",
            "pagedown" => "PageDown",
            _ => normalized,
        };
        return Enum.TryParse(alias, ignoreCase: true, out key) && key != VirtualKey.None;
    }

    private static string FormatKey(VirtualKey key) => key switch
    {
        VirtualKey.Escape => "Esc",
        VirtualKey.Back => "Backspace",
        _ => key.ToString(),
    };
}

public sealed record CommandKeybinding(string CommandId, KeyGesture Gesture, string? When, WhenNode WhenExpression);

public sealed record CommandKeybindingConflict(string CommandId, string CommandTitle, string Gesture, string? When);

public sealed record CommandDefinition(
    string Id,
    string Title,
    string Category,
    string Description,
    IReadOnlyList<string> SearchTerms,
    string? EnableWhen,
    Func<Task> ExecuteAsync,
    IReadOnlyList<string>? DefaultShortcuts = null,
    Func<bool>? CanExecute = null,
    string? DisabledReason = null);

public sealed record CommandMatch(
    CommandDefinition Definition,
    bool IsEnabled,
    string? DisabledReason,
    int Rank);

public sealed class CommandRegistry
{
    private readonly Dictionary<string, (CommandDefinition Definition, WhenNode Enablement)> _commands =
        new(StringComparer.Ordinal);

    public IReadOnlyList<CommandDefinition> Commands => _commands.Values
        .Select(static entry => entry.Definition)
        .OrderBy(static command => command.Category, StringComparer.OrdinalIgnoreCase)
        .ThenBy(static command => command.Title, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public void Register(CommandDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.Id) || _commands.ContainsKey(definition.Id))
        {
            throw new InvalidOperationException($"Command id '{definition.Id}' is empty or already registered.");
        }

        if (!WhenExpression.TryParse(definition.EnableWhen, out var enablement, out var error))
        {
            throw new InvalidOperationException($"Command '{definition.Id}' has an invalid context: {error}");
        }

        _commands.Add(definition.Id, (definition, enablement));
    }

    public bool TryGet(string commandId, out CommandDefinition definition)
    {
        if (_commands.TryGetValue(commandId, out var entry))
        {
            definition = entry.Definition;
            return true;
        }

        definition = null!;
        return false;
    }

    public bool CanExecute(string commandId, CommandContext context, out string? reason)
    {
        if (!_commands.TryGetValue(commandId, out var entry))
        {
            reason = "This command is not available.";
            return false;
        }

        if (!entry.Enablement.Evaluate(context) || entry.Definition.CanExecute?.Invoke() == false)
        {
            reason = entry.Definition.DisabledReason ?? "This command is not available in the current context.";
            return false;
        }

        reason = null;
        return true;
    }

    public async Task<bool> ExecuteAsync(string commandId, CommandContext context)
    {
        if (!_commands.TryGetValue(commandId, out var entry) || !CanExecute(commandId, context, out _))
        {
            return false;
        }

        await entry.Definition.ExecuteAsync();
        return true;
    }

    public IReadOnlyList<CommandMatch> Search(string? query, CommandContext context)
    {
        var normalized = (query ?? string.Empty).Trim();
        return _commands.Values
            .Select(entry =>
            {
                var rank = Rank(entry.Definition, normalized);
                var enabled = CanExecute(entry.Definition.Id, context, out var reason);
                return new CommandMatch(entry.Definition, enabled, reason, rank);
            })
            .Where(static match => match.Rank > 0)
            .OrderByDescending(static match => match.Rank)
            .ThenBy(static match => match.Definition.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static match => match.Definition.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int Rank(CommandDefinition command, string query)
    {
        if (query.Length == 0)
        {
            return 1;
        }

        var fields = new[] { command.Title, command.Id, command.Category, command.Description }
            .Concat(command.SearchTerms);
        var best = 0;
        var index = 0;
        foreach (var field in fields)
        {
            var rank = string.Equals(field, query, StringComparison.OrdinalIgnoreCase)
                ? 400
                : field.StartsWith(query, StringComparison.OrdinalIgnoreCase)
                    ? 300
                    : field.Contains(query, StringComparison.OrdinalIgnoreCase)
                        ? 200
                        : 0;
            best = Math.Max(best, rank - index);
            index++;
        }

        return best;
    }
}

public sealed class CommandKeybindingManager
{
    private readonly CommandRegistry _registry;
    private readonly Dictionary<string, CommandKeybinding> _custom = new(StringComparer.Ordinal);
    private readonly List<CommandKeybinding> _defaults = [];

    public CommandKeybindingManager(CommandRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        foreach (var command in registry.Commands)
        {
            foreach (var shortcut in command.DefaultShortcuts ?? [])
            {
                if (KeyGesture.TryParse(shortcut, out var gesture, out _) &&
                    WhenExpression.TryParse(command.EnableWhen, out var when, out _))
                {
                    _defaults.Add(new CommandKeybinding(command.Id, gesture, command.EnableWhen, when));
                }
            }
        }
    }

    public event EventHandler? Changed;

    public IReadOnlyList<CommandKeybinding> CustomBindings => _custom.Values
        .OrderBy(binding => _registry.TryGet(binding.CommandId, out var command) ? command.Title : binding.CommandId,
            StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public IReadOnlyList<CommandKeybinding> EffectiveBindings
    {
        get
        {
            var overridden = _custom.Keys.ToHashSet(StringComparer.Ordinal);
            return _defaults.Where(binding => !overridden.Contains(binding.CommandId))
                .Concat(_custom.Values)
                .ToArray();
        }
    }

    public void Load(IEnumerable<(string CommandId, string Gesture, string? When)> preferences)
    {
        _custom.Clear();
        foreach (var preference in preferences.Take(256))
        {
            if (!_registry.TryGet(preference.CommandId, out _) ||
                !KeyGesture.TryParse(preference.Gesture, out var gesture, out _) ||
                !WhenExpression.TryParse(preference.When, out var when, out _))
            {
                continue;
            }

            var candidate = new CommandKeybinding(
                preference.CommandId,
                gesture,
                string.IsNullOrWhiteSpace(preference.When) ? null : preference.When.Trim(),
                when);
            var conflicts = EffectiveBindings.Any(binding =>
                binding.CommandId != candidate.CommandId &&
                binding.Gesture == candidate.Gesture &&
                WhenExpression.Overlaps(binding.WhenExpression, candidate.WhenExpression));
            if (!conflicts)
            {
                _custom[preference.CommandId] = candidate;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool TrySet(
        string commandId,
        string gestureText,
        string? whenText,
        out IReadOnlyList<CommandKeybindingConflict> conflicts,
        out string error)
    {
        conflicts = [];
        if (!_registry.TryGet(commandId, out _))
        {
            error = "Select a registered command.";
            return false;
        }

        if (!KeyGesture.TryParse(gestureText, out var gesture, out error) ||
            !WhenExpression.TryParse(whenText, out var when, out error))
        {
            return false;
        }

        conflicts = EffectiveBindings
            .Where(binding => binding.CommandId != commandId &&
                              binding.Gesture == gesture &&
                              WhenExpression.Overlaps(binding.WhenExpression, when))
            .Select(binding => new CommandKeybindingConflict(
                binding.CommandId,
                _registry.TryGet(binding.CommandId, out var command) ? command.Title : binding.CommandId,
                binding.Gesture.ToString(),
                binding.When))
            .ToArray();
        if (conflicts.Count > 0)
        {
            error = $"{gesture} overlaps {string.Join(", ", conflicts.Select(static conflict => conflict.CommandTitle))}.";
            return false;
        }

        _custom[commandId] = new CommandKeybinding(
            commandId,
            gesture,
            string.IsNullOrWhiteSpace(whenText) ? null : whenText.Trim(),
            when);
        error = string.Empty;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Reset(string commandId)
    {
        if (_custom.Remove(commandId))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public string? ShortcutLabel(string commandId, CommandContext context) =>
        EffectiveBindings.LastOrDefault(binding =>
            binding.CommandId == commandId && binding.WhenExpression.Evaluate(context))?.Gesture.ToString();

    public string? Resolve(KeyGesture gesture, CommandContext context)
    {
        for (var index = EffectiveBindings.Count - 1; index >= 0; index--)
        {
            var binding = EffectiveBindings[index];
            if (binding.Gesture == gesture && binding.WhenExpression.Evaluate(context))
            {
                return binding.CommandId;
            }
        }

        return null;
    }
}
