using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PiStation.ClientRuntime.Ssh;
using PiStation.Protocol.Identifiers;

namespace PiStation.App.Views.Controls;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The settings owner deactivates the panel and each operation disposes its cancellation source.")]
public sealed partial class SshConnectionsPanel : UserControl
{
    private CancellationTokenSource? _operation;
    private bool _active;
    private IReadOnlyList<DiscoveredSshHost> _discovered = [];
    private readonly Dictionary<SshSetupStep, SshSetupCheck> _setupChecks = [];
    private int _setupGeneration;
    private static App? CurrentApp => Application.Current as App;
    public SshConnectionsPanel() => InitializeComponent();

    public async void Activate()
    {
        _active = true;
        _setupGeneration++;
        SetupResults.Visibility = Visibility.Collapsed;
        _setupChecks.Clear();
        try { RefreshSaved(); await DiscoverAsync(); }
        catch (Exception exception) { Status.Text = exception.Message; }
    }

    public void Deactivate()
    {
        _active = false;
        _setupGeneration++;
        if (CurrentApp?.IsReplacingWindow(XamlRoot) != true) _operation?.Cancel();
    }

    private void RefreshSaved()
    {
        var id = (SavedConnections.SelectedItem as SshConnectionProfile)?.Id;
        var profiles = CurrentApp?.RemoteAccess?.SshConnections.Load() ?? [];
        SavedConnections.ItemsSource = profiles;
        SavedConnections.SelectedItem = profiles.FirstOrDefault(p => p.Id == id);
        if (SavedConnections.SelectedIndex < 0 && profiles.Count > 0) SavedConnections.SelectedIndex = 0;
        SetBusy(_operation is not null);
    }

    private void SetBusy(bool busy)
    {
        AddConnection.IsEnabled = !busy;
        CheckSetup.IsEnabled = !busy;
        CancelConnection.IsEnabled = busy;
        var selected = SavedConnections.SelectedItem is SshConnectionProfile;
        OpenConnection.IsEnabled = DisconnectConnection.IsEnabled = ForgetConnection.IsEnabled = !busy && selected;
        CheckSavedSetup.IsEnabled = !busy && selected;
        UpdateConnection.IsEnabled = false; // Automatic setup is deferred.
        ConnectionName.IsEnabled = Target.IsEnabled = ServerPath.IsEnabled = DataRoot.IsEnabled = PiExecutable.IsEnabled = !busy;
        SshPort.IsEnabled = DiscoveredHosts.IsEnabled = DiscoverHosts.IsEnabled = !busy;
        SavedConnections.IsEnabled = !busy;
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_operation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        SetBusy(true);
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { if (_active) Status.Text = "SSH connection canceled."; }
        catch (Exception exception) { if (_active) Status.Text = exception.Message; }
        finally
        {
            _operation = null;
            SetBusy(false);
            if (_active)
            {
                try { RefreshSaved(); }
                catch (Exception exception) { Status.Text = exception.Message; }
            }
        }
    }

    private Task OpenAsync(SshConnectionProfile profile, CancellationToken cancellationToken)
    {
        var progress = new Progress<string>(message => { if (_active) Status.Text = message; });
        return CurrentApp?.OpenSshEnvironmentAsync(profile, progress, cancellationToken, XamlRoot) ?? Task.CompletedTask;
    }

    private SshConnectionProfile ReadProfile()
    {
        var profile = new SshConnectionProfile(Guid.NewGuid(),
            string.IsNullOrWhiteSpace(ConnectionName.Text) ? Target.Text.Trim() : ConnectionName.Text.Trim(),
            Target.Text.Trim(), ServerPath.Text.Trim(), EmptyToNull(DataRoot.Text), EmptyToNull(PiExecutable.Text), null, ClientId.New(), ReadPort());
        profile.Validate();
        return profile;
    }

    private async void OnAddConnection(object sender, RoutedEventArgs e) => await RunAsync(token => OpenAsync(ReadProfile(), token));

    private async void OnCheckSetup(object sender, RoutedEventArgs e) => await RunAsync(token => CheckSetupAsync(ReadProfile(), token));

    private async void OnCheckSavedSetup(object sender, RoutedEventArgs e) => await RunAsync(token =>
        SavedConnections.SelectedItem is SshConnectionProfile profile ? CheckSetupAsync(profile, token) : Task.CompletedTask);

    private async Task CheckSetupAsync(SshConnectionProfile profile, CancellationToken cancellationToken)
    {
        if (CurrentApp is not { } app) return;
        var generation = ++_setupGeneration;
        _setupChecks.Clear();
        foreach (var step in Enum.GetValues<SshSetupStep>()) _setupChecks[step] = new(step, SshSetupState.NotChecked, string.Empty);
        SetupTarget.Text = $"Setup check · {profile.Target}{(profile.Port is { } port ? $" · port {port}" : string.Empty)}\nHost data: {profile.DataRoot ?? "remote desktop's default directory"}";
        SetupResults.Visibility = Visibility.Visible;
        RefreshSetupChecks();
        // Dispatch synchronously on the UI thread when already there. A posted callback
        // must still belong to this check and settings visit before it can update results.
        var checks = new SetupProgress(check =>
        {
            void Apply()
            {
                if (!_active || generation != _setupGeneration) return;
                _setupChecks[check.Step] = check;
                RefreshSetupChecks();
            }
            if (DispatcherQueue.HasThreadAccess) Apply();
            else DispatcherQueue.TryEnqueue(Apply);
        });
        var finished = false;
        var progress = new Progress<string>(message => { if (_active && generation == _setupGeneration && !finished) Status.Text = message; });
        await using var connection = new ManagedSshConnection(profile, progress,
            (request, token) => app.RequestSshPasswordAsync(profile.Id, request, XamlRoot, token), checks);
        try { await connection.EnsureConnectedAsync(cancellationToken); }
        finally { finished = true; }
        if (_active && generation == _setupGeneration)
            Status.Text = "SSH setup passed. The temporary check connection is closing; the remote PiStation host stays running.";
    }

