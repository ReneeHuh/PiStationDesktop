using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;

namespace PiStation.App.ViewModels;

public sealed class ThreadViewModel : ObservableObject
{
    private const string EmptyThreadMessage = "Add a local project, create a thread, and ask Pi a question.";
    private bool _hasPiCrash;
    private bool _hasSelectedThread;
    private string _latestAssistantText = EmptyThreadMessage;
    private string _piCrashMessage = string.Empty;
    private ThreadProjection? _projection;
    private string _turnStatus = "No active thread";

    public ObservableCollection<TimelineItemViewModel> Timeline { get; } = [];

    public ThreadQueueViewModel Queue { get; } = new();

    public ThreadProjection? Projection
    {
        get => _projection;
        private set => SetProperty(ref _projection, value);
    }

    public string TurnStatus
    {
        get => _turnStatus;
        private set => SetProperty(ref _turnStatus, value);
    }

    public Visibility TurnStatusRunningVisibility => Projection?.RuntimeState is
        ThreadRuntimeState.Starting or ThreadRuntimeState.Hydrating or
        ThreadRuntimeState.Running or ThreadRuntimeState.Stopping
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility TurnStatusCriticalVisibility => Projection?.RuntimeState == ThreadRuntimeState.Crashed
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility TurnStatusIdleVisibility =>
        TurnStatusRunningVisibility == Visibility.Collapsed &&
        TurnStatusCriticalVisibility == Visibility.Collapsed
            ? Visibility.Visible
            : Visibility.Collapsed;

    public bool HasPiCrash
    {
        get => _hasPiCrash;
        private set => SetProperty(ref _hasPiCrash, value);
    }

    public string PiCrashMessage
    {
        get => _piCrashMessage;
        private set => SetProperty(ref _piCrashMessage, value);
    }

    public string LatestAssistantText
    {
        get => _latestAssistantText;
        private set => SetProperty(ref _latestAssistantText, value);
    }

    public Visibility EmptyTimelineVisibility => _hasSelectedThread && Timeline.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public void ApplyProjection(ThreadProjection? projection, bool hasSelectedThread)
    {
        _hasSelectedThread = hasSelectedThread;
        Projection = projection;
        Queue.Apply(projection?.Queue, projection?.RuntimeState);
        Reconcile(Timeline, CreatePresentationTimeline(
            projection?.Timeline ?? [],
            projection?.Checkpoints ?? [],
            projection?.RuntimeState == ThreadRuntimeState.Ready));
        OnPropertyChanged(nameof(EmptyTimelineVisibility));
        LatestAssistantText = projection?.Timeline
            .OfType<MessageTimelineItem>()
            .LastOrDefault(message => message.Role == MessageRole.Assistant)?.Text ?? EmptyThreadMessage;
        TurnStatus = projection?.RuntimeState switch
        {
            ThreadRuntimeState.Starting => "Starting Pi",
            ThreadRuntimeState.Hydrating => "Loading session",
            ThreadRuntimeState.Ready => "Idle",
            ThreadRuntimeState.Running => "Pi is working",
            ThreadRuntimeState.Stopping => "Stopping",
            ThreadRuntimeState.Crashed => "Pi crashed",
            ThreadRuntimeState.Stopped => "Idle · resumes on send",
            _ => hasSelectedThread ? "Connecting" : "No active thread",
        };
        OnPropertyChanged(nameof(TurnStatusRunningVisibility));
        OnPropertyChanged(nameof(TurnStatusCriticalVisibility));
        OnPropertyChanged(nameof(TurnStatusIdleVisibility));
        if (projection?.RuntimeState == ThreadRuntimeState.Crashed)
        {
            PiCrashMessage = projection.LastError?.Message ?? "The Pi process stopped unexpectedly.";
            HasPiCrash = true;
        }
        else
        {
            PiCrashMessage = string.Empty;
            HasPiCrash = false;
        }
    }

    public void Clear(bool hasSelectedThread) => ApplyProjection(null, hasSelectedThread);

