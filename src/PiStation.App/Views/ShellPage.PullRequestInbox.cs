using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using PiStation.App.ViewModels;
using PiStation.ClientRuntime;
using PiStation.Protocol.Models;

namespace PiStation.App.Views;

public sealed partial class ShellPage
{
    private async Task ShowPullRequestInboxAsync()
    {
        PullRequestInboxPage? page = null;
        IReadOnlyList<PullRequestInboxConnection> connections = [];
        CancellationTokenSource? pending = null;
        PullRequestInboxRow? selected = null;
        var closed = false;
        var content = new StackPanel { Spacing = 8, MinWidth = 480 };
        var description = new TextBlock { Text = "Browse project repositories in connected PiStation windows. Every review uses the connection and project shown on its row.", TextWrapping = TextWrapping.Wrap };
        content.Children.Add(description);
        var filters = new StackPanel { Spacing = 8 };
        var environment = new ComboBox { Header = "Environment", DisplayMemberPath = "Value", SelectedValuePath = "Key" };
        environment.ItemsSource = new[] { new KeyValuePair<string, string>("", "All connected environments") }
            .Concat(ShellViewModel.ConnectedPullRequestSources().Select(connection => new KeyValuePair<string, string>(connection.Source.Id, connection.Source.Name))).ToArray();
        environment.SelectedIndex = 0;
        filters.Children.Add(environment);
        var provider = new ComboBox { Header = "Provider", ItemsSource = new[] { "All", "GitHub", "GitLab", "AzureDevOps", "Bitbucket" }, SelectedIndex = 0 };
        var state = new ComboBox { Header = "State", ItemsSource = new[] { "All", "Open", "Closed", "Merged", "Draft" }, SelectedIndex = 1 };
        filters.Children.Add(provider); filters.Children.Add(state);
        var repository = Text("Repository URL contains", "PullRequestInboxRepository");
        var query = Text("Search title, number or branch", "PullRequestInboxQuery");
        var author = Text("Author login (Azure: unique name)", "PullRequestInboxAuthor");
        var labels = Text("Required labels, separated by | (all required)", "PullRequestInboxLabels");
        var excluded = Text("Excluded labels, separated by |", "PullRequestInboxExcludedLabels");
        var draft = Choice<PullRequestDraftFilter>("Drafts");
        var involvement = Choice<PullRequestInvolvement>("Involvement (GitHub)");
        var reviewFilter = Choice<PullRequestReviewFilter>("Review decision (GitHub)");
        var checks = Choice<PullRequestChecksFilter>("Checks (GitHub)");
        filters.Children.Add(new TextBlock { Text = "GitHub filters run on the provider. Other providers' text and author filters inspect loaded pages; load more to search further. Labels are supported for GitHub/GitLab; Bitbucket draft transitions are unavailable. Account involvement, @me, review and check filters require GitHub. Unsupported filters show a notice for each affected repository.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new Expander { Header = "Inbox filters", Content = filters, HorizontalAlignment = HorizontalAlignment.Stretch });
        var list = new ListView { MaxHeight = 280, SelectionMode = ListViewSelectionMode.Single, DisplayMemberPath = nameof(PullRequestInboxRow.DisplayText) };
        list.DisplayMemberPath = "";
        list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <TextBlock Text="{Binding DisplayText}" TextWrapping="Wrap" Margin="4" />
            </DataTemplate>
            """);
        AutomationProperties.SetAutomationId(list, "PullRequestInboxList");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        AutomationProperties.SetAutomationId(status, "PullRequestInboxStatus");
        var refresh = new Button { Content = "Apply / refresh" };
        var more = new Button { Content = "Load more", IsEnabled = false };
        var retry = new Button { Content = "Retry failed repositories", IsEnabled = false };
        AutomationProperties.SetAutomationId(refresh, "PullRequestInboxRefresh");
        AutomationProperties.SetAutomationId(more, "PullRequestInboxMore");
        AutomationProperties.SetAutomationId(retry, "PullRequestInboxRetry");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(refresh); buttons.Children.Add(more); buttons.Children.Add(retry);
        content.Children.Add(buttons); content.Children.Add(list);
        content.Children.Add(new ScrollViewer { Content = status, MaxHeight = 170 });
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Pull request inbox", Content = new ScrollViewer { Content = content, MaxHeight = 700 },
            PrimaryButtonText = "Review selected", SecondaryButtonText = "Provider website", CloseButtonText = "Close", DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false, IsSecondaryButtonEnabled = false };
        list.SelectionChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = list.SelectedItem is PullRequestInboxRow;
            dialog.IsSecondaryButtonEnabled = list.SelectedItem is PullRequestInboxRow;
        };
        dialog.PrimaryButtonClick += (_, args) => { selected = list.SelectedItem as PullRequestInboxRow; args.Cancel = selected is null; };
        dialog.SecondaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (list.SelectedItem is PullRequestInboxRow row && Uri.TryCreate(row.PullRequest.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                await SafeReviewActionAsync(() => OpenHostingLinkAsync(uri));
        };
        refresh.Click += async (_, _) => await ReadAsync(false, false);
        more.Click += async (_, _) => await ReadAsync(true, false);
        retry.Click += async (_, _) => await ReadAsync(true, true);
        var initial = ReadAsync(false, false);
        try
        {
            while (true)
            {
                selected = null;
                _hostingReviewDialog = dialog;
                await dialog.ShowAsync();
                _hostingReviewDialog = null;
                pending?.Cancel();
                if (selected is null) break;
                var connection = connections.FirstOrDefault(candidate => candidate.Source.Id == selected.SourceId);
                if (connection is null) { status.Text = "The originating connection is unavailable. Refresh the inbox."; continue; }
                try { await ShowInboxReviewAsync(connection, selected); }
                catch (Exception exception) { status.Text = exception.Message; }
            }
        }
        finally
        {
            closed = true;
            _hostingReviewDialog = null;
            pending?.Cancel();
            await initial;
            pending?.Dispose();
        }

        async Task ReadAsync(bool continuation, bool failures)
        {
            pending?.Cancel();
            var current = new CancellationTokenSource();
            var previous = pending;
            pending = current;
            // The preceding read owns its token until it finishes.
            previous?.Dispose();
            more.IsEnabled = false; retry.IsEnabled = false;
            list.IsEnabled = false;
            dialog.IsPrimaryButtonEnabled = false; dialog.IsSecondaryButtonEnabled = false;
            if (!continuation) { page = null; list.ItemsSource = null; }
            status.Text = "Loading repositories…";
            try
            {
                var held = page;
                PullRequestInboxPage next;
                if (continuation && held is not null) next = await PullRequestInbox.ContinueAsync(held, failures, current.Token);
                else
                {
                    connections = ShellViewModel.ConnectedPullRequestSources();
                    var parsed = new PullRequestListFilters(query.Text.Trim(), (PullRequestInvolvement)involvement.SelectedItem,
                        (PullRequestDraftFilter)draft.SelectedItem, (PullRequestReviewFilter)reviewFilter.SelectedItem, (PullRequestChecksFilter)checks.SelectedItem,
                        author.Text.Trim(), Split(labels.Text).Select(label => (IReadOnlyList<string>)new[] { label }).ToArray(), Split(excluded.Text));
                    var scope = new PullRequestInboxQuery(Enum.TryParse<PullRequestState>(state.SelectedItem as string, out var stateValue) ? stateValue : null,
                        parsed, Enum.TryParse<SourceControlProvider>(provider.SelectedItem as string, out var providerValue) ? providerValue : null,
                        string.IsNullOrEmpty(environment.SelectedValue as string) ? null : (string)environment.SelectedValue, repository.Text.Trim());
                    next = await PullRequestInbox.StartAsync(connections.Select(connection => connection.Source).ToArray(), scope, current.Token);
                }
                if (closed || current.IsCancellationRequested || !ReferenceEquals(pending, current)) return;
                page = next;
                list.ItemsSource = page.Rows;
                status.Text = page.Summary;
                more.IsEnabled = page.HasMore; retry.IsEnabled = page.Repositories.Any(item => item.Error is not null && item.NextOffset is not null);
            }
            catch (OperationCanceledException) when (current.IsCancellationRequested) { }
            catch (Exception exception) { if (!closed && ReferenceEquals(pending, current)) status.Text = exception.Message; }
            finally
            {
                if (!closed && ReferenceEquals(pending, current))
                {
                    list.IsEnabled = true;
                    dialog.IsPrimaryButtonEnabled = list.SelectedItem is PullRequestInboxRow;
                    dialog.IsSecondaryButtonEnabled = list.SelectedItem is PullRequestInboxRow;
                    more.IsEnabled = page?.HasMore == true;
                    retry.IsEnabled = page?.Repositories.Any(item => item.Error is not null && item.NextOffset is not null) == true;
                }
            }
        }
        TextBox Text(string header, string id)
        {
            var control = new TextBox { Header = header, MaxLength = 512 };
            AutomationProperties.SetAutomationId(control, id); filters.Children.Add(control); return control;
        }
        ComboBox Choice<T>(string header) where T : struct, Enum
        {
            var control = new ComboBox { Header = header, ItemsSource = Enum.GetValues<T>(), SelectedIndex = 0 };
            filters.Children.Add(control); return control;
        }
        static string[] Split(string value) => value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private async Task ShowInboxReviewAsync(PullRequestInboxConnection connection, PullRequestInboxRow row)
    {
        using var review = connection.Owner.CreateInboxReview(connection);
        using var cancellation = new CancellationTokenSource();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(30);
        async void Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) =>
            await SafeReviewActionAsync(() => review.RefreshLiveAsync(cancellation.Token));
        timer.Tick += Tick;
        try
        {
            await review.LoadAsync(row.Project.ProjectId, row.Target, row.PullRequest);
            var content = new StackPanel { Spacing = 8, MinWidth = 480 };
            content.Children.Add(new TextBlock { Text = row.DisplayText, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(BuildReviewWorkspace(review));
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
            content.Children.Add(status);
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Pull request review", CloseButtonText = "Back to inbox",
                Content = new ScrollViewer { Content = content, MaxHeight = 760 }, DefaultButton = ContentDialogButton.Close };
            var checkout = new Button { Content = "Review in Pi (originating environment)" };
            AutomationProperties.SetAutomationId(checkout, "PullRequestInboxReviewInPi");
            checkout.SetBinding(IsEnabledProperty, new Binding { Source = review, Path = new PropertyPath(nameof(review.CanCreateReviewThread)), Mode = BindingMode.OneWay });
            checkout.Click += async (_, _) => await SafeReviewActionAsync(async () =>
            {
                var thread = await review.CreateReviewThreadAsync(row.Project.DefaultModel ?? connection.Owner.Layout.LastModel,
                    row.Project.DefaultThinkingLevel ?? connection.Owner.Layout.LastThinkingLevel, cancellation.Token);
                if (thread is null) return;
                await connection.Owner.SelectProjectAsync(row.Project, cancellation.Token);
                await connection.Owner.SelectThreadAsync(thread, cancellation.Token);
                status.Text = $"Opened {thread.Title} in the {row.Environment} window.";
            });
            content.Children.Add(checkout);
            dialog.Closing += async (_, args) =>
            {
                if (review.IsBusy) { args.Cancel = true; status.Text = "Wait for the current review operation to finish."; return; }
                var deferral = args.GetDeferral();
                try { await review.SaveNowAsync(); }
                catch (Exception exception) { args.Cancel = true; status.Text = $"Could not save the draft: {exception.Message}"; }
                finally { deferral.Complete(); }
            };
            _hostingReviewDialog = dialog;
            timer.Start();
            await dialog.ShowAsync();
            await review.SaveNowAsync();
        }
        finally
        {
            timer.Stop(); timer.Tick -= Tick; cancellation.Cancel();
            _hostingReviewDialog = null; connection.Owner.IsHostingReviewOpen = false;
        }
    }
}
