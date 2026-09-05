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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(launchOptions);
        launchOptions.Log("Starting embedded environment.");

        var piInstallation = await ResolvePiAsync(launchOptions, cancellationToken).ConfigureAwait(false);
        launchOptions.Log("Pi runtime resolved; starting loopback host.");
        var hostOptions = new HostOptions
        {
            ApplicationDataRoot = launchOptions.DataRoot,
            EnvironmentName = "Local",
            PiInstallation = piInstallation,
            AdditionalPiArguments = launchOptions.FakePiScenario is null
                ? []
                : ["--fake-pi-scenario", launchOptions.FakePiScenario],
            JournalEventLimit = launchOptions.UiTestJournalEventLimit ?? 512,
        };
        var host = await EmbeddedEnvironmentHost.StartAsync(hostOptions, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
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
