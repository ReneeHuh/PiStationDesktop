using Microsoft.AspNetCore.SignalR.Client;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

public sealed partial class EnvironmentClient
{
    public async Task<BrowserAutomationSession> OpenBrowserAutomationAsync(OpenBrowserAutomationRequest request, CancellationToken cancellationToken = default)
    {
        var descriptor = EnsureConnected();
        if (!descriptor.Capabilities.Contains("browser.automation", StringComparer.Ordinal))
            throw new NotSupportedException("Browser automation requires an updated host and permission to operate it.");
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        var connection = _supervisor.Connection;
        var connectionId = connection.ConnectionId;
        ConnectionStateChanged += OnConnectionChanged;
        try
        {
            EnsureConnected();
            var lease = await connection.InvokeAsync<BrowserAutomationLease>("OpenBrowserAutomation", request, lifetime.Token).ConfigureAwait(false);
            return new BrowserAutomationSession(
                token => connection.InvokeAsync<BrowserAutomationPoll>("PollBrowserAutomation", lease.Id, token),
                (id, result, token) => connection.InvokeAsync("CompleteBrowserAutomation", lease.Id, id, result, token),
                async () =>
                {
                    ConnectionStateChanged -= OnConnectionChanged;
                    lifetime.Dispose();
                    if (ConnectionState == EnvironmentConnectionState.Connected && connection.ConnectionId == connectionId)
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        try { await connection.InvokeAsync("CloseBrowserAutomation", lease.Id, timeout.Token).ConfigureAwait(false); }
                        catch (Exception) { /* Host connection/heartbeat expiry also releases the lease. */ }
                    }
                }, lifetime.Token);
        }
        catch { ConnectionStateChanged -= OnConnectionChanged; lifetime.Dispose(); throw; }

        void OnConnectionChanged(object? sender, ConnectionStateChangedEventArgs args)
        {
            if (args.State == EnvironmentConnectionState.Connected) return;
            try { lifetime.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }
}
