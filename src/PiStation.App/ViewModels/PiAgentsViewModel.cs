using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;

namespace PiStation.App.ViewModels;

public sealed class PiAgentsViewModel : ObservableObject
{
    private PiAgentSetup? _snapshot;
    private bool _available;
    private bool _ready;
    private bool _busy;
    private bool _launcherExpanded;
    private string _status = string.Empty;
    private string _mode = "single";
    private string? _selectedPreset;
    private string _name = "scout", _description = string.Empty, _instructions = string.Empty, _tools = "read, grep, find, ls", _model = string.Empty;
    private string? _threadId;
    private bool _loadedEditor;
    private readonly Dictionary<string, (string Mode, AgentTaskEditorViewModel[] Tasks)> _drafts = new(StringComparer.Ordinal);
    public PiAgentsViewModel() { AddTask(); }
    public PiAgentSetup? Snapshot => _snapshot;
    public string? EditRevision { get; private set; }
    public ObservableCollection<string> PresetNames { get; } = [];
    public ObservableCollection<AgentTaskEditorViewModel> Tasks { get; } = [];
    public IReadOnlyList<string> Modes { get; } = ["single", "parallel", "chain"];
    public string Mode { get => _mode; set { SetProperty(ref _mode, value); RaiseState(); } }
    public string Status { get => _status; internal set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _busy; internal set { SetProperty(ref _busy, value); RaiseState(); } }
    public bool LauncherExpanded { get => _launcherExpanded; set => SetProperty(ref _launcherExpanded, value); }
    public string? SelectedPreset
    {
        get => _selectedPreset;
        set { if (SetProperty(ref _selectedPreset, value) && _snapshot?.Presets.FirstOrDefault(p => p.Name == value) is { } preset) LoadPreset(preset); }
    }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Description { get => _description; set => SetProperty(ref _description, value); }
    public string Instructions { get => _instructions; set => SetProperty(ref _instructions, value); }
    public string Tools { get => _tools; set => SetProperty(ref _tools, value); }
    public string Model { get => _model; set => SetProperty(ref _model, value); }
    public Visibility Visibility => _snapshot is null ? Visibility.Collapsed : Visibility.Visible;
    public bool CanManage => _available && _ready && !IsBusy;
    public bool CanLaunch => CanManage && _snapshot is { Available: true, Enabled: true };
    public bool CanRun => CanLaunch && Tasks.Count is > 0 and <= 8 && (Mode != "single" || Tasks.Count == 1) && Tasks.All(task => !string.IsNullOrWhiteSpace(task.Agent) && !string.IsNullOrWhiteSpace(task.Task));
    public string SetupSummary => _snapshot is null ? "Agent setup has not loaded." : !_snapshot.Available ? _snapshot.Message : $"Bundled integration · {(_snapshot.Enabled ? "enabled" : "disabled")} · {_snapshot.Presets.Count} presets";
    public string ToggleAction => _snapshot?.Enabled == true ? "disable" : "enable";
    public string ToggleLabel => _snapshot?.Enabled == true ? "Disable" : "Enable";
    public string WorkflowHint => Mode == "single" ? "One task. The selected preset controls its tools and instructions." : Mode == "parallel"
        ? "Up to eight tasks, four at once. Children share files; assign separate work to avoid conflicting edits."
        : "Tasks run in order. Use {previous} in a task to include the prior result. A failure stops the chain.";

    internal void SetCommandsAvailable(bool available) { _available = available; RaiseState(); }
    internal void Apply(ThreadProjection? projection)
    {
        var threadId = projection?.ThreadId.Value;
        if (_threadId != threadId)
        {
            if (_threadId is not null) _drafts[_threadId] = (Mode, Tasks.ToArray());
            _threadId = threadId;
            Tasks.Clear();
            if (threadId is not null && _drafts.TryGetValue(threadId, out var draft))
            { Mode = draft.Mode; foreach (var task in draft.Tasks) Tasks.Add(task); }
            else { Mode = "single"; AddTask(); }
            Status = string.Empty;
        }
        _ready = projection?.RuntimeState is ThreadRuntimeState.Ready or ThreadRuntimeState.Stopped && projection.Queue?.PendingMessageCount is null or 0 && projection.Plan?.Mode is null or "off";
        var setup = projection?.AgentSetup;
        if (!ReferenceEquals(_snapshot, setup))
        {
            var previous = _snapshot;
            _snapshot = setup;
            if (setup is not null && (setup.Revision != previous?.Revision || previous is null))
            {
                var selected = _selectedPreset;
                PresetNames.Clear();
                foreach (var preset in setup.Presets) PresetNames.Add(preset.Name);
                _selectedPreset = selected is not null && PresetNames.Contains(selected) ? selected : PresetNames.FirstOrDefault();
                if (!_loadedEditor && setup.Presets.FirstOrDefault(p => p.Name == _selectedPreset) is { } initial) { LoadPreset(initial); _loadedEditor = true; }
                if (setup.Presets.FirstOrDefault(p => p.Name == Name) is { } saved && saved.Description == Description && saved.SystemPrompt == Instructions.ReplaceLineEndings("\n") &&
                    saved.Tools.SequenceEqual(EditedPreset().Tools) && (saved.Model ?? string.Empty) == Model) EditRevision = setup.Revision;
                OnPropertyChanged(nameof(SelectedPreset));
            }
        }
        RaiseState();
    }
    private void LoadPreset(PiAgentPreset preset)
    {
        Name = preset.Name; Description = preset.Description; Instructions = preset.SystemPrompt; Tools = string.Join(", ", preset.Tools); Model = preset.Model ?? string.Empty;
        EditRevision = _snapshot?.Revision;
    }
    public PiAgentPreset EditedPreset() => new(Name.Trim(), Description.Trim(), Instructions.ReplaceLineEndings("\n"),
        Tools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), string.IsNullOrWhiteSpace(Model) ? null : Model.Trim());
    public PiAgentWorkflow Workflow() => new(Mode, Tasks.Select(task => new PiAgentTask(task.Agent, task.Task.ReplaceLineEndings("\n"))).ToArray());
    public void NewPreset() { SelectedPreset = null; Name = "custom-agent"; Description = string.Empty; Instructions = string.Empty; Tools = "read, grep, find, ls"; Model = string.Empty; EditRevision = _snapshot?.Revision; }
    public void ReloadPreset() { if (_snapshot?.Presets.FirstOrDefault(p => p.Name == SelectedPreset) is { } preset) LoadPreset(preset); }
    public void AddTask()
    {
        if (Tasks.Count >= 8) return;
        var task = new AgentTaskEditorViewModel(PresetNames);
        task.PropertyChanged += (_, _) => RaiseState();
        Tasks.Add(task); RaiseState();
    }
    public void RemoveTask(AgentTaskEditorViewModel task) { Tasks.Remove(task); RaiseState(); }
    private void RaiseState()
    {
        foreach (var name in new[] { nameof(Visibility), nameof(CanManage), nameof(CanLaunch), nameof(CanRun), nameof(SetupSummary), nameof(ToggleAction), nameof(ToggleLabel), nameof(WorkflowHint) }) OnPropertyChanged(name);
    }
}

public sealed class AgentTaskEditorViewModel(ObservableCollection<string> presets) : ObservableObject
{
    private string _agent = "scout", _task = string.Empty;
    public ObservableCollection<string> PresetNames { get; } = presets;
    public string Agent { get => _agent; set => SetProperty(ref _agent, value); }
    public string Task { get => _task; set => SetProperty(ref _task, value); }
}
