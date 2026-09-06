using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;

namespace PiStation.App.ViewModels;

public sealed class PiPlanViewModel : ObservableObject
{
    private string _editText = string.Empty;
    private string _status = string.Empty;
    private bool _busy;
    private bool _dirty;
    private bool _ready;
    private bool _commandsAvailable;
    private string? _sessionId;
    private readonly Dictionary<string, (string Text, long Revision)> _edits = new(StringComparer.Ordinal);
    public PiPlanState? Snapshot { get; private set; }
    public long EditRevision { get; private set; }
    public string EditText
    {
        get => _editText;
        set
        {
            value = value.ReplaceLineEndings("\n");
            if (!SetProperty(ref _editText, value)) return;
            if (!_dirty) EditRevision = Snapshot?.Revision ?? 0;
            _dirty = value != Snapshot?.Text;
            if (_sessionId is { } id)
            {
                if (_dirty) _edits[id] = (value, EditRevision); else _edits.Remove(id);
            }
            RaiseState();
        }
    }
    public string Status { get => _status; internal set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _busy; internal set { SetProperty(ref _busy, value); RaiseState(); } }
    public Visibility Visibility => Snapshot is null ? Visibility.Collapsed : Visibility.Visible;
    public string Heading => Snapshot is null ? "Plan" : $"Plan · {Snapshot.Mode} · {Snapshot.Steps.Count(step => step.Completed)}/{Snapshot.Steps.Count} steps";
    public string Policy => Snapshot?.Mode is "off" or "executing"
        ? "Normal Pi tools are enabled." : "Read-only planning: dedicated file read/search tools only. Approve execution to enable normal Pi tools.";
    public string Progress => Snapshot is null ? string.Empty : string.Join('\n', Snapshot.Steps.Select(step => $"{(step.Completed ? "✓" : "○")} {step.Number}. {step.Text}"));
    public bool CanAct => Snapshot is not null && _ready && _commandsAvailable && !IsBusy;
    public bool CanSave => CanAct && _dirty && !string.IsNullOrWhiteSpace(EditText);
    public bool CanExecute => CanAct && !_dirty && Snapshot?.Mode is "ready" or "paused" && Snapshot.Steps.Any(step => !step.Completed);
    public bool CanExport => Snapshot?.Text.Length > 0 && !IsBusy;
    public bool CanChangeMode => CanAct && !_dirty;

    internal void SetCommandsAvailable(bool available) { _commandsAvailable = available; RaiseState(); }

    internal void Apply(ThreadProjection? projection)
    {
        var state = projection?.Plan;
        var changedSession = state?.SessionId != _sessionId;
        _sessionId = state?.SessionId;
        Snapshot = state;
        _ready = projection?.RuntimeState == ThreadRuntimeState.Ready && projection.Queue?.PendingMessageCount is null or 0;
        if (changedSession)
        {
            Status = string.Empty;
            _dirty = state is not null && _edits.ContainsKey(state.SessionId);
            var edit = state is not null ? _edits.GetValueOrDefault(state.SessionId) : default;
            _editText = _dirty ? edit.Text : state?.Text ?? string.Empty;
            EditRevision = _dirty ? edit.Revision : state?.Revision ?? 0;
        }
        else if (!_dirty || state?.Text == _editText)
        {
            _editText = state?.Text ?? string.Empty;
            EditRevision = state?.Revision ?? 0;
            _dirty = false;
            if (_sessionId is { } id) _edits.Remove(id);
        }
        OnPropertyChanged(nameof(EditText));
        RaiseState();
    }

    public void ReloadEditor()
    {
        _dirty = false;
        if (_sessionId is { } id) _edits.Remove(id);
        _editText = Snapshot?.Text ?? string.Empty;
        EditRevision = Snapshot?.Revision ?? 0;
        OnPropertyChanged(nameof(EditText));
        RaiseState();
    }

    private void RaiseState()
    {
        foreach (var name in new[] { nameof(Visibility), nameof(Heading), nameof(Policy), nameof(Progress), nameof(CanAct), nameof(CanSave), nameof(CanExecute), nameof(CanExport), nameof(CanChangeMode) })
            OnPropertyChanged(name);
    }
}
