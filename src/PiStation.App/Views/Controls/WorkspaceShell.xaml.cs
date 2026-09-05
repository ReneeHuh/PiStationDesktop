using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace PiStation.App.Views.Controls;

public sealed partial class WorkspaceShell : UserControl
{
    public static readonly DependencyProperty SidebarProperty = DependencyProperty.Register(
        nameof(Sidebar),
        typeof(UIElement),
        typeof(WorkspaceShell),
        new PropertyMetadata(null, OnSidebarChanged));

    public static readonly DependencyProperty MainContentProperty = DependencyProperty.Register(
        nameof(MainContent),
        typeof(UIElement),
        typeof(WorkspaceShell),
        new PropertyMetadata(null, OnMainContentChanged));

    public static readonly DependencyProperty RightPanelProperty = DependencyProperty.Register(
        nameof(RightPanel),
        typeof(UIElement),
        typeof(WorkspaceShell),
        new PropertyMetadata(null, OnRightPanelChanged));

    public WorkspaceShell()
    {
        InitializeComponent();
    }

    public UIElement? Sidebar
    {
        get => (UIElement?)GetValue(SidebarProperty);
        set => SetValue(SidebarProperty, value);
    }

    public UIElement? MainContent
    {
        get => (UIElement?)GetValue(MainContentProperty);
        set => SetValue(MainContentProperty, value);
    }

    public UIElement? RightPanel
    {
        get => (UIElement?)GetValue(RightPanelProperty);
        set => SetValue(RightPanelProperty, value);
    }

    private static void OnSidebarChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is WorkspaceShell shell)
        {
            shell.SidebarPresenter.Content = args.NewValue;
        }
    }

    private static void OnMainContentChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is WorkspaceShell shell)
        {
            shell.MainContentPresenter.Content = args.NewValue;
        }
    }

    private static void OnRightPanelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is WorkspaceShell shell)
        {
            shell.RightPanelPresenter.Content = args.NewValue;
            shell.UpdateRightPanelConstraint();
        }
    }

    private void OnShellSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateRightPanelConstraint();

    private void OnSidebarSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateRightPanelConstraint();

    private void UpdateRightPanelConstraint()
    {
        if (ShellGrid.ActualWidth <= 0)
        {
            return;
        }

        var narrowBreakpoint = (double)Application.Current.Resources["PiBreakpointNarrow"];
        var compactBreakpoint = (double)Application.Current.Resources["PiBreakpointCompact"];
        var workbenchBreakpoint = (double)Application.Current.Resources["PiBreakpointWorkbench"];
        var layoutName = ShellGrid.ActualWidth >= workbenchBreakpoint
            ? "Wide"
            : ShellGrid.ActualWidth >= compactBreakpoint
                ? "Standard"
                : ShellGrid.ActualWidth >= narrowBreakpoint
                    ? "Compact"
                    : "Narrow";
        AutomationProperties.SetHelpText(ShellGrid, $"Responsive layout: {layoutName}");

        if (RightPanel is not FrameworkElement panel)
        {
            return;
        }

        var availableWidth = ShellGrid.ActualWidth < workbenchBreakpoint
            ? Math.Max(0, ShellGrid.ActualWidth - SidebarPresenter.ActualWidth)
            : (double?)null;
        if (panel is RightPanelHost rightPanel)
        {
            rightPanel.SetAvailableWidth(availableWidth);
        }
        else
        {
            panel.MaxWidth = availableWidth ?? double.PositiveInfinity;
        }
    }
}
