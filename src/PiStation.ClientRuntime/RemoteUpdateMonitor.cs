using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

/// <summary>Observes one durable request; never retries an activation command.</summary>
public static class RemoteUpdateMonitor
{
    public static async Task<RemoteUpdateReceipt> FollowAsync(Guid requestId,
        Func<CancellationToken, Task<RemoteUpdateReceipt?>> read,
        IProgress<RemoteUpdateReceipt> progress, TimeSpan interval, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receipt = await read(cancellationToken).ConfigureAwait(false);
            if (receipt is not null)
            {
                if (receipt.RequestId != requestId) throw new InvalidDataException("The host returned a different update request.");
                progress.Report(receipt);
                if (receipt.State is RemoteUpdateState.Succeeded or RemoteUpdateState.Failed or RemoteUpdateState.Canceled or RemoteUpdateState.Ready)
                    return receipt;
            }
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }
}
