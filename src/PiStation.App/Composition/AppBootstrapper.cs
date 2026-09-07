using Microsoft.UI.Dispatching;
using PiStation.App.ViewModels;
using PiStation.ClientRuntime;
using PiStation.Host;
using PiStation.Host.Hosting;
using PiStation.PiRpc.Discovery;

namespace PiStation.App.Composition;

internal static class AppBootstrapper
{
    public static ShellViewModel CreateShellViewModel(
        DispatcherQueue dispatcherQueue,
        bool enableUiTestFaultControls = false,
        string? layoutSettingsPath = null,
        string? previewCaptureRoot = null) =>
        new(dispatcherQueue, enableUiTestFaultControls, layoutSettingsPath, previewCaptureRoot);

    public static async Task<AppRuntime> StartAsync(
        ShellViewModel viewModel,
        AppLaunchOptions launchOptions,
        RemoteAccessController? remoteAccess = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(launchOptions);
        if (await SshEnvironmentHost.TryDiscoverAsync(launchOptions.DataRoot, cancellationToken).ConfigureAwait(false) is { } existing)
            return await AttachExistingAsync(viewModel, existing, launchOptions, cancellationToken).ConfigureAwait(false);
        launchOptions.Log("Starting embedded environment.");

        var piInstallation = await ResolvePiAsync(launchOptions, cancellationToken).ConfigureAwait(false);
        launchOptions.Log("Pi runtime resolved; starting loopback host.");
        var hostOptions = new HostOptions
        {
            ApplicationDataRoot = launchOptions.DataRoot,
            EnvironmentName = Environment.MachineName,
            PiInstallation = piInstallation,
            AdditionalPiArguments = launchOptions.FakePiScenario is null
                ? []
                : ["--fake-pi-scenario", launchOptions.FakePiScenario],
            JournalEventLimit = launchOptions.UiTestJournalEventLimit ?? 512,
        };
        EmbeddedEnvironmentHost host;
        try { host = await EmbeddedEnvironmentHost.StartAsync(hostOptions, cancellationToken: cancellationToken).ConfigureAwait(false); }
        catch (IOException)
        {
            // A simultaneous SSH/desktop launch may acquire the data lock first.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (await SshEnvironmentHost.TryDiscoverAsync(launchOptions.DataRoot, cancellationToken).ConfigureAwait(false) is { } winner)
                    return await AttachExistingAsync(viewModel, winner, launchOptions, cancellationToken).ConfigureAwait(false);
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
        launchOptions.Log("Loopback host started; connecting desktop client.");
        EnvironmentClient? client = null;
        try
        {
            client = new EnvironmentClient(new ClientRuntimeOptions
            {
                HubAddress = host.HubAddress,
                BearerCredential = host.BearerCredential,
            });
            viewModel.Attach(client);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await viewModel.LoadProjectsAsync(cancellationToken).ConfigureAwait(false);
            if (remoteAccess is not null) await remoteAccess.AttachAsync(host.Environment).ConfigureAwait(false);
            launchOptions.Log($"Environment ready at {host.Address}.");
            return new AppRuntime(host, client, viewModel);
        }
        catch
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<AppRuntime> AttachExistingAsync(ShellViewModel viewModel,
        PiStation.Protocol.Models.SshHostInfo info, AppLaunchOptions launchOptions, CancellationToken cancellationToken)
    {
        info.Validate();
        var client = new EnvironmentClient(new ClientRuntimeOptions
        {
            HubAddress = new Uri($"https://127.0.0.1:{info.Port}/environment"),
            BearerCredential = info.BearerCredential, CertificateFingerprint = info.CertificateFingerprint,
            ExpectedEnvironmentId = info.EnvironmentId,
        });
        try
        {
            viewModel.Attach(client);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await viewModel.LoadProjectsAsync(cancellationToken).ConfigureAwait(false);
            launchOptions.Log("Attached to the running shared environment; its owner controls its lifetime.");
            return new AppRuntime(null, client, viewModel);
        }
        catch { await client.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static Task<PiInstallation> ResolvePiAsync(
        AppLaunchOptions launchOptions,
        CancellationToken cancellationToken)
    {
        if (launchOptions.IsUiTest && launchOptions.FakePiScenario is not null)
        {
            return Task.FromResult(new PiInstallation(
                PiInstallationKind.NativeExecutable,
                launchOptions.PiExecutable!,
                [],
                new SemanticVersion(0, 84, 4),
                null,
                null,
                "ui-test"));
        }

        return new PiLocator().LocateAsync(
            new PiLocatorOptions { ExplicitPiPath = launchOptions.PiExecutable },
            cancellationToken);
    }
}
