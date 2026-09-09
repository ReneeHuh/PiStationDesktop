using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiStation.ClientRuntime;
using PiStation.Host;
using PiStation.Host.Hosting;
using PiStation.Host.Security;
using PiStation.Protocol.Models;

namespace PiStation.App.Composition;

internal sealed partial class RemoteAccessController(string dataRoot) : IAsyncDisposable
{
    private EnvironmentService? _environment;
    private RemoteAccessStore? _access;
    private RemoteEnvironmentHost? _listener;
    private X509Certificate2? _certificate;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private readonly RemoteDiagnosticLog _diagnostics = new(Path.Combine(dataRoot, "remote-access.log"));

    public RemoteConnectionStore Connections { get; } = new(Path.Combine(dataRoot, "remote-connections.protected"));
    public PiStation.ClientRuntime.Ssh.SshConnectionStore SshConnections { get; } = new(Path.Combine(dataRoot, "ssh-connections.protected"));
    public bool IsSharing => _listener is not null;
    public bool NeedsAddress => _tailscaleServe is not null ? !_tailscaleServe.IsRunning
        : _listener is not null && !GetNetworkAddresses().Contains(_listener.Address.Host);
    public bool CanHost => _environment is not null && _access is not null;
    public string Address => _tailscaleServe?.Address.AbsoluteUri ?? _listener?.Address.AbsoluteUri ?? string.Empty;
    public string Fingerprint => _certificate?.GetCertHashString(HashAlgorithmName.SHA256) ?? string.Empty;
    public string? StartupError { get; private set; }
    public RemoteAccessStore? Access => _access;
    public bool AllowsRemoteUpdates
    {
        get => _environment?.Updates.Enabled == true;
        set
        {
            if (_environment is null) throw new InvalidOperationException("The local host is unavailable.");
            _environment.Updates.Enabled = value;
        }
    }

    public static IReadOnlyList<string> GetNetworkAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
        .Select(a => a.ToString()).Distinct().ToArray();

    public async Task AttachAsync(EnvironmentService environment)
    {
        try
        {
            RemoteListenerSettings? settings;
            await _gate.WaitAsync();
            try
            {
                if (_disposed) return;
                _environment = environment;
                _access = new RemoteAccessStore(Path.Combine(dataRoot, "remote-access.db"));
                var settingsPath = Path.Combine(dataRoot, "remote-listener.protected");
                if (!File.Exists(settingsPath)) return;
                settings = JsonSerializer.Deserialize(WindowsProtectedStorage.Read(settingsPath), RemoteSettingsJsonContext.Default.RemoteListenerSettings);
            }
            finally { _gate.Release(); }
            if (settings is { Enabled: true })
            {
                if (settings.TailscaleServe) await StartTailscaleServeAsync(settings.Port);
                else await StartAsync(settings.Address, settings.Port);
            }
        }
        catch (Exception exception)
        {
            _diagnostics.Write($"remote sharing startup failed ({exception.GetType().Name})");
            StartupError = $"Remote sharing could not start: {exception.Message}";
        }
    }

    public async Task StartAsync(string address, int port)
    {
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_environment is null || _access is null) throw new InvalidOperationException("The local environment is not available.");
            if (_listener is not null) throw new InvalidOperationException("Stop sharing before changing the address.");
            if (!IPAddress.TryParse(address, out var ip) || !GetNetworkAddresses().Contains(address))
                throw new ArgumentException("Choose an active local IPv4 network address.", nameof(address));
            if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "Choose a port from 1024 through 65535.");
            _certificate ??= RemoteHostCertificate.LoadOrCreate(dataRoot);
            var listener = await RemoteEnvironmentHost.StartAsync(
                _environment, _access, ip, port, _certificate, cancellationToken: default,
                diagnosticLog: _diagnostics.Write);
            try { SaveSettings(new(true, address, port)); }
            catch (Exception exception)
            {
                _diagnostics.Write($"remote sharing settings save failed ({exception.GetType().Name})");
                await listener.DisposeAsync();
                throw;
            }
            _listener = listener;
            StartupError = null;
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var listener = _listener;
            var serve = _tailscaleServe;
            // Clear the observable state before awaiting anything: callers must never see
            // sharing enabled after the listener has been asked to stop.
            _listener = null;
            _tailscaleServe = null;
            try
            {
                await DesktopLifecycle.StopSharingAsync(
                    () => DisposeSharingAsync(serve, listener),
                    () => { SaveSettings(new(false, string.Empty, 52740)); _access?.ClearInvitations(); });
            }
            catch (Exception exception)
            {
                _diagnostics.Write($"remote sharing stop failed ({exception.GetType().Name})");
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<CreatedRemotePairing> CreateInvitationAsync(RemoteAccessLevel level, TimeSpan lifetime, string? label,
        bool useTailscaleDns = false, CancellationToken cancellationToken = default)
    {
        var tailscale = useTailscaleDns ? await TailscaleDiscovery.DiscoverAsync(cancellationToken) : null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is null || _access is null || NeedsAddress) throw new InvalidOperationException("Start sharing on an available connection first.");
            var address = _tailscaleServe?.Address ?? _listener.Address;
            if (useTailscaleDns && _tailscaleServe is null)
            {
                if (tailscale is not { Running: true, Self: { DnsName: not null } self } || self.Address != address.Host)
                    throw new InvalidOperationException("Refresh Tailscale and share on its adapter, or turn off MagicDNS to use the IP address.");
                address = self.GetHttpsAddress(address.Port);
            }
            var issued = _access.IssueInvitation(level, lifetime, label);
            return new(issued.Invitation, new RemoteInvitation(address, Fingerprint, issued.Token).Encode());
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            try { await DisposeSharingAsync(_tailscaleServe, _listener); }
            finally
            {
                _access?.Dispose();
                _certificate?.Dispose();
                _access = null;
                _certificate = null;
                _listener = null;
                _tailscaleServe = null;
                _environment = null;
            }
        }
        finally { _gate.Release(); }
    }

    private void SaveSettings(RemoteListenerSettings settings) => WindowsProtectedStorage.Write(
        Path.Combine(dataRoot, "remote-listener.protected"), JsonSerializer.SerializeToUtf8Bytes(settings, RemoteSettingsJsonContext.Default.RemoteListenerSettings));
}

internal sealed record RemoteListenerSettings(bool Enabled, string Address, int Port, bool TailscaleServe = false);
[JsonSerializable(typeof(RemoteListenerSettings))]
internal sealed partial class RemoteSettingsJsonContext : JsonSerializerContext;
