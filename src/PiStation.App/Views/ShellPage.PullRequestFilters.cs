using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using PiStation.Protocol.Models;

namespace PiStation.App.Views;

public sealed partial class ShellPage
{
    private Expander BuildPullRequestFilters()
    {
        var filters = ViewModel.Settings.PullRequestFilters ?? new();
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Filter the selected project's pull requests. Advanced filters are available for GitHub.", TextWrapping = TextWrapping.Wrap });
        var state = new ComboBox { Header = "State", ItemsSource = new[] { "All", "Open", "Closed", "Merged", "Draft" }, SelectedItem = ViewModel.Settings.PullRequestStateFilter?.ToString() ?? "All" };
        AutomationProperties.SetAutomationId(state, "PullRequestFilterState");
        panel.Children.Add(state);
        var query = Text("Search text", "PullRequestFilterQuery", filters.Query);
        query.MaxLength = 512;
        var involvement = Choice("Involvement", "PullRequestFilterInvolvement", filters.Involvement);
        var draft = Choice("Drafts", "PullRequestFilterDraft", filters.Draft);
        var review = Choice("Review decision", "PullRequestFilterReview", filters.Review);
        var checks = Choice("Checks", "PullRequestFilterChecks", filters.Checks);
        var author = Text("Author login (@me for yourself)", "PullRequestFilterAuthor", filters.Author ?? "");
        var groups = Text("Label groups: one group per line; separate alternatives with |", "PullRequestFilterLabels",
            string.Join(Environment.NewLine, (filters.LabelGroups ?? []).Select(group => string.Join(" | ", group))), true);
        var excluded = Text("Exclude labels: one label per line", "PullRequestFilterExcludedLabels", string.Join(Environment.NewLine, filters.ExcludedLabels ?? []), true);
        var apply = new Button { Content = "Apply filters" };
        AutomationProperties.SetAutomationId(apply, "PullRequestApplyFilters");
        apply.Click += async (_, _) => await SafeReviewActionAsync(async () =>
        {
            var parsed = new PullRequestListFilters(query.Text, (PullRequestInvolvement)involvement.SelectedValue, (PullRequestDraftFilter)draft.SelectedValue,
                (PullRequestReviewFilter)review.SelectedValue, (PullRequestChecksFilter)checks.SelectedValue, author.Text.Trim(),
                Lines(groups.Text).Select(line => (IReadOnlyList<string>)line.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToArray(), Lines(excluded.Text));
            await ViewModel.PullRequestReview.SaveNowAsync();
            ViewModel.PullRequestReview.Suspend("Select a matching pull request to load its review.");
            ViewModel.Settings.SetPullRequestFilters(Enum.TryParse<PullRequestState>(state.SelectedItem as string, out var selected) ? selected : null, parsed);
            await ViewModel.RefreshPullRequestsAsync();
        });
        var reset = new Button { Content = "Clear filters" };
        AutomationProperties.SetAutomationId(reset, "PullRequestClearFilters");
        reset.Click += async (_, _) => await SafeReviewActionAsync(async () =>
        {
            state.SelectedItem = "All"; query.Text = ""; author.Text = ""; groups.Text = ""; excluded.Text = "";
            involvement.SelectedValue = PullRequestInvolvement.All; draft.SelectedValue = PullRequestDraftFilter.Any;
            review.SelectedValue = PullRequestReviewFilter.Any; checks.SelectedValue = PullRequestChecksFilter.Any;
            await ViewModel.PullRequestReview.SaveNowAsync();
            ViewModel.PullRequestReview.Suspend("Select a pull request to load its review.");
            ViewModel.Settings.SetPullRequestFilters(null, null);
            await ViewModel.RefreshPullRequestsAsync();
        });
        var more = new Button { Content = "Load more matching pull requests" };
        AutomationProperties.SetAutomationId(more, "PullRequestMoreFiltered");
        more.SetBinding(IsEnabledProperty, new Binding { Source = ViewModel.Settings, Path = new PropertyPath(nameof(ViewModel.Settings.CanLoadMorePullRequests)), Mode = BindingMode.OneWay });
        more.Click += async (_, _) => await SafeReviewActionAsync(() => ViewModel.LoadMorePullRequestsAsync());
        panel.Children.Add(apply); panel.Children.Add(reset); panel.Children.Add(more);
        var expander = new Expander { Header = "Filter pull requests", Content = panel, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(expander, "PullRequestFilterPanel");
        return expander;

        static string[] Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        ComboBox Choice<T>(string header, string id, T selected) where T : struct, Enum
        {
            var choices = Enum.GetValues<T>().Select(value => new KeyValuePair<T, string>(value, value.ToString() switch {
                "Authored" => "Authored by me", "ReviewRequested" => "Review requested from me", "ChangesRequested" => "Changes requested", "ReviewRequired" => "Review required", _ => value.ToString() })).ToArray();
            var control = new ComboBox { Header = header, ItemsSource = choices, DisplayMemberPath = "Value", SelectedValuePath = "Key", SelectedValue = selected };
            AutomationProperties.SetAutomationId(control, id); panel.Children.Add(control); return control;
        }
        TextBox Text(string header, string id, string value, bool multiline = false)
        {
            var control = new TextBox { Header = header, Text = value, AcceptsReturn = multiline, MaxHeight = 120, MaxLength = 2048, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(control, id); panel.Children.Add(control); return control;
        }
    }
}
