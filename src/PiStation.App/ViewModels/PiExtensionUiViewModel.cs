using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;

namespace PiStation.App.ViewModels;

public sealed class PiExtensionUiViewModel : ObservableObject
{
    private string _heading = "Pi extensions";
    private string _status = string.Empty;
    private string _notifications = string.Empty;
    private string _aboveWidgets = string.Empty;
    private string _belowWidgets = string.Empty;
    private PiExtensionEditorSuggestion? _suggestion;
    private (ThreadId Thread, ProjectionEpoch Epoch)? _scope;
    private readonly HashSet<(ThreadId Thread, ProjectionEpoch Epoch, string Id)> _usedSuggestions = [];
    private readonly Queue<(ThreadId Thread, ProjectionEpoch Epoch, string Id)> _usedSuggestionOrder = [];
    public string Heading { get => _heading; private set => SetProperty(ref _heading, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Notifications { get => _notifications; private set => SetProperty(ref _notifications, value); }
    public string AboveWidgets { get => _aboveWidgets; private set => SetProperty(ref _aboveWidgets, value); }
    public string BelowWidgets { get => _belowWidgets; private set => SetProperty(ref _belowWidgets, value); }
    public string SuggestedText => _suggestion?.Text ?? string.Empty;
    public Visibility PanelVisibility => Heading != "Pi extensions" || Status.Length + Notifications.Length + AboveWidgets.Length > 0 || _suggestion is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BelowVisibility => BelowWidgets.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SuggestionVisibility => _suggestion is null ? Visibility.Collapsed : Visibility.Visible;

    internal void Apply(ThreadProjection? projection)
    {
        var state = projection?.ExtensionUi ?? PiExtensionUiState.Empty;
        _scope = projection is null ? null : (projection.ThreadId, projection.ProjectionEpoch);
        Heading = string.IsNullOrWhiteSpace(state.Title) ? "Pi extensions" : state.Title;
        Status = string.Join(" · ", state.Statuses.Select(item => item.Text));
        Notifications = string.Join(Environment.NewLine, state.Notifications.TakeLast(5).Select(item => $"{item.Severity}: {item.Text}"));
        AboveWidgets = Widgets(state, "aboveEditor");
        BelowWidgets = Widgets(state, "belowEditor");
        _suggestion = state.EditorSuggestion is { } suggestion && _scope is { } scope &&
            !_usedSuggestions.Contains((scope.Thread, scope.Epoch, suggestion.Id)) ? suggestion : null;
        RaiseVisibility();
    }

    internal string TakeSuggestedText()
    {
        var text = SuggestedText;
        if (_suggestion is { } suggestion && _scope is { } scope && _usedSuggestions.Add((scope.Thread, scope.Epoch, suggestion.Id)))
        {
            _usedSuggestionOrder.Enqueue((scope.Thread, scope.Epoch, suggestion.Id));
            if (_usedSuggestionOrder.Count > 1024) _usedSuggestions.Remove(_usedSuggestionOrder.Dequeue());
        }
        _suggestion = null;
        RaiseVisibility();
        return text;
    }

    private static string Widgets(PiExtensionUiState state, string placement) => string.Join(Environment.NewLine + Environment.NewLine,
        state.Widgets.Where(item => item.Placement == placement).Select(item => string.Join(Environment.NewLine, item.Lines)));

    private void RaiseVisibility()
    {
        OnPropertyChanged(nameof(SuggestedText));
        OnPropertyChanged(nameof(PanelVisibility));
        OnPropertyChanged(nameof(BelowVisibility));
        OnPropertyChanged(nameof(SuggestionVisibility));
    }
}
