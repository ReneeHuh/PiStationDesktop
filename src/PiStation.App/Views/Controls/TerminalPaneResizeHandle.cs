using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using PiStation.App.ViewModels;

namespace PiStation.App.Views.Controls;

public sealed class TerminalPaneResizeHandle : Grid
{
    private double _ratio = WorkbenchTerminalViewModel.DefaultSplitRatio;

    public event EventHandler<TerminalPaneRatioChangedEventArgs>? RatioChanged;

    public double Ratio => _ratio;

    public void SynchronizeRatio(double ratio)
    {
        var previous = _ratio;
        _ratio = WorkbenchTerminalViewModel.NormalizeSplitRatio(ratio);
        if (Math.Abs(previous - _ratio) >= 0.0001 &&
            FrameworkElementAutomationPeer.FromElement(this) is TerminalPaneResizeHandleAutomationPeer peer)
        {
            peer.RaiseValueChanged(previous * 100, _ratio * 100);
        }
    }

    public void SetRatio(double ratio, bool commit)
    {
        SynchronizeRatio(ratio);
        RatioChanged?.Invoke(this, new TerminalPaneRatioChangedEventArgs(_ratio, commit));
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new TerminalPaneResizeHandleAutomationPeer(this);

    private sealed class TerminalPaneResizeHandleAutomationPeer(TerminalPaneResizeHandle owner)
        : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        private TerminalPaneResizeHandle ResizeHandle => (TerminalPaneResizeHandle)Owner;

        public bool IsReadOnly => false;

        public double LargeChange => 10;

        public double Maximum => WorkbenchTerminalViewModel.MaximumSplitRatio * 100;

        public double Minimum => WorkbenchTerminalViewModel.MinimumSplitRatio * 100;

        public double SmallChange => 5;

        public double Value => ResizeHandle.Ratio * 100;

        protected override string GetClassNameCore() => nameof(TerminalPaneResizeHandle);

        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.Thumb;

        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.RangeValue
                ? this
                : base.GetPatternCore(patternInterface);

        public void SetValue(double value) => ResizeHandle.SetRatio(value / 100, commit: true);

        internal void RaiseValueChanged(double previous, double current) => RaisePropertyChangedEvent(
            RangeValuePatternIdentifiers.ValueProperty,
            previous,
            current);
    }
}

public sealed class TerminalPaneRatioChangedEventArgs(double ratio, bool commit) : EventArgs
{
    public double Ratio { get; } = ratio;

    public bool Commit { get; } = commit;
}