    private List<TimelineItemViewModel> CreatePresentationTimeline(
        IReadOnlyList<TimelineItem> timeline,
        IReadOnlyList<ThreadCheckpoint> checkpoints,
        bool threadIsReady)
    {
        var previousThinking = Timeline
            .OfType<ThinkingTimelineItemViewModel>()
            .ToDictionary(item => item.ItemId, StringComparer.Ordinal);
        var previousToolGroups = Timeline
            .OfType<ToolActivityGroupTimelineItemViewModel>()
            .ToDictionary(item => item.ItemId, StringComparer.Ordinal);
        var result = new List<TimelineItemViewModel>();
        var checkpointsByTurn = checkpoints.ToDictionary(
            static checkpoint => checkpoint.TurnId,
            static checkpoint => checkpoint);

        for (var index = 0; index < timeline.Count; index++)
        {
            if (timeline[index] is not ToolTimelineItem firstTool)
            {
                if (timeline[index] is TurnBoundaryTimelineItem
                    {
                        Boundary: TurnBoundaryKind.Settled,
                    } boundary && checkpointsByTurn.TryGetValue(boundary.BoundaryTurnId, out var checkpoint))
                {
                    result.Add(CreateCheckpointViewModel(checkpoint, threadIsReady));
                }

                result.Add(CreateItemViewModel(timeline[index], previousThinking));
                continue;
            }

            var tools = new List<ToolTimelineItem>();
            do
            {
                var tool = (ToolTimelineItem)timeline[index];
                tools.Add(tool);
                index++;
            }
            while (index < timeline.Count &&
                timeline[index] is ToolTimelineItem nextTool &&
                nextTool.TurnId == firstTool.TurnId);

            index--;
            var groupId = $"tool-group-{firstTool.ItemId}";
            if (previousToolGroups.TryGetValue(groupId, out var previousGroup))
            {
                previousGroup.Apply(tools);
                result.Add(previousGroup);
            }
            else
            {
                result.Add(new ToolActivityGroupTimelineItemViewModel(groupId, tools));
            }
        }

        return result;
    }

    private static CheckpointTimelineItemViewModel CreateCheckpointViewModel(
        ThreadCheckpoint checkpoint,
        bool threadIsReady)
    {
        var additions = checkpoint.Files.Sum(static file => file.Additions);
        var deletions = checkpoint.Files.Sum(static file => file.Deletions);
        var files = checkpoint.Files
            .Select(file => new CheckpointFileViewModel(
                checkpoint.TurnCount,
                file.RelativePath,
                file.Additions,
                file.Deletions))
            .ToArray();
        return new CheckpointTimelineItemViewModel(
            $"checkpoint-{checkpoint.TurnCount}",
            checkpoint.TurnCount,
            checkpoint.TurnCount - 1,
            checkpoint.Status,
            files,
            additions,
            deletions,
            threadIsReady &&
            checkpoint.Status == ThreadCheckpointStatus.Ready &&
            (checkpoint.TurnCount == 1 || !string.IsNullOrWhiteSpace(checkpoint.PiEntryIdBeforeTurn)));
    }

    private static TimelineItemViewModel CreateItemViewModel(
        TimelineItem item,
        IReadOnlyDictionary<string, ThinkingTimelineItemViewModel> previousThinking) => item switch
    {
        MessageTimelineItem message => new MessageTimelineItemViewModel(
            message.ItemId,
            message.Role switch
            {
                MessageRole.User => "You",
                MessageRole.Assistant => "Pi",
                MessageRole.System => "System",
                _ => "Tool",
            },
            message.Text,
            message.IsComplete, message.Content),
        ThinkingTimelineItem thinking => CreateThinkingViewModel(thinking, previousThinking),
        StatusTimelineItem status => new StatusTimelineItemViewModel(
            status.ItemId,
            status.Text,
            status.State.ToString(),
            status.State == TimelineActivityState.Running),
        ErrorTimelineItem error => new ErrorTimelineItemViewModel(
            error.ItemId,
            error.Error.Code,
            error.Error.Message,
            error.Error.IsRetryable ? "Retry available" : "Action required"),
        TurnBoundaryTimelineItem boundary => CreateTurnBoundaryViewModel(boundary),
        ApprovalTimelineItem approval => new ApprovalTimelineItemViewModel(
            approval.ItemId,
            approval.InteractionId.Value,
            approval.Title,
            approval.Message,
            approval.State.ToString(),
            approval.State == InteractionState.Pending,
            approval.Decision?.ToString() ?? string.Empty),
        QuestionTimelineItem question => new QuestionTimelineItemViewModel(
            question.ItemId,
            question.InteractionId.Value,
            question.InputKind,
            question.Title,
            question.Prompt ?? string.Empty,
            question.Options,
            question.InitialValue ?? string.Empty,
            question.State.ToString(),
            question.State == InteractionState.Pending,
            question.Answer ?? string.Empty),
        _ => throw new ArgumentOutOfRangeException(nameof(item), item, "Unsupported timeline item."),
    };

