using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed class CommandPaletteItemViewModel(
    string resultId,
    string kindLabel,
    string title,
    string description,
    string shortcutLabel,
    bool isEnabled,
    string? commandId = null,
    GlobalSearchItem? searchItem = null)
{
    public string ResultId { get; set; } = resultId;

    public string KindLabel { get; set; } = kindLabel;

    public string Title { get; set; } = title;

    public string Description { get; set; } = description;

    public string ShortcutLabel { get; set; } = shortcutLabel;

    public bool IsEnabled { get; set; } = isEnabled;

    public string? CommandId { get; set; } = commandId;

    public GlobalSearchItem? SearchItem { get; set; } = searchItem;

    public double ResultOpacity => IsEnabled ? 1 : 0.55;
}

public sealed class CommandKeybindingRowViewModel(
    string commandId,
    string commandTitle,
    string gesture,
    string context)
{
    public string CommandId { get; set; } = commandId;

    public string CommandTitle { get; set; } = commandTitle;

    public string Gesture { get; set; } = gesture;

    public string Context { get; set; } = context;

    public string Summary => string.IsNullOrWhiteSpace(Context)
        ? Gesture
        : $"{Gesture} • when {Context}";
}
