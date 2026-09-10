using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using System.Collections.Specialized;
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
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public ShellViewModel ViewModel { get; }
    public event EventHandler<bool>? ReadingHistoryChanged;

    public async void RevealMessage(string messageId)
    {
        var itemId = messageId.StartsWith("message-", StringComparison.Ordinal) ? messageId : $"message-{messageId}";
        var thread = _scrollThread;
        _followOutput = false;
        // A citation may point outside the initial window. Stop on a failed
        // page or navigation change; loading remains bounded to each page.
        while (ViewModel.Thread.Timeline.All(item => item.ItemId != itemId) && ViewModel.Thread.CanLoadHistory)
        {
            var count = ViewModel.Thread.Timeline.Count;
            await ViewModel.LoadEarlierHistoryAsync();
            if (_scrollThread != thread || ViewModel.Thread.Timeline.Count <= count) return;
        }
        if (ViewModel.Thread.Timeline.FirstOrDefault(item =>
                string.Equals(item.ItemId, itemId, StringComparison.Ordinal)) is { } item)
        {
            TranscriptList.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
        }
    }

    private readonly Dictionary<ThreadId, (double Offset, bool Follow)> _positions = [];
    private ScrollViewer? _scrollViewer;
    private ThreadId? _scrollThread;
    private bool _followOutput = true;
    private bool _scrollQueued;
    private double? _restoreOffset;
    private double _lastObservedOffset;
    private double _lastScrollableHeight;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _scrollViewer = FindScroller(TranscriptList);
        if (_scrollViewer is not null) _scrollViewer.ViewChanged += OnViewChanged;
        ViewModel.Workspace.PropertyChanged += OnWorkspaceChanged;
        ViewModel.Thread.Timeline.CollectionChanged += OnTimelineChanged;
        ViewModel.CitationRequested += OnCitationRequested;
        TranscriptList.SizeChanged += OnTranscriptSizeChanged;
        TranscriptList.LayoutUpdated += OnTranscriptLayoutUpdated;
        SwitchScrollThread();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _sentAttachmentCancellation?.Cancel();
        RememberPosition();
        if (_scrollViewer is not null) _scrollViewer.ViewChanged -= OnViewChanged;
        ViewModel.Workspace.PropertyChanged -= OnWorkspaceChanged;
        ViewModel.Thread.Timeline.CollectionChanged -= OnTimelineChanged;
        ViewModel.CitationRequested -= OnCitationRequested;
        TranscriptList.SizeChanged -= OnTranscriptSizeChanged;
        TranscriptList.LayoutUpdated -= OnTranscriptLayoutUpdated;
    }

    private static ScrollViewer? FindScroller(DependencyObject parent)
    {
        if (parent is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScroller(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }

    private void RememberPosition()
    {
        if (_scrollThread is { } thread && _scrollViewer is { } viewer)
            _positions[thread] = (viewer.VerticalOffset, _followOutput);
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.SelectedThread)) SwitchScrollThread();
    }

    private void SwitchScrollThread()
    {
        var next = ViewModel.Workspace.SelectedThread?.ThreadId;
        if (next == _scrollThread) return;
        RememberPosition();
        _scrollThread = next;
        var saved = next is { } id && _positions.TryGetValue(id, out var position) ? position : (Offset: 0d, Follow: true);
        _followOutput = saved.Follow;
        ReadingHistoryChanged?.Invoke(this, !_followOutput);
        _restoreOffset = saved.Follow ? null : saved.Offset;
        JumpToLatestButton.Visibility = Visibility.Collapsed;
        QueueScroll();
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_restoreOffset is not null || _scrollViewer is null || e.IsIntermediate) return;
        var heightChanged = Math.Abs(_scrollViewer.ScrollableHeight - _lastScrollableHeight) > 1;
        var offsetChanged = Math.Abs(_scrollViewer.VerticalOffset - _lastObservedOffset) > 1;
        _lastScrollableHeight = _scrollViewer.ScrollableHeight;
        _lastObservedOffset = _scrollViewer.VerticalOffset;
        if (_followOutput && heightChanged && !offsetChanged) { QueueScroll(); return; }
        _followOutput = _scrollViewer.ScrollableHeight - _scrollViewer.VerticalOffset <= 40;
        JumpToLatestButton.Visibility = _followOutput ? Visibility.Collapsed : Visibility.Visible;
        ReadingHistoryChanged?.Invoke(this, !_followOutput);
        RememberPosition();
    }

    private void OnTimelineChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_followOutput) JumpToLatestButton.Visibility = Visibility.Visible;
        QueueScroll();
    }

    private void OnTranscriptSizeChanged(object sender, SizeChangedEventArgs e) => QueueScroll();
    private void OnTranscriptLayoutUpdated(object? sender, object e)
    {
        if (_followOutput && _scrollViewer is { } viewer && viewer.ScrollableHeight - viewer.VerticalOffset > 1) QueueScroll();
    }

    private void QueueScroll()
    {
        if (_scrollQueued || !IsLoaded) return;
        _scrollQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _scrollQueued = false;
            if (_scrollViewer is null || ViewModel.Thread.Projection?.ThreadId != _scrollThread || ViewModel.Thread.Timeline.Count == 0) return;
            if (_restoreOffset is { } offset)
            {
                _scrollViewer.ChangeView(null, Math.Min(offset, _scrollViewer.ScrollableHeight), null, true);
                _restoreOffset = null;
            }
            else if (_followOutput) _scrollViewer.ChangeView(null, _scrollViewer.ScrollableHeight, null, true);
        });
    }

    private void OnJumpToLatestClicked(object sender, RoutedEventArgs e)
    {
        _restoreOffset = null;
        _followOutput = true;
        ReadingHistoryChanged?.Invoke(this, false);
        JumpToLatestButton.Visibility = Visibility.Collapsed;
        QueueScroll();
    }

    private async void OnLoadEarlierClicked(object sender, RoutedEventArgs e)
    {
        var thread = _scrollThread;
        var anchor = ViewModel.Thread.Timeline.FirstOrDefault()?.ItemId;
        _followOutput = false;
        ReadingHistoryChanged?.Invoke(this, true);
        await ViewModel.LoadEarlierHistoryAsync();
        if (_scrollThread == thread && ViewModel.Thread.Timeline.FirstOrDefault(item => item.ItemId == anchor) is { } existing)
            TranscriptList.ScrollIntoView(existing, ScrollIntoViewAlignment.Leading);
    }

    private void OnCitationRequested(object? sender, string messageId) => DispatcherQueue.TryEnqueue(() => RevealMessage(messageId));
    private async void OnWorkspaceLinkRequested(object? sender, string link) => await ViewModel.OpenMarkdownLinkAsync(link);
    private async void OnBrowserLinkRequested(object? sender, BrowserLinkEventArgs args) =>
        await ViewModel.OpenBrowserLinkAsync(args.Uri, args.ForceSystem);
    private void OnSelectionQuoteRequested(object? sender, string text)
    {
        if (sender is MarkdownView { DataContext: MessageTimelineItemViewModel message }) ViewModel.QuoteResponse(message, text);
    }
    private void OnSelectionCiteRequested(object? sender, string text)
    {
        if (sender is MarkdownView { DataContext: MessageTimelineItemViewModel message }) ViewModel.CiteResponse(message, text);
    }

    private void OnQuoteResponseClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MessageTimelineItemViewModel message })
        {
            ViewModel.QuoteResponse(message);
        }
    }

    private void OnCiteResponseClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MessageTimelineItemViewModel message })
        {
            ViewModel.CiteResponse(message);
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

    private void OnSecretQuestionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: QuestionTimelineItemViewModel question } password) question.AnswerText = password.Password;
    }

    private static void ClearSecretInput(DependencyObject element, QuestionTimelineItemViewModel question)
    {
        if (element is PasswordBox box && ReferenceEquals(box.DataContext, question)) box.Password = string.Empty;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            ClearSecretInput(VisualTreeHelper.GetChild(element, index), question);
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
            if (question.InputKind == QuestionInputKind.Secret) { question.AnswerText = string.Empty; ClearSecretInput(this, question); }
            await ViewModel.AnswerQuestionAsync(InteractionId.Parse(question.InteractionId), answer);
        }
    }

    private async void OnCancelInteractionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: QuestionTimelineItemViewModel { InputKind: QuestionInputKind.Secret } secret })
        { secret.AnswerText = string.Empty; ClearSecretInput(this, secret); }
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