    private static ThinkingTimelineItemViewModel CreateThinkingViewModel(
        ThinkingTimelineItem thinking,
        IReadOnlyDictionary<string, ThinkingTimelineItemViewModel> previousThinking)
    {
        if (previousThinking.TryGetValue(thinking.ItemId, out var previous))
        {
            previous.Apply(thinking.Text, thinking.IsComplete);
            return previous;
        }

        return new ThinkingTimelineItemViewModel(thinking.ItemId, thinking.Text, thinking.IsComplete);
    }

    private static TurnBoundaryTimelineItemViewModel CreateTurnBoundaryViewModel(
        TurnBoundaryTimelineItem boundary)
    {
        var label = boundary.Boundary == TurnBoundaryKind.Started ? "Turn started" : "Turn completed";
        var metadata = FormatTurnMetrics(boundary.Metrics);
        var details = FormatTurnMetricDetails(boundary.Metrics);
        var accessibleName = string.IsNullOrEmpty(metadata) ? label : $"{label}, {metadata.Replace(" • ", ", ")}";
        if (!string.IsNullOrEmpty(details))
        {
            accessibleName += $"; {details.Replace(" • ", ", ")}";
        }

        return new TurnBoundaryTimelineItemViewModel(
            boundary.ItemId,
            label,
            metadata,
            details,
            string.IsNullOrEmpty(metadata) ? Visibility.Collapsed : Visibility.Visible,
            accessibleName);
    }

    private static string FormatTurnMetrics(TurnMetrics? metrics)
    {
        if (metrics is null)
        {
            return string.Empty;
        }

        var parts = new List<string>(3);
        if (metrics.ElapsedMilliseconds is { } elapsedMilliseconds)
        {
            parts.Add(FormatElapsedTime(elapsedMilliseconds));
        }

        if (metrics.Usage is { } usage)
        {
            var total = usage.TotalTokens > 0
                ? usage.TotalTokens
                : usage.InputTokens + usage.OutputTokens + usage.CacheReadTokens + usage.CacheWriteTokens;
            parts.Add($"{FormatNumber(total)} {(total == 1 ? "token" : "tokens")}");
        }

        if (metrics.ContextTokens is { } contextTokens && metrics.ContextWindow is > 0)
        {
            parts.Add($"{FormatNumber(contextTokens)} / {FormatNumber(metrics.ContextWindow.Value)} context");
        }

        return string.Join(" • ", parts);
    }

    private static string FormatTurnMetricDetails(TurnMetrics? metrics)
    {
        if (metrics?.Usage is not { } usage)
        {
            return string.Empty;
        }

        var parts = new List<string>
        {
            $"Input {FormatNumber(usage.InputTokens)}",
            $"Output {FormatNumber(usage.OutputTokens)}",
            $"Cache read {FormatNumber(usage.CacheReadTokens)}",
            $"Cache write {FormatNumber(usage.CacheWriteTokens)}",
        };
        if (usage.ReasoningTokens is { } reasoningTokens)
        {
            parts.Add($"Reasoning {FormatNumber(reasoningTokens)}");
        }

        return string.Join(" • ", parts);
    }

    private static string FormatElapsedTime(long elapsedMilliseconds)
    {
        if (elapsedMilliseconds < 1_000)
        {
            return $"{elapsedMilliseconds} ms";
        }

        if (elapsedMilliseconds < 60_000)
        {
            return $"{(elapsedMilliseconds / 1_000d).ToString("0.0", CultureInfo.InvariantCulture)} s";
        }

        var elapsed = TimeSpan.FromMilliseconds(elapsedMilliseconds);
        return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s";
    }

