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
        string? previewCaptureRoot = null,
        string? browserAutomationRoot = null) =>
        new(dispatcherQueue, enableUiTestFaultControls, layoutSettingsPath, previewCaptureRoot, browserAutomationRoot);

    public static async Task<AppRuntime> StartAsync(
        ShellViewModel viewModel,
        AppLaunchOptions launchOptions,
        RemoteAccessController? remoteAccess = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(launchOptions);
        if (await SshEnvironmentHost.TryDiscoverAsync(launchOptions.DataRoot, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            if (launchOptions.UiTestHostingFixture is not null) throw new InvalidOperationException("Stop the existing host before starting a hosting UI fixture.");
            return await AttachExistingAsync(viewModel, existing, launchOptions, cancellationToken).ConfigureAwait(false);
        }
        launchOptions.Log("Starting embedded environment.");

        PiInstallation? piInstallation = null;
        PiStation.Protocol.Models.PiRuntimeConfiguration configuration;
        string? settingsError = null;
        try { configuration = PiRuntimeSettingsStore.Load(launchOptions.DataRoot); }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            configuration = new(null, new());
            settingsError = $"Pi settings could not be loaded: {exception.Message}";
        }
        var configuredPath = launchOptions.PiExecutable ?? configuration.ExecutablePath;
        viewModel.ReportPiExtensions(configuration.Extensions);
        viewModel.ReportPiLaunchConfiguration(configuration.Launch ?? new());
        try
        {
            piInstallation = await ResolvePiAsync(launchOptions with { PiExecutable = configuredPath }, cancellationToken).ConfigureAwait(false);
            viewModel.ReportPiSetup(configuredPath, $"Pi {piInstallation.PiVersion} is ready.");
        }
        catch (PiDiscoveryException exception)
        {
            viewModel.ReportPiSetup(configuredPath, $"Pi needs setup: {exception.Message} Open Settings → Pi and runtime.", requiresSetup: true);
        }
        launchOptions.Log("Pi runtime resolved; starting loopback host.");
        if (settingsError is not null) viewModel.ReportPiSetup(configuredPath, settingsError, requiresSetup: piInstallation is null);
        var hostOptions = new HostOptions
        {
            ApplicationDataRoot = launchOptions.DataRoot,
            EnvironmentName = Environment.MachineName,
            PiInstallation = piInstallation,
            LaunchConfiguration = configuration.Launch ?? new(),
            Extensions = launchOptions.FakePiScenario is null ? configuration.Extensions : new(),
            PlanExtensionPath = launchOptions.FakePiScenario is null or "plan-workflow"
                ? Path.Combine(AppContext.BaseDirectory, "PiExtensions", "pistation-plan.ts") : null,
            AgentExtensionPath = launchOptions.FakePiScenario is null or "agent-workflow"
                ? Path.Combine(AppContext.BaseDirectory, "PiExtensions", "pistation-agents.ts") : null,
            ManagementExtensionPath = launchOptions.FakePiScenario is null
                ? Path.Combine(AppContext.BaseDirectory, "PiExtensions", "pistation-resources.ts") : null,
            AdditionalPiArguments = launchOptions.FakePiScenario is null
                ? []
                : ["--fake-pi-scenario", launchOptions.FakePiScenario],
            BrowserAutomationExtensionPath = launchOptions.FakePiScenario is null
                ? Path.Combine(AppContext.BaseDirectory, "PiExtensions", "pistation-browser.ts")
                : null,
            BrowserAutomationRoot = Path.Combine(launchOptions.DataRoot, "browser-automation"),
            JournalEventLimit = launchOptions.UiTestJournalEventLimit ?? 512,
        };
        EmbeddedEnvironmentHost host;
        try
        {
#if DEBUG
            var hostingFixture = launchOptions.UiTestHostingFixture is { } fixturePath ? new UiTestHostingFixture(fixturePath) : null;
            host = await EmbeddedEnvironmentHost.StartAsync(hostOptions, sourceControlFactory: hostingFixture is null ? null :
                (resolver, projects) => new(resolver, projects, hostingFixture.ExecuteAsync), cancellationToken: cancellationToken).ConfigureAwait(false);
#else
            host = await EmbeddedEnvironmentHost.StartAsync(hostOptions, cancellationToken: cancellationToken).ConfigureAwait(false);
#endif
        }
        catch (IOException) when (launchOptions.UiTestHostingFixture is null)
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
            if (Microsoft.UI.Xaml.Application.Current is App app)
                host.Environment.Updates.SetOwner(new DesktopUpdateOwner(app.PrepareDesktopUpdateAsync, app.FinishDesktopUpdateAsync));
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
