using Microsoft.AspNetCore.SignalR.Client;
using PiStation.Protocol;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

public sealed partial class EnvironmentClient
{
    private readonly SemaphoreSlim _browserChannelGate = new(1, 1);
    private BrowserControlChannel? _browserChannel;

    public async Task<BrowserAutomationSession> OpenBrowserAutomationAsync(OpenBrowserAutomationRequest request, CancellationToken cancellationToken = default)
    {
        var descriptor = EnsureConnected();
        if (!descriptor.Capabilities.Contains("browser.automation", StringComparer.Ordinal))
            throw new NotSupportedException("Browser automation requires an updated host and permission to operate it.");
        var channel = await GetBrowserChannelAsync(descriptor, cancellationToken).ConfigureAwait(false);
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, channel.Stopping);
        var connection = channel.Connection;
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            var lease = await connection.InvokeAsync<BrowserAutomationLease>("OpenBrowserAutomation", request, lifetime.Token).ConfigureAwait(false);
            return new BrowserAutomationSession(
                token => connection.InvokeAsync<BrowserAutomationPoll>("PollBrowserAutomation", lease.Id, token),
                (id, result, token) => connection.InvokeAsync("CompleteBrowserAutomation", lease.Id, id, result, token),
                async () =>
                {
                    lifetime.Dispose();
                    if (channel.IsActive)
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        try { await connection.InvokeAsync("CloseBrowserAutomation", lease.Id, timeout.Token).ConfigureAwait(false); }
                        catch (Exception) { /* Host connection/heartbeat expiry also releases the lease. */ }
                    }
                }, lifetime.Token);
        }
        catch { lifetime.Dispose(); throw; }
    }

    private async Task<BrowserControlChannel> GetBrowserChannelAsync(EnvironmentDescriptor expected, CancellationToken cancellationToken)
    {
        await _browserChannelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            var parentId = _supervisor.Connection.ConnectionId;
            if (_browserChannel is { IsActive: true } existing && existing.ParentId == parentId) return existing;
            if (_browserChannel is { } previous) await previous.DisposeAsync().ConfigureAwait(false);
            // One browser-only connection per environment window. Ordinary SignalR calls
            // serialize on a connection; workspace startup must not block browser safety
            // heartbeats, cancellation, completion or permission changes.
            var channel = new BrowserControlChannel(ConnectionSupervisor.CreateConnection(_options), parentId, _stopping.Token);
            _browserChannel = channel;
            try
            {
                using var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, channel.Stopping);
                await channel.Connection.StartAsync(opening.Token).ConfigureAwait(false);
                var descriptor = await channel.Connection.InvokeAsync<EnvironmentDescriptor>("GetEnvironmentDescriptor", opening.Token).ConfigureAwait(false);
                if (descriptor.EnvironmentId != expected.EnvironmentId || ProtocolVersion.Current < descriptor.MinimumProtocolVersion ||
                    ProtocolVersion.Current > descriptor.MaximumProtocolVersion || !descriptor.Capabilities.Contains("browser.automation", StringComparer.Ordinal))
                    throw new InvalidOperationException("The browser connection no longer matches this environment and its permissions.");
                EnsureConnected();
                if (_supervisor.Connection.ConnectionId != parentId) throw new IOException("The environment connection changed while opening browser access.");
                opening.Token.ThrowIfCancellationRequested();
                return channel;
            }
            catch
            {
                _browserChannel = null;
                await channel.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { _browserChannelGate.Release(); }
    }

    private void StopBrowserChannel()
    {
        if (_browserChannel is not { } channel) return;
        channel.Cancel();
        _ = CloseBrowserChannelAsync(channel);
    }

    private async Task CloseBrowserChannelAsync(BrowserControlChannel? expected = null)
    {
        await _browserChannelGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_browserChannel is not { } channel || expected is not null && !ReferenceEquals(channel, expected)) return;
            _browserChannel = null;
            await channel.DisposeAsync().ConfigureAwait(false);
        }
        finally { _browserChannelGate.Release(); }
    }

    private sealed class BrowserControlChannel : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stopping;
        public HubConnection Connection { get; }
        public string? ParentId { get; }
        public CancellationToken Stopping { get; }
        public bool IsActive => !Stopping.IsCancellationRequested && Connection.State == HubConnectionState.Connected;
        public BrowserControlChannel(HubConnection connection, string? parentId, CancellationToken stopping)
        {
            Connection = connection;
            ParentId = parentId;
            _stopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            Stopping = _stopping.Token;
            connection.Closed += _ => { Cancel(); return Task.CompletedTask; };
        }
        public void Cancel()
        {
            try { _stopping.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync()
        {
            Cancel();
            try { await Connection.DisposeAsync().ConfigureAwait(false); }
            finally { _stopping.Dispose(); }
        }
    }
}
