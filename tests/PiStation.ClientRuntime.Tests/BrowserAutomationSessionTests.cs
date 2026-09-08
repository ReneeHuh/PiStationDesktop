using System.Text.Json;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class BrowserAutomationSessionTests
{
    [Fact]
    public async Task HostClockSkewDoesNotCancelAJustReceivedRequest()
    {
        var request = new BrowserAutomationRequest(Guid.NewGuid().ToString(), "status", JsonSerializer.SerializeToElement(new { }), DateTimeOffset.UtcNow.AddHours(-2));
        var polls = 0;
        var completed = false;
        await using var session = new BrowserAutomationSession(
            _ => Task.FromResult(new BrowserAutomationPoll(Interlocked.Increment(ref polls) == 1 ? request : null, request.Id)),
            (_, _, _) => { completed = true; return Task.CompletedTask; }, () => Task.CompletedTask, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        BrowserAutomationWork? work;
        while ((work = session.TakeNext()) is null) await Task.Delay(10, timeout.Token);
        Assert.False(work.CancellationToken.IsCancellationRequested);
        await session.CompleteAsync(work, new(true));
        Assert.True(completed);
    }

    [Fact]
    public async Task LostHeartbeatCancelsTheWorkAndNeverRedeliversIt()
    {
        var request = new BrowserAutomationRequest(Guid.NewGuid().ToString(), "click", JsonSerializer.SerializeToElement(new { }), DateTimeOffset.UtcNow);
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var polls = 0;
        await using var session = new BrowserAutomationSession(async token =>
        {
            if (Interlocked.Increment(ref polls) == 1) return new(request, request.Id);
            await fail.Task.WaitAsync(token);
            throw new IOException("Connection lost");
        }, (_, _, _) => throw new InvalidOperationException("Cancelled work must not complete"), () => Task.CompletedTask, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        BrowserAutomationWork? work;
        while ((work = session.TakeNext()) is null) await Task.Delay(10, timeout.Token);
        fail.SetResult();
        while (!work.CancellationToken.IsCancellationRequested) await Task.Delay(10, timeout.Token);
        Assert.False(session.IsActive);
        Assert.Null(session.TakeNext());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.CompleteAsync(work, new(true)));
    }
}
