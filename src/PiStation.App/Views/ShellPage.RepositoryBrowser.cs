using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PiStation.Protocol.Models;

namespace PiStation.App.Views;

public sealed partial class ShellPage
{
    private async void OnBrowseHostedRepositoriesClicked(object sender, RoutedEventArgs e)
    {
        try { await ShowRepositoryBrowserAsync(); }
        catch (Exception error) { ViewModel.Settings.Status = error.Message; }
    }

    private async Task ShowRepositoryBrowserAsync()
    {
        var connection = ViewModel.CaptureRepositoryBrowserConnection();
        using var lifetime = new CancellationTokenSource();
        var closed = false;
        var busy = false;
        HostingBrowseLocation? loadedLocation = null;
        string? loadedScope = null;
        int? accountPage = null, repositoryPage = null;
        var accounts = new List<HostingAccountScope>();
        var repositories = new List<HostedRepositoryChoice>();
        var content = new StackPanel { Spacing = 8, MinWidth = 420 };
        content.Children.Add(new TextBlock { Text = "Browse with this environment's active hosting credentials. Clone destinations are paths on this host. Azure requires an organization name. GitLab groups and Bitbucket workspaces appear as account scopes.", TextWrapping = TextWrapping.Wrap });
        var provider = new ComboBox { Header = "Provider", ItemsSource = new[] { "GitHub", "GitLab", "Bitbucket", "AzureDevOps" }, SelectedIndex = 0 };
        var host = Input("Server (blank uses provider default)", "RepositoryBrowserHost");
        var organization = Input("Azure organization name", "RepositoryBrowserOrganization");
        var account = new ComboBox { Header = "Account / organization / workspace / project", DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Stretch };
        var refresh = new Button { Content = "Load accounts" };
        var moreAccounts = new Button { Content = "More accounts", IsEnabled = false };
        var load = new Button { Content = "Load repositories", IsEnabled = false };
        var more = new Button { Content = "More repositories", IsEnabled = false };
        var query = Input("Filter loaded repository names", "RepositoryBrowserFilter");
        var list = new ListView { DisplayMemberPath = "Name", MaxHeight = 200, SelectionMode = ListViewSelectionMode.Single };
        var remote = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var destination = Input("Clone destination on this host", "RepositoryBrowserDestination");
        destination.Text = CloneDestinationInput.Text;
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        AutomationProperties.SetAutomationId(list, "RepositoryBrowserList");
        AutomationProperties.SetAutomationId(account, "RepositoryBrowserAccount");
        AutomationProperties.SetAutomationId(status, "RepositoryBrowserStatus");
        AutomationProperties.SetAutomationId(refresh, "RepositoryBrowserRefresh");
        foreach (var element in new UIElement[] { provider, host, organization, refresh, account, moreAccounts, load, query, list, more, remote, destination, status }) content.Children.Add(element);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Browse and clone repository", Content = new ScrollViewer { Content = content, MaxHeight = 650 },
            PrimaryButtonText = "Clone and add project", CloseButtonText = "Close", IsPrimaryButtonEnabled = false };
        void Render()
        {
            provider.IsEnabled = host.IsEnabled = organization.IsEnabled = account.IsEnabled = refresh.IsEnabled = !busy;
            moreAccounts.IsEnabled = !busy && accountPage is not null;
            more.IsEnabled = !busy && repositoryPage is not null;
            load.IsEnabled = !busy && loadedLocation is not null && account.SelectedItem is HostingAccountScope;
            dialog.IsPrimaryButtonEnabled = !busy && ViewModel.CanOperate && list.SelectedItem is HostedRepositoryChoice && !string.IsNullOrWhiteSpace(destination.Text);
        }
        void Filter()
        {
            list.ItemsSource = repositories.Where(repo => repo.Name.Contains(query.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            remote.Text = ""; Render();
        }
        void ClearRepositories()
        {
            repositories.Clear(); repositoryPage = null; loadedScope = null; Filter();
        }
        void Reset()
        {
            loadedLocation = null; accounts.Clear(); account.ItemsSource = null; accountPage = null; ClearRepositories(); status.Text = "Load accounts for the selected provider and server.";
        }
        async Task Run(Func<Task> action)
        {
            if (busy || closed) return;
            busy = true; status.Text = "Loading…"; Render();
            try { _ = connection(); await action(); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) { if (!closed) status.Text = error.Message; }
            finally { busy = false; if (!closed) Render(); }
        }
        async Task Accounts(bool append)
        {
            var location = append ? loadedLocation! : new HostingBrowseLocation(Enum.Parse<SourceControlProvider>((string)provider.SelectedItem), host.Text.Trim(), organization.Text.Trim());
            var page = append ? accountPage!.Value : 1;
            if (!append) Reset();
            var result = await connection().ListHostingAccountsAsync(new(location, page), lifetime.Token);
            if (closed) return;
            _ = connection(); loadedLocation = location; accounts.AddRange(result.Accounts);
            var previous = account.SelectedItem as HostingAccountScope;
            account.ItemsSource = accounts.DistinctBy(item => item.Id).ToArray();
            account.SelectedItem = previous; accountPage = result.NextPage;
            status.Text = result.Notice + $" {accounts.Count} account scopes loaded.";
        }
        async Task Repositories(bool append)
        {
            var scope = (HostingAccountScope)account.SelectedItem;
            var page = append ? repositoryPage!.Value : 1;
            if (!append) ClearRepositories();
            if (append && loadedScope != scope.Id) throw new InvalidOperationException("Reload repositories after changing account scope.");
            var result = await connection().BrowseHostedRepositoriesAsync(new(loadedLocation!, scope.Id, page), lifetime.Token);
            if (closed) return;
            _ = connection(); loadedScope = scope.Id;
            repositories.AddRange(result.Repositories.Where(repo => repositories.All(existing => existing.Id != repo.Id)));
            repositoryPage = result.NextPage; Filter(); status.Text = result.Notice + $" {repositories.Count} repositories loaded.";
        }
        provider.SelectionChanged += (_, _) => { host.Text = ""; Reset(); };
        host.TextChanged += (_, _) => Reset(); organization.TextChanged += (_, _) => Reset();
        account.SelectionChanged += (_, _) => ClearRepositories();
        query.TextChanged += (_, _) => Filter(); destination.TextChanged += (_, _) => Render();
        list.SelectionChanged += (_, _) => { remote.Text = list.SelectedItem is HostedRepositoryChoice repo ? repo.CloneUrl + (repo.IsPrivate ? " (private)" : " (public)") : ""; Render(); };
        refresh.Click += async (_, _) => await Run(() => Accounts(false));
        moreAccounts.Click += async (_, _) => await Run(() => Accounts(true));
        load.Click += async (_, _) => await Run(() => Repositories(false));
        more.Click += async (_, _) => await Run(() => Repositories(true));
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (busy || list.SelectedItem is not HostedRepositoryChoice repository) return;
            var deferral = args.GetDeferral();
            try
            {
                await Run(async () =>
                {
                    var result = await ViewModel.CloneBrowsedRepositoryAsync(connection, repository, destination.Text, lifetime.Token);
                    if (!closed) { status.Text = result.Message; if (result.Succeeded) dialog.Hide(); }
                });
            }
            finally { deferral.Complete(); }
        };
        dialog.Closed += (_, _) => { closed = true; lifetime.Cancel(); };
        await dialog.ShowAsync();

        static TextBox Input(string header, string id)
        {
            var box = new TextBox { Header = header }; AutomationProperties.SetAutomationId(box, id); return box;
        }
    }
}
