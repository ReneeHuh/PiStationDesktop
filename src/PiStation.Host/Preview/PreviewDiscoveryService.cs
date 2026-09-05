using PiStation.Host.Errors;
using PiStation.Host.Persistence;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Models;

namespace PiStation.Host.Preview;

public sealed class PreviewDiscoveryService : IDisposable
{
    private readonly HostDatabase _database;
    private readonly PreviewPortScanner _scanner;

    public PreviewDiscoveryService(HostDatabase database, PreviewPortScanner? scanner = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _scanner = scanner ?? new PreviewPortScanner();
    }

    public async Task<DiscoverProjectPreviewServersResult> DiscoverAsync(
        DiscoverProjectPreviewServersRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ProjectId.Value))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.PreviewDiscoveryInvalid,
                "Preview discovery requires a project ID.");
        }

        if (await _database.GetProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ProjectNotFound,
                $"Project '{request.ProjectId}' was not found.");
        }

        var (servers, isTruncated) = await _scanner.ScanAsync(cancellationToken).ConfigureAwait(false);
        return new DiscoverProjectPreviewServersResult(
            request.ProjectId,
            DateTimeOffset.UtcNow,
            servers,
            isTruncated);
    }

    public void Dispose() => _scanner.Dispose();
}
