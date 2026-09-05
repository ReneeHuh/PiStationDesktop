using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using PiStation.App.ViewModels;

namespace PiStation.App.Views.Controls;

public sealed class WorkbenchResizeHandle : Grid
{
    public ShellLayoutViewModel? Layout { get; set; }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new WorkbenchResizeHandleAutomationPeer(this);

    private sealed class WorkbenchResizeHandleAutomationPeer(WorkbenchResizeHandle owner)
        : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        private WorkbenchResizeHandle ResizeHandle => (WorkbenchResizeHandle)Owner;

        public bool IsReadOnly => ResizeHandle.Layout is null;

        public double LargeChange => 40;

        public double Maximum => ShellLayoutViewModel.MaximumRightPanelWidth;

        public double Minimum => ShellLayoutViewModel.MinimumRightPanelWidth;

        public double SmallChange => 8;

        public double Value => ResizeHandle.Layout?.RightPanelWidth ??
            ShellLayoutViewModel.DefaultRightPanelWidth;

        protected override string GetClassNameCore() => nameof(WorkbenchResizeHandle);

        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.Thumb;

        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.RangeValue
                ? this
                : base.GetPatternCore(patternInterface);

        public void SetValue(double value)
        {
            if (ResizeHandle.Layout is not { } layout)
            {
                return;
            }

            layout.ResizeRightPanel(value);
            layout.CommitRightPanelWidth();
        }
    }
}
