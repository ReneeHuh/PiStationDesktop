using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host;

public sealed partial class EnvironmentService
{
    public Task<PiAutomationSettings> GetPiAutomationSettingsAsync(CancellationToken token = default) => _database.GetPiAutomationSettingsAsync(token);
    public Task<PiAutomationSettings> SavePiAutomationSettingsAsync(PiAutomationSettings settings, CancellationToken token = default) => _database.SavePiAutomationSettingsAsync(settings, token);
    public async Task<PiAutomationStatus> GetPiAutomationStatusAsync(ThreadId threadId, CancellationToken token = default)
    {
        var saved = await _database.GetPiAutomationSettingsAsync(token).ConfigureAwait(false);
        return _threads.TryGetController(threadId, out var controller) && controller is not null ? controller.AutomationStatus(saved) :
            new(saved, null, null, null, "Runtime not loaded. Saved preferences apply when it starts.");
    }
    public async Task<PiAutomationStatus> ApplyPiAutomationAsync(ThreadId threadId, CancellationToken token = default) =>
        await (await _threads.GetAsync(threadId, token).ConfigureAwait(false)).ApplyAutomationWhileIdleAsync(token).ConfigureAwait(false);
}
