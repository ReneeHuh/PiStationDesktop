using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed class WorkbenchTerminalViewModel : ObservableObject
{
    public const double DefaultSplitRatio = 0.5;
    public const double MinimumSplitRatio = 0.25;
    public const double MaximumSplitRatio = 0.75;
    public const int MaximumPaneCount = 4;

    private int _activePaneIndex;
    private string _inputText = string.Empty;
    private bool _isBusy;
    private bool _allowOperations = true;

    public bool AllowOperations
    {
        get => _allowOperations;
        internal set
        {
            if (!SetProperty(ref _allowOperations, value)) return;
            OnPropertyChanged(nameof(CanCreate));
            RaiseCommandStateChanged();
            RaisePaneStateChanged();
        }
    }
    private double _lastSplitRatio = DefaultSplitRatio;
    private TerminalPaneNode _root = TerminalPaneNode.CreateLeaf();
    private TerminalSessionItemViewModel? _selectedSession;
    private TerminalShellOption _selectedShell;
    private string _status = "Select a workspace to use terminals";

    public WorkbenchTerminalViewModel()
    {
        Shells =
        [
            new TerminalShellOption(TerminalShellKind.PowerShell, "PowerShell"),
            new TerminalShellOption(TerminalShellKind.CommandPrompt, "Command Prompt"),
        ];
        _selectedShell = Shells[0];
    }

    public ObservableCollection<TerminalSessionItemViewModel> Sessions { get; } = [];

    internal event EventHandler<TerminalSurfaceOutputEventArgs>? SurfaceOutputChanged;

    public IReadOnlyList<TerminalShellOption> Shells { get; }

    public TerminalShellOption SelectedShell
    {
        get => _selectedShell;
        set => SetProperty(ref _selectedShell, value ?? Shells[0]);
    }

    public TerminalSessionItemViewModel? SelectedSession
    {
        get => _selectedSession;
        private set
        {
            if (SetProperty(ref _selectedSession, value))
            {
                RaiseCommandStateChanged();
            }
        }
    }

    public string InputText
    {
        get => _inputText;
        set
        {
            if (SetProperty(ref _inputText, value))
            {
                OnPropertyChanged(nameof(CanSend));
            }
        }
    }

    public string RawOutput => GetPaneOutput(ActivePaneIndex);

    public string Status
    {
        get => _status;
        internal set => SetProperty(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        internal set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanCreate));
                RaiseCommandStateChanged();
                RaisePaneStateChanged();
            }
        }
    }

    public int PaneCount => GetLeaves().Count;

    public bool IsSplit => PaneCount > 1;

    public TerminalSplitOrientation SplitOrientation => _root.IsLeaf
        ? TerminalSplitOrientation.Right
        : _root.SplitOrientation;

    public int ActivePaneIndex => _activePaneIndex;

    public string ActivePaneId
    {
        get
        {
            var leaves = GetLeaves();
            return leaves.Count == 0 ? string.Empty : leaves[Math.Clamp(_activePaneIndex, 0, leaves.Count - 1)].PaneId;
        }
    }

    public double SplitRatio => _root.IsLeaf ? _lastSplitRatio : _root.SplitRatio;

    public TerminalPaneLayoutNodeSnapshot LayoutRoot => CreateSnapshot(_root, new PaneIndexCounter());

    public bool CanCreate => AllowOperations && !IsBusy;

    public bool CanSend => AllowOperations && !IsBusy &&
        SelectedSession?.Descriptor.State == TerminalSessionState.Running &&
        !string.IsNullOrWhiteSpace(InputText);

    public bool CanStop => AllowOperations && !IsBusy && SelectedSession?.Descriptor.State == TerminalSessionState.Running;

    public bool CanRestart => AllowOperations && !IsBusy && SelectedSession is not null;

    public bool CanClose => AllowOperations && !IsBusy && SelectedSession is not null;

    public bool CanSplit => AllowOperations && !IsBusy && PaneCount < MaximumPaneCount && SelectedSession is not null;

    public bool CanClosePane => AllowOperations && !IsBusy && PaneCount > 1;

    public string PaneSummary => PaneCount switch
    {
        <= 1 => "Single pane",
        2 => $"Pane {_activePaneIndex + 1} of 2 • split {(SplitOrientation == TerminalSplitOrientation.Right ? "right" : "down")}",
        _ => $"Pane {_activePaneIndex + 1} of {PaneCount} • nested splits",
    };

    public string SessionSummary => SelectedSession is null
        ? "No terminal selected"
        : SelectedSession.Descriptor.State switch
        {
            TerminalSessionState.Running => $"{SelectedSession.Descriptor.ShellDisplayName} • " +
                (SelectedSession.Descriptor.HasRunningSubprocess switch
                { true => SelectedSession.Descriptor.ForegroundCommand ?? "child process running", false => "no child process detected", _ => "checking activity" }),
            TerminalSessionState.Interrupted => "Saved history • restart to open a fresh shell",
            TerminalSessionState.Exited => $"{SelectedSession.Descriptor.ShellDisplayName} • exited " +
                $"({SelectedSession.Descriptor.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"})",
            TerminalSessionState.Failed => $"{SelectedSession.Descriptor.ShellDisplayName} • failed",
            _ => SelectedSession.Descriptor.ShellDisplayName,
        };

    internal IReadOnlyList<TerminalSessionId> VisibleSessionIds => GetLeaves()
        .Where(pane => pane.SessionId is not null)
        .Select(pane => pane.SessionId!.Value)
        .Distinct()
        .ToArray();

    internal void Reset(bool hasProject)
    {
        Sessions.Clear();
        _root = TerminalPaneNode.CreateLeaf();
        _lastSplitRatio = DefaultSplitRatio;
        _activePaneIndex = 0;
        SelectedSession = null;
        InputText = string.Empty;
        IsBusy = false;
        RaisePaneStateChanged(layoutChanged: true);
        PublishAllSurfaceResets();
        Status = hasProject ? "No terminal sessions yet" : "Select a workspace to use terminals";
    }

    internal void ApplySessions(
        IReadOnlyList<TerminalSessionDescriptor> descriptors,
        TerminalSessionId? selectedSessionId = null)
    {
        Sessions.Clear();
        foreach (var descriptor in descriptors)
        {
            Sessions.Add(new TerminalSessionItemViewModel(descriptor));
        }

        if (descriptors.Count == 0)
        {
            _root = TerminalPaneNode.CreateLeaf();
            _activePaneIndex = 0;
        }
        else
        {
            var availableIds = descriptors.Select(descriptor => descriptor.TerminalSessionId).ToHashSet();
            var usedIds = new HashSet<TerminalSessionId>();
            foreach (var pane in GetLeaves())
            {
                if (pane.SessionId is not { } sessionId ||
                    !availableIds.Contains(sessionId) ||
                    !usedIds.Add(sessionId))
                {
                    pane.SessionId = null;
                    pane.RawOutput = string.Empty;
                }
            }

            _root = PruneEmptyLeaves(_root) ?? TerminalPaneNode.CreateLeaf();
            var leaves = GetLeaves();
            _activePaneIndex = Math.Clamp(_activePaneIndex, 0, leaves.Count - 1);

            if (selectedSessionId is { } selected && availableIds.Contains(selected))
            {
                var existingIndex = leaves.FindIndex(pane => pane.SessionId == selected);
                if (existingIndex >= 0)
                {
                    _activePaneIndex = existingIndex;
                }
                else
                {
                    leaves[_activePaneIndex].SessionId = selected;
                    leaves[_activePaneIndex].RawOutput = string.Empty;
                }
            }

            leaves = GetLeaves();
            if (leaves[0].SessionId is null)
            {
                leaves[0].SessionId = descriptors[0].TerminalSessionId;
            }
        }

        UpdateSelectedSession();
        InputText = string.Empty;
        Status = Sessions.Count == 0
            ? "No terminal sessions yet"
            : $"{Sessions.Count} terminal session" + (Sessions.Count == 1 ? string.Empty : "s");
        RaisePaneStateChanged(layoutChanged: true);
    }

    internal void Select(TerminalSessionId terminalSessionId)
    {
        if (Sessions.All(item => item.Descriptor.TerminalSessionId != terminalSessionId))
        {
            return;
        }

        var leaves = GetLeaves();
        var existingIndex = leaves.FindIndex(pane => pane.SessionId == terminalSessionId);
        if (existingIndex >= 0)
        {
            ActivatePane(existingIndex);
            return;
        }

        SetPaneSession(_activePaneIndex, terminalSessionId);
        UpdateSelectedSession();
        InputText = string.Empty;
        RaisePaneStateChanged();
    }

    internal void Split(TerminalSplitOrientation orientation, TerminalSessionId terminalSessionId)
    {
        var leaves = GetLeaves();
        if (leaves.Count >= MaximumPaneCount ||
            leaves[_activePaneIndex].SessionId is null ||
            Sessions.All(item => item.Descriptor.TerminalSessionId != terminalSessionId) ||
            leaves.Any(pane => pane.SessionId == terminalSessionId))
        {
            return;
        }

        var activePaneId = leaves[_activePaneIndex].PaneId;
        var newLeaf = TerminalPaneNode.CreateLeaf(terminalSessionId);
        _root = ReplaceLeaf(
            _root,
            activePaneId,
            existing => TerminalPaneNode.CreateSplit(
                orientation,
                existing,
                newLeaf,
                _lastSplitRatio));
        _activePaneIndex = GetLeaves().FindIndex(pane => pane.PaneId == newLeaf.Pane!.PaneId);
        UpdateSelectedSession();
        InputText = string.Empty;
        RaisePaneStateChanged(layoutChanged: true);
    }

    internal void RestoreLayout(TerminalPaneLayoutPreference? preference)
    {
        if (preference is null || Sessions.Count == 0)
        {
            return;
        }

        var availableIds = Sessions.Select(item => item.Descriptor.TerminalSessionId).ToHashSet();
        var outputBySession = GetLeaves()
            .Where(pane => pane.SessionId is not null)
            .GroupBy(pane => pane.SessionId!.Value)
            .ToDictionary(group => group.Key, group => group.First().RawOutput);
        var usedSessionIds = new HashSet<TerminalSessionId>();
        var usedPaneIds = new HashSet<string>(StringComparer.Ordinal);
        var restored = preference.Root is null
            ? RestoreLegacyLayout(preference, availableIds, usedSessionIds)
            : RestoreNode(preference.Root, availableIds, usedSessionIds, usedPaneIds, depth: 0);

        _root = restored ?? TerminalPaneNode.CreateLeaf(Sessions[0].Descriptor.TerminalSessionId);
        foreach (var pane in GetLeaves())
        {
            if (pane.SessionId is { } sessionId && outputBySession.TryGetValue(sessionId, out var output))
            {
                pane.RawOutput = output;
            }
        }
        _lastSplitRatio = _root.IsLeaf
            ? NormalizeSplitRatio(preference.SplitRatio)
            : _root.SplitRatio;
        var leaves = GetLeaves();
        _activePaneIndex = Math.Clamp(preference.ActivePaneIndex, 0, leaves.Count - 1);
        UpdateSelectedSession();
        RaisePaneStateChanged(layoutChanged: true);
    }

    internal TerminalPaneLayoutPreference CaptureLayout()
    {
        var leaves = GetLeaves();
        return new TerminalPaneLayoutPreference(
            IsSplit,
            SplitOrientation,
            SplitRatio,
            _activePaneIndex,
            leaves.ElementAtOrDefault(0)?.SessionId?.Value,
            leaves.ElementAtOrDefault(1)?.SessionId?.Value,
            CreatePreference(_root));
    }

    internal void ResizeSplit(double ratio) => ResizeSplit(_root.NodeId, ratio);

    internal void ResizeSplit(string splitNodeId, double ratio)
    {
        var split = FindNode(_root, splitNodeId);
        if (split is null || split.IsLeaf)
        {
            return;
        }

        var normalized = NormalizeSplitRatio(ratio);
        if (Math.Abs(split.SplitRatio - normalized) < 0.0001)
        {
            return;
        }

        split.SplitRatio = normalized;
        _lastSplitRatio = normalized;
        if (ReferenceEquals(split, _root))
        {
            OnPropertyChanged(nameof(SplitRatio));
        }
    }

    internal double GetSplitRatio(string splitNodeId) =>
        FindNode(_root, splitNodeId) is { IsLeaf: false } split
            ? split.SplitRatio
            : DefaultSplitRatio;

    internal TerminalSessionId? CloseActivePane()
    {
        var leaves = GetLeaves();
        if (leaves.Count <= 1)
        {
            return null;
        }

        var removed = leaves[_activePaneIndex];
        _root = RemoveLeaf(_root, removed.PaneId) ?? TerminalPaneNode.CreateLeaf();
        leaves = GetLeaves();
        _activePaneIndex = Math.Min(_activePaneIndex, leaves.Count - 1);
        UpdateSelectedSession();
        InputText = string.Empty;
        RaisePaneStateChanged(layoutChanged: true);
        return removed.SessionId;
    }

    internal void ActivatePane(int paneIndex)
    {
        if (paneIndex < 0 || paneIndex >= PaneCount || _activePaneIndex == paneIndex)
        {
            return;
        }

        _activePaneIndex = paneIndex;
        UpdateSelectedSession();
        RaisePaneStateChanged();
    }

    internal int GetPaneIndex(string paneId) =>
        GetLeaves().FindIndex(pane => string.Equals(pane.PaneId, paneId, StringComparison.Ordinal));

    internal string? GetPaneId(int paneIndex)
    {
        var leaves = GetLeaves();
        return paneIndex >= 0 && paneIndex < leaves.Count ? leaves[paneIndex].PaneId : null;
    }

    internal TerminalSessionDescriptor? GetPaneSession(int paneIndex)
    {
        var leaves = GetLeaves();
        if (paneIndex < 0 || paneIndex >= leaves.Count)
        {
            return null;
        }

        var terminalSessionId = leaves[paneIndex].SessionId;
        return terminalSessionId is null
            ? null
            : Sessions.FirstOrDefault(item => item.Descriptor.TerminalSessionId == terminalSessionId.Value)?.Descriptor;
    }

    internal string GetPaneOutput(int paneIndex)
    {
        var leaves = GetLeaves();
        return paneIndex >= 0 && paneIndex < leaves.Count ? leaves[paneIndex].RawOutput : string.Empty;
    }

    internal void Apply(
        TerminalSessionId terminalSessionId,
        TerminalSessionDescriptor? descriptor,
        string output,
        string? appendedOutput = null,
        bool outputWasReset = false)
    {
        var session = Sessions.FirstOrDefault(item => item.Descriptor.TerminalSessionId == terminalSessionId);
        if (descriptor is not null && session is not null)
        {
            session.Descriptor = descriptor;
        }

        var leaves = GetLeaves();
        for (var paneIndex = 0; paneIndex < leaves.Count; paneIndex++)
        {
            var pane = leaves[paneIndex];
            if (pane.SessionId != terminalSessionId)
            {
                continue;
            }

            var canAppend = !outputWasReset && output.Length >= pane.RawOutput.Length &&
                output.AsSpan(0, pane.RawOutput.Length).SequenceEqual(pane.RawOutput.AsSpan());
            string surfaceText;
            bool surfaceReset;
            if (!outputWasReset && appendedOutput is not null)
            {
                surfaceText = appendedOutput;
                surfaceReset = false;
            }
            else if (canAppend)
            {
                surfaceText = output[pane.RawOutput.Length..];
                surfaceReset = false;
            }
            else
            {
                surfaceText = output;
                surfaceReset = true;
            }

            pane.RawOutput = output;
            SurfaceOutputChanged?.Invoke(this, new TerminalSurfaceOutputEventArgs(paneIndex, surfaceText, surfaceReset));
        }

        if (leaves[_activePaneIndex].SessionId == terminalSessionId)
        {
            Status = descriptor?.ErrorMessage ?? SessionSummary;
            RaiseCommandStateChanged();
        }
    }

    internal void ApplyDescriptor(TerminalSessionDescriptor descriptor)
    {
        var session = Sessions.FirstOrDefault(
            item => item.Descriptor.TerminalSessionId == descriptor.TerminalSessionId);
        if (session is null)
        {
            return;
        }

        session.Descriptor = descriptor;
        var leaves = GetLeaves();
        if (leaves[_activePaneIndex].SessionId == descriptor.TerminalSessionId)
        {
            Status = descriptor.ErrorMessage ?? SessionSummary;
            RaiseCommandStateChanged();
        }
    }

    internal void ClearOutput(int? paneIndex = null)
    {
        var targetPane = paneIndex ?? _activePaneIndex;
        var leaves = GetLeaves();
        if (targetPane < 0 || targetPane >= leaves.Count)
        {
            return;
        }

        leaves[targetPane].RawOutput = string.Empty;
        PublishSurfaceReset(targetPane, string.Empty);
        if (targetPane == _activePaneIndex)
        {
            Status = "Terminal output cleared locally";
        }
    }

    private void SetPaneSession(int paneIndex, TerminalSessionId? terminalSessionId)
    {
        var leaves = GetLeaves();
        if (paneIndex < 0 || paneIndex >= leaves.Count)
        {
            return;
        }

        var pane = leaves[paneIndex];
        if (pane.SessionId == terminalSessionId)
        {
            return;
        }

        pane.SessionId = terminalSessionId;
        pane.RawOutput = string.Empty;
        PublishSurfaceReset(paneIndex, string.Empty);
    }

    private TerminalPaneNode RestoreLegacyLayout(
        TerminalPaneLayoutPreference preference,
        HashSet<TerminalSessionId> availableIds,
        HashSet<TerminalSessionId> usedSessionIds)
    {
        var primary = FindSessionId(preference.PrimarySessionId);
        if (primary is null || !availableIds.Contains(primary.Value))
        {
            primary = Sessions[0].Descriptor.TerminalSessionId;
        }

        usedSessionIds.Add(primary.Value);
        var primaryNode = TerminalPaneNode.CreateLeaf(primary);
        if (!preference.IsSplit)
        {
            return primaryNode;
        }

        var secondary = FindSessionId(preference.SecondarySessionId);
        if (secondary is null || !availableIds.Contains(secondary.Value) || !usedSessionIds.Add(secondary.Value))
        {
            secondary = Sessions
                .Select(item => (TerminalSessionId?)item.Descriptor.TerminalSessionId)
                .FirstOrDefault(id => id is not null && usedSessionIds.Add(id.Value));
        }

        return secondary is null
            ? primaryNode
            : TerminalPaneNode.CreateSplit(
                preference.SplitOrientation,
                primaryNode,
                TerminalPaneNode.CreateLeaf(secondary),
                preference.SplitRatio);
    }

    private TerminalPaneNode? RestoreNode(
        TerminalPaneLayoutNodePreference preference,
        HashSet<TerminalSessionId> availableIds,
        HashSet<TerminalSessionId> usedSessionIds,
        HashSet<string> usedPaneIds,
        int depth)
    {
        if (depth >= MaximumPaneCount || usedSessionIds.Count >= MaximumPaneCount)
        {
            return null;
        }

        if (preference.First is null || preference.Second is null || preference.SplitOrientation is null)
        {
            var sessionId = FindSessionId(preference.SessionId);
            if (sessionId is null || !availableIds.Contains(sessionId.Value) || !usedSessionIds.Add(sessionId.Value))
            {
                return null;
            }

            var paneId = string.IsNullOrWhiteSpace(preference.PaneId) || usedPaneIds.Contains(preference.PaneId)
                ? CreateNodeId("pane")
                : preference.PaneId;
            usedPaneIds.Add(paneId);
            return TerminalPaneNode.CreateLeaf(sessionId, paneId);
        }

        var first = RestoreNode(preference.First, availableIds, usedSessionIds, usedPaneIds, depth + 1);
        var second = RestoreNode(preference.Second, availableIds, usedSessionIds, usedPaneIds, depth + 1);
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return TerminalPaneNode.CreateSplit(
            preference.SplitOrientation.Value,
            first,
            second,
            preference.SplitRatio,
            preference.NodeId);
    }

    private TerminalSessionId? FindSessionId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        foreach (var session in Sessions)
        {
            if (string.Equals(session.Descriptor.TerminalSessionId.Value, value, StringComparison.Ordinal))
            {
                return session.Descriptor.TerminalSessionId;
            }
        }

        return null;
    }

    private void UpdateSelectedSession()
    {
        var leaves = GetLeaves();
        _activePaneIndex = Math.Clamp(_activePaneIndex, 0, Math.Max(0, leaves.Count - 1));
        var selectedId = leaves.Count == 0 ? null : leaves[_activePaneIndex].SessionId;
        SelectedSession = selectedId is null
            ? null
            : Sessions.FirstOrDefault(item => item.Descriptor.TerminalSessionId == selectedId.Value);
        OnPropertyChanged(nameof(RawOutput));
    }

    private void RaiseCommandStateChanged()
    {
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanRestart));
        OnPropertyChanged(nameof(CanClose));
        OnPropertyChanged(nameof(SessionSummary));
    }

    private void RaisePaneStateChanged(bool layoutChanged = false)
    {
        OnPropertyChanged(nameof(PaneCount));
        OnPropertyChanged(nameof(IsSplit));
        OnPropertyChanged(nameof(SplitOrientation));
        OnPropertyChanged(nameof(ActivePaneIndex));
        OnPropertyChanged(nameof(ActivePaneId));
        OnPropertyChanged(nameof(SplitRatio));
        OnPropertyChanged(nameof(CanSplit));
        OnPropertyChanged(nameof(CanClosePane));
        OnPropertyChanged(nameof(PaneSummary));
        if (layoutChanged)
        {
            OnPropertyChanged(nameof(LayoutRoot));
        }
    }

    private void PublishAllSurfaceResets()
    {
        var leaves = GetLeaves();
        for (var paneIndex = 0; paneIndex < leaves.Count; paneIndex++)
        {
            PublishSurfaceReset(paneIndex, leaves[paneIndex].RawOutput);
        }
    }

    private void PublishSurfaceReset(int paneIndex, string text) =>
        SurfaceOutputChanged?.Invoke(this, new TerminalSurfaceOutputEventArgs(paneIndex, text, isReset: true));

    private List<TerminalPaneState> GetLeaves()
    {
        var leaves = new List<TerminalPaneState>(MaximumPaneCount);
        CollectLeaves(_root, leaves);
        return leaves;
    }

    private static void CollectLeaves(TerminalPaneNode node, List<TerminalPaneState> leaves)
    {
        if (node.Pane is not null)
        {
            leaves.Add(node.Pane);
            return;
        }

        CollectLeaves(node.First!, leaves);
        CollectLeaves(node.Second!, leaves);
    }

    private static TerminalPaneNode ReplaceLeaf(
        TerminalPaneNode node,
        string paneId,
        Func<TerminalPaneNode, TerminalPaneNode> replacement)
    {
        if (node.Pane is not null)
        {
            return string.Equals(node.Pane.PaneId, paneId, StringComparison.Ordinal)
                ? replacement(node)
                : node;
        }

        node.First = ReplaceLeaf(node.First!, paneId, replacement);
        node.Second = ReplaceLeaf(node.Second!, paneId, replacement);
        return node;
    }

    private static TerminalPaneNode? RemoveLeaf(TerminalPaneNode node, string paneId)
    {
        if (node.Pane is not null)
        {
            return string.Equals(node.Pane.PaneId, paneId, StringComparison.Ordinal) ? null : node;
        }

        node.First = RemoveLeaf(node.First!, paneId);
        node.Second = RemoveLeaf(node.Second!, paneId);
        if (node.First is null)
        {
            return node.Second;
        }

        if (node.Second is null)
        {
            return node.First;
        }

        return node;
    }

    private static TerminalPaneNode? PruneEmptyLeaves(TerminalPaneNode node)
    {
        if (node.Pane is not null)
        {
            return node.Pane.SessionId is null ? null : node;
        }

        node.First = PruneEmptyLeaves(node.First!);
        node.Second = PruneEmptyLeaves(node.Second!);
        if (node.First is null)
        {
            return node.Second;
        }

        if (node.Second is null)
        {
            return node.First;
        }

        return node;
    }

    private static TerminalPaneNode? FindNode(TerminalPaneNode node, string nodeId)
    {
        if (string.Equals(node.NodeId, nodeId, StringComparison.Ordinal))
        {
            return node;
        }

        return node.IsLeaf
            ? null
            : FindNode(node.First!, nodeId) ?? FindNode(node.Second!, nodeId);
    }

    private static TerminalPaneLayoutNodeSnapshot CreateSnapshot(TerminalPaneNode node, PaneIndexCounter counter)
    {
        if (node.Pane is not null)
        {
            return new TerminalPaneLayoutNodeSnapshot(
                node.NodeId,
                counter.Value++,
                null,
                DefaultSplitRatio,
                null,
                null);
        }

        return new TerminalPaneLayoutNodeSnapshot(
            node.NodeId,
            null,
            node.SplitOrientation,
            node.SplitRatio,
            CreateSnapshot(node.First!, counter),
            CreateSnapshot(node.Second!, counter));
    }

    private static TerminalPaneLayoutNodePreference CreatePreference(TerminalPaneNode node) =>
        node.Pane is not null
            ? new TerminalPaneLayoutNodePreference(
                node.NodeId,
                node.Pane.PaneId,
                node.Pane.SessionId?.Value,
                null,
                DefaultSplitRatio,
                null,
                null)
            : new TerminalPaneLayoutNodePreference(
                node.NodeId,
                null,
                null,
                node.SplitOrientation,
                node.SplitRatio,
                CreatePreference(node.First!),
                CreatePreference(node.Second!));

    private static string CreateNodeId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    internal static double NormalizeSplitRatio(double ratio) => double.IsFinite(ratio)
        ? Math.Clamp(ratio, MinimumSplitRatio, MaximumSplitRatio)
        : DefaultSplitRatio;

    private sealed class TerminalPaneNode
    {
        private TerminalPaneNode(string nodeId)
        {
            NodeId = nodeId;
        }

        public string NodeId { get; }

        public TerminalPaneState? Pane { get; private init; }

        public TerminalSplitOrientation SplitOrientation { get; private init; }

        public double SplitRatio { get; set; } = DefaultSplitRatio;

        public TerminalPaneNode? First { get; set; }

        public TerminalPaneNode? Second { get; set; }

        public bool IsLeaf => Pane is not null;

        public static TerminalPaneNode CreateLeaf(TerminalSessionId? sessionId = null, string? paneId = null)
        {
            paneId = string.IsNullOrWhiteSpace(paneId) ? CreateNodeId("pane") : paneId;
            return new TerminalPaneNode(paneId)
            {
                Pane = new TerminalPaneState(paneId, sessionId),
            };
        }

        public static TerminalPaneNode CreateSplit(
            TerminalSplitOrientation orientation,
            TerminalPaneNode first,
            TerminalPaneNode second,
            double ratio = DefaultSplitRatio,
            string? nodeId = null) => new(
                string.IsNullOrWhiteSpace(nodeId) ? CreateNodeId("split") : nodeId)
            {
                SplitOrientation = orientation,
                SplitRatio = NormalizeSplitRatio(ratio),
                First = first,
                Second = second,
            };
    }

    private sealed class TerminalPaneState(string paneId, TerminalSessionId? sessionId = null)
    {
        public string PaneId { get; } = paneId;

        public TerminalSessionId? SessionId { get; set; } = sessionId;

        public string RawOutput { get; set; } = string.Empty;
    }

    private sealed class PaneIndexCounter
    {
        public int Value { get; set; }
    }
}

