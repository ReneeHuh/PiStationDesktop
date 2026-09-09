using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    private bool _requiresPiSetup;
    private bool _runtimeSettingsLoaded;
    private PiLaunchConfiguration? _loadedLaunchConfiguration;
    private readonly SemaphoreSlim _runtimeSettingsLoadGate = new(1, 1);
    public bool CanConfigurePiRuntime => CanOperate && _runtimeSettingsLoaded && (!IsRemote || IsConnected);
    public string PiExecutableBrowseLabel => IsRemote ? "Browse host…" : "Browse…";
    public string PiExecutablePathHeader => IsRemote ? "Pi executable or package path on the host" : "Pi executable or package path";
    internal void ReportPiLaunchConfiguration(PiLaunchConfiguration launch) => RunOnUiThread(() =>
    {
        _loadedLaunchConfiguration = launch;
        Settings.PiArguments = PiStation.ClientRuntime.PiLaunchEditor.FormatArguments(launch);
        Settings.PiEnvironment = PiStation.ClientRuntime.PiLaunchEditor.FormatEnvironment(launch);
        Settings.PiCommandTimeout = launch.CommandTimeoutSeconds;
        Settings.PiShutdownTimeout = launch.ShutdownTimeoutSeconds;
        _runtimeSettingsLoaded = true;
        OnPropertyChanged(nameof(CanConfigurePiRuntime));
    });
    public bool RequiresPiSetup { get => _requiresPiSetup; private set => SetProperty(ref _requiresPiSetup, value); }

    internal void ReportPiExtensions(PiExtensionConfiguration extensions) => RunOnUiThread(() =>
    {
        Settings.DiscoverPiExtensions = extensions.DiscoverInstalled;
        Settings.PiExtensionPaths = string.Join(Environment.NewLine, extensions.Paths ?? []);
    });

    internal void ReportPiSetup(string? path, string message, bool requiresSetup = false) => RunOnUiThread(() =>
    {
        RequiresPiSetup = requiresSetup;
        Settings.PiExecutablePath = path ?? string.Empty;
        Settings.RuntimeSetupStatus = message;
    });

    public async Task ConfigurePiRuntimeAsync(CancellationToken cancellationToken = default)
    {
        if (!CanConfigurePiRuntime)
        {
            RunOnUiThread(() => Settings.RuntimeSetupStatus = "Load the host runtime settings with operate access before saving.");
            return;
        }
        var path = Settings.PiExecutablePath;
        var extensions = new PiExtensionConfiguration(Settings.DiscoverPiExtensions,
            Settings.PiExtensionPaths.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        RunOnUiThread(() => Settings.RuntimeSetupStatus = "Checking Pi installation…");
        try
        {
            var client = RequireClient();
            var launch = PiStation.ClientRuntime.PiLaunchEditor.Parse(Settings.PiArguments, Settings.PiEnvironment,
                checked((int)Settings.PiCommandTimeout), checked((int)Settings.PiShutdownTimeout), _loadedLaunchConfiguration);
            var result = await client.ConfigurePiRuntimeAsync(new ConfigurePiRuntimeRequest(path, extensions, launch), cancellationToken).ConfigureAwait(false);
            RunOnUiThread(() => { Settings.RuntimeSetupStatus = result.Message; if (result.Available) { RequiresPiSetup = false; _loadedLaunchConfiguration = launch; } });
            if (!result.Available) return;
            await client.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            RunOnUiThread(ClearError);
            await LoadProjectsAsync(cancellationToken).ConfigureAwait(false);
            if (Thread.Projection?.RuntimeState is PiStation.Protocol.Projections.ThreadRuntimeState.Crashed or PiStation.Protocol.Projections.ThreadRuntimeState.Stopped)
                await RestartPiAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) { RunOnUiThread(() => Settings.RuntimeSetupStatus = exception.Message); }
    }

    public async Task LoadPiRuntimeConfigurationAsync(CancellationToken cancellationToken = default)
    {
        if (!CanOperate || _runtimeSettingsLoaded || !IsConnected) return;
        await _runtimeSettingsLoadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_runtimeSettingsLoaded || _runtimeStopped) return;
            var configuration = await RequireClient().GetPiRuntimeConfigurationAsync(cancellationToken).ConfigureAwait(false);
            await RunOnUiThreadAsync(() =>
            {
                ReportPiExtensions(configuration.Extensions);
                ReportPiSetup(configuration.ExecutablePath, "Host runtime settings loaded.", requiresSetup: _client?.Descriptor?.PiAvailable != true);
                ReportPiLaunchConfiguration(configuration.Launch ?? new());
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RunOnUiThread(() => Settings.RuntimeSetupStatus = $"Host runtime settings could not load: {exception.Message}");
        }
        finally { _runtimeSettingsLoadGate.Release(); }
    }

    internal void PrepareToLoadHostRuntimeConfiguration() => RunOnUiThread(() =>
    {
        _runtimeSettingsLoaded = false;
        OnPropertyChanged(nameof(CanConfigurePiRuntime));
    });

    public Task<HostPathPage> BrowseHostPathAsync(BrowseHostPathRequest request, CancellationToken cancellationToken = default) =>
        RequireClient().BrowseHostPathAsync(request, cancellationToken);
}
