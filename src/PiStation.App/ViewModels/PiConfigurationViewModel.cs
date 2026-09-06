using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed class PiConfigurationViewModel : ObservableObject
{
    private bool _isPending;
    private PiModelOptionViewModel? _selectedModel;
    private PiThinkingLevelOptionViewModel? _selectedThinkingLevel;
    private string _status = string.Empty;

    public ObservableCollection<PiModelOptionViewModel> Models { get; } = [];

    public ObservableCollection<PiThinkingLevelOptionViewModel> ThinkingLevels { get; } = [];

    internal ThreadPiConfigurationSnapshot? Snapshot { get; set; }

    internal bool IsPending
    {
        get => _isPending;
        set => SetProperty(ref _isPending, value);
    }

    public string Status
    {
        get => _status;
        internal set => SetProperty(ref _status, value);
    }

    public PiModelOptionViewModel? SelectedModel
    {
        get => _selectedModel;
        internal set => SetProperty(ref _selectedModel, value);
    }

    public PiThinkingLevelOptionViewModel? SelectedThinkingLevel
    {
        get => _selectedThinkingLevel;
        internal set => SetProperty(ref _selectedThinkingLevel, value);
    }

    public Visibility ModelSelectorVisibility => Models.Count == 0
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility ThinkingLevelSelectorVisibility => ThinkingLevels.Count == 0
        ? Visibility.Collapsed
        : Visibility.Visible;

    internal void Apply(ThreadPiConfigurationSnapshot snapshot)
    {
        Snapshot = snapshot;
        Replace(Models, snapshot.Capabilities.Models.Select(static model => new PiModelOptionViewModel(model)));
        Replace(
            ThinkingLevels,
            snapshot.Capabilities.ThinkingLevels.Select(static level => new PiThinkingLevelOptionViewModel(level)));
        SelectedModel = Models.FirstOrDefault(model => Matches(snapshot.ActiveModel, model));
        SelectedThinkingLevel = ThinkingLevels.FirstOrDefault(level => level.Value == snapshot.ActiveThinkingLevel);
        Status = Models.Count == 0 && ThinkingLevels.Count == 0
            ? "Pi reports no configurable settings"
            : $"{SelectedModel?.DisplayName ?? "Default model"} • " +
              $"{SelectedThinkingLevel?.DisplayName ?? "Default reasoning"}";
        RaiseSelectorVisibilityChanged();
    }

    internal void Clear(string status)
    {
        Snapshot = null;
        Replace(Models, []);
        Replace(ThinkingLevels, []);
        SelectedModel = null;
        SelectedThinkingLevel = null;
        Status = status;
        RaiseSelectorVisibilityChanged();
    }

    internal void SetSaving()
    {
        IsPending = true;
        Status = "Saving Pi settings…";
    }

    private void RaiseSelectorVisibilityChanged()
    {
        OnPropertyChanged(nameof(ModelSelectorVisibility));
        OnPropertyChanged(nameof(ThinkingLevelSelectorVisibility));
    }

    private static bool Matches(
        PiStation.Protocol.Models.PiModelSelection? model,
        PiModelOptionViewModel option) =>
        model is not null &&
        string.Equals(model.ProviderId, option.ProviderId, StringComparison.Ordinal) &&
        string.Equals(model.ModelId, option.ModelId, StringComparison.Ordinal);

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
