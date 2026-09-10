using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Text;
using PiStation.App.ViewModels;
using PiStation.Protocol.Models;

namespace PiStation.App.Views;

public sealed partial class ShellPage
{
    private ContentDialog? _hostingReviewDialog;

    private async Task OpenHostingLinkAsync(Uri uri)
    {
        if (Composition.BrowserLinkRouter.ShouldOpenInApp(uri, ViewModel.Layout.BrowserLinkTarget,
            ViewModel.Workspace.SelectedThread is not null, false)) _hostingReviewDialog?.Hide();
        await ViewModel.OpenBrowserLinkAsync(uri);
    }

    private async void OnHostingReviewRequested(object? sender, EventArgs e)
    {
        if (ViewModel.IsHostingReviewOpen) return;
        ViewModel.IsHostingReviewOpen = true;
        var showInbox = false;
        try
        {
            await ViewModel.RefreshSettingsAsync();
            var review = ViewModel.PullRequestReview;
            var pullRequests = new ListView { ItemsSource = ViewModel.Settings.PullRequests, MaxHeight = 150, SelectionMode = ListViewSelectionMode.Single, DisplayMemberPath = "Title" };
            AutomationProperties.SetAutomationId(pullRequests, "PullRequestReviewList");
            var title = new TextBox { Header = "Title", PlaceholderText = "Describe the change" };
            var body = new TextBox { Header = "Description", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 180 };
            var draft = new CheckBox { Content = "Create as draft", IsChecked = true };
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            status.SetBinding(TextBlock.TextProperty, new Binding { Source = ViewModel.Settings, Path = new PropertyPath("Status"), Mode = BindingMode.OneWay });
            var refresh = new Button { Content = "Refresh" };
            refresh.Click += async (_, _) => await SafeReviewActionAsync(() => ViewModel.RefreshSettingsAsync());
            var open = new Button { Content = "Open selected PR" };
            open.Click += async (_, _) =>
            {
                if (pullRequests.SelectedItem is PullRequestDescriptor selected && Uri.TryCreate(selected.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                    await OpenHostingLinkAsync(uri);
            };
            var link = new Button { Content = "Link to task" };
            link.Click += async (_, _) => { if (pullRequests.SelectedItem is PullRequestDescriptor selected) await SafeReviewActionAsync(() => ViewModel.LinkPullRequestAsync(selected)); };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(refresh); actions.Children.Add(open); actions.Children.Add(link);
            var workspace = BuildReviewWorkspace(review);
            pullRequests.SelectionChanged += async (_, _) =>
            {
                if (pullRequests.SelectedItem is not PullRequestDescriptor selected || ViewModel.Workspace.SelectedProject is not { } project) return;
                var target = new WorkspaceTarget(project.ProjectId, ViewModel.Workspace.SelectedThread?.ThreadId);
                await SafeReviewActionAsync(() => review.LoadAsync(project.ProjectId, target, selected));
            };

            var content = new StackPanel { Spacing = 10, MinWidth = 480 };
            content.Children.Add(BindText(ViewModel.Settings, nameof(ViewModel.Settings.SourceControlSummary)));
            content.Children.Add(BuildPullRequestFilters());
            content.Children.Add(pullRequests); content.Children.Add(actions);
            content.Children.Add(title); content.Children.Add(body); content.Children.Add(draft); content.Children.Add(workspace); content.Children.Add(status);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Pull requests and review",
                Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 760 },
                PrimaryButtonText = "Create pull request", CloseButtonText = "Close", DefaultButton = ContentDialogButton.Close,
                SecondaryButtonText = "All repositories",
            };
            dialog.SecondaryButtonClick += (_, _) => showInbox = true;
            dialog.SetBinding(ContentDialog.IsPrimaryButtonEnabledProperty, new Binding { Source = ViewModel.Settings, Path = new PropertyPath("CanCreatePullRequest"), Mode = BindingMode.OneWay });
            using var checkoutCancellation = new CancellationTokenSource();
            var refreshTimer = DispatcherQueue.CreateTimer();
            refreshTimer.Interval = TimeSpan.FromSeconds(30);
            async void OnLiveRefreshTick(Microsoft.UI.Dispatching.DispatcherQueueTimer timer, object args)
            {
                if (pullRequests.SelectedItem is PullRequestDescriptor selected && selected.Number == review.Snapshot?.PullRequest.Number &&
                    review.ProjectId == ViewModel.Workspace.SelectedProject?.ProjectId)
                    await SafeReviewActionAsync(() => review.RefreshLiveAsync(checkoutCancellation.Token));
            }
            refreshTimer.Tick += OnLiveRefreshTick;
            refreshTimer.Start();
            var checkout = new Button { Content = "Review in Pi", HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetAutomationId(checkout, "PullRequestReviewInPiButton");
            ToolTipService.SetToolTip(checkout, "Open an isolated worktree and a linked Pi thread with a prepared review prompt.");
            void UpdateCheckoutState()
            {
                checkout.IsEnabled = review.CanCreateReviewThread &&
                    review.ProjectId == ViewModel.Workspace.SelectedProject?.ProjectId &&
                    pullRequests.SelectedItem is PullRequestDescriptor selected &&
                    selected.Number == review.Snapshot?.PullRequest.Number &&
                    selected.Repository == review.Snapshot?.PullRequest.Repository;
            }
            System.ComponentModel.PropertyChangedEventHandler checkoutStateChanged = (_, _) => UpdateCheckoutState();
            SelectionChangedEventHandler checkoutSelectionChanged = (_, _) => UpdateCheckoutState();
            review.PropertyChanged += checkoutStateChanged;
            pullRequests.SelectionChanged += checkoutSelectionChanged;
            UpdateCheckoutState();
            checkout.Click += async (_, _) => await SafeReviewActionAsync(async () =>
            {
                if (checkout.IsEnabled && await ViewModel.CreatePullRequestReviewThreadAsync(checkoutCancellation.Token)) dialog.Hide();
            });
            content.Children.Insert(3, checkout);
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                args.Cancel = true;
                if (string.IsNullOrWhiteSpace(title.Text)) { ViewModel.Settings.Status = "Enter a pull-request title."; return; }
                var deferral = args.GetDeferral();
                try { await ViewModel.CreatePullRequestAsync(title.Text, body.Text, draft.IsChecked == true); }
                catch (Exception exception) { ViewModel.ReportRuntimeError(exception); }
                finally { deferral.Complete(); }
            };
            dialog.Closing += async (_, _) =>
            {
                checkoutCancellation.Cancel();
                try { await review.SaveNowAsync(); }
                catch (Exception exception) { ViewModel.Settings.Status = $"Review draft could not be saved: {exception.Message}"; }
            };
            _hostingReviewDialog = dialog;
            try { await dialog.ShowAsync(); }
            finally
            {
                _hostingReviewDialog = null;
                refreshTimer.Stop();
                refreshTimer.Tick -= OnLiveRefreshTick;
                checkoutCancellation.Cancel();
                review.PropertyChanged -= checkoutStateChanged;
                pullRequests.SelectionChanged -= checkoutSelectionChanged;
                try { await review.SaveNowAsync(); }
                finally { review.Suspend(); }
            }
        }
        catch (Exception exception) { ViewModel.ReportRuntimeError(exception); }
        finally { ViewModel.IsHostingReviewOpen = false; }
        if (showInbox) await SafeReviewActionAsync(ShowPullRequestInboxAsync);
    }

