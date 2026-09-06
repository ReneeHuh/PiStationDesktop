using PiStation.PiRpc.Discovery;
using PiStation.Protocol.Models;

namespace PiStation.Host;

public sealed partial class EnvironmentService
{
    private readonly SemaphoreSlim _runtimeConfigurationGate = new(1, 1);

    public async Task<PiRuntimeSetupResult> ConfigurePiRuntimeAsync(ConfigurePiRuntimeRequest request, CancellationToken cancellationToken = default)
    {
        await _runtimeConfigurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = string.IsNullOrWhiteSpace(request.ExecutablePath) ? null : request.ExecutablePath.Trim();
            var extensions = PiRuntimeSettingsStore.Validate(request.Extensions ?? _options.Extensions);
            var installation = await new PiLocator().LocateAsync(new PiLocatorOptions { ExplicitPiPath = path }, cancellationToken).ConfigureAwait(false);
            await PiRuntimeSettingsStore.SaveAsync(_options.CanonicalDataRoot, new(path, extensions), cancellationToken).ConfigureAwait(false);
            _options.PiInstallation = installation;
            _options.Extensions = extensions;
            return new PiRuntimeSetupResult(true, path, installation.PiVersion.ToString(),
                $"Pi {installation.PiVersion} is ready. Extension changes apply to new runtimes. Restart an idle thread to apply them there.", extensions);
        }
        catch (Exception exception) when (exception is PiDiscoveryException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new PiRuntimeSetupResult(false, request.ExecutablePath, null, exception.Message);
        }
        finally { _runtimeConfigurationGate.Release(); }
    }
}
