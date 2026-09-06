using PiStation.PiRpc.Discovery;
using PiStation.Protocol.Models;
using PiStation.Host.Errors;
using PiStation.Protocol.Errors;

namespace PiStation.Host;

public sealed partial class EnvironmentService
{
    private readonly SemaphoreSlim _runtimeConfigurationGate = new(1, 1);

    public async Task<PiResourcesSnapshot> ManagePiResourcesAsync(ManagePiResourcesRequest request, CancellationToken cancellationToken = default)
    {
        await _runtimeConfigurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var controller = await _threads.GetAsync(request.ThreadId, cancellationToken).ConfigureAwait(false);
            return await controller.ManageResourcesAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally { _runtimeConfigurationGate.Release(); }
    }

    public async Task<PiSetupTerminalResult> StartPiSetupAsync(StartPiSetupRequest request, CancellationToken cancellationToken = default)
    {
        var installation = _options.PiInstallation ?? throw new HostOperationException(ProtocolErrorCodes.PiCommandRejected, "Configure a Pi executable first.");
        var setup = PiSetupCommand.Create(installation, request.Action);
        var terminal = await _terminals.StartAsync(new StartTerminalSessionRequest(request.ProjectId,
            TerminalShellKind.PowerShell, ThreadId: request.ThreadId), cancellationToken).ConfigureAwait(false);
        try
        {
            await _terminals.WriteAsync(new WriteTerminalInputRequest(terminal.TerminalSessionId, setup.Command + "\r"), cancellationToken).ConfigureAwait(false);
            return new(terminal, setup.Instructions);
        }
        catch
        {
            await _terminals.CloseAsync(new CloseTerminalSessionRequest(terminal.TerminalSessionId), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

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
