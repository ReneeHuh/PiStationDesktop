using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using PiStation.Protocol.Models;

namespace PiStation.App.Views;

public sealed partial class ShellPage
{
    private async void OnHostingReviewRequested(object? sender, EventArgs e)
    {
        await ViewModel.RefreshSettingsAsync();
        var title = new TextBox { Header = "Title", PlaceholderText = "Describe the change" };
        var body = new TextBox { Header = "Description", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100, MaxHeight = 240 };
        var draft = new CheckBox { Content = "Create as draft", IsChecked = true };
        var pullRequests = new ListView { ItemsSource = ViewModel.Settings.PullRequests, DisplayMemberPath = "Title", MaxHeight = 160, SelectionMode = ListViewSelectionMode.Single };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        status.SetBinding(TextBlock.TextProperty, new Binding { Source = ViewModel.Settings, Path = new PropertyPath("Status"), Mode = BindingMode.OneWay });
        var refresh = new Button { Content = "Refresh" };
        refresh.Click += async (_, _) => await ViewModel.RefreshSettingsAsync();
        var open = new Button { Content = "Open selected PR" };
        open.Click += async (_, _) =>
        {
            if (pullRequests.SelectedItem is PullRequestDescriptor selected && Uri.TryCreate(selected.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https")
                await Windows.System.Launcher.LaunchUriAsync(uri);
        };
        var link = new Button { Content = "Link to task" };
        link.Click += async (_, _) => { if (pullRequests.SelectedItem is PullRequestDescriptor selected) await ViewModel.LinkPullRequestAsync(selected); };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(refresh); actions.Children.Add(open); actions.Children.Add(link);
        var content = new StackPanel { Spacing = 10, MinWidth = 420 };
        content.Children.Add(new TextBlock { Text = ViewModel.Settings.SourceControlSummary, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(pullRequests); content.Children.Add(actions);
        content.Children.Add(title); content.Children.Add(body); content.Children.Add(draft); content.Children.Add(status);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Pull requests", Content = new ScrollViewer { Content = content },
            PrimaryButtonText = "Create pull request", CloseButtonText = "Close", DefaultButton = ContentDialogButton.Close,
        };
        dialog.SetBinding(ContentDialog.IsPrimaryButtonEnabledProperty, new Binding { Source = ViewModel.Settings, Path = new PropertyPath("CanCreatePullRequest"), Mode = BindingMode.OneWay });
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (string.IsNullOrWhiteSpace(title.Text)) { ViewModel.Settings.Status = "Enter a pull-request title."; return; }
            var deferral = args.GetDeferral();
            try { await ViewModel.CreatePullRequestAsync(title.Text, body.Text, draft.IsChecked == true); }
            finally { deferral.Complete(); }
        };
        await dialog.ShowAsync();
    }
}
