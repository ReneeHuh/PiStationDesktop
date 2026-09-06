using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using PiStation.Protocol.Projections;

namespace PiStation.App.ViewModels;

public sealed class ThreadQueueViewModel : ObservableObject
{
    private QueueDeliveryMode _followUpMode = QueueDeliveryMode.OneAtATime;
    private QueueDeliveryState _deliveryState;
    private QueueDeliveryMode _steeringMode = QueueDeliveryMode.OneAtATime;
    private ThreadRuntimeState _runtimeState;
    private int _pendingMessageCount;

    public ObservableCollection<QueuedMessageViewModel> Messages { get; } = [];

    public string Summary => _pendingMessageCount == 0
        ? DeliveryState == QueueDeliveryState.Cleared ? "Queue cleared" : "Queue empty"
        : $"{_pendingMessageCount} queued message{(_pendingMessageCount == 1 ? string.Empty : "s")}";

    public string SteeringModeLabel => $"Steer: {FormatMode(SteeringMode)}";

    public string FollowUpModeLabel => $"Follow-up: {FormatMode(FollowUpMode)}";

    public string DeliveryStateLabel => DeliveryState switch
    {
        QueueDeliveryState.Queued => "Queued",
        QueueDeliveryState.Delivering => "Delivering",
        QueueDeliveryState.Cleared => "Cleared",
        _ => "Empty",
    };

    public QueueDeliveryMode SteeringMode
    {
        get => _steeringMode;
        private set
        {
            if (SetProperty(ref _steeringMode, value))
            {
                OnPropertyChanged(nameof(SteeringModeLabel));
            }
        }
    }

    public QueueDeliveryMode FollowUpMode
    {
        get => _followUpMode;
        private set
        {
            if (SetProperty(ref _followUpMode, value))
            {
                OnPropertyChanged(nameof(FollowUpModeLabel));
            }
        }
    }

    public QueueDeliveryState DeliveryState
    {
        get => _deliveryState;
        private set
        {
            if (SetProperty(ref _deliveryState, value))
            {
                OnPropertyChanged(nameof(DeliveryStateLabel));
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public Visibility InspectorVisibility =>
        _runtimeState is ThreadRuntimeState.Running or ThreadRuntimeState.Stopping || Messages.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility MessagesVisibility => Messages.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public void Apply(ThreadQueueProjection? queue, ThreadRuntimeState? runtimeState)
    {
        _runtimeState = runtimeState ?? ThreadRuntimeState.Stopped;
        var source = queue?.Messages ?? [];
        for (var index = 0; index < source.Count; index++)
        {
            var message = source[index];
            var row = new QueuedMessageViewModel(
                message.Kind,
                message.Position,
                message.Text);
            if (index < Messages.Count)
            {
                Messages[index] = row;
            }
            else
            {
                Messages.Add(row);
            }
        }

        while (Messages.Count > source.Count)
        {
            Messages.RemoveAt(Messages.Count - 1);
        }

        SteeringMode = queue?.SteeringMode ?? QueueDeliveryMode.OneAtATime;
        FollowUpMode = queue?.FollowUpMode ?? QueueDeliveryMode.OneAtATime;
        _pendingMessageCount = queue?.PendingMessageCount ?? 0;
        DeliveryState = queue?.DeliveryState ?? QueueDeliveryState.Empty;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(InspectorVisibility));
        OnPropertyChanged(nameof(MessagesVisibility));
    }

    private static string FormatMode(QueueDeliveryMode mode) =>
        mode == QueueDeliveryMode.All ? "all" : "one at a time";
}

public sealed record QueuedMessageViewModel(
    QueuedMessageKind Kind,
    int Position,
    string Text)
{
    public string KindLabel => Kind == QueuedMessageKind.Steering ? "Steer" : "Follow-up";

    public string AccessibleName => $"{KindLabel} message {Position}: {Text}";
}