    private void RefreshSetupChecks() => SetupChecks.ItemsSource = _setupChecks.OrderBy(pair => pair.Key).Select(pair => pair.Value.DisplayText).ToArray();

    private sealed class SetupProgress(Action<SshSetupCheck> report) : IProgress<SshSetupCheck>
    {
        public void Report(SshSetupCheck value) => report(value);
    }

    private async void OnOpenConnection(object sender, RoutedEventArgs e) => await RunAsync(token =>
        SavedConnections.SelectedItem is SshConnectionProfile profile ? OpenAsync(profile, token) : Task.CompletedTask);

    private async void OnDisconnectConnection(object sender, RoutedEventArgs e) => await RunAsync(async _ =>
    {
        if (SavedConnections.SelectedItem is SshConnectionProfile profile && CurrentApp is { } app)
        {
            await app.CloseSshEnvironmentAsync(profile.Id);
            Status.Text = "Disconnected. The remote PiStation host is still running.";
        }
    });

    private async void OnForgetConnection(object sender, RoutedEventArgs e) => await RunAsync(async _ =>
    {
        if (SavedConnections.SelectedItem is SshConnectionProfile profile && CurrentApp is { } app)
        {
            await app.CloseSshEnvironmentAsync(profile.Id);
            app.RemoteAccess?.SshConnections.Forget(profile.Id);
            Status.Text = "Saved SSH connection removed. Remote projects and your SSH keys were not deleted.";
        }
    });

    private async void OnUpdateConnection(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (SavedConnections.SelectedItem is not SshConnectionProfile profile || CurrentApp is not { } app) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Update SSH host?",
            Content = "This reconnects using the host bundled with this desktop. A server owned by this connection will stop, interrupting active agents and terminals. Saved projects and threads remain. A reused desktop or separate server will not be stopped; update that host at its source if its version differs.",
            PrimaryButtonText = "Update and reconnect", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
        };
        using var canceled = token.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
        if (await app.ShowConnectionDialogAsync(dialog, token) != ContentDialogResult.Primary) return;
        token.ThrowIfCancellationRequested();
        await app.OpenSshEnvironmentAsync(profile, new Progress<string>(message => { if (_active) Status.Text = message; }),
            token, XamlRoot, updateWithBundledHost: true);
    });

    private async Task DiscoverAsync()
    {
        var found = await SshHostDiscovery.DiscoverAsync();
        if (!_active) return;
        _discovered = found;
        DiscoveredHosts.ItemsSource = _discovered;
    }

    private async void OnInstallOwnerUpdate(object sender, RoutedEventArgs e) => await RunAsync(token => RunOwnerUpdateAsync(false, token));
    private async void OnCheckOwnerUpdate(object sender, RoutedEventArgs e) => await RunAsync(token => RunOwnerUpdateAsync(true, token));
    private async Task RunOwnerUpdateAsync(bool historyOnly, CancellationToken cancellationToken)
    {
        if (SavedConnections.SelectedItem is not SshConnectionProfile profile || CurrentApp is not { } app) return;
        var progress = new Progress<string>(message => { if (_active) Status.Text = message; });
        await using var connection = ManagedSshConnection.ForHostUpdate(profile, progress,
            (request, token) => app.RequestSshPasswordAsync(profile.Id, request, XamlRoot, token));
        await connection.EnsureConnectedAsync(cancellationToken);
        await using var updates = new PiStation.ClientRuntime.RemoteUpdateClient(connection.CreateOptions());
        await PiStation.App.Composition.RemoteUpdateWorkflow.RunAsync(updates, XamlRoot, progress, historyOnly, cancellationToken);
    }

    private async void OnDiscoverHosts(object sender, RoutedEventArgs e) => await RunAsync(async _ =>
    {
        await DiscoverAsync();
        Status.Text = _discovered.Count == 0 ? "No named targets found. Enter user@host or add an SSH config alias." : $"Found {_discovered.Count} SSH targets. Hashed known-host entries cannot be listed.";
    });

    private void OnDiscoveredHostSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DiscoveredHosts.SelectedItem is not DiscoveredSshHost host) return;
        Target.Text = host.Target;
        SshPort.Value = host.Port ?? double.NaN;
    }

    private int? ReadPort()
    {
        if (double.IsNaN(SshPort.Value)) return null;
        if (SshPort.Value != Math.Truncate(SshPort.Value)) throw new ArgumentException("Enter a whole-number SSH port.");
        return checked((int)SshPort.Value);
    }

    private void OnCancelConnection(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Details.Text = SavedConnections.SelectedItem is SshConnectionProfile profile
            ? $"{profile.Target}{(profile.Port is { } port ? $" · port {port}" : string.Empty)}\nConnects to an already-running PiStation host\nHost data: {profile.DataRoot ?? "remote desktop's default directory"}" : string.Empty;
        if (SavedConnections.SelectedItem is SshConnectionProfile selected && CurrentApp?.GetSshHostInfo(selected.Id) is { } info)
            Details.Text += $"\nLast connected host: {info.HostKind}, version {info.ServerVersion ?? "unknown"}\nDesktop version: {PiStation.Protocol.ProductVersion.Current}";
        SetBusy(_operation is not null);
    }
    private static string? EmptyToNull(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