    private static string FormatNumber(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static void Reconcile(
        ObservableCollection<TimelineItemViewModel> target,
        List<TimelineItemViewModel> items)
    {
        for (var index = 0; index < items.Count; index++)
        {
            var desired = items[index];
            if (index < target.Count && ReferenceEquals(target[index], desired))
            {
                continue;
            }

            var existingIndex = -1;
            for (var candidate = index; candidate < target.Count; candidate++)
            {
                if (target[candidate].ItemId == desired.ItemId &&
                    target[candidate].GetType() == desired.GetType())
                {
                    existingIndex = candidate;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                target.Move(existingIndex, index);
                if (!Equals(target[index], desired))
                {
                    target[index] = desired;
                }
            }
            else
            {
                target.Insert(index, desired);
            }
        }

        while (target.Count > items.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}

public abstract record TimelineItemViewModel(string ItemId) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetValue<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record MessageTimelineItemViewModel(
    string ItemId,
    string Role,
    string Text,
    bool IsComplete,
    SentMessageContent? Content = null) : TimelineItemViewModel(ItemId)
{
    public IReadOnlyList<DraftAttachmentViewModel> Attachments => Content?.Attachments.Select(a => new DraftAttachmentViewModel(a)).ToArray() ?? [];
    public IReadOnlyList<ComposerContextChipViewModel> Citations => Content?.Citations.Select(ComposerContextChipViewModel.FromContext).ToArray() ?? [];

    public Visibility MarkdownVisibility => Role == "Pi" ? Visibility.Visible : Visibility.Collapsed;

    public Visibility UserVisibility => Role == "You" ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SupportingMessageVisibility => Role is not ("Pi" or "You")
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string AccessibleName
    {
        get
        {
            var summary = Text.ReplaceLineEndings(" ").Trim();
            if (summary.Length > 120)
            {
                summary = $"{summary[..117]}…";
            }

            return string.IsNullOrWhiteSpace(summary) ? Role : $"{Role}: {summary}";
        }
    }
}

public sealed record ThinkingTimelineItemViewModel : TimelineItemViewModel
{
    private bool _isComplete;
    private bool _isExpanded;
    private string _text;

    public ThinkingTimelineItemViewModel(string itemId, string text, bool isComplete)
        : base(itemId)
    {
        _text = text;
        _isComplete = isComplete;
        _isExpanded = !isComplete;
    }

    public string Text
    {
        get => _text;
        private set => SetValue(ref _text, value);
    }

    public bool IsComplete
    {
        get => _isComplete;
        private set
        {
            if (SetValue(ref _isComplete, value))
            {
                OnPropertyChanged(nameof(State));
                OnPropertyChanged(nameof(AccessibleName));
                OnPropertyChanged(nameof(RunningVisibility));
                OnPropertyChanged(nameof(CompletedVisibility));
            }
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetValue(ref _isExpanded, value);
    }

    public string State => IsComplete ? "Completed" : "Running";

    public string AccessibleName => $"Reasoning, {State.ToLowerInvariant()}";

    public Visibility RunningVisibility => IsComplete ? Visibility.Collapsed : Visibility.Visible;

    public Visibility CompletedVisibility => IsComplete ? Visibility.Visible : Visibility.Collapsed;

    public void Apply(string text, bool isComplete)
    {
        Text = text;
        if (IsComplete != isComplete)
        {
            IsExpanded = !isComplete;
        }

        IsComplete = isComplete;
    }
}

public sealed record ToolActivityGroupTimelineItemViewModel : TimelineItemViewModel
{
    public ToolActivityGroupTimelineItemViewModel(
        string itemId,
        IReadOnlyList<ToolTimelineItem> tools)
        : base(itemId)
    {
        Apply(tools);
    }

    public ObservableCollection<ToolActivityItemViewModel> Tools { get; } = [];

    public ToolExecutionState State => Tools.Any(tool => tool.State == ToolExecutionState.Failed)
        ? ToolExecutionState.Failed
        : Tools.Any(tool => tool.State == ToolExecutionState.Running)
            ? ToolExecutionState.Running
            : ToolExecutionState.Completed;

    public string CountLabel => Tools.Count == 1 ? "1 tool" : $"{Tools.Count} tools";

    public string StateLabel => State.ToString();

    public string AccessibleName => $"Tools, {CountLabel}, {StateLabel.ToLowerInvariant()}";

    public Visibility RunningVisibility => State == ToolExecutionState.Running
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility CompletedVisibility => State == ToolExecutionState.Completed
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility FailedVisibility => State == ToolExecutionState.Failed
        ? Visibility.Visible
        : Visibility.Collapsed;

    public void Apply(IReadOnlyList<ToolTimelineItem> tools)
    {
        for (var index = 0; index < tools.Count; index++)
        {
            var tool = tools[index];
            var existingIndex = -1;
            for (var candidate = index; candidate < Tools.Count; candidate++)
            {
                if (Tools[candidate].ItemId == tool.ItemId)
                {
                    existingIndex = candidate;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                Tools.Move(existingIndex, index);
                Tools[index].Apply(tool);
            }
            else
            {
                Tools.Insert(index, new ToolActivityItemViewModel(tool));
            }
        }

        while (Tools.Count > tools.Count)
        {
            Tools.RemoveAt(Tools.Count - 1);
        }

        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(AccessibleName));
        OnPropertyChanged(nameof(RunningVisibility));
        OnPropertyChanged(nameof(CompletedVisibility));
        OnPropertyChanged(nameof(FailedVisibility));
    }
}

public sealed class ToolActivityItemViewModel : ObservableObject
{
    private string _arguments;
    private bool _isExpanded;
    private string _name;
    private string _output;
    private ToolExecutionState _state;

    public ToolActivityItemViewModel(ToolTimelineItem tool)
    {
        ItemId = tool.ItemId;
        ToolCallId = tool.ToolCallId;
        _name = tool.ToolName;
        _arguments = tool.ArgumentsPreview;
        _output = tool.OutputPreview;
        _state = tool.State;
        _isExpanded = tool.State is ToolExecutionState.Running or ToolExecutionState.Failed;
    }

    public string ItemId { get; }

    public string ToolCallId { get; }

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    public string Arguments
    {
        get => _arguments;
        private set
        {
            if (SetProperty(ref _arguments, value))
            {
                OnPropertyChanged(nameof(ArgumentsDisplay));
            }
        }
    }

    public string ArgumentsDisplay => string.IsNullOrWhiteSpace(Arguments) ? "No arguments" : Arguments;

    public string Output
    {
        get => _output;
        private set
        {
            if (SetProperty(ref _output, value))
            {
                OnPropertyChanged(nameof(OutputDisplay));
            }
        }
    }

    public string OutputDisplay => string.IsNullOrEmpty(Output)
        ? State == ToolExecutionState.Running ? "Waiting for output…" : "No output returned."
        : Output;

    public ToolExecutionState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateLabel));
                OnPropertyChanged(nameof(AccessibleName));
                OnPropertyChanged(nameof(OutputDisplay));
                OnPropertyChanged(nameof(RunningVisibility));
                OnPropertyChanged(nameof(CompletedVisibility));
                OnPropertyChanged(nameof(FailedVisibility));
            }
        }
    }

