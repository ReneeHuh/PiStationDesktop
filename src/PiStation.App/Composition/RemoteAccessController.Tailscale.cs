using System.Net;
using PiStation.ClientRuntime;
using PiStation.Host.Hosting;

namespace PiStation.App.Composition;

internal sealed partial class RemoteAccessController
{
    private TailscaleServeSession? _tailscaleServe;
    public bool UsesTailscaleServe => _tailscaleServe is not null;

    public async Task StartTailscaleServeAsync(int port, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_environment is null || _access is null) throw new InvalidOperationException("The local environment is not available.");
            if (_listener is not null) throw new InvalidOperationException("Stop sharing before changing the connection.");
            if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "Choose a port from 1024 through 65535.");
            var network = await TailscaleDiscovery.DiscoverAsync(cancellationToken);
            if (network is not { Running: true, Self: { } self }) throw new InvalidOperationException(network.Message);
            _certificate ??= RemoteHostCertificate.LoadOrCreate(dataRoot);
            var listener = await RemoteEnvironmentHost.StartAsync(_environment, _access, IPAddress.Loopback, 0, _certificate,
                diagnosticLog: _diagnostics.Write, cancellationToken: cancellationToken);
            TailscaleServeSession? serve = null;
            try
            {
                serve = await TailscaleServeSession.StartAsync(self, listener.Address.Port, port, Fingerprint,
                    _environment.GetDescriptor().EnvironmentId, cancellationToken);
                listener.AdvertiseForwardedAddress(serve.Address);
                SaveSettings(new(true, self.Address, port, TailscaleServe: true));
                _listener = listener;
                _tailscaleServe = serve;
                StartupError = null;
            }
            catch
            {
                await DisposeSharingAsync(serve, listener);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task VerifyTailscaleServeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_tailscaleServe is not { IsRunning: true } serve || _environment is null)
                throw new InvalidOperationException("Start Tailscale sharing first.");
            await RemoteEndpointProbe.VerifyAsync(serve.Address, Fingerprint, _environment.GetDescriptor().EnvironmentId, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private static async Task DisposeSharingAsync(TailscaleServeSession? serve, RemoteEnvironmentHost? listener)
    {
        try { if (serve is not null) await serve.DisposeAsync(); }
        finally { if (listener is not null) await listener.DisposeAsync(); }
    }
}