    private Border BuildReviewWorkspace(PullRequestReviewViewModel review)
    {
        var notice = BindText(review, nameof(review.Status), "PullRequestReviewStatus");
        var details = BindText(review, nameof(review.DetailsSummary));
        details.FontWeight = FontWeights.SemiBold;
        var head = BindText(review, nameof(review.HeadSummary));
        var description = BindText(review, nameof(review.Description));
        var commits = BindText(review, nameof(review.CommitsSummary));
        var commitsPanel = new ScrollViewer { Content = commits, MaxHeight = 100, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var checks = BindText(review, nameof(review.ChecksSummary));
        var checksPanel = new ScrollViewer { Content = checks, MaxHeight = 100, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var files = new ListView { ItemsSource = review.Files, MaxHeight = 115, SelectionMode = ListViewSelectionMode.Single, DisplayMemberPath = "Path" };
        AutomationProperties.SetAutomationId(files, "PullRequestReviewFiles");
        files.SelectionChanged += (_, _) => review.SelectedFile = files.SelectedItem as PullRequestChangedFile;
        files.SetBinding(ListView.SelectedItemProperty, new Binding { Source = review, Path = new PropertyPath(nameof(review.SelectedFile)), Mode = BindingMode.OneWay });
        var fileSummary = BindText(review, nameof(review.SelectedFileSummary));
        var lines = new ListView
        {
            ItemsSource = review.Lines, MaxHeight = 220, SelectionMode = ListViewSelectionMode.Single,
            FontFamily = ThemeResourceLookup.Get<Microsoft.UI.Xaml.Media.FontFamily>(this, "PiMonospaceFontFamily"),
            FontSize = ThemeResourceLookup.Get<double>(this, "PiCodeFontSize"),
            ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><TextBlock Text=\"{Binding DisplayText}\" TextWrapping=\"" +
                (ViewModel.Layout.WordWrap ? "Wrap" : "NoWrap") + "\" /></DataTemplate>"),
        };
        ScrollViewer.SetHorizontalScrollMode(lines, ViewModel.Layout.WordWrap ? ScrollMode.Disabled : ScrollMode.Enabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(lines, ViewModel.Layout.WordWrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        AutomationProperties.SetAutomationId(lines, "PullRequestReviewDiffLines");
        lines.SelectionChanged += (_, _) => review.SelectedLine = lines.SelectedItem as PullRequestReviewLineViewModel;
        lines.SetBinding(ListView.SelectedItemProperty, new Binding { Source = review, Path = new PropertyPath(nameof(review.SelectedLine)), Mode = BindingMode.OneWay });
        var side = new ComboBox { ItemsSource = Enum.GetValues<PullRequestDiffSide>(), Width = 115 };
        AutomationProperties.SetAutomationId(side, "PullRequestReviewSide");
        side.SetBinding(ComboBox.SelectedItemProperty, new Binding { Source = review, Path = new PropertyPath(nameof(review.SelectedSide)), Mode = BindingMode.TwoWay });
        var inlineBody = new TextBox { Header = "Inline comment", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 50, MaxHeight = 100 };
        AutomationProperties.SetAutomationId(inlineBody, "PullRequestInlineCommentInput");
        var addInline = new Button { Content = "Add line draft" };
        addInline.Click += (_, _) => { if (review.AddInlineComment(inlineBody.Text)) inlineBody.Text = string.Empty; };
        addInline.SetBinding(Button.IsEnabledProperty, new Binding { Source = review, Path = new PropertyPath(nameof(review.CanAddInlineComment)), Mode = BindingMode.OneWay });
        var inlineDrafts = new ListView { ItemsSource = review.InlineComments, MaxHeight = 120, SelectionMode = ListViewSelectionMode.Single, DisplayMemberPath = "Body" };
        AutomationProperties.SetAutomationId(inlineDrafts, "PullRequestInlineDrafts");
        var removeInline = new Button { Content = "Remove selected line draft" };
        removeInline.Click += (_, _) => { if (inlineDrafts.SelectedItem is PullRequestInlineComment comment) review.RemoveInlineComment(comment); };
        var threads = new ListView { ItemsSource = review.Discussions, MaxHeight = 145, SelectionMode = ListViewSelectionMode.Single, DisplayMemberPath = "Id" };
        AutomationProperties.SetAutomationId(threads, "PullRequestReviewThreads");
        threads.SelectionChanged += (_, _) => review.SelectedDiscussion = threads.SelectedItem as PullRequestDiscussion;
        threads.SetBinding(ListView.SelectedItemProperty, new Binding { Source = review, Path = new PropertyPath(nameof(review.SelectedDiscussion)), Mode = BindingMode.OneWay });
        var discussion = BindText(review, nameof(review.DiscussionSummary));
        var discussionPanel = new ScrollViewer { Content = discussion, MaxHeight = 90, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var reply = new TextBox { Header = "Reply", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 50, MaxHeight = 100 };
        AutomationProperties.SetAutomationId(reply, "PullRequestReplyInput");
        reply.SetBinding(TextBox.TextProperty, new Binding { Source = review, Path = new PropertyPath(nameof(review.ReplyBody)), Mode = BindingMode.OneWay });
        reply.TextChanged += (_, _) => review.SetReplyBody(reply.Text);
        reply.SetBinding(TextBox.IsEnabledProperty, new Binding { Source = review, Path = new PropertyPath(nameof(review.CanEditDraft)), Mode = BindingMode.OneWay });
        var replyButton = new Button { Content = "Reply" };
        replyButton.Click += async (_, _) => await RefreshAfterReviewWriteAsync(review, () => review.ReplyAsync());
        replyButton.SetBinding(Button.IsEnabledProperty, new Binding { Source = review, Path = new PropertyPath(nameof(review.CanReply)), Mode = BindingMode.OneWay });
        var resolve = new Button { Content = "Resolve / reopen" };
        resolve.Click += async (_, _) => await RefreshAfterReviewWriteAsync(review, () => review.SetResolvedAsync(review.SelectedDiscussion?.IsResolved != true));
        resolve.SetBinding(Button.IsEnabledProperty, new Binding { Source = review, Path = new PropertyPath(nameof(review.CanResolve)), Mode = BindingMode.OneWay });
        var reviewBody = new TextBox { Header = "Review summary", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, MaxHeight = 150 };
        AutomationProperties.SetAutomationId(reviewBody, "PullRequestReviewSummaryInput");
        reviewBody.SetBinding(TextBox.TextProperty, new Binding { Source = review, Path = new PropertyPath(nameof(review.Body)), Mode = BindingMode.OneWay });
        reviewBody.TextChanged += (_, _) => review.SetBody(reviewBody.Text);
        reviewBody.SetBinding(TextBox.IsEnabledProperty, new Binding { Source = review, Path = new PropertyPath(nameof(review.CanEditDraft)), Mode = BindingMode.OneWay });
        var reviewEvent = new ComboBox { Header = "Review action", Width = 180 };
        reviewEvent.SetBinding(ItemsControl.ItemsSourceProperty, new Binding { Source = review, Path = new PropertyPath(nameof(review.ReviewEvents)), Mode = BindingMode.OneWay });
        AutomationProperties.SetAutomationId(reviewEvent, "PullRequestReviewEventSelector");
        reviewEvent.SelectionChanged += (_, _) => { if (reviewEvent.SelectedItem is PullRequestReviewEvent value) review.ReviewEvent = value; };
        reviewEvent.SetBinding(ComboBox.SelectedItemProperty, new Binding { Source = review, Path = new PropertyPath(nameof(review.ReviewEvent)), Mode = BindingMode.OneWay });
        reviewEvent.SetBinding(ComboBox.IsEnabledProperty, new Binding { Source = review, Path = new PropertyPath(nameof(review.CanEditDraft)), Mode = BindingMode.OneWay });
        var submit = new Button { Content = "Submit review" };
        AutomationProperties.SetAutomationId(submit, "SubmitPullRequestReviewButton");
        submit.Click += async (_, _) => await RefreshAfterReviewWriteAsync(review, () => review.SubmitAsync());
        submit.SetBinding(Button.IsEnabledProperty, new Binding { Source = review, Path = new PropertyPath(nameof(review.CanSubmit)), Mode = BindingMode.OneWay });
        var reload = new Button { Content = "Reload review" }; reload.Click += async (_, _) => await SafeReviewActionAsync(() => review.ReloadAsync());
        var recover = new Button { Content = "Refresh operation history" }; recover.Click += async (_, _) => await SafeReviewActionAsync(async () =>
        {
            await review.RecoverPendingAsync();
            await review.RefreshLiveAsync();
        });
        var discard = new Button { Content = "Discard saved draft" }; discard.Click += async (_, _) => await SafeReviewActionAsync(() => review.DiscardDraftAsync());

        var loadMore = new Button { Content = "Load more review data" };
        AutomationProperties.SetAutomationId(loadMore, "PullRequestReviewLoadMore");
        loadMore.SetBinding(Button.IsEnabledProperty, new Binding { Source = review, Path = new PropertyPath(nameof(review.CanLoadMore)), Mode = BindingMode.OneWay });
        loadMore.Click += async (_, _) => await SafeReviewActionAsync(() => review.LoadMoreAsync());
        var root = new StackPanel { Spacing = 8, MaxWidth = 720 };
        root.Children.Add(BuildPullRequestManagement(review));
        root.Children.Add(BuildPullRequestAdvanced(review));
        root.Children.Add(BindText(review, nameof(review.LiveStatus), "PullRequestLiveStatus"));
        root.Children.Add(BindText(review, nameof(review.PaginationSummary), "PullRequestReviewPagination"));
        root.Children.Add(loadMore);
        root.Children.Add(new Expander { Header = "Review details", IsExpanded = true, Content = new StackPanel { Spacing = 4, Children = { details, head, description, new TextBlock { Text = "Commits", FontWeight = FontWeights.SemiBold }, commitsPanel, new TextBlock { Text = "Checks", FontWeight = FontWeights.SemiBold }, checksPanel } } });
        var diffPanel = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "Hosted files", FontWeight = FontWeights.SemiBold }, files, fileSummary, lines } };
        var inlinePanel = new StackPanel { Spacing = 8, Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { side, addInline } }, inlineBody, inlineDrafts, removeInline } };
        var replyPanel = new StackPanel { Spacing = 8, Children = { reply, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { replyButton, resolve } } } };
        var composerPanel = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "Submit review", FontWeight = FontWeights.SemiBold }, reviewBody, reviewEvent, submit } };
        root.Children.Add(diffPanel); root.Children.Add(inlinePanel);
        root.Children.Add(new TextBlock { Text = "Discussions", FontWeight = FontWeights.SemiBold }); root.Children.Add(threads); root.Children.Add(discussionPanel); root.Children.Add(replyPanel);
        root.Children.Add(composerPanel);
        var website = new HyperlinkButton { Content = "Open pull request on provider website" };
        AutomationProperties.SetAutomationId(website, "PullRequestProviderWebsite");
        website.Click += async (_, _) =>
        {
            await SafeReviewActionAsync(async () =>
            {
                if (Uri.TryCreate(review.Snapshot?.PullRequest.Url, UriKind.Absolute, out var url) && url.Scheme == "https") await OpenHostingLinkAsync(url);
            });
        };
        root.Children.Add(website);
        void RefreshProviderControls()
        {
            diffPanel.Visibility = review.HasProviderDiff ? Visibility.Visible : Visibility.Collapsed;
            inlinePanel.Visibility = review.HasReviewComposer && review.ReviewCapabilities.InlineComments ? Visibility.Visible : Visibility.Collapsed;
            replyPanel.Visibility = composerPanel.Visibility = review.HasReviewComposer ? Visibility.Visible : Visibility.Collapsed;
        }
        void ProviderChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(review.Snapshot) or nameof(review.ReviewCapabilities)) RefreshProviderControls();
        }
        root.Loaded += (_, _) => { review.PropertyChanged -= ProviderChanged; review.PropertyChanged += ProviderChanged; RefreshProviderControls(); };
        root.Unloaded += (_, _) => review.PropertyChanged -= ProviderChanged;
        RefreshProviderControls();
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { reload, recover, discard } }); root.Children.Add(notice);
        return new Border { Padding = new Thickness(8), Child = root };
    }

    private static void BindReviewProviderVisibility(FrameworkElement element, PullRequestReviewViewModel review, Action refresh)
    {
        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(review.Snapshot) or nameof(review.ReviewCapabilities)) refresh();
        }
        element.Loaded += (_, _) => { review.PropertyChanged -= Changed; review.PropertyChanged += Changed; refresh(); };
        element.Unloaded += (_, _) => review.PropertyChanged -= Changed;
        refresh();
    }

    private static TextBlock BindText(object review, string property, string? automationId = null)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        text.SetBinding(TextBlock.TextProperty, new Binding { Source = review, Path = new PropertyPath(property), Mode = BindingMode.OneWay });
        if (automationId is not null) AutomationProperties.SetAutomationId(text, automationId);
        return text;
    }

    private Task RefreshAfterReviewWriteAsync(PullRequestReviewViewModel review, Func<Task<SourceControlOperationResult?>> action) =>
        SafeReviewActionAsync(async () =>
        {
            var snapshot = review.Snapshot;
            var result = await action();
            if (result?.Succeeded == true && ReferenceEquals(snapshot, review.Snapshot) && !review.HasPendingOperation)
                await review.ReloadAsync();
        });

    private async Task SafeReviewActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ViewModel.ReportRuntimeError(exception); }
    }
}
