using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PiStation.App.Composition;
using PiStation.App.ViewModels;
using PiStation.ClientRuntime;
using PiStation.ClientRuntime.Ssh;

namespace PiStation.App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The application-wide SSH open gate remains valid until process exit, including after the local window closes.")]
public partial class App : Application
{
    private AppRuntime? _runtime;
    private AppLaunchOptions? _launchOptions;
    private Window? _window;
    private bool _localWindowClosed;
    private readonly Dictionary<Window, AppRuntime> _remoteWindows = new();
    private readonly Dictionary<string, Window> _environmentWindows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SavedRemoteEnvironment> _remoteProfiles = new(StringComparer.Ordinal);
    private readonly Dictionary<Window, TaskCompletionSource> _remoteCloseCompletions = new();
    private readonly HashSet<Window> _remoteReplacementCloses = [];
    private readonly HashSet<Window> _remoteClosingWindows = [];
    private readonly SemaphoreSlim _sshOpenGate = new(1, 1);
    private readonly SemaphoreSlim _sshPasswordGate = new(1, 1);
    private readonly Dictionary<Guid, (string HostKind, string? ServerVersion)> _sshHostInfo = new();
    private readonly HashSet<Guid> _sshNeedsReplacement = [];
    private DispatcherQueue? _dispatcherQueue;
    internal RemoteAccessController? RemoteAccess { get; private set; }

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
        _dispatcherQueue = dispatcherQueue;
        ShellViewModel viewModel;
        try
        {
            var processArguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
            _launchOptions = processArguments.Length == 0
                ? AppLaunchOptions.Parse(args.Arguments)
                : AppLaunchOptions.Parse(processArguments);
            RemoteAccess = new RemoteAccessController(_launchOptions.DataRoot);
            ApplyUiTestTextScale(_launchOptions.UiTestTextScalePercent);
            var enableUiTestFaultControls =
                _launchOptions.IsUiTest && _launchOptions.FakePiScenario is not null;
            viewModel = AppBootstrapper.CreateShellViewModel(
                dispatcherQueue,
                enableUiTestFaultControls,
                Path.Combine(_launchOptions.DataRoot, "layout-settings.json"),
                Path.Combine(_launchOptions.DataRoot, "preview-captures"));
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
            var runtime = await AppBootstrapper.StartAsync(viewModel, _launchOptions, RemoteAccess);
            if (_localWindowClosed) await runtime.DisposeAsync();
            else _runtime = runtime;
        }
        catch (Exception exception)
        {
            _launchOptions?.Log($"Startup failed: {exception}");
            viewModel.ReportRuntimeError(exception);
        }
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        try
        {
            _localWindowClosed = true;
            _window = null;
            try { if (RemoteAccess is not null) await RemoteAccess.DisposeAsync(); }
            catch (Exception exception) { SafeLog($"Remote access shutdown failed ({exception.GetType().Name})"); }
            if (_runtime is not null)
            {
                await _runtime.DisposeAsync();
                _runtime = null;
            }
        }
        catch (Exception exception)
        {
            SafeLog($"Window shutdown failed ({exception.GetType().Name})");
        }
    }

    internal Window? FindWindow(Microsoft.UI.Xaml.XamlRoot xamlRoot) =>
        _remoteWindows.Keys.Where(window => !_remoteClosingWindows.Contains(window)).Append(_window)
            .FirstOrDefault(window => window?.Content?.XamlRoot == xamlRoot);

    internal EnvironmentClient? FindRemoteClient(string id) =>
        _environmentWindows.TryGetValue(id, out var window) && !_remoteClosingWindows.Contains(window) &&
        _remoteWindows.TryGetValue(window, out var runtime) ? runtime.Client : null;

    internal void CloseRemoteEnvironment(string id)
    {
        if (_environmentWindows.TryGetValue(id, out var window) && !_remoteClosingWindows.Contains(window)) window.Close();
    }

    internal async Task OpenSshEnvironmentAsync(SshConnectionProfile profile, IProgress<string>? progress,
        CancellationToken cancellationToken, XamlRoot? promptRoot = null, bool updateWithBundledHost = false)
    {
        if (updateWithBundledHost)
        {
            await ManagedSshConnection.CheckBundledHostAsync(cancellationToken);
            profile = profile with { ServerPath = string.Empty };
        }
        await _sshOpenGate.WaitAsync(cancellationToken);
        try
        {
            var id = "ssh:" + profile.Id;
            var replaceExisting = updateWithBundledHost || _sshNeedsReplacement.Contains(profile.Id);
            if (updateWithBundledHost && _environmentWindows.TryGetValue(id, out var updatingWindow) &&
                _remoteWindows.TryGetValue(updatingWindow, out var updatingRuntime))
            {
                // Keep the client window alive for progress, password prompts and retry. Only
                // its connection-owned server stops; an external host is never terminated.
                _sshNeedsReplacement.Add(profile.Id);
                progress?.Report("Stopping this connection before updating; saved work is retained…");
                await updatingRuntime.DisposeAsync();
            }
            if (_environmentWindows.TryGetValue(id, out var existing))
            {
                if (_remoteClosingWindows.Contains(existing) && _remoteCloseCompletions.TryGetValue(existing, out var closing))
                    await closing.Task.WaitAsync(cancellationToken);
                else if (!replaceExisting)
                {
                    existing.Activate();
                    if (_remoteWindows.TryGetValue(existing, out var runtime)) await runtime.ReconnectAsync(cancellationToken);
                    progress?.Report("SSH environment reopened.");
                    return;
                }
            }
            var connection = new ManagedSshConnection(profile, progress,
                (request, token) => RequestSshPasswordAsync(profile.Id, request, promptRoot, token));
            try
            {
                await connection.EnsureConnectedAsync(cancellationToken);
                // Verify the descriptor through the pinned tunnel before saving first-use identity.
                await using (var probe = new EnvironmentClient(connection.CreateOptions()))
                    await probe.ConnectAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                RemoteAccess?.SshConnections.Save(profile with { ExpectedEnvironmentId = connection.Info.EnvironmentId });
                var info = connection.Info;
                _sshHostInfo[profile.Id] = (info.HostKind, info.ServerVersion);
                await OpenRemoteEnvironmentAsync(new(info.EnvironmentId, profile.Name, connection.Address,
                    info.CertificateFingerprint, info.BearerCredential, profile.ClientId), connection, id, replaceExisting, cancellationToken);
                _sshNeedsReplacement.Remove(profile.Id);
            }
            catch { await connection.DisposeAsync(); throw; }
        }
        finally { _sshOpenGate.Release(); }
    }

    internal async Task CloseSshEnvironmentAsync(Guid profileId)
    {
        await _sshOpenGate.WaitAsync();
        try { await CloseSshEnvironmentCoreAsync(profileId); }
        finally { _sshOpenGate.Release(); }
    }

    private async Task CloseSshEnvironmentCoreAsync(Guid profileId)
    {
        var id = "ssh:" + profileId;
        if (!_environmentWindows.TryGetValue(id, out var window)) return;
        CloseRemoteEnvironment(id);
        if (_remoteCloseCompletions.TryGetValue(window, out var completion)) await completion.Task;
    }

    internal (string HostKind, string? ServerVersion)? GetSshHostInfo(Guid profileId) =>
        _sshHostInfo.TryGetValue(profileId, out var info) ? info : null;

    internal bool IsReplacingWindow(XamlRoot root) => _remoteReplacementCloses.Any(window => window.Content?.XamlRoot == root);

    private Task<string?> RequestSshPasswordAsync(Guid profileId, SshPasswordRequest request,
        XamlRoot? preferredRoot, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = _dispatcherQueue;
        if (queue is null || !queue.TryEnqueue(async () =>
        {
            var entered = false;
            try
            {
                await _sshPasswordGate.WaitAsync(cancellationToken);
                entered = true;
                var owner = _environmentWindows.GetValueOrDefault("ssh:" + profileId) ??
                    (preferredRoot is null ? null : FindWindow(preferredRoot)) ?? _window ??
                    _remoteWindows.Keys.FirstOrDefault(window => !_remoteClosingWindows.Contains(window));
                if (owner?.Content?.XamlRoot is not { } root) { completion.TrySetResult(null); return; }
                var password = new PasswordBox { Header = "Password or key passphrase", MaxLength = 4096 };
                var content = new StackPanel { Spacing = 12 };
                content.Children.Add(new TextBlock
                {
                    Text = $"Sign in to {request.Target}. Attempt {request.Attempt} of 2. The secret is kept only for this connection, never saved.",
                    TextWrapping = TextWrapping.Wrap,
                });
                content.Children.Add(password);
                var dialog = new ContentDialog
                {
                    XamlRoot = root, Title = "SSH authentication", Content = content,
                    PrimaryButtonText = "Connect", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
                };
                using var canceled = cancellationToken.Register(() => queue.TryEnqueue(dialog.Hide));
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = await dialog.ShowAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                    completion.TrySetResult(result == ContentDialogResult.Primary ? password.Password : null);
                }
                finally { password.Password = string.Empty; }
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally { if (entered) _sshPasswordGate.Release(); }
        })) completion.TrySetResult(null);
        return completion.Task.WaitAsync(cancellationToken);
    }

    internal async Task ReplaceRemoteAddressAsync(SavedRemoteEnvironment expected, Uri address, CancellationToken cancellationToken)
    {
        if (RemoteAccess is null) throw new InvalidOperationException("Connection settings are unavailable.");
        PiStation.Protocol.Models.RemoteEndpoint.Validate(address);
        var replacement = expected with { Address = address };
        var id = expected.EnvironmentId.ToString();
        if (_environmentWindows.TryGetValue(id, out var window) && _remoteWindows.TryGetValue(window, out var runtime) && !_remoteClosingWindows.Contains(window))
        {
            await runtime.ReplaceEndpointAsync(replacement, () => RemoteAccess.Connections.Replace(expected, replacement), cancellationToken);
            _remoteProfiles[id] = replacement;
        }
        else await RemoteAccess.Connections.VerifyAndReplaceAsync(expected, address, cancellationToken);
    }

    internal string GetRemoteDiagnostics(SavedRemoteEnvironment environment) =>
        _environmentWindows.TryGetValue(environment.EnvironmentId.ToString(), out var window) && _remoteWindows.TryGetValue(window, out var runtime)
            ? runtime.ExportDiagnostics() : "This saved environment is closed. Open it to inspect connection diagnostics.";

    internal Task PrepareDesktopUpdateAsync() => OnUpdateUiAsync(() =>
    {
        if (_runtime?.HasUnsavedChanges == true || _remoteWindows.Values.Any(runtime => runtime.HasUnsavedChanges))
            throw new InvalidOperationException("Save open drafts and file edits on the host desktop before updating.");
        return Task.CompletedTask;
    });

    internal Task FinishDesktopUpdateAsync() => OnUpdateUiAsync(async () =>
    {
        if (_runtime?.HasUnsavedChanges == true || _remoteWindows.Values.Any(runtime => runtime.HasUnsavedChanges))
            throw new InvalidOperationException("New edits were made while preparing the update. Save them and retry.");
        var windows = _remoteWindows.Keys.Append(_window).Where(window => window is not null).ToArray();
        // Remove interactive surfaces in this UI dispatch before awaiting shutdown.
        foreach (var window in windows) window!.Content = new TextBlock { Text = "Installing the desktop update…", Margin = new Thickness(24) };
        foreach (var runtime in _remoteWindows.Values.ToArray()) await runtime.DisposeAsync();
        if (RemoteAccess is not null) await RemoteAccess.DisposeAsync();
        if (_runtime is not null) await _runtime.DisposeAsync();
        foreach (var window in windows) window!.Close();
        Exit();
    });

    private Task OnUpdateUiAsync(Func<Task> operation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_dispatcherQueue?.TryEnqueue(async () =>
        {
            try { await operation(); completion.TrySetResult(); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) != true) completion.TrySetException(new InvalidOperationException("The desktop is closing."));
        return completion.Task;
    }

    internal async Task OpenRemoteEnvironmentAsync(SavedRemoteEnvironment environment,
        ManagedSshConnection? ssh = null, string? windowId = null, bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_launchOptions is null) throw new InvalidOperationException("Application settings are unavailable.");
        var id = windowId ?? environment.EnvironmentId.ToString();
        Window? replacingWindow = null;
        if (replaceExisting) _environmentWindows.TryGetValue(id, out replacingWindow);
        if (!replaceExisting && _environmentWindows.TryGetValue(id, out var existing))
        {
            if (_remoteClosingWindows.Contains(existing) && _remoteCloseCompletions.TryGetValue(existing, out var closing) && !closing.Task.IsCompleted)
            {
                await closing.Task.ConfigureAwait(true);
                await OpenRemoteEnvironmentAsync(environment, ssh, windowId, cancellationToken: cancellationToken);
                return;
            }
            if (_remoteProfiles.TryGetValue(id, out var previous) && DesktopLifecycle.ProfileChanged(previous, environment))
            {
                if (!_remoteWindows.TryGetValue(existing, out var replacingRuntime)) throw new InvalidOperationException("The remote window is closing.");
                // Verify fresh credentials before switching the existing client. Dirty editors and drafts stay in place.
                await replacingRuntime.ReplaceEndpointAsync(environment, () => _remoteProfiles[id] = environment, cancellationToken);
                existing.Activate();
                return;
            }
            existing.Activate();
            if (_remoteWindows.TryGetValue(existing, out var existingRuntime))
                await existingRuntime.ReconnectAsync(cancellationToken);
            return;
        }
        // Hash the environment identity before using it as a directory name.
        var folder = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)));
        var root = Path.Combine(_launchOptions.DataRoot, "remote-environments", folder);
        var viewModel = AppBootstrapper.CreateShellViewModel(DispatcherQueue.GetForCurrentThread(),
            layoutSettingsPath: Path.Combine(root, "layout-settings.json"), previewCaptureRoot: Path.Combine(root, "preview-captures"));
        viewModel.ConfigureRemote(environment.Name);
        var client = new EnvironmentClient(ssh?.CreateOptions() ?? environment.CreateOptions());
        viewModel.Attach(client);
        var window = new MainWindow(viewModel) { Title = $"Pi Station • {environment.Name} (Remote)" };
        var runtime = new AppRuntime(null, client, viewModel, ssh);
        window.Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated) runtime.NotifyActivated();
        };
        _remoteWindows.Add(window, runtime);
        _environmentWindows[id] = window;
        _remoteProfiles[id] = environment;
        var closeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _remoteCloseCompletions[window] = closeCompletion;
        window.Closed += (_, _) =>
        {
            _remoteClosingWindows.Add(window);
            _ = HandleRemoteWindowClosedAsync(window, id, runtime, closeCompletion);
        };
        window.Activate();
        if (replacingWindow is not null && !_remoteClosingWindows.Contains(replacingWindow))
        {
            _remoteReplacementCloses.Add(replacingWindow);
            replacingWindow.Close();
        }
        try
        {
            await client.ConnectAsync(cancellationToken);
            if (_remoteWindows.ContainsKey(window) && !_remoteClosingWindows.Contains(window)) await viewModel.LoadProjectsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!_remoteClosingWindows.Contains(window)) window.Close();
            await closeCompletion.Task;
            throw;
        }
        catch (Exception exception)
        {
            if (_remoteWindows.ContainsKey(window) && !_remoteClosingWindows.Contains(window)) viewModel.ReportConnectionError(exception);
        }
    }

    private async Task HandleRemoteWindowClosedAsync(
        Window window, string id, AppRuntime runtime, TaskCompletionSource completion)
    {
        try
        {
            var replacement = _remoteReplacementCloses.Remove(window);
            await runtime.DisposeAsync(flushDraft: !replacement);
            _remoteWindows.Remove(window);
            if (_environmentWindows.GetValueOrDefault(id) == window)
            {
                _environmentWindows.Remove(id);
                _remoteProfiles.Remove(id);
            }
            _remoteCloseCompletions.Remove(window);
            _remoteClosingWindows.Remove(window);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            _remoteWindows.Remove(window);
            if (_environmentWindows.GetValueOrDefault(id) == window)
            {
                _environmentWindows.Remove(id);
                _remoteProfiles.Remove(id);
            }
            _remoteCloseCompletions.Remove(window);
            _remoteClosingWindows.Remove(window);
            SafeLog($"Remote window shutdown failed ({exception.GetType().Name})");
            // Closing a window is best effort; allow a replacement profile to proceed even
            // when one cleanup component has already failed.
            completion.TrySetResult();
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

    private void SafeLog(string message)
    {
        try { _launchOptions?.Log(message); }
        catch { }
    }
}
