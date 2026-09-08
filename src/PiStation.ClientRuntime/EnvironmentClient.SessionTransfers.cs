using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

public sealed partial class EnvironmentClient
{
    public async Task<ThreadDescriptor> ImportPiSessionFileAsync(PiSessionImportFile import, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(import);
        var descriptor = EnsureConnected();
        var thread = await RunSessionTransferAsync((http, token) => PiSessionFileTransfer.ImportAsync(http, import, progress, token), cancellationToken).ConfigureAwait(false);
        if (thread.ThreadId.Value != import.Request.OperationId.ToString("N")) throw new InvalidDataException("The host returned a different session import identity.");
        ApplyThreadDescriptor(descriptor, thread, import.Request.ProjectId);
        return ThreadMetadata.GetCurrent(thread.ThreadId) ?? thread;
    }

    public Task<PiSessionExportResult> DownloadPiSessionAsync(ThreadId threadId, string destinationPath, PiSessionExportFormat format,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
        RunSessionTransferAsync((http, token) => PiSessionFileTransfer.DownloadAsync(http, threadId, destinationPath, format, progress, token), cancellationToken);

    private async Task<T> RunSessionTransferAsync<T>(Func<HttpClient, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        var descriptor = EnsureConnected();
        if (!descriptor.Capabilities.Contains("session.transfer", StringComparer.Ordinal))
            throw new NotSupportedException("Session transfers require an updated host and permission to operate it.");
        var options = _options;
        using var http = new HttpClient(RemoteTransport.CreateHandler(options.CertificateFingerprint))
            { BaseAddress = new Uri(options.HubAddress, "/"), Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new("Bearer", options.BearerCredential);
        http.DefaultRequestHeaders.Add("X-PiStation-Environment-Id", descriptor.EnvironmentId.Value);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        lifetime.CancelAfter(TimeSpan.FromMinutes(15));
        ConnectionStateChanged += OnConnectionChanged;
        try
        {
            EnsureConnected();
            return await action(http, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            throw new IOException("The session transfer was interrupted. Reconnect and retry to recover the saved import or download the export again.", error);
        }
        finally { ConnectionStateChanged -= OnConnectionChanged; }

        void OnConnectionChanged(object? sender, ConnectionStateChangedEventArgs args)
        {
            if (args.State == EnvironmentConnectionState.Connected) return;
            try { lifetime.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }
}
