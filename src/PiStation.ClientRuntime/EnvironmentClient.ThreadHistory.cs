using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

public sealed partial class EnvironmentClient
{
    public Task<ThreadHistoryPage> ReadThreadHistoryAsync(ReadThreadHistoryRequest request, CancellationToken token = default) =>
        InvokeAsync<ThreadHistoryPage>("ReadThreadHistory", request, token);
}
