using System.Security.Cryptography;
using System.Text;
using PiStation.Host.Errors;
using PiStation.Host.Persistence;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.Host.SourceControl;

public sealed class HostingOperationRunner(HostDatabase database)
{
    public async Task<SourceControlOperationResult> RunAsync(
        CommandId? operationId, string action, string body, Func<CancellationToken, Task<SourceControlOperationResult>> execute,
        CancellationToken cancellationToken = default)
    {
        if (operationId is not { } id) throw new HostOperationException(ProtocolErrorCodes.CommandConflict, "Hosting writes require an operation identity.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(action + "\n" + body)));
        var (operation, created) = await database.AcquireHostingOperationAsync(id, action, hash, cancellationToken).ConfigureAwait(false);
        if (!created) return operation.Result ?? new SourceControlOperationResult(false,
            $"{action} is {operation.State}. Refresh operation history before retrying; this request was not sent again.", OperationId: id, State: operation.State);
        SourceControlOperationResult result;
        try
        {
            // A disconnected client must not cancel a write already accepted by the host.
            result = (await execute(CancellationToken.None).ConfigureAwait(false)) with { OperationId = id };
        }
        catch (Exception exception)
        {
            result = new SourceControlOperationResult(false,
                $"The outcome of {action} could not be confirmed: {exception.Message} Inspect the provider before starting another operation.",
                OperationId: id, State: CommandReceiptState.DispatchUncertain);
        }
        await database.CompleteHostingOperationAsync(operation with { State = result.State, Result = result, UpdatedUtc = DateTimeOffset.UtcNow }, CancellationToken.None).ConfigureAwait(false);
        return result;
    }
}
