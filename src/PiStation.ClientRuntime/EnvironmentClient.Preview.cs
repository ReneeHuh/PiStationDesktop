using System.Net.WebSockets;
using Microsoft.AspNetCore.SignalR.Client;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

public sealed partial class EnvironmentClient
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, RemotePreviewProxy> _previews = new();

    public async Task<RemotePreviewProxy> OpenRemotePreviewAsync(OpenPreviewRequest request, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var channel = new PreviewChannel(this, request);
        var id = Guid.NewGuid();
        try
        {
            await channel.EnsureLeaseAsync(cancellationToken).ConfigureAwait(false);
            var proxy = await RemotePreviewProxy.StartAsync(request.Address, channel.ConnectAsync, async () =>
            {
                _previews.TryRemove(id, out _);
                await channel.DisposeAsync().ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            proxy.IsAvailable = () => !_disposed && ConnectionState == EnvironmentConnectionState.Connected;
            _previews[id] = proxy;
            return proxy;
        }
        catch { await channel.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private sealed class PreviewChannel : IAsyncDisposable
    {
        private readonly EnvironmentClient _client;
        private readonly OpenPreviewRequest _request;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _renew;
        private PreviewLease? _lease;
        private string? _connectionId;
        private bool _disposed;

        public PreviewChannel(EnvironmentClient client, OpenPreviewRequest request)
        {
            _client = client; _request = request;
            _renew = Task.Run(RenewAsync);
        }

        public async Task<PreviewLease> EnsureLeaseAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _client.EnsureConnected();
                var id = _client._supervisor.Connection.ConnectionId;
                if (_lease is null || _connectionId != id || _lease.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    _lease = await _client._supervisor.Connection.InvokeAsync<PreviewLease>("OpenPreview", _request, cancellationToken).ConfigureAwait(false);
                    _connectionId = id;
                }
                else if (_lease.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(4))
                    _lease = await _client._supervisor.Connection.InvokeAsync<PreviewLease>("RenewPreview", _lease.Id, cancellationToken).ConfigureAwait(false);
                return _lease;
            }
            finally { _gate.Release(); }
        }

        public async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
        {
            var lease = await EnsureLeaseAsync(cancellationToken).ConfigureAwait(false);
            var options = _client._options;
            var uri = new UriBuilder(options.HubAddress)
            {
                Scheme = options.HubAddress.Scheme == "https" ? "wss" : "ws",
                Path = "/previews/" + lease.Id + "/tunnel", Query = string.Empty, Fragment = string.Empty,
            }.Uri;
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + options.BearerCredential);
            if (options.CertificateFingerprint is { } pin)
                socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) => RemoteTransport.Matches(certificate, pin);
            try { await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false); return new PreviewWebSocketStream(socket); }
            catch { socket.Dispose(); throw; }
        }

        private async Task RenewAsync()
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(60), _stopping.Token).ConfigureAwait(false);
                    try { await EnsureLeaseAsync(_stopping.Token).ConfigureAwait(false); }
                    catch (Exception) when (!_stopping.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
            await _renew.ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                _disposed = true;
                if (_lease is not null && _client.ConnectionState == EnvironmentConnectionState.Connected)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await _client._supervisor.Connection.InvokeAsync("ClosePreview", _lease.Id, timeout.Token).ConfigureAwait(false); }
                    catch (Exception) { }
                }
            }
            finally { _gate.Release(); }
        }
    }
}
