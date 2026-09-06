using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    private bool _requiresPiSetup;
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
        var path = Settings.PiExecutablePath;
        var extensions = new PiExtensionConfiguration(Settings.DiscoverPiExtensions,
            Settings.PiExtensionPaths.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        RunOnUiThread(() => Settings.RuntimeSetupStatus = "Checking Pi installation…");
        try
        {
            var client = RequireClient();
            var result = await client.ConfigurePiRuntimeAsync(new ConfigurePiRuntimeRequest(path, extensions), cancellationToken).ConfigureAwait(false);
            RunOnUiThread(() => { Settings.RuntimeSetupStatus = result.Message; if (result.Available) RequiresPiSetup = false; });
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
}
