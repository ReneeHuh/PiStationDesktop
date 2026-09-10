using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using PiStation.App.ViewModels;
using PiStation.Protocol.Models;

namespace PiStation.App.Views;

public sealed partial class ShellPage
{
    private Expander BuildPullRequestAdvanced(PullRequestReviewViewModel review)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(BindText(review, nameof(review.AdvancedStatus), "PullRequestAdvancedStatus"));
        var mergeMethod = new ComboBox { Header = "Merge method" };
        AutomationProperties.SetAutomationId(mergeMethod, "PullRequestMergeMethod");
        mergeMethod.SetBinding(ItemsControl.ItemsSourceProperty, Bind(nameof(review.MergeMethods)));
        mergeMethod.SetBinding(ComboBox.SelectedItemProperty, Bind(nameof(review.SelectedMergeMethod), BindingMode.TwoWay));
        mergeMethod.SetBinding(IsEnabledProperty, Bind(nameof(review.CanManage)));
        panel.Children.Add(mergeMethod);
        var confirmMerge = Confirm("Confirm merging this pull request", "PullRequestConfirmMerge");
        mergeMethod.SelectionChanged += (_, _) => confirmMerge.IsChecked = false;
        panel.Children.Add(confirmMerge);
        panel.Children.Add(Action("Merge pull request", "PullRequestMerge", nameof(review.CanMerge), async () =>
        {
            if (!Confirmed(confirmMerge)) return;
            await Write(PullRequestManagementAction.Merge);
        }));
        panel.Children.Add(Action("Enable automatic merge", "PullRequestEnableAutoMerge", nameof(review.CanEnableAutoMerge), () => Write(PullRequestManagementAction.EnableAutoMerge)));
        panel.Children.Add(Action("Disable automatic merge", "PullRequestDisableAutoMerge", nameof(review.CanDisableAutoMerge), () => Write(PullRequestManagementAction.DisableAutoMerge)));
        var updateStart = panel.Children.Count;
        var updateMethod = new ComboBox { Header = "Update branch method" };
        updateMethod.SetBinding(ItemsControl.ItemsSourceProperty, Bind(nameof(review.UpdateMethods)));
        AutomationProperties.SetAutomationId(updateMethod, "PullRequestUpdateMethod");
        updateMethod.SetBinding(ComboBox.SelectedItemProperty, Bind(nameof(review.SelectedUpdateMethod), BindingMode.TwoWay));
        updateMethod.SetBinding(IsEnabledProperty, Bind(nameof(review.CanManage)));
        panel.Children.Add(updateMethod);
        panel.Children.Add(Action("Update branch from base", "PullRequestUpdateBranch", nameof(review.CanUpdateBranch), () => Write(PullRequestManagementAction.UpdateBranch)));
        var updateControls = panel.Children.Skip(updateStart).ToArray();
        var githubStart = panel.Children.Count;
        panel.Children.Add(new TextBlock { Text = "Revert creates a new draft pull request reversing the merged changes.", TextWrapping = TextWrapping.Wrap });
        var confirmRevert = Confirm("Confirm creating a revert pull request", "PullRequestConfirmRevert");
        panel.Children.Add(confirmRevert);
        panel.Children.Add(Action("Create draft revert PR", "PullRequestRevert", nameof(review.CanRevert), async () =>
        {
            if (!Confirmed(confirmRevert)) return;
            await Write(PullRequestManagementAction.Revert);
        }));
        panel.Children.Add(BindText(review, nameof(review.WorkflowStatus), "PullRequestWorkflowStatus"));
        panel.Children.Add(Action("Refresh workflows awaiting approval", "PullRequestLoadWorkflows", nameof(review.CanLoadWorkflows), () => review.LoadWorkflowsAsync()));
        var workflows = new ListView { ItemsSource = review.Workflows, DisplayMemberPath = nameof(PullRequestWorkflow.DisplayLabel), MaxHeight = 180, SelectionMode = ListViewSelectionMode.Single };
        AutomationProperties.SetAutomationId(workflows, "PullRequestWorkflows");
        workflows.SetBinding(ListView.SelectedItemProperty, Bind(nameof(review.SelectedWorkflow), BindingMode.TwoWay));
        workflows.SetBinding(IsEnabledProperty, Bind(nameof(review.CanManage)));
        panel.Children.Add(workflows);
        panel.Children.Add(Action("Open selected workflow", "PullRequestOpenWorkflow", nameof(review.CanApproveWorkflow), async () =>
        {
            if (Uri.TryCreate(review.SelectedWorkflow?.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                await OpenHostingLinkAsync(uri);
        }));
        panel.Children.Add(Action("Load more workflows", "PullRequestMoreWorkflows", nameof(review.CanLoadMoreWorkflows), () => review.LoadWorkflowsAsync(true)));
        var confirmWorkflow = Confirm("Allow the selected fork workflow to run", "PullRequestConfirmWorkflow");
        workflows.SelectionChanged += (_, _) => confirmWorkflow.IsChecked = false;
        panel.Children.Add(confirmWorkflow);
        panel.Children.Add(Action("Approve selected workflow", "PullRequestApproveWorkflow", nameof(review.CanApproveWorkflow), async () =>
        {
            if (!Confirmed(confirmWorkflow)) return;
            await Write(PullRequestManagementAction.ApproveWorkflow, review.SelectedWorkflow?.Id);
        }));
        var githubControls = panel.Children.Skip(githubStart).ToArray();
        var reactionStart = panel.Children.Count;
        panel.Children.Add(new TextBlock { Text = "Pull request reactions", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(BuildPullRequestReactions(review, false));
        var reactionControls = panel.Children.Skip(reactionStart).ToArray();
        var expander = new Expander { Header = "Merge, workflows and reactions", Content = panel, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(expander, "PullRequestAdvancedPanel");
        BindReviewProviderVisibility(expander, review, () =>
        {
            foreach (var child in updateControls) child.Visibility = review.UpdateMethods.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var child in githubControls) child.Visibility = review.PullRequest?.Provider == SourceControlProvider.GitHub ? Visibility.Visible : Visibility.Collapsed;
            foreach (var child in reactionControls) child.Visibility = review.PullRequest?.Provider is SourceControlProvider.GitHub or SourceControlProvider.GitLab ? Visibility.Visible : Visibility.Collapsed;
            expander.Header = review.PullRequest?.Provider == SourceControlProvider.AzureDevOps ? "Merge options" : "Merge, updates and reactions";
        });
        return expander;

        Binding Bind(string property, BindingMode mode = BindingMode.OneWay) => new() { Source = review, Path = new PropertyPath(property), Mode = mode };
        CheckBox Confirm(string label, string id)
        {
            var check = new CheckBox { Content = label };
            AutomationProperties.SetAutomationId(check, id);
            check.SetBinding(IsEnabledProperty, Bind(nameof(review.CanManage)));
            return check;
        }
        bool Confirmed(CheckBox check)
        {
            if (check.IsChecked != true) { ViewModel.Settings.Status = check.Content + " first."; return false; }
            check.IsChecked = false;
            return true;
        }
        Button Action(string label, string id, string enabled, Func<Task> action)
        {
            var button = new Button { Content = label };
            AutomationProperties.SetAutomationId(button, id);
            button.SetBinding(IsEnabledProperty, Bind(enabled));
            button.Click += async (_, _) => await SafeReviewActionAsync(action);
            return button;
        }
        async Task Write(PullRequestManagementAction action, string? item = null)
        {
            var result = await review.ManageAsync(action, item);
            if (result?.Succeeded == true) await review.RefreshLiveAsync();
        }
    }

    private StackPanel BuildPullRequestReactions(PullRequestReviewViewModel review, bool comment)
    {
        var panel = new StackPanel { Spacing = 4 };
        var buttons = new Dictionary<PullRequestReactionContent, Button>();
        var labels = new[] { "👍 Thumbs up", "👎 Thumbs down", "😄 Laugh", "🎉 Hooray", "😕 Confused", "❤️ Heart", "🚀 Rocket", "👀 Eyes" };
        foreach (var content in Enum.GetValues<PullRequestReactionContent>())
        {
            var button = new Button();
            buttons.Add(content, button);
            AutomationProperties.SetAutomationId(button, $"PullRequest{(comment ? "Comment" : "")}Reaction{content}");
            button.SetBinding(IsEnabledProperty, new Binding { Source = review, Path = new PropertyPath(comment ? nameof(review.CanReactToComment) : nameof(review.CanReactToPullRequest)) });
            button.Click += async (_, _) => await SafeReviewActionAsync(async () =>
            {
                var result = await review.ToggleReactionAsync(content, comment);
                if (result?.Succeeded == true) await review.RefreshLiveAsync();
            });
            panel.Children.Add(button);
        }
        void Refresh()
        {
            var reactions = comment ? review.SelectedComment?.Reactions : review.Snapshot?.Reactions;
            foreach (var pair in buttons)
            {
                var reaction = reactions?.FirstOrDefault(value => value.Content == pair.Key);
                pair.Value.Content = $"{labels[(int)pair.Key]} · {reaction?.Count ?? 0}{(reaction?.ViewerHasReacted == true ? " · You reacted" : "")}";
                AutomationProperties.SetName(pair.Value, $"{(reaction?.ViewerHasReacted == true ? "Remove" : "Add")} {pair.Key} reaction. {reaction?.Count ?? 0} reactions.");
            }
        }
        void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(review.Snapshot) or nameof(review.SelectedComment)) Refresh();
        }
        panel.Loaded += (_, _) => { review.PropertyChanged -= Changed; review.PropertyChanged += Changed; Refresh(); };
        panel.Unloaded += (_, _) => review.PropertyChanged -= Changed;
        Refresh();
        return panel;
    }
}
