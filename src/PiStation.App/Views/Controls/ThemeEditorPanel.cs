using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using PiStation.App.ViewModels;
using PiStation.ClientRuntime.Themes;
using Windows.ApplicationModel.DataTransfer;

namespace PiStation.App.Views.Controls;

public sealed class ThemeEditorPanel : UserControl
{
    public event EventHandler? InspectRequested;
    public event EventHandler? ImportFileRequested;
    public event EventHandler? ExportFileRequested;
    private readonly ThemeEditorViewModel _model;
    public ThemeEditorPanel(ThemeEditorViewModel model)
    {
        _model = model;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "Custom palettes", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Create a palette, preview it throughout the app, then save or cancel. Light and dark colors can be edited separately.", TextWrapping = TextWrapping.Wrap });
        var saved = new TextBlock { TextWrapping = TextWrapping.Wrap }; Bind(saved, TextBlock.TextProperty, nameof(model.ActiveSummary)); panel.Children.Add(saved);
        var themes = new ComboBox { Header = "Saved themes", ItemsSource = model.Themes, HorizontalAlignment = HorizontalAlignment.Stretch };
        Id(themes, "PaletteLibrary"); Bind(themes, ComboBox.SelectedItemProperty, nameof(model.SelectedTheme), true); panel.Children.Add(themes);
        panel.Children.Add(Buttons(("New", "PaletteNew", model.New), ("Edit", "PaletteEdit", () => model.Edit()), ("Duplicate", "PaletteDuplicate", () => model.Edit(true))));
        panel.Children.Add(Buttons(("Apply theme", "PaletteApply", model.ApplySelected), ("Native defaults", "PaletteDefaults", model.UseDefaults), ("Delete", "PaletteDelete", model.DeleteSelected)));
        var name = new TextBox { Header = "Theme name", MaxLength = 48 }; Id(name, "PaletteName"); Bind(name, TextBox.TextProperty, nameof(model.Name), true); panel.Children.Add(name);
        var appearance = new ComboBox { Header = "Edit appearance", ItemsSource = new[] { "Dark", "Light" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        Id(appearance, "PaletteAppearance"); Bind(appearance, ComboBox.SelectedIndexProperty, nameof(model.AppearanceIndex), true); panel.Children.Add(appearance);
        var roles = new ComboBox { Header = "Color role", ItemsSource = model.Roles, HorizontalAlignment = HorizontalAlignment.Stretch };
        Id(roles, "PaletteRole"); Bind(roles, ComboBox.SelectedItemProperty, nameof(model.SelectedRole), true); panel.Children.Add(roles);
        var input = new TextBox { Header = "Color", PlaceholderText = "#RRGGBB, rgb(), oklch() or CSS name", MaxLength = 200 }; Id(input, "PaletteColor"); Bind(input, TextBox.TextProperty, nameof(model.ColorInput), true); panel.Children.Add(input);
        panel.Children.Add(Buttons(("Apply color", "PaletteApplyColor", model.ApplyColor), ("Reset color", "PaletteResetColor", model.ResetColor), ("Inspect app", "PaletteInspect", () => InspectRequested?.Invoke(this, EventArgs.Empty))));
        var picker = new ColorPicker { IsAlphaEnabled = true, IsMoreButtonVisible = false, MaxWidth = 320 };
        var choose = new Button { Content = "Choose color…" }; Id(choose, "PaletteChooseColor");
        var pickerPanel = new StackPanel { Spacing = 8 }; pickerPanel.Children.Add(picker);
        var accept = new Button { Content = "Use this color" }; pickerPanel.Children.Add(accept);
        var flyout = new Flyout { Content = pickerPanel }; choose.Flyout = flyout;
        flyout.Opening += (_, _) => { if (ThemeColor.TryParse(model.ColorInput, out var color)) picker.Color = Windows.UI.Color.FromArgb((byte)Math.Round(color.A * 255), (byte)Math.Round(color.R * 255), (byte)Math.Round(color.G * 255), (byte)Math.Round(color.B * 255)); };
        accept.Click += (_, _) => { var color = picker.Color; model.ColorInput = new ThemeColor(color.R / 255d, color.G / 255d, color.B / 255d, color.A / 255d).Hex; model.Run(model.ApplyColor); flyout.Hide(); };
        panel.Children.Add(choose);
        var preview = new ToggleSwitch { Header = "Live preview" }; Id(preview, "PalettePreview"); Bind(preview, ToggleSwitch.IsOnProperty, nameof(model.PreviewEnabled), true); panel.Children.Add(preview);
        panel.Children.Add(Buttons(("Save palette", "PaletteSave", model.Save), ("Cancel preview", "PaletteCancel", model.Cancel), ("Restore library backup", "PaletteRecover", model.Recover)));
        panel.Children.Add(Buttons(("Reload library", "PaletteReload", model.ReloadLibrary), ("Reset damaged library", "PaletteResetDamaged", model.ResetDamagedLibrary)));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap }; Id(status, "PaletteStatus"); AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite); Bind(status, TextBlock.TextProperty, nameof(model.Status)); panel.Children.Add(status);
        var import = new ComboBox { Header = "Import format", ItemsSource = new[] { "Detect T3 / VS Code", "T3 theme", "VS Code theme", "Pi terminal theme" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        Id(import, "PaletteImportFormat"); Bind(import, ComboBox.SelectedIndexProperty, nameof(model.ImportFormat), true); panel.Children.Add(import);
        panel.Children.Add(new TextBlock { Text = "Import a standalone JSON theme up to 256 KB. Exports use the T3 format. VS Code syntax rules and Pi terminal layout are not converted.", TextWrapping = TextWrapping.Wrap });
        var json = new TextBox { Header = "Theme JSON", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 160, MaxLength = ThemeFiles.MaximumBytes };
        ScrollViewer.SetVerticalScrollBarVisibility(json, ScrollBarVisibility.Auto); Id(json, "PaletteJson"); Bind(json, TextBox.TextProperty, nameof(model.Json), true); panel.Children.Add(json);
        panel.Children.Add(Buttons(("Import preview", "PaletteImport", model.Import), ("Open file…", "PaletteOpenFile", () => ImportFileRequested?.Invoke(this, EventArgs.Empty))));
        panel.Children.Add(Buttons(("Export JSON", "PaletteExport", () => model.Export()), ("Save file…", "PaletteSaveFile", () => ExportFileRequested?.Invoke(this, EventArgs.Empty)), ("Copy JSON", "PaletteCopy", () => { var data = new DataPackage(); data.SetText(model.Export()); Clipboard.SetContent(data); })));
        Content = panel;
    }
    private Grid Buttons(params (string label, string id, Action action)[] actions)
    {
        var row = new Grid { ColumnSpacing = 6 };
        foreach (var (label, id, action) in actions)
        {
            var button = new Button { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetName(button, label); Id(button, id); button.Click += (_, _) => _model.Run(action);
            Grid.SetColumn(button, row.ColumnDefinitions.Count); row.ColumnDefinitions.Add(new ColumnDefinition()); row.Children.Add(button);
        }
        return row;
    }
    private void Bind(FrameworkElement target, DependencyProperty property, string path, bool twoWay = false) => target.SetBinding(property,
        new Binding { Source = _model, Path = new PropertyPath(path), Mode = twoWay ? BindingMode.TwoWay : BindingMode.OneWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
    private static void Id(DependencyObject target, string id) => AutomationProperties.SetAutomationId(target, id);
}