    public string StateLabel => State.ToString();

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public string AccessibleName => $"{Name} tool, {StateLabel.ToLowerInvariant()}";

    public Visibility RunningVisibility => State == ToolExecutionState.Running
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility CompletedVisibility => State == ToolExecutionState.Completed
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility FailedVisibility => State == ToolExecutionState.Failed
        ? Visibility.Visible
        : Visibility.Collapsed;

    public void Apply(ToolTimelineItem tool)
    {
        Name = tool.ToolName;
        Arguments = tool.ArgumentsPreview;
        Output = tool.OutputPreview;
        if (State != tool.State)
        {
            IsExpanded = tool.State is ToolExecutionState.Running or ToolExecutionState.Failed;
        }

        State = tool.State;
    }
}

public sealed record StatusTimelineItemViewModel(
    string ItemId,
    string Text,
    string State,
    bool IsRunning) : TimelineItemViewModel(ItemId);

public sealed record ErrorTimelineItemViewModel(
    string ItemId,
    string Code,
    string Message,
    string Recovery) : TimelineItemViewModel(ItemId);

public sealed record TurnBoundaryTimelineItemViewModel(
    string ItemId,
    string Label,
    string Metadata,
    string Details,
    Visibility MetadataVisibility,
    string AccessibleName) : TimelineItemViewModel(ItemId);

