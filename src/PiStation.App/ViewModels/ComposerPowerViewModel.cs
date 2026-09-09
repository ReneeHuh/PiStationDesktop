using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed class ComposerPowerViewModel : ObservableObject
{
    private readonly ComposerViewModel _composer;
    private readonly ShellLayoutViewModel _layout;

    public ComposerPowerViewModel(ComposerViewModel composer, ShellLayoutViewModel layout)
    {
        _composer = composer;
        _layout = layout;
        ContextChips.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ContextChipsVisibility));
    }
    private bool _isBusy;
    private bool _isOpen;
    private int _selectedIndex = -1;
    private string _status = string.Empty;

    public ObservableCollection<ComposerCommandDescriptor> Commands { get; } = [];

    public ObservableCollection<ComposerCommandDescriptor> Suggestions { get; } = [];

    public ObservableCollection<PromptStash> Stashes { get; } = [];

    public ObservableCollection<ComposerContextChipViewModel> ContextChips => _composer.ContextChips;

    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (SetProperty(ref _isOpen, value))
            {
                OnPropertyChanged(nameof(SuggestionsVisibility));
            }
        }
    }

    public Visibility SuggestionsVisibility => IsOpen ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ContextChipsVisibility => ContextChips.Count == 0
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility StashesVisibility => Stashes.Count == 0
        ? Visibility.Collapsed
        : Visibility.Visible;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => SetProperty(ref _selectedIndex, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        internal set => SetProperty(ref _isBusy, value);
    }

    public string Status
    {
        get => _status;
        internal set => SetProperty(ref _status, value);
    }

    internal void ApplyDiscovery(ComposerDiscoveryResult discovery)
    {
        Commands.Clear();
        foreach (var command in discovery.Commands.OrderBy(static command => command.Name, StringComparer.OrdinalIgnoreCase))
        {
            Commands.Add(command);
        }

        Status = $"{Commands.Count} composer commands available";
    }

    internal void UpdateSuggestions(string text, int caret)
    {
        Suggestions.Clear();
        if (!TryReadCommandToken(text, caret, out var prefix, out var query))
        {
            CloseSuggestions();
            return;
        }

        foreach (var command in PiStation.App.Composition.ComposerPresentation.Suggestions(
                     Commands, prefix, query, _layout.ShowSkillsInSlashMenu))
        {
            Suggestions.Add(command);
        }

        SelectedIndex = Suggestions.Count == 0 ? -1 : 0;
        IsOpen = Suggestions.Count != 0;
        Status = Suggestions.Count == 0
            ? prefix == '$' ? "No matching skills" : "No matching commands"
            : prefix == '$' ? "Skills" : "Commands and prompts";
    }

    internal void CloseSuggestions()
    {
        IsOpen = false;
        SelectedIndex = -1;
        Suggestions.Clear();
    }

    internal void MoveSelection(int delta)
    {
        if (!IsOpen || Suggestions.Count == 0 || delta == 0)
        {
            return;
        }

        var start = SelectedIndex < 0 ? (delta > 0 ? -1 : 0) : SelectedIndex;
        SelectedIndex = (start + delta + Suggestions.Count) % Suggestions.Count;
    }

    internal void ReplaceStashes(IReadOnlyList<PromptStash> stashes)
    {
        Stashes.Clear();
        foreach (var stash in stashes.OrderByDescending(static stash => stash.UpdatedUtc))
        {
            Stashes.Add(stash);
        }

        OnPropertyChanged(nameof(StashesVisibility));
    }

    internal bool AddContext(string kind, string label, string text,
        PiStation.Protocol.Identifiers.ThreadId? sourceThreadId = null, string? messageId = null,
        string? relativePath = null, int? startLine = null, int? endLine = null, string? sourceTextSha256 = null)
    {
        try
        {
            _composer.AddContext(new ComposerContextChipViewModel(Guid.NewGuid().ToString("N"), kind, label, text,
                sourceThreadId, messageId, relativePath, startLine, endLine, sourceTextSha256));
            OnPropertyChanged(nameof(ContextChipsVisibility));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            Status = exception.Message;
            return false;
        }
    }

    internal void RemoveContext(ComposerContextChipViewModel chip)
    {
        ContextChips.Remove(chip);
        OnPropertyChanged(nameof(ContextChipsVisibility));
    }

    internal void ClearContext()
    {
        ContextChips.Clear();
        OnPropertyChanged(nameof(ContextChipsVisibility));
    }

    internal static string AppendContext(string prompt, IReadOnlyList<ComposerContext>? context) =>
        ComposerContextDefaults.AppendToPrompt(prompt, context);

    internal static bool TryReadCommandToken(
        string text,
        int caret,
        out char prefix,
        out string query,
        out int start,
        out int end)
    {
        prefix = default;
        query = string.Empty;
        start = -1;
        end = -1;
        if (caret <= 0 || caret > text.Length)
        {
            return false;
        }

        start = caret - 1;
        while (start >= 0 && !char.IsWhiteSpace(text[start]))
        {
            if (text[start] is '/' or '$')
            {
                break;
            }

            start--;
        }

        if (start < 0 || text[start] is not ('/' or '$') ||
            (start > 0 && !char.IsWhiteSpace(text[start - 1])))
        {
            return false;
        }

        prefix = text[start];
        query = text[(start + 1)..caret];
        if (query.Any(char.IsWhiteSpace) || query.IndexOfAny(['/', '$']) >= 0)
        {
            return false;
        }

        end = caret;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        return true;
    }

    private static bool TryReadCommandToken(string text, int caret, out char prefix, out string query) =>
        TryReadCommandToken(text, caret, out prefix, out query, out _, out _);

}

public sealed record ComposerContextChipViewModel(
    string Id,
    string Kind,
    string Label,
    string Text,
    PiStation.Protocol.Identifiers.ThreadId? SourceThreadId = null,
    string? MessageId = null,
    string? RelativePath = null,
    int? StartLine = null,
    int? EndLine = null,
    string? SourceTextSha256 = null,
    string? Comment = null)
{
    public ComposerContext ToContext() => new(Id, Kind, Label, Text, SourceThreadId, MessageId, RelativePath, StartLine, EndLine, SourceTextSha256, Comment);

    public static ComposerContextChipViewModel FromContext(ComposerContext context) => new(
        context.Id, context.Kind, context.Label, context.Text, context.SourceThreadId,
        context.MessageId, context.RelativePath, context.StartLine, context.EndLine, context.SourceTextSha256, context.Comment);
}
