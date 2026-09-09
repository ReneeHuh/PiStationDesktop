using CommunityToolkit.Mvvm.ComponentModel;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;

namespace PiStation.App.ViewModels;

public sealed class PiShellViewModel : ObservableObject
{
    private ThreadProjection? _projection;
    private readonly Dictionary<ThreadId, (string Text, bool Include)> _drafts = [];
    private string _commandText = "";
    private string _notice = "";
    private bool _includeInContext = true;
    private bool _isBusy;
    private bool _canOperate;
    public PiShellExecution? Snapshot => _projection?.ShellExecution;
    public bool IsBusy { get => _isBusy; internal set { if (SetProperty(ref _isBusy, value)) RaiseState(); } }
    public string Notice { get => _notice; internal set => SetProperty(ref _notice, value); }
    public string CommandText { get => _commandText; set { if (SetProperty(ref _commandText, value)) RaiseState(); } }
    public bool IncludeInContext { get => _includeInContext; set => SetProperty(ref _includeInContext, value); }
    public bool CanEdit => !IsBusy && Snapshot?.IsActive != true;
    public bool IsInputReadOnly => !CanEdit;
    public bool CanRun => _canOperate && CanEdit && _projection is { RuntimeState: ThreadRuntimeState.Ready or ThreadRuntimeState.Stopped }
        && _projection.Plan is not { Mode: not "off" } && _projection.Compaction?.State != ContextCompactionState.Running
        && _projection.Queue?.PendingMessageCount is not > 0
        && !_projection.Timeline.Any(item => item is ApprovalTimelineItem { State: InteractionState.Pending } or QuestionTimelineItem { State: InteractionState.Pending })
        && !string.IsNullOrWhiteSpace(CommandText) && CommandText.Length <= PiShellExecution.MaximumCommandLength;
    public bool CanCancel => _canOperate && !IsBusy && Snapshot is { IsActive: true };
    public string LastCommand => Snapshot?.Command ?? "No shell command has run in this thread.";
    public string Output => Snapshot?.Output ?? "";
    public string ResultSummary => Snapshot is not { } state ? "" :
        $"{state.State} · Context setting: {(state.ExcludeFromContext ? "Exclude output" : "Include output with the next prompt")}" +
        (state.ExitCode is { } code ? $" · Exit {code}" : "") +
        (state.Truncated ? "\nOutput is truncated; showing a bounded result." : "") +
        (state.FullOutputPath is { } path ? $"\nFull output on the host: {path}" : "") +
        (state.Error is { } error ? "\n" + error : "");

    internal void Apply(ThreadProjection? projection)
    {
        if (_projection?.ThreadId != projection?.ThreadId)
        {
            if (_projection is { } previous)
            {
                _drafts[previous.ThreadId] = (CommandText, IncludeInContext);
                if (_drafts.Count > 128) _drafts.Remove(_drafts.Keys.First());
            }
            var draft = projection is not null && _drafts.TryGetValue(projection.ThreadId, out var saved) ? saved : ("", true);
            CommandText = draft.Item1;
            IncludeInContext = draft.Item2;
            Notice = "";
        }
        if (Snapshot?.IsActive == true && projection?.ShellExecution?.IsActive == false) Notice = "";
        _projection = projection;
        RaiseState();
    }

    internal void SetAvailable(bool available) { _canOperate = available; RaiseState(); }

    private void RaiseState()
    {
        foreach (var name in new[] { nameof(CanRun), nameof(CanCancel), nameof(CanEdit), nameof(IsInputReadOnly),
                     nameof(Output), nameof(ResultSummary), nameof(LastCommand), nameof(Snapshot) }) OnPropertyChanged(name);
    }
}
