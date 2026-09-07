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
    private static App? CurrentApp => Application.Current as App;
    public SshConnectionsPanel() => InitializeComponent();

    public async void Activate()
    {
        _active = true;
        try { RefreshSaved(); await DiscoverAsync(); }
        catch (Exception exception) { Status.Text = exception.Message; }
    }

    public void Deactivate()
    {
        _active = false;
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
        CancelConnection.IsEnabled = busy;
        var selected = SavedConnections.SelectedItem is SshConnectionProfile;
        OpenConnection.IsEnabled = DisconnectConnection.IsEnabled = ForgetConnection.IsEnabled = !busy && selected;
        UpdateConnection.IsEnabled = !busy && selected;
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

    private async void OnAddConnection(object sender, RoutedEventArgs e) => await RunAsync(cancellationToken =>
    {
        var profile = new SshConnectionProfile(Guid.NewGuid(),
            string.IsNullOrWhiteSpace(ConnectionName.Text) ? Target.Text.Trim() : ConnectionName.Text.Trim(),
            Target.Text.Trim(), ServerPath.Text.Trim(), EmptyToNull(DataRoot.Text), EmptyToNull(PiExecutable.Text), null, ClientId.New(), ReadPort());
        profile.Validate();
        return OpenAsync(profile, cancellationToken);
    });

    private async void OnOpenConnection(object sender, RoutedEventArgs e) => await RunAsync(token =>
        SavedConnections.SelectedItem is SshConnectionProfile profile ? OpenAsync(profile, token) : Task.CompletedTask);

    private async void OnDisconnectConnection(object sender, RoutedEventArgs e) => await RunAsync(async _ =>
    {
        if (SavedConnections.SelectedItem is SshConnectionProfile profile && CurrentApp is { } app)
        {
            await app.CloseSshEnvironmentAsync(profile.Id);
            Status.Text = "Disconnected. Any host started by this connection was stopped; separately running hosts were left alone.";
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
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
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
            ? $"{profile.Target}{(profile.Port is { } port ? $" · port {port}" : string.Empty)}\n{(string.IsNullOrWhiteSpace(profile.ServerPath) ? "Matching bundled Windows host" : profile.ServerPath)}\nHost data: {profile.DataRoot ?? "remote desktop's default directory"}" : string.Empty;
        if (SavedConnections.SelectedItem is SshConnectionProfile selected && CurrentApp?.GetSshHostInfo(selected.Id) is { } info)
            Details.Text += $"\nLast connected host: {info.HostKind}, version {info.ServerVersion ?? "unknown"}\nDesktop version: {PiStation.Protocol.ProductVersion.Current}";
        SetBusy(_operation is not null);
    }
    private static string? EmptyToNull(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
