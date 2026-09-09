using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host;

public sealed partial class EnvironmentService
{
    public Task<DiagnosticsDownload> DownloadDiagnosticsAsync(CancellationToken cancellationToken = default) =>
        _diagnostics.DownloadAsync(cancellationToken);

    public async Task<byte[]?> ReadProjectIconAsync(ProjectId projectId, CancellationToken cancellationToken = default)
    {
        var project = await _database.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException("The project was not found.");
        if (string.IsNullOrWhiteSpace(project.Icon) || project.Icon.StartsWith("emoji:", StringComparison.Ordinal)) return null;
        try { return await Projects.ProjectIconStorage.ReadAsync(project.Icon, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public async Task<HostPathPage> BrowseHostPathAsync(BrowseHostPathRequest request, CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _catalogStopping.Token);
        return await Task.Run(() => Files.HostPathBrowser.Browse(request, lifetime.Token), lifetime.Token).ConfigureAwait(false);
    }
}
