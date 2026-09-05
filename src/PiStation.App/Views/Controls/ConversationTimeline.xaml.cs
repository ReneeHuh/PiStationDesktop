using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PiStation.App.ViewModels;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;

namespace PiStation.App.Views.Controls;

public sealed partial class ConversationTimeline : UserControl
{
    public ConversationTimeline(ShellViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    public ShellViewModel ViewModel { get; }

    public void RevealMessage(string messageId)
    {
        var itemId = $"message-{messageId}";
        if (ViewModel.Thread.Timeline.FirstOrDefault(item =>
                string.Equals(item.ItemId, itemId, StringComparison.Ordinal)) is { } item)
        {
            TranscriptList.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
        }
    }

    private void OnTranscriptContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (!ReferenceEquals(sender, TranscriptList) || args.ItemContainer is null)
        {
            return;
        }

        if (args.Item is MessageTimelineItemViewModel message)
        {
            AutomationProperties.SetName(args.ItemContainer, message.AccessibleName);
        }
        else
        {
            args.ItemContainer.ClearValue(AutomationProperties.NameProperty);
        }
    }

    private async void OnApproveInteractionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ApprovalTimelineItemViewModel approval })
        {
            await ViewModel.RespondToApprovalAsync(
                InteractionId.Parse(approval.InteractionId),
                ApprovalDecision.Approve);
        }
    }

    private async void OnRejectInteractionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ApprovalTimelineItemViewModel approval })
        {
            await ViewModel.RespondToApprovalAsync(
                InteractionId.Parse(approval.InteractionId),
                ApprovalDecision.Reject);
        }
    }

    private async void OnAnswerQuestionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: QuestionTimelineItemViewModel question })
        {
            return;
        }

        var answer = question.InputKind == QuestionInputKind.Select
            ? question.SelectedOption
            : question.AnswerText;
        if (answer is not null)
        {
            await ViewModel.AnswerQuestionAsync(InteractionId.Parse(question.InteractionId), answer);
        }
    }

    private async void OnCancelInteractionClicked(object sender, RoutedEventArgs e)
    {
        var interactionId = (sender as Button)?.DataContext switch
        {
            ApprovalTimelineItemViewModel approval => approval.InteractionId,
            QuestionTimelineItemViewModel question => question.InteractionId,
            _ => null,
        };
        if (interactionId is not null)
        {
            await ViewModel.CancelInteractionAsync(InteractionId.Parse(interactionId));
        }
    }

    private async void OnCheckpointFileClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CheckpointFileViewModel file })
        {
            await ViewModel.OpenCheckpointDiffAsync(
                file.TurnCount,
                CheckpointDiffScope.Turn,
                file.RelativePath);
        }
    }

    private async void OnTurnCheckpointDiffClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CheckpointTimelineItemViewModel checkpoint })
        {
            await ViewModel.OpenCheckpointDiffAsync(checkpoint.TurnCount, CheckpointDiffScope.Turn);
        }
    }

    private async void OnFullThreadCheckpointDiffClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CheckpointTimelineItemViewModel checkpoint })
        {
            await ViewModel.OpenCheckpointDiffAsync(checkpoint.TurnCount, CheckpointDiffScope.FullThread);
        }
    }

    private async void OnRevertCheckpointClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CheckpointTimelineItemViewModel checkpoint } ||
            !checkpoint.CanRevert)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Revert turn {checkpoint.TurnCount}?",
            Content = $"This restores the workspace to before turn {checkpoint.TurnCount} and rewinds Pi's " +
                $"conversation. Turn {checkpoint.TurnCount} and every later message and checkpoint will be " +
                "discarded, including newer uncommitted workspace edits. This cannot be undone.",
            PrimaryButtonText = "Revert turn",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.RevertCheckpointAsync(checkpoint.RevertTargetTurnCount);
        }
    }
}