public sealed record TerminalPaneLayoutNodeSnapshot(
    string NodeId,
    int? PaneIndex,
    TerminalSplitOrientation? SplitOrientation,
    double SplitRatio,
    TerminalPaneLayoutNodeSnapshot? First,
    TerminalPaneLayoutNodeSnapshot? Second)
{
    public bool IsLeaf => PaneIndex is not null;
}

public sealed class TerminalSessionItemViewModel : ObservableObject
{
    private TerminalSessionDescriptor _descriptor;

    public TerminalSessionItemViewModel(TerminalSessionDescriptor descriptor)
    {
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    }

    public TerminalSessionDescriptor Descriptor
    {
        get => _descriptor;
        internal set
        {
            if (SetProperty(ref _descriptor, value))
            {
                OnPropertyChanged(nameof(Name));
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    public string Name => Descriptor.State == TerminalSessionState.Running && Descriptor.HasRunningSubprocess == true
        ? Descriptor.ForegroundCommand ?? Descriptor.Name : Descriptor.Name;

    public string AccessibleName => $"{Name}, {Descriptor.State}" + (Descriptor.State == TerminalSessionState.Running
        ? Descriptor.HasRunningSubprocess switch { true => ", child process running", false => ", no child process detected", _ => ", activity unknown" } : "");
}

public sealed record TerminalShellOption(TerminalShellKind Kind, string DisplayName);

public enum TerminalSplitOrientation
{
    Right,
    Down,
}

internal sealed class TerminalSurfaceOutputEventArgs(int paneIndex, string text, bool isReset) : EventArgs
{
    public int PaneIndex { get; } = paneIndex;

    public string Text { get; } = text;

    public bool IsReset { get; } = isReset;
}
