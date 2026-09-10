using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PiStation.ClientRuntime.Themes;
using Windows.Foundation;

namespace PiStation.App.Views;

internal static class ThemeInspector
{
    private static Brush? Paint(FrameworkElement node, int paint) => paint switch
    {
        1 => node switch { TextBlock text => text.Foreground, RichTextBlock rich => rich.Foreground, Control control => control.Foreground, _ => null },
        2 => node switch { Border border => border.BorderBrush, Control control => control.BorderBrush, _ => null },
        _ => node switch { Border border => border.Background, Panel panel => panel.Background, Control control => control.Background, _ => null },
    };
    public static string? InspectArea(FrameworkElement owner, int area)
    {
        var (target, paint) = area switch { 0 => ("AppMainWindow", 0), 1 => ("AppSidebar", 0), 2 => ("AppTitleBar", 0),
            3 => ("WorkbenchFileEditor", 0), 4 => ("", 0), 5 => ("TitleBarBrandText", 1), _ => ("AppSidebar", 2) };
        if (owner.XamlRoot?.Content is not FrameworkElement root) return null;
        var pending = new Stack<DependencyObject>(); pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node is FrameworkElement element && element.Visibility == Visibility.Visible && element.ActualWidth > 0)
            {
                if (target.Length > 0 && (element.Name == target || AutomationProperties.GetAutomationId(element) == target)) return MatchBrush(element, Paint(element, paint));
                if (area == 4 && element is Border border && MatchBrush(element, border.Background) == "messageSurface") return "messageSurface";
            }
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--) pending.Push(VisualTreeHelper.GetChild(node, i));
        }
        return null;
    }
    internal static string? MatchBrush(FrameworkElement owner, Brush? brush)
    {
        if (brush is null || brush is SolidColorBrush { Color.A: 0 }) return null;
        // Match resource identity, not RGB: several independent roles may currently share a color.
        foreach (var (token, role) in ThemeFiles.NativeRoles)
            if (ThemeResourceLookup.TryGet(owner, token + "Brush", out var resource) && ReferenceEquals(resource, brush)) return role;
        return null;
    }
    public static (string Role, string Area)? Inspect(FrameworkElement root, Point point, int paint)
    {
        foreach (var hit in VisualTreeHelper.FindElementsInHostCoordinates(point, root))
        {
            for (DependencyObject? element = hit; element is FrameworkElement node; element = VisualTreeHelper.GetParent(element))
            {
                if (MatchBrush(node, Paint(node, paint)) is { } role)
                {
                    var label = AutomationProperties.GetName(node);
                    return (role, string.IsNullOrWhiteSpace(label) ? node.Name.Length > 0 ? node.Name : node.GetType().Name : label);
                }
                if (ReferenceEquals(node, root)) break;
            }
        }
        return null;
    }
}
