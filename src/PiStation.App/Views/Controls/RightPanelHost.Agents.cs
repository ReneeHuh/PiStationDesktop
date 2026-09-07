using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using PiStation.App.ViewModels;

namespace PiStation.App.Views.Controls;

public sealed partial class RightPanelHost
{
    private async void OnManageAgentsClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.ManageAgentsAsync((sender as FrameworkElement)?.Tag as string ?? "inspect");
    private void OnNewAgentPresetClicked(object sender, RoutedEventArgs e) => ViewModel.Agents.NewPreset();
    private void OnReloadAgentPresetClicked(object sender, RoutedEventArgs e) => ViewModel.Agents.ReloadPreset();
    private void OnAddAgentTaskClicked(object sender, RoutedEventArgs e) => ViewModel.Agents.AddTask();
    private void OnRemoveAgentTaskClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AgentTaskEditorViewModel task) ViewModel.Agents.RemoveTask(task);
    }
    private async void OnRunAgentWorkflowClicked(object sender, RoutedEventArgs e) => await ViewModel.StartAgentWorkflowAsync();
    private async void OnAgentDetailsClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AgentActivityRowViewModel activity) return;
        var threadId = ViewModel.Thread.Projection?.ThreadId;
        var transcript = new TextBlock { Text = activity.Transcript, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        AutomationProperties.SetAutomationId(transcript, "AgentTranscriptText");
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = $"{activity.Title} · {activity.StateLabel}",
            Content = new ScrollViewer { Content = transcript, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = "Close", PrimaryButtonText = activity.CanResume ? "Continue child" : string.Empty,
            IsPrimaryButtonEnabled = activity.CanResume && ViewModel.Agents.CanLaunch,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || ViewModel.Thread.Projection?.ThreadId != threadId) return;
        var task = new TextBox { Header = "Follow-up task", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100, MaxLength = 8192 };
        AutomationProperties.SetAutomationId(task, "AgentContinuationTaskTextBox");
        var followUp = new ContentDialog { XamlRoot = XamlRoot, Title = "Continue " + activity.Title, Content = task,
            PrimaryButtonText = "Run continuation", CloseButtonText = "Cancel", IsPrimaryButtonEnabled = false };
        task.TextChanged += (_, _) => followUp.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(task.Text) && ViewModel.Agents.CanLaunch;
        if (await followUp.ShowAsync() == ContentDialogResult.Primary && ViewModel.Thread.Projection?.ThreadId == threadId)
            await ViewModel.ContinueAgentAsync(activity, task.Text);
    }
}
