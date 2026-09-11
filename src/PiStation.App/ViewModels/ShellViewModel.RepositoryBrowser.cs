using PiStation.ClientRuntime;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    internal Func<IEnvironmentClient> CaptureRepositoryBrowserConnection()
    {
        var client = RequireClient();
        var identity = client.Descriptor?.EnvironmentId;
        return () => IsConnected && ReferenceEquals(_client, client) && client.Descriptor?.EnvironmentId == identity
            ? client : throw new InvalidOperationException("The browsing connection changed. Close this browser and reopen it in the intended environment.");
    }

    internal async Task<SourceControlOperationResult> CloneBrowsedRepositoryAsync(Func<IEnvironmentClient> connection,
        HostedRepositoryChoice repository, string destination, CancellationToken token)
    {
        if (!CanOperate) throw new InvalidOperationException("Cloning requires an operate connection.");
        var result = await connection().CloneHostedRepositoryAsync(new(repository.CloneUrl, destination.Trim()), token).ConfigureAwait(false);
        _ = connection();
        RunOnUiThread(() => Settings.Status = result.Message);
        await LoadProjectsAsync(token).ConfigureAwait(false);
        return result;
    }
}
