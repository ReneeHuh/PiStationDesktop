using PiStation.Host.Persistence;
using PiStation.Host.SourceControl;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.Host.Tests;

public sealed class HostingOperationRunnerTests
{
    [Fact]
    public async Task ReplayedWriteReturnsDurableResultWithoutExecutingAgain()
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions();
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var runner = new HostingOperationRunner(database);
        var id = CommandId.New();
        var calls = 0;
        Task<SourceControlOperationResult> Write(CancellationToken token)
        {
            Assert.False(token.CanBeCanceled);
            calls++;
            return Task.FromResult(new SourceControlOperationResult(true, "Comment created"));
        }
        var first = await runner.RunAsync(id, "comment", "same body", Write);
        var restarted = new HostDatabase(options);
        await restarted.InitializeAsync();
        var replay = await new HostingOperationRunner(restarted).RunAsync(id, "comment", "same body", Write);
        Assert.Equal(1, calls);
        Assert.Equal(first, replay);
        Assert.Equal(CommandReceiptState.Completed, replay.State);
        await Assert.ThrowsAsync<PiStation.Host.Errors.HostOperationException>(() => runner.RunAsync(id, "comment", "different body", Write));
    }

    [Fact]
    public async Task FailureAfterAcceptanceIsUncertainAndCannotBeReplayed()
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions();
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var runner = new HostingOperationRunner(database);
        var id = CommandId.New();
        var calls = 0;
        Task<SourceControlOperationResult> Write(CancellationToken token)
        {
            calls++;
            throw new IOException("Connection closed after remote write");
        }
        var first = await runner.RunAsync(id, "publish", "body", Write);
        var replay = await runner.RunAsync(id, "publish", "body", Write);
        Assert.Equal(1, calls);
        Assert.Equal(CommandReceiptState.DispatchUncertain, first.State);
        Assert.Equal(first, replay);
        var interrupted = CommandId.New();
        await database.AcquireHostingOperationAsync(interrupted, "comment", "hash");
        var restarted = new HostDatabase(options);
        await restarted.InitializeAsync();
        Assert.Equal(CommandReceiptState.DispatchUncertain,
            (await restarted.ListHostingOperationsAsync()).Single(item => item.OperationId == interrupted).State);
    }
}
