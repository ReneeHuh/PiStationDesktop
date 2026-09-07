namespace PiStation.Host.Threads;

public sealed partial class PiThreadController
{
    internal async Task<bool> TryAutoSettleAsync(long revision, CancellationToken token)
    {
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (ThreadSettlementPolicy.HasLiveWork(Journal.Projection)) return false;
            return await _database.TryAutoSettleAsync(_thread.ThreadId, revision, token).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }
}
