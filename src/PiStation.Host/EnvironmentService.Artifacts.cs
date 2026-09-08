using PiStation.Host.Files;
using PiStation.Protocol.Models;

namespace PiStation.Host;

public sealed partial class EnvironmentService
{
    public async Task<ReadArtifactFileResult> ReadArtifactFileAsync(ReadArtifactFileRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Authorization remains at the environment boundary; workspace writes keep their existing confinement.
        await new Workspaces.ThreadWorkspaceResolver(_database).ResolveAsync(request.Target.ProjectId, request.Target.ThreadId, token).ConfigureAwait(false);
        return await ArtifactFileService.ReadAsync(request.AbsolutePath, token).ConfigureAwait(false);
    }
}
