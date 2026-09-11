using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using PiStation.App.ViewModels;
using PiStation.Protocol.Models;

namespace PiStation.App.Views;

public sealed partial class ShellPage
{
    private Expander BuildPullRequestManagement(PullRequestReviewViewModel review)
    {
        var title = Editor("PR title", nameof(review.EditedTitle), "PullRequestEditTitle", nameof(review.CanEditDetails), false, review.SetEditedTitle);
        title.MaxLength = 256;
        var description = Editor("PR description", nameof(review.EditedDescription), "PullRequestEditDescription", nameof(review.CanEditDetails), true, review.SetEditedDescription);
        description.MaxLength = PullRequestReviewDefaults.MaximumDescriptionCharacters;
        var save = Action("Save PR details", "PullRequestSaveDetails", nameof(review.CanSaveDetails), () => Write(PullRequestManagementAction.EditDetails));
        var draft = Action("", "PullRequestToggleDraft", nameof(review.CanChangeDraft), () => Write(PullRequestManagementAction.SetDraft));
        draft.SetBinding(Button.ContentProperty, Binding(nameof(review.DraftActionLabel)));
        var discardDetails = Action("Discard details edits", "PullRequestDiscardDetails", nameof(review.CanManage), () => { review.UseLatestDetails(); return Task.CompletedTask; });
        var keepDetails = Action("Keep my details edits against latest", "PullRequestKeepDetails", nameof(review.CanManage), () => { review.UseLatestDetails(true); return Task.CompletedTask; });
        var labels = new ComboBox { Header = "Label", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(labels, "PullRequestLabelToRemove");
        labels.SetBinding(ItemsControl.ItemsSourceProperty, Binding("Snapshot.PullRequest.Labels"));
        labels.SetBinding(ComboBox.SelectedItemProperty, Binding(nameof(review.SelectedLabel), BindingMode.TwoWay));
        var removeLabel = Action("Remove selected label", "PullRequestRemoveLabel", nameof(review.CanRemoveLabel), () => Write(PullRequestManagementAction.RemoveLabel, review.SelectedLabel));
        var reviewers = new ComboBox { Header = "Requested reviewer", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(reviewers, "PullRequestReviewerToRemove");
        reviewers.SetBinding(ItemsControl.ItemsSourceProperty, Binding("Snapshot.PullRequest.Reviewers"));
        reviewers.SetBinding(ComboBox.SelectedItemProperty, Binding(nameof(review.SelectedReviewer), BindingMode.TwoWay));
        var removeReviewer = Action("Remove selected reviewer", "PullRequestRemoveReviewer", nameof(review.CanRemoveReviewer), () => Write(PullRequestManagementAction.RemoveReviewer, review.SelectedReviewer));
        var comments = new ListView { ItemsSource = review.EditableComments, DisplayMemberPath = "Body", MaxHeight = 150, SelectionMode = ListViewSelectionMode.Single };
        AutomationProperties.SetAutomationId(comments, "PullRequestCommentToEdit");
        comments.SetBinding(ListView.SelectedItemProperty, Binding(nameof(review.SelectedComment)));
        var confirmDelete = new CheckBox { Content = "Confirm deletion of the selected comment" };
        AutomationProperties.SetAutomationId(confirmDelete, "PullRequestConfirmDeleteComment");
        comments.SelectionChanged += (_, _) => { confirmDelete.IsChecked = false; review.SelectComment(comments.SelectedItem as PullRequestReviewComment); };
        var comment = Editor("Edit your comment", nameof(review.EditedCommentBody), "PullRequestEditComment", nameof(review.CanEditComment), true, review.SetEditedCommentBody);
        var saveComment = Action("Save comment", "PullRequestSaveComment", nameof(review.CanSaveComment), () => Write(PullRequestManagementAction.EditComment));
        var deleteComment = Action("Delete comment", "PullRequestDeleteComment", nameof(review.CanDeleteComment), async () =>
        {
            if (confirmDelete.IsChecked != true) { ViewModel.Settings.Status = "Confirm deletion of the selected comment first."; return; }
            await Write(PullRequestManagementAction.DeleteComment);
            confirmDelete.IsChecked = false;
        });
        var discardComment = Action("Discard comment edits", "PullRequestDiscardComment", nameof(review.CanManage), () => { review.UseLatestComment(); return Task.CompletedTask; });
        var keepComment = Action("Keep my comment edits against latest", "PullRequestKeepComment", nameof(review.CanManage), () => { review.UseLatestComment(true); return Task.CompletedTask; });
        var panel = new StackPanel { Spacing = 8 };
        foreach (var child in new UIElement[] { title, description, save, draft, discardDetails, keepDetails, labels, removeLabel, reviewers, removeReviewer }) panel.Children.Add(child);
        var commentControls = new UIElement[] {
            new TextBlock { Text = "Comments (load more review data for additional comments)" }, comments,
            new Expander { Header = "Latest hosted comment", Content = BindText(review, nameof(review.LatestCommentBody)) },
            new Expander { Header = "Selected comment reactions", Content = BuildPullRequestReactions(review, true) },
            comment, saveComment, discardComment, keepComment, confirmDelete, deleteComment,
            BindText(review, nameof(review.EditConflictNotice), "PullRequestEditConflict") };
        foreach (var child in commentControls) panel.Children.Add(child);
        var expander = new Expander { Header = "Edit PR and manage comments", Content = panel, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(expander, "PullRequestManagementPanel");
        BindReviewProviderVisibility(expander, review, () =>
        {
            draft.Visibility = review.PullRequest?.Provider == SourceControlProvider.Bitbucket ? Visibility.Collapsed : Visibility.Visible;
            labels.Visibility = removeLabel.Visibility = review.ReviewCapabilities.RemoveLabels ? Visibility.Visible : Visibility.Collapsed;
            reviewers.Visibility = removeReviewer.Visibility = review.ReviewCapabilities.RemoveReviewers ? Visibility.Visible : Visibility.Collapsed;
            foreach (var child in commentControls) child.Visibility = review.PullRequest?.Provider == SourceControlProvider.AzureDevOps ? Visibility.Collapsed : Visibility.Visible;
            expander.Header = review.PullRequest?.Provider == SourceControlProvider.AzureDevOps ? "Edit PR details and reviewers" : "Edit PR and manage comments";
        });
        return expander;

        Binding Binding(string path, BindingMode mode = BindingMode.OneWay) => new() { Source = review, Path = new PropertyPath(path), Mode = mode };
        TextBox Editor(string header, string property, string id, string enabled, bool multiline, Action<string> changed)
        {
            var editor = new TextBox { Header = header, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap, MaxHeight = multiline ? 180 : 80,
                MaxLength = PullRequestReviewDefaults.MaximumBodyCharacters };
            AutomationProperties.SetAutomationId(editor, id);
            editor.SetBinding(TextBox.TextProperty, Binding(property));
            editor.SetBinding(TextBox.IsEnabledProperty, Binding(enabled));
            editor.TextChanged += (_, _) => changed(editor.Text);
            return editor;
        }
        Button Action(string label, string id, string enabled, Func<Task> action)
        {
            var button = new Button { Content = label };
            AutomationProperties.SetAutomationId(button, id);
            button.SetBinding(Button.IsEnabledProperty, Binding(enabled));
            button.Click += async (_, _) => await SafeReviewActionAsync(action);
            return button;
        }
        async Task Write(PullRequestManagementAction action, string? item = null)
        {
            var result = await review.ManageAsync(action, item);
            if (result?.Succeeded == true) await review.RefreshLiveAsync();
        }
    }
}
