using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using PiStation.Host.Security;
using PiStation.Protocol.Models;

namespace PiStation.Host.Preview;

public sealed class PreviewLeaseRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<object, int> _controlPorts = new();
    private readonly SemaphoreSlim _connections = new(64, 64);
    private readonly object _gate = new();
    private bool _disposed;
    public void RegisterControlPort(object owner, int port) => _controlPorts[owner] = port;
    public void UnregisterControlPort(object owner) => _controlPorts.TryRemove(owner, out _);

    public PreviewLease Open(OpenPreviewRequest request, string principal, string connectionId, CancellationToken disconnected)
    {
        ArgumentNullException.ThrowIfNull(request);
        var address = request.Address;
        if (!address.IsAbsoluteUri || address.Scheme is not ("http" or "https") || !address.IsLoopback ||
            address.UserInfo.Length != 0 || address.AbsoluteUri.Length > PreviewDiscoveryDefaults.MaximumUrlLength ||
            _controlPorts.Values.Contains(address.Port))
            throw new ArgumentException("Select a host-loopback development server. Management listeners cannot be previewed.", nameof(request));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Prune();
            if (_leases.Values.Count(l => l.Principal == principal) >= 12) throw new InvalidOperationException("Close a preview before opening another.");
            var descriptor = new PreviewLease(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), address, DateTimeOffset.UtcNow.AddMinutes(5));
            _leases[descriptor.Id] = new(descriptor, principal, connectionId, disconnected);
            return descriptor;
        }
    }

    public PreviewLease Renew(string id, string principal, string connectionId)
    {
        lock (_gate)
        {
            Prune();
            if (!_leases.TryGetValue(id, out var lease) || lease.Principal != principal || lease.Connection != connectionId)
                throw new UnauthorizedAccessException("The preview lease has ended.");
            lease.Stopping.CancelAfter(TimeSpan.FromMinutes(5));
            return lease.Descriptor = lease.Descriptor with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };
        }
    }

    public void Close(string id, string principal)
    {
        lock (_gate)
            if (_leases.TryGetValue(id, out var lease) && lease.Principal == principal && _leases.TryRemove(id, out _)) lease.Dispose();
    }

    public async Task TunnelAsync(HttpContext context)
    {
        var id = context.Request.RouteValues["lease"]?.ToString() ?? string.Empty;
        var principal = Principal(context);
        Lease? lease;
        lock (_gate)
        {
            Prune();
            _leases.TryGetValue(id, out lease);
            if (lease?.Principal != principal) lease = null;
        }
        if (lease is null || !context.WebSockets.IsWebSocketRequest)
        { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        if (!await _connections.WaitAsync(0, context.RequestAborted).ConfigureAwait(false))
        { context.Response.StatusCode = StatusCodes.Status429TooManyRequests; return; }
        try
        {
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lease.Token);
            using var tcp = new TcpClient();
            var address = lease.Descriptor.Address;
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(10));
                await tcp.ConnectAsync(address.HostNameType == UriHostNameType.IPv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, address.Port, connect.Token).ConfigureAwait(false);
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var stream = tcp.GetStream();
            var upload = CopyToTcpAsync(socket, stream, stopping.Token);
            var download = CopyToWebSocketAsync(stream, socket, stopping.Token);
            await Task.WhenAny(upload, download).ConfigureAwait(false);
            // A normal upstream EOF must flush data and its close frame before tearing down TCP.
            if (download.IsCompletedSuccessfully)
            {
                try { await upload.WaitAsync(TimeSpan.FromSeconds(5), stopping.Token).ConfigureAwait(false); }
                catch (Exception) { /* An unresponsive peer is bounded by the close deadline. */ }
            }
            await stopping.CancelAsync().ConfigureAwait(false);
            if (socket.State != WebSocketState.Closed) socket.Abort();
            tcp.Close();
            try { await Task.WhenAll(upload, download).ConfigureAwait(false); }
            catch (Exception) when (stopping.IsCancellationRequested) { }
        }
        finally { _connections.Release(); }
    }

    internal static string Principal(HttpContext context) =>
        context.Items[RemoteAuthorizationFilter.AuthorizationItem] is RemoteAuthorization authorization
            ? authorization.Device.DeviceId : "host-owner";

    private static async Task CopyToTcpAsync(WebSocket socket, Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).AsTask().WaitAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            if (read.MessageType == WebSocketMessageType.Close) return;
            if (read.MessageType != WebSocketMessageType.Binary) throw new InvalidDataException("Preview channels carry binary data.");
            await stream.WriteAsync(buffer.AsMemory(0, read.Count), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CopyToWebSocketAsync(Stream stream, WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).AsTask().WaitAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "upstream closed", cancellationToken).ConfigureAwait(false);
                return;
            }
            await socket.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Prune()
    {
        foreach (var pair in _leases)
            if ((pair.Value.Stopping.IsCancellationRequested || pair.Value.Descriptor.ExpiresAt <= DateTimeOffset.UtcNow) &&
                _leases.TryRemove(pair.Key, out var expired)) expired.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var lease in _leases.Values) lease.Dispose();
            _leases.Clear();
        }
    }

    private sealed class Lease : IDisposable
    {
        public Lease(PreviewLease descriptor, string principal, string connection, CancellationToken disconnected)
        {
            Descriptor = descriptor; Principal = principal; Connection = connection;
            Stopping = CancellationTokenSource.CreateLinkedTokenSource(disconnected);
            Token = Stopping.Token;
            Stopping.CancelAfter(TimeSpan.FromMinutes(5));
        }
        public PreviewLease Descriptor { get; set; }
        public string Principal { get; }
        public string Connection { get; }
        public CancellationTokenSource Stopping { get; }
        public CancellationToken Token { get; }
        public void Dispose() { Stopping.Cancel(); Stopping.Dispose(); }
    }
}
