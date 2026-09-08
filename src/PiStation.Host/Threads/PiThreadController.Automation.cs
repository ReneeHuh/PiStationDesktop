using PiStation.PiRpc.Diagnostics;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;

namespace PiStation.Host.Threads;

public sealed partial class PiThreadController
{
    private volatile PiAutomationStatus? _automationStatus;

    internal PiAutomationStatus AutomationStatus(PiAutomationSettings saved)
    {
        var status = _automationStatus;
        return status is null ? new(saved, null, null, null, "Saved preferences have not been applied in this runtime.") :
            status with { Saved = saved, Message = status.Saved.Revision == saved.Revision ? status.Message :
                "New preferences are saved and pending. They apply before the next turn or runtime restart." };
    }

    internal async Task<PiAutomationStatus> ApplyAutomationWhileIdleAsync(CancellationToken token)
    {
        await EnsureReadyAsync(token).ConfigureAwait(false);
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Journal.Projection.RuntimeState != ThreadRuntimeState.Ready || ThreadSettlementPolicy.HasLiveWork(Journal.Projection))
                throw new InvalidOperationException("Finish the running turn, queued work and pending interactions before applying preferences.");
            await ApplyPiAutomationAsync(token).ConfigureAwait(false);
            return AutomationStatus(await _database.GetPiAutomationSettingsAsync(token).ConfigureAwait(false));
        }
        finally { _lifecycle.Release(); }
    }

    private async Task ApplyPiAutomationAsync(CancellationToken token)
    {
        await _database.PiAutomationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var saved = await _database.GetPiAutomationSettingsAsync(token).ConfigureAwait(false);
            if (_automationStatus?.AppliedRevision == saved.Revision) return;
            var connection = _process!.Connection;
            try
            {
                if (saved.AutoCompaction is { } compaction) await connection.SetAutoCompactionAsync(compaction, token).ConfigureAwait(false);
                if (saved.AutoRetry is { } retry) await connection.SetAutoRetryAsync(retry, token).ConfigureAwait(false);
                var state = await connection.GetStateAsync(token).ConfigureAwait(false);
                if (saved.AutoCompaction is { } expected && state.ReportedAutoCompactionEnabled != expected)
                    throw new PiRpcCommandException("set_auto_compaction", "Pi reports a different effective value. Check project settings overrides.");
                bool? verifiedRetry = null;
                if (_options.ManagementExtensionPath is not null)
                {
                    var effective = await connection.ManageAsync(new System.Text.Json.Nodes.JsonObject { ["action"] = "automation" }, token).ConfigureAwait(false);
                    verifiedRetry = effective.GetProperty("autoRetry").GetBoolean();
                    if (saved.AutoRetry is { } expectedRetry && verifiedRetry != expectedRetry)
                        throw new PiRpcCommandException("set_auto_retry", "Pi's effective retry settings differ from the saved override. Check project settings.");
                }
                _automationStatus = new(saved, saved.Revision, state.ReportedAutoCompactionEnabled, saved.AutoRetry,
                    verifiedRetry is null ? "Preferences acknowledged. Retry readback requires the desktop management extension." :
                    $"Preferences applied. Pi SDK effective retry readback: {verifiedRetry}. Compaction was verified with Pi runtime state.", verifiedRetry);
            }
            catch (Exception exception)
            {
                _automationStatus = new(saved, null, null, null,
                    $"Preferences were not fully verified; some changes may have applied. No task was submitted. {exception.Message}");
                throw;
            }
        }
        finally { _database.PiAutomationGate.Release(); }
    }
}
