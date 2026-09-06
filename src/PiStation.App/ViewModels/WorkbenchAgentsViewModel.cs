using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PiStation.Protocol.Projections;

namespace PiStation.App.ViewModels;

public sealed class WorkbenchAgentsViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherQueueTimer _elapsedTimer;
    private string _summary = "No agent or workflow activity for this thread.";

    public WorkbenchAgentsViewModel(DispatcherQueue dispatcherQueue)
    {
        ArgumentNullException.ThrowIfNull(dispatcherQueue);
        _elapsedTimer = dispatcherQueue.CreateTimer();
        _elapsedTimer.Interval = TimeSpan.FromSeconds(1);
        _elapsedTimer.IsRepeating = true;
        _elapsedTimer.Tick += OnElapsedTimerTick;
    }

    public ObservableCollection<AgentActivityRowViewModel> Activities { get; } = [];

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public Visibility EmptyVisibility => Activities.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ActivityVisibility => Activities.Count == 0
        ? Visibility.Collapsed
        : Visibility.Visible;

    public void Apply(IReadOnlyList<AgentActivityProjection>? activities)
    {
        var ordered = FlattenHierarchy(activities ?? []);
        var existing = Activities.ToDictionary(
            static activity => activity.ActivityId,
            StringComparer.Ordinal);
        for (var index = 0; index < ordered.Count; index++)
        {
            var item = ordered[index];
            if (existing.TryGetValue(item.Activity.ActivityId, out var row))
            {
                var currentIndex = Activities.IndexOf(row);
                if (currentIndex != index)
                {
                    Activities.Move(currentIndex, index);
                }

                row.Apply(item.Activity, item.Depth);
            }
            else
            {
                Activities.Insert(index, new AgentActivityRowViewModel(item.Activity, item.Depth));
            }
        }

        while (Activities.Count > ordered.Count)
        {
            Activities.RemoveAt(Activities.Count - 1);
        }

        var activeCount = Activities.Count(static row => row.IsActive);
        var failureCount = Activities.Count(static row => row.State == AgentActivityState.Failed);
        var completedCount = Activities.Count(static row => row.State == AgentActivityState.Completed);
        var visibleUsageRows = Activities.Where(row =>
            row.Kind == AgentActivityKind.Agent ||
            !Activities.Any(child => child.ParentActivityId == row.ActivityId));
        var totalTokens = visibleUsageRows.Sum(static row => row.TotalTokens);
        Summary = Activities.Count == 0
            ? "No agent or workflow activity for this thread."
            : string.Join(" • ", new[]
            {
                $"{Activities.Count} activit{(Activities.Count == 1 ? "y" : "ies")}",
                activeCount == 0 ? $"{completedCount} completed" : $"{activeCount} live",
                failureCount == 0 ? null : $"{failureCount} failed",
                totalTokens == 0 ? null : $"{totalTokens:N0} tokens",
            }.Where(static value => value is not null));

        OnPropertyChanged(nameof(EmptyVisibility));
        OnPropertyChanged(nameof(ActivityVisibility));
        if (activeCount > 0)
        {
            if (!_elapsedTimer.IsRunning)
            {
                _elapsedTimer.Start();
            }
        }
        else
        {
            _elapsedTimer.Stop();
        }
    }

    public void Dispose()
    {
        _elapsedTimer.Stop();
        _elapsedTimer.Tick -= OnElapsedTimerTick;
    }

    private void OnElapsedTimerTick(DispatcherQueueTimer sender, object args)
    {
        foreach (var activity in Activities.Where(static row => row.IsActive))
        {
            activity.RefreshElapsedTime();
        }
    }

    private static List<HierarchyItem> FlattenHierarchy(
        IReadOnlyList<AgentActivityProjection> activities)
    {
        var byParent = activities
            .Where(static activity => !string.IsNullOrWhiteSpace(activity.ParentActivityId))
            .GroupBy(static activity => activity.ParentActivityId!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(ActivityOrder).ToArray(),
                StringComparer.Ordinal);
        var ids = activities.Select(static activity => activity.ActivityId)
            .ToHashSet(StringComparer.Ordinal);
        var roots = activities
            .Where(activity => string.IsNullOrWhiteSpace(activity.ParentActivityId) ||
                !ids.Contains(activity.ParentActivityId))
            .OrderBy(ActivityOrder)
            .ToArray();
        var result = new List<HierarchyItem>(activities.Count);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            Add(root, 0);
        }

        foreach (var orphan in activities.OrderBy(ActivityOrder))
        {
            Add(orphan, 0);
        }

        return result;

        void Add(AgentActivityProjection activity, int depth)
        {
            if (!visited.Add(activity.ActivityId))
            {
                return;
            }

            result.Add(new HierarchyItem(activity, depth));
            if (!byParent.TryGetValue(activity.ActivityId, out var children))
            {
                return;
            }

            foreach (var child in children)
            {
                Add(child, depth + 1);
            }
        }
    }

    private static (DateTimeOffset StartedUtc, int Step, int AgentIndex, string Id) ActivityOrder(
        AgentActivityProjection activity) =>
        (activity.StartedUtc, activity.Step ?? int.MaxValue, activity.AgentIndex ?? int.MaxValue, activity.ActivityId);

    private sealed record HierarchyItem(AgentActivityProjection Activity, int Depth);
}

