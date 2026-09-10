namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    public async Task LoadEarlierHistoryAsync()
    {
        if (Thread.IsLoadingHistory || _subscription is not { } subscription ||
            subscription.Store.Current is not { EarlierHistory: { } cursor } projection) return;
        Thread.IsLoadingHistory = true;
        Thread.HistoryStatus = "Loading earlier messages…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var page = await RequireClient().ReadThreadHistoryAsync(new(projection.ThreadId, projection.ProjectionEpoch, cursor.BeforeItemId), timeout.Token);
            // Stream and RPC replies are independent. Wait for the live watermark
            // before prepending, and reject a branch/restart or a thread switch.
            while (ReferenceEquals(_subscription, subscription) && subscription.Store.Current is { } current &&
                   current.ProjectionEpoch == page.ProjectionEpoch && current.EarlierHistory?.BeforeItemId == page.BeforeItemId)
            {
                if (subscription.Store.TryMergeHistory(page))
                {
                    ApplyThreadProjection(subscription.Store.Current);
                    Thread.HistoryStatus = "Earlier messages loaded.";
                    return;
                }
                await Task.Delay(50, timeout.Token);
            }
            if (ReferenceEquals(_subscription, subscription)) Thread.HistoryStatus = "Conversation changed. Load earlier messages again.";
        }
        catch (Exception error) { if (ReferenceEquals(_subscription, subscription)) Thread.HistoryStatus = error.Message; }
        finally { Thread.IsLoadingHistory = false; }
    }
}
