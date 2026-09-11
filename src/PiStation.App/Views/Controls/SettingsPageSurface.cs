using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Windows.System;

namespace PiStation.App.Views.Controls;

/// <summary>A persistent settings page with an awaitable navigation lifetime.</summary>
[ContentProperty(Name = nameof(PageContent))]
public sealed class SettingsPageSurface : UserControl
{
    private readonly ContentPresenter _body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly TextBlock _heading = new() { Text = "Settings", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _back = new() { Content = "←", MinWidth = 32 };
    private TaskCompletionSource<ContentDialogResult>? _navigation;

    public SettingsPageSurface()
    {
        Visibility = Visibility.Collapsed;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        var root = (Grid)XamlReader.Load("""<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Background="{ThemeResource PiCanvasBrush}" />""");
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Padding = new Thickness(12, 6, 12, 6) };
        _back.Style = (Style)Application.Current.Resources["PiToolbarIconButtonStyle"];
        AutomationProperties.SetName(_back, "Back to conversation");
        AutomationProperties.SetAutomationId(_back, "SettingsBackButton");
        ToolTipService.SetToolTip(_back, "Back to conversation (Escape)");
        _back.Click += (_, _) => Hide();
        header.Children.Add(_back);
        header.Children.Add(_heading);
        root.Children.Add(header);
        Grid.SetRow(_body, 1);
        root.Children.Add(_body);
        Content = root;
        KeyDown += OnPageKeyDown;
        Unloaded += (_, _) => Hide();
    }

    public UIElement? PageContent { get => _body.Content as UIElement; set => _body.Content = value; }
    public string Title { get => _heading.Text; set => _heading.Text = value; }
    public event EventHandler? Closed;

    public Task<ContentDialogResult> ShowAsync()
    {
        if (_navigation is not null) return _navigation.Task;
        _navigation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(() => { if (_navigation is not null) _back.Focus(FocusState.Programmatic); });
        return _navigation.Task;
    }

    public void Hide()
    {
        var navigation = _navigation;
        if (navigation is null) return;
        _navigation = null;
        Visibility = Visibility.Collapsed;
        Closed?.Invoke(this, EventArgs.Empty);
        navigation.TrySetResult(ContentDialogResult.None);
    }

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape || args.Handled) return;
        // ComboBox/flyout dismissal owns the first Escape.
        if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Any(popup => popup.IsOpen)) return;
        args.Handled = true;
        Hide();
    }
}
