using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using PiStation.App.ViewModels;

namespace PiStation.App.Views.Controls;

public sealed partial class ComposerSurface
{
    private async void OnOpenModelPicker(object sender, RoutedEventArgs e)
    {
        var search = new TextBox { PlaceholderText = "Search model or provider" };
        var showHidden = new CheckBox { Content = "Show hidden models" };
        var favoritesOnly = new CheckBox { Content = "Favorites only" };
        var list = new StackPanel { Spacing = 8 };
        var panel = new StackPanel { Spacing = 8, MinWidth = 460 };
        panel.Children.Add(search);
        panel.Children.Add(showHidden);
        panel.Children.Add(favoritesOnly);
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 430 });
        var selected = ViewModel.PiConfiguration.SelectedModel;
        void Render()
        {
            list.Children.Clear();
            var models = ViewModel.PiConfiguration.Models.Where(model =>
                (model.DisplayName + " " + model.ModelId + " " + model.ProviderId).Contains(search.Text, StringComparison.OrdinalIgnoreCase));
            foreach (var group in models.GroupBy(model => model.ProviderId).OrderBy(group => group.Key))
            {
                var visible = group.Where(model => showHidden.IsChecked == true || !ViewModel.Layout.GetModelPreference(model.Selection).Hidden)
                    .Where(model => favoritesOnly.IsChecked != true || ViewModel.Layout.GetModelPreference(model.Selection).Favorite)
                    .OrderByDescending(model => ViewModel.Layout.GetModelPreference(model.Selection).Favorite)
                    .ThenBy(model => ViewModel.Layout.GetModelPreference(model.Selection).Order).ThenBy(model => model.DisplayName).ToArray();
                if (visible.Length == 0) continue;
                list.Children.Add(new TextBlock { Text = group.Key, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                foreach (var model in visible)
                {
                    var preference = ViewModel.Layout.GetModelPreference(model.Selection);
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    var choose = new RadioButton { Content = model.DisplayName, GroupName = "PiModelPicker", MinWidth = 210,
                        IsChecked = model.Selection == selected?.Selection };
                    ToolTipService.SetToolTip(choose, model.ModelId);
                    choose.Checked += (_, _) => selected = model;
                    row.Children.Add(choose);
                    void AddAction(string label, Action action)
                    {
                        var button = new Button { Content = label };
                        AutomationProperties.SetName(button, label + " " + model.DisplayName);
                        button.Click += (_, _) => { action(); Render(); };
                        row.Children.Add(button);
                    }
                    AddAction(preference.Favorite ? "★" : "☆", () => ViewModel.Layout.SetModelPreference(preference with { Favorite = !preference.Favorite }));
                    AddAction(preference.Hidden ? "Show" : "Hide", () => ViewModel.Layout.SetModelPreference(preference with { Hidden = !preference.Hidden }));
                    void Move(int delta)
                    {
                        var index = Array.IndexOf(visible, model);
                        var target = index + delta;
                        if (target < 0 || target >= visible.Length) return;
                        (visible[index], visible[target]) = (visible[target], visible[index]);
                        for (var rank = 0; rank < visible.Length; rank++)
                            ViewModel.Layout.SetModelPreference(ViewModel.Layout.GetModelPreference(visible[rank].Selection) with { Order = rank });
                    }
                    AddAction("↑", () => Move(-1));
                    AddAction("↓", () => Move(1));
                    list.Children.Add(row);
                }
            }
            if (list.Children.Count == 0) list.Children.Add(new TextBlock { Text = "No matching models" });
        }
        search.TextChanged += (_, _) => Render();
        showHidden.Checked += (_, _) => Render(); showHidden.Unchecked += (_, _) => Render();
        favoritesOnly.Checked += (_, _) => Render(); favoritesOnly.Unchecked += (_, _) => Render();
        Render();
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Pi models", Content = panel,
            PrimaryButtonText = "Use model", CloseButtonText = "Close" };
        var result = await dialog.ShowAsync();
        ViewModel.PiConfiguration.ApplyModelPreferences(ViewModel.Layout);
        if (result == ContentDialogResult.Primary) await ViewModel.SelectPiModelAsync(selected);
    }
}
