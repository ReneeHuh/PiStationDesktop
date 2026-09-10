using Microsoft.UI.Xaml;
using PiStation.App.Composition;
using PiStation.App.ViewModels;
using PiStation.ClientRuntime;
using PiStation.Host;

namespace PiStation.App;

public partial class App
{
    private readonly HashSet<Window> _temporaryWindows = [];

    internal async Task OpenTemporarySessionAsync(ShellViewModel source)
    {
        if (source.IsRemote) throw new InvalidOperationException("Temporary sessions currently run in a local project.");
        if (_launchOptions is null || _dispatcherQueue is null) throw new InvalidOperationException("The local app is not ready.");
        var project = source.Workspace.SelectedProject ?? throw new InvalidOperationException("Select a local project first.");
        var storage = TemporarySessionStorage.Create(Path.Combine(_launchOptions.DataRoot, "temporary-sessions"));
        AppRuntime? runtime = null;
        MainWindow? window = null;
        try
        {
            var launch = _launchOptions with { DataRoot = storage.Root, LogFile = null, TemporaryHistory = true,
                RuntimeOverride = PiRuntimeSettingsStore.Load(_launchOptions.DataRoot) };
            var viewModel = AppBootstrapper.CreateShellViewModel(_dispatcherQueue, layoutSettingsPath: Path.Combine(storage.Root, "layout.json"),
                previewCaptureRoot: Path.Combine(storage.Root, "preview-captures"), browserAutomationRoot: Path.Combine(storage.Root, "browser-automation"));
            viewModel.BrowserDataRoot = storage.Root;
            viewModel.Layout.UseSharedBrowserSettings(Path.Combine(storage.Root, "browser-settings.json"));
            window = new MainWindow(viewModel) { Title = "Temporary — conversation and draft discarded on close · PiStation" };
            window.MarkTemporary();
            _temporaryWindows.Add(window);
            // Register cleanup before startup so a close during initialization waits for it.
            var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.BeforeCloseAsync = async () =>
            {
                await startup.Task;
                window.ReleaseSurfaces();
                if (runtime is not null) await runtime.DisposeTemporaryAsync();
                await storage.DisposeAsync();
            };
            window.Closed += (_, _) => _temporaryWindows.Remove(window);
            window.Activate();
            try
            {
                runtime = await AppBootstrapper.StartAsync(viewModel, launch);
                await viewModel.AddProjectAsync(project.CanonicalPath);
                await viewModel.CreateThreadAsync();
            }
            finally { startup.TrySetResult(); }
        }
        catch
        {
            if (window is not null) await window.CloseWithRecoveryAsync();
            else { if (runtime is not null) await runtime.DisposeTemporaryAsync(); await storage.DisposeAsync(); }
            throw;
        }
    }
}
