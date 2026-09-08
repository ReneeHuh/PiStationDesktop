using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

public sealed partial class EnvironmentClient
{
    public async Task<string> GetAttachmentFileAsync(DraftAttachment attachment, string cacheRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var descriptor = EnsureConnected();
        if (attachment.EnvironmentId != descriptor.EnvironmentId)
            throw new InvalidOperationException("This attachment belongs to a different environment.");
        if (!descriptor.Capabilities.Contains("attachment.download", StringComparer.Ordinal))
            throw new NotSupportedException("Update the host to enable attachment downloads.");
        var options = _options;
        // Use the same certificate pin and credentials as the connection, with no redirects.
        using var http = new HttpClient(RemoteTransport.CreateHandler(options.CertificateFingerprint))
            { BaseAddress = new Uri(options.HubAddress, "/"), Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new("Bearer", options.BearerCredential);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        lifetime.CancelAfter(TimeSpan.FromMinutes(5));
        ConnectionStateChanged += OnConnectionChanged;
        try
        {
            EnsureConnected();
            return await AttachmentFileCache.GetFileAsync(http, attachment, cacheRoot, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            throw new IOException("The attachment download was interrupted. Reconnect and try again.");
        }
        finally { ConnectionStateChanged -= OnConnectionChanged; }

        void OnConnectionChanged(object? sender, ConnectionStateChangedEventArgs args)
        {
            if (args.State == EnvironmentConnectionState.Connected) return;
            try { lifetime.Cancel(); }
            catch (ObjectDisposedException) { /* A connection notification may already be in flight when unsubscribing. */ }
        }
    }
}