public sealed record CheckpointTimelineItemViewModel(
    string ItemId,
    int TurnCount,
    int RevertTargetTurnCount,
    ThreadCheckpointStatus Status,
    IReadOnlyList<CheckpointFileViewModel> Files,
    int Additions,
    int Deletions,
    bool CanRevert) : TimelineItemViewModel(ItemId)
{
    public string Summary => Status switch
    {
        ThreadCheckpointStatus.Error => "Checkpoint capture failed",
        ThreadCheckpointStatus.Missing when Files.Count == 0 => "Workspace saved; conversation anchor missing",
        ThreadCheckpointStatus.Missing =>
            $"{Files.Count} changed {(Files.Count == 1 ? "file" : "files")} • conversation anchor missing",
        _ when Files.Count == 0 => "No files changed",
        _ => $"{Files.Count} changed {(Files.Count == 1 ? "file" : "files")}",
    };

    public string TurnLabel => $"Turn {TurnCount} checkpoint";

    public string AdditionsLabel => $"+{Additions.ToString("N0", CultureInfo.InvariantCulture)}";

    public string DeletionsLabel => $"−{Deletions.ToString("N0", CultureInfo.InvariantCulture)}";

    public Visibility StatsVisibility => Status != ThreadCheckpointStatus.Error
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility FilesVisibility => Files.Count == 0
        ? Visibility.Collapsed
        : Visibility.Visible;

    public bool CanViewDiff => Status != ThreadCheckpointStatus.Error;

    public string AccessibleName =>
        $"Turn {TurnCount} checkpoint, {Summary}, {AdditionsLabel} additions, {DeletionsLabel} deletions";
}

public sealed record CheckpointFileViewModel(
    int TurnCount,
    string RelativePath,
    int Additions,
    int Deletions)
{
    public string FileName => RelativePath.Split('/').LastOrDefault() ?? RelativePath;

    public string AdditionsLabel => $"+{Additions.ToString("N0", CultureInfo.InvariantCulture)}";

    public string DeletionsLabel => $"−{Deletions.ToString("N0", CultureInfo.InvariantCulture)}";

    public string AccessibleName =>
        $"{RelativePath}, {AdditionsLabel} additions, {DeletionsLabel} deletions";
}

public sealed record ApprovalTimelineItemViewModel(
    string ItemId,
    string InteractionId,
    string Title,
    string Message,
    string State,
    bool IsPending,
    string Decision) : TimelineItemViewModel(ItemId)
{
    public string Resolution => Decision switch
    {
        nameof(ApprovalDecision.Approve) => "Approved",
        nameof(ApprovalDecision.Reject) => "Rejected",
        _ => State,
    };
}

public sealed record QuestionTimelineItemViewModel(
    string ItemId,
    string InteractionId,
    QuestionInputKind InputKind,
    string Title,
    string Prompt,
    IReadOnlyList<string> Options,
    string InitialValue,
    string State,
    bool IsPending,
    string SubmittedAnswer) : TimelineItemViewModel(ItemId)
{
    public string AnswerText { get; set; } = InitialValue;

    public string? SelectedOption { get; set; } = Options.Count == 0 ? null : Options[0];

    public Visibility OptionsVisibility => InputKind == QuestionInputKind.Select
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility TextVisibility => InputKind == QuestionInputKind.Select
        ? Visibility.Collapsed
        : Visibility.Visible;

    public string Resolution => string.IsNullOrEmpty(SubmittedAnswer) ? State : $"{State}: {SubmittedAnswer}";
}
