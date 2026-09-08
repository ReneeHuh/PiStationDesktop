using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class RemoteUpdateMonitorTests
{
    [Fact]
    public async Task LostConnectionIsFollowedToTheSameRequestsFinalReceipt()
    {
        var id = Guid.NewGuid();
        var pending = new RemoteUpdateReceipt(id, RemoteUpdateState.WaitingForIdle);
        var finished = pending with { State = RemoteUpdateState.Succeeded };
        var replies = new Queue<RemoteUpdateReceipt?>([pending, null, pending with { State = RemoteUpdateState.Restarting }, null, finished]);
        var progress = new List<RemoteUpdateReceipt>();
        var result = await RemoteUpdateMonitor.FollowAsync(id, _ => Task.FromResult(replies.Dequeue()),
            new ImmediateProgress(progress.Add), TimeSpan.FromMilliseconds(1), CancellationToken.None);
        Assert.Equal(finished, result);
        Assert.Equal(3, progress.Count);
        Assert.All(progress, receipt => Assert.Equal(id, receipt.RequestId));
    }

    [Fact]
    public async Task StoppingObservationDoesNotIssueAnActivationOrCancellation()
    {
        using var stopping = new CancellationTokenSource();
        var id = Guid.NewGuid();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RemoteUpdateMonitor.FollowAsync(id, _ =>
        {
            stopping.Cancel();
            return Task.FromResult<RemoteUpdateReceipt?>(null);
        }, new ImmediateProgress(_ => { }), TimeSpan.FromSeconds(1), stopping.Token));
    }

    [Fact]
    public async Task ReceiptForAnotherRequestIsRejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => RemoteUpdateMonitor.FollowAsync(Guid.NewGuid(),
            _ => Task.FromResult<RemoteUpdateReceipt?>(new(Guid.NewGuid(), RemoteUpdateState.Succeeded)),
            new ImmediateProgress(_ => { }), TimeSpan.FromMilliseconds(1), CancellationToken.None));
    }

    private sealed class ImmediateProgress(Action<RemoteUpdateReceipt> report) : IProgress<RemoteUpdateReceipt>
    {
        public void Report(RemoteUpdateReceipt value) => report(value);
    }
}
