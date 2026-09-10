using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PiStation.App.Views.Controls;
using PiStation.ClientRuntime.Themes;
using Windows.Storage.Pickers;

namespace PiStation.App.Views;

public sealed partial class ShellPage
{
    private Grid? _themeInspector;
    private void InitializeThemeEditor()
    {
        var editor = new ThemeEditorPanel(ViewModel.Layout.Themes);
        editor.InspectRequested += async (_, _) => await InspectThemeAsync();
        editor.ImportFileRequested += OnImportThemeFile;
        editor.ExportFileRequested += OnExportThemeFile;
        ThemeEditorHost.Content = editor;
    }
    private async void OnImportThemeFile(object? sender, EventArgs args)
    {
        try
        {
            if ((Application.Current as App)?.FindWindow(XamlRoot) is not { } window) return;
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json"); picker.FileTypeFilter.Add(".jsonc");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            if (await picker.PickSingleFileAsync() is not { } file) return;
            using var stream = File.OpenRead(file.Path);
            if (stream.Length > ThemeFiles.MaximumBytes) throw new FormatException("Theme files must be 256 KB or smaller.");
            using var reader = new StreamReader(stream);
            var buffer = new char[ThemeFiles.MaximumBytes + 1]; var length = await reader.ReadBlockAsync(buffer.AsMemory());
            if (length > ThemeFiles.MaximumBytes) throw new FormatException("Theme file is too large.");
            ViewModel.Layout.Themes.Json = new string(buffer, 0, length);
            ViewModel.Layout.Themes.Run(ViewModel.Layout.Themes.Import);
        }
        catch (Exception error) { ViewModel.Layout.Themes.Status = error.Message; }
    }
    private async void OnExportThemeFile(object? sender, EventArgs args)
    {
        try
        {
            var json = ViewModel.Layout.Themes.Export();
            if ((Application.Current as App)?.FindWindow(XamlRoot) is not { } window) return;
            var picker = new FileSavePicker { SuggestedFileName = "pistation-theme" }; picker.FileTypeChoices.Add("T3 theme", [".json"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            if (await picker.PickSaveFileAsync() is not { } file) return;
            await File.WriteAllTextAsync(file.Path, json); ViewModel.Layout.Themes.Status = "Exported theme to " + file.Name + ".";
        }
        catch (Exception error) { ViewModel.Layout.Themes.Status = error.Message; }
    }
    private async Task InspectThemeAsync()
    {
        if (_themeInspector is not null || _disposed) return;
        _settingsSuspended = true; SettingsDialog.Hide();
        if (_settingsShowTask is { } settings) await settings;
        if (_disposed) { _settingsSuspended = false; return; }
        var overlay = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        _themeInspector = overlay; Canvas.SetZIndex(overlay, 1000);
        AutomationProperties.SetAutomationId(overlay, "ThemeInspector");
        var bar = new StackPanel { Spacing = 8 };
        var instructions = new TextBlock { Text = "Choose a paint type, then click an app area. Escape cancels.", TextWrapping = TextWrapping.Wrap };
        bar.Children.Add(instructions);
        var paint = new ComboBox { Header = "Paint type", ItemsSource = new[] { "Background", "Text", "Border" }, SelectedIndex = 0 }; bar.Children.Add(paint);
        AutomationProperties.SetAutomationId(paint, "ThemeInspectorPaint");
        // Keyboard users can inspect the same named resource areas without pointing.
        var areas = new ComboBox { Header = "Or select an area", ItemsSource = new[] { "Canvas", "Sidebar", "Window chrome", "File editor", "Message background", "Text", "Border" }, SelectedIndex = 0 };
        AutomationProperties.SetAutomationId(areas, "ThemeInspectorArea"); bar.Children.Add(areas);
        var inspectArea = new Button { Content = "Inspect selected area" }; AutomationProperties.SetAutomationId(inspectArea, "ThemeInspectSelectedArea"); bar.Children.Add(inspectArea);
        inspectArea.Click += async (_, _) =>
        {
            if (ThemeInspector.InspectArea(PageLayoutGrid, areas.SelectedIndex) is not { } role) { instructions.Text = "This area has no visible editable color. Open the area first, or choose another."; return; }
            ViewModel.Layout.Themes.InspectRole(role); await FinishThemeInspectionAsync();
        };
        var cancel = new Button { Content = "Cancel inspection" }; AutomationProperties.SetAutomationId(cancel, "ThemeInspectorCancel"); bar.Children.Add(cancel);
        cancel.Click += async (_, _) => await FinishThemeInspectionAsync();
        var controls = new Border { Child = bar, Padding = new Thickness(12), Margin = new Thickness(16), MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            RequestedTheme = ElementTheme.Light, Background = ThemeResourceLookup.Get<Brush>(ElementTheme.Light, "PiOverlayBrush"),
            BorderBrush = ThemeResourceLookup.Get<Brush>(ElementTheme.Light, "PiTextPrimaryBrush"), BorderThickness = new Thickness(2) };
        instructions.Foreground = ThemeResourceLookup.Get<Brush>(ElementTheme.Light, "PiTextPrimaryBrush");
        overlay.Children.Add(controls); PageLayoutGrid.Children.Add(overlay);
        overlay.PointerPressed += async (_, e) =>
        {
            for (DependencyObject? node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node)) if (ReferenceEquals(node, controls)) return;
            e.Handled = true; var point = e.GetCurrentPoint(PageLayoutGrid).Position;
            overlay.IsHitTestVisible = false;
            (string Role, string Area)? result;
            try { result = ThemeInspector.Inspect(PageLayoutGrid, point, paint.SelectedIndex); }
            finally { overlay.IsHitTestVisible = true; }
            if (result is not { } found) { instructions.Text = "No editable native color found here. Try another area or paint type."; return; }
            ViewModel.Layout.Themes.InspectRole(found.Role); ViewModel.Layout.Themes.Status = $"{found.Area}: {found.Role}. Edit its color below.";
            await FinishThemeInspectionAsync();
        };
        overlay.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; await FinishThemeInspectionAsync(); } };
        paint.Focus(FocusState.Programmatic);
    }
    private void ReleaseThemeInspector()
    {
        if (_themeInspector is { } overlay) PageLayoutGrid.Children.Remove(overlay);
        _themeInspector = null; _settingsSuspended = false;
    }
    private async Task FinishThemeInspectionAsync()
    {
        ReleaseThemeInspector(); if (!_disposed) await RestoreSettingsAsync();
    }
}