public sealed class AgentActivityRowViewModel : ObservableObject
{
    private AgentActivityProjection _activity;
    private int _depth;

    public AgentActivityRowViewModel(AgentActivityProjection activity, int depth)
    {
        _activity = activity;
        _depth = depth;
    }

    public string ActivityId => _activity.ActivityId;

    public string? ParentActivityId => _activity.ParentActivityId;

    public AgentActivityKind Kind => _activity.Kind;

    public AgentActivityState State => _activity.State;

    public string Title => _activity.Title;

    public string KindLabel => Kind == AgentActivityKind.Workflow ? "Workflow" : "Agent";

    public string StateLabel => State switch
    {
        AgentActivityState.Pending => "Pending",
        AgentActivityState.Running => "Running",
        AgentActivityState.Waiting => "Waiting",
        AgentActivityState.Completed => "Completed",
        AgentActivityState.Failed => "Failed",
        AgentActivityState.Interrupted => "Interrupted",
        _ => State.ToString(),
    };

    public string Task => _activity.Task;

    public string CurrentActivity => string.IsNullOrWhiteSpace(_activity.CurrentActivity)
        ? State switch
        {
            AgentActivityState.Pending => "Waiting to start",
            AgentActivityState.Running => "Working",
            AgentActivityState.Waiting => "Waiting for another step",
            _ => "No current activity",
        }
        : _activity.CurrentActivity;

    public string ModelSummary => string.Join(" • ", new[]
    {
        _activity.Model,
        _activity.ReasoningLevel,
        _activity.Step is null ? null : $"Step {_activity.Step}",
    }.Where(static value => !string.IsNullOrWhiteSpace(value)));

    public string ResultSummary => _activity.ResultSummary ?? string.Empty;

    public string FailureSummary => _activity.FailureSummary ?? string.Empty;

    public Visibility ResultVisibility => string.IsNullOrWhiteSpace(ResultSummary)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility FailureVisibility => string.IsNullOrWhiteSpace(FailureSummary)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility ModelVisibility => string.IsNullOrWhiteSpace(ModelSummary)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public bool IsActive => State is AgentActivityState.Pending or
        AgentActivityState.Running or AgentActivityState.Waiting;

    public bool CanInterrupt => _activity.CanInterrupt && IsActive;

    public Visibility InterruptVisibility => CanInterrupt
        ? Visibility.Visible
        : Visibility.Collapsed;

    public long TotalTokens => _activity.Usage?.TotalTokens ?? 0;

    public string Metrics => string.Join(" • ", new[]
    {
        $"{_activity.ToolCount} tool{(_activity.ToolCount == 1 ? string.Empty : "s")}",
        TotalTokens == 0 ? null : $"{TotalTokens:N0} tokens",
        Elapsed,
    }.Where(static value => value is not null));

    public string Elapsed
    {
        get
        {
            var end = _activity.CompletedUtc ?? DateTimeOffset.UtcNow;
            var elapsed = end < _activity.StartedUtc
                ? TimeSpan.Zero
                : end - _activity.StartedUtc;
            return elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m"
                : elapsed.TotalMinutes >= 1
                    ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
                    : $"{Math.Max(0, (int)elapsed.TotalSeconds)}s";
        }
    }

    public Thickness IndentMargin => new(Math.Min(_depth, 3) * 18, 0, 0, 0);

    public string AccessibleName => $"{KindLabel} {Title}, {StateLabel}, {CurrentActivity}, {Metrics}";

    public void Apply(AgentActivityProjection activity, int depth)
    {
        _activity = activity;
        _depth = depth;
        OnPropertyChanged(string.Empty);
    }

    public void RefreshElapsedTime()
    {
        OnPropertyChanged(nameof(Elapsed));
        OnPropertyChanged(nameof(Metrics));
        OnPropertyChanged(nameof(AccessibleName));
    }
}
