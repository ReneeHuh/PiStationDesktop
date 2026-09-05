using PiStation.App.Commands;

namespace PiStation.CommandSystem.Tests;

public sealed class CommandSystemTests
{
    [Fact]
    public void ContextParserEvaluatesBooleanExpressionsAndRejectsUnknownKeys()
    {
        Assert.True(WhenExpression.TryParse(
            "projectOpen && (!turnRunning || terminalOpen)",
            out var expression,
            out var error));
        Assert.Equal(string.Empty, error);
        Assert.True(expression.Evaluate(Context(
            ("projectOpen", true),
            ("turnRunning", false))));
        Assert.False(expression.Evaluate(Context(
            ("projectOpen", true),
            ("turnRunning", true),
            ("terminalOpen", false))));

        Assert.False(WhenExpression.TryParse("projectOpen && imaginaryState", out _, out error));
        Assert.Contains("Unknown context key", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ConflictAnalysisAllowsMutuallyExclusivePanelsAndFindsRealOverlap()
    {
        Assert.True(WhenExpression.TryParse("changesOpen", out var changes, out _));
        Assert.True(WhenExpression.TryParse("previewOpen", out var preview, out _));
        Assert.True(WhenExpression.TryParse("terminalOpen", out var terminal, out _));
        Assert.True(WhenExpression.TryParse("terminalOpen && terminalFocus", out var terminalFocus, out _));

        Assert.False(WhenExpression.Overlaps(changes, preview));
        Assert.True(WhenExpression.Overlaps(terminal, terminalFocus));
    }

    [Fact]
    public void GestureParserNormalizesKeysAndRejectsRepeatedModifiers()
    {
        Assert.True(KeyGesture.TryParse("shift+ctrl+5", out var gesture, out var error));
        Assert.Equal(string.Empty, error);
        Assert.Equal("Ctrl+Shift+Number5", gesture.ToString());

        Assert.False(KeyGesture.TryParse("Ctrl+Ctrl+K", out _, out error));
        Assert.Contains("repeated", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(KeyGesture.TryParse("K", out _, out error));
        Assert.Contains("require", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(KeyGesture.TryParse("Ctrl+Comma", out _, out error));
        Assert.Contains("not a supported", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KeybindingManagerRejectsOverlappingContextsAndDropsPersistedConflicts()
    {
        var registry = new CommandRegistry();
        registry.Register(Command("changes.show", "Show Changes", "changesOpen", "Ctrl+R"));
        registry.Register(Command("preview.show", "Show Preview", "previewOpen", "Ctrl+R"));
        registry.Register(Command("palette.show", "Show Palette", null, "Ctrl+K"));
        registry.Register(Command("sidebar.toggle", "Toggle Sidebar", null));
        var manager = new CommandKeybindingManager(registry);

        Assert.True(manager.TrySet(
            "preview.show", "Ctrl+R", "previewOpen", out var exclusiveConflicts, out var error));
        Assert.Empty(exclusiveConflicts);
        Assert.Equal(string.Empty, error);
        Assert.False(manager.TrySet(
            "sidebar.toggle", "Ctrl+K", null, out var conflicts, out error));
        Assert.Equal("palette.show", Assert.Single(conflicts).CommandId);
        Assert.Contains("Show Palette", error, StringComparison.Ordinal);

        manager.Load([("sidebar.toggle", "Ctrl+K", (string?)null)]);
        Assert.Empty(manager.CustomBindings);
    }

    private static CommandDefinition Command(
        string id,
        string title,
        string? enableWhen,
        string? shortcut = null) =>
        new(
            id,
            title,
            "Tests",
            title,
            [],
            enableWhen,
            static () => Task.CompletedTask,
            shortcut is null ? [] : [shortcut]);

    private static CommandContext Context(params (string Key, bool Value)[] values) =>
        new(values.ToDictionary(static value => value.Key, static value => value.Value, StringComparer.Ordinal));
}
