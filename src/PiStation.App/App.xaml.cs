using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PiStation.App.Composition;
using PiStation.App.ViewModels;

namespace PiStation.App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private AppRuntime? _runtime;
    private AppLaunchOptions? _launchOptions;
    private Window? _window;

    internal Window? MainWindow => _window;

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args) =>
        _launchOptions?.Log($"Unhandled UI exception: {args.Exception}");

    /// <summary>
    /// Creates and activates the main application window.
    /// </summary>
    /// <param name="args">Details about the launch request.</param>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        ShellViewModel viewModel;
        try
        {
            var processArguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
            _launchOptions = processArguments.Length == 0
                ? AppLaunchOptions.Parse(args.Arguments)
                : AppLaunchOptions.Parse(processArguments);
            ApplyUiTestTextScale(_launchOptions.UiTestTextScalePercent);
            var enableUiTestFaultControls =
                _launchOptions.IsUiTest && _launchOptions.FakePiScenario is not null;
            viewModel = AppBootstrapper.CreateShellViewModel(
                dispatcherQueue,
                enableUiTestFaultControls,
                Path.Combine(_launchOptions.DataRoot, "layout-settings.json"),
                Path.Combine(_launchOptions.DataRoot, "preview-captures"),
                Path.Combine(_launchOptions.DataRoot, "browser-automation"));
        }
        catch (Exception exception)
        {
            viewModel = AppBootstrapper.CreateShellViewModel(dispatcherQueue);
            _window = new MainWindow(viewModel);
            _window.Closed += OnWindowClosed;
            _window.Activate();
            viewModel.ReportRuntimeError(exception);
            return;
        }

        try
        {
            _window = new MainWindow(viewModel, _launchOptions.UiTestTextScalePercent ?? 100);
        }
        catch (Exception exception)
        {
            _launchOptions.Log($"Window creation failed: {exception}");
            throw;
        }
        _window.Closed += OnWindowClosed;
        _window.Activate();

        try
        {
            _runtime = await AppBootstrapper.StartAsync(viewModel, _launchOptions);
        }
        catch (Exception exception)
        {
            _launchOptions?.Log($"Startup failed: {exception}");
            viewModel.ReportRuntimeError(exception);
        }
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_runtime is not null)
        {
            await _runtime.DisposeAsync();
            _runtime = null;
        }
    }

    private void ApplyUiTestTextScale(int? scalePercent)
    {
        if (scalePercent is null || scalePercent == 100)
        {
            return;
        }

        var scale = scalePercent.Value / 100d;
        foreach (var key in new[]
        {
            "PiFontSizeCaption",
            "PiFontSizeBody",
            "PiFontSizeBodyLarge",
            "PiFontSizeHeading",
            "PiFontSizeTitle",
        })
        {
            if (Resources[key] is double fontSize)
            {
                Resources[key] = fontSize * scale;
            }
        }
    }
}
