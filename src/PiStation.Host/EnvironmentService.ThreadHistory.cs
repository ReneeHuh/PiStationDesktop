using PiStation.Host.Threads;
using PiStation.Protocol.Models;

namespace PiStation.Host;

public sealed partial class EnvironmentService
{
    public async Task<ThreadHistoryPage> ReadThreadHistoryAsync(ReadThreadHistoryRequest request, CancellationToken token = default)
    {
        var controller = await _threads.GetAsync(request.ThreadId, token).ConfigureAwait(false);
        await controller.HydratePersistedSessionAsync(token).ConfigureAwait(false);
        return ThreadHistoryWindow.Read(controller.Journal.Projection, request);
    }
}
