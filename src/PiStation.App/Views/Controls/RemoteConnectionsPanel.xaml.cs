using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Security.Cryptography;
using PiStation.App.Composition;
using PiStation.ClientRuntime;
using PiStation.Protocol.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace PiStation.App.Views.Controls;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The owning Settings page calls Deactivate on close and release.")]
public sealed partial class RemoteConnectionsPanel : UserControl
{
    private readonly DispatcherQueueTimer _refresh;
    private CancellationTokenSource? _pairing;
    private bool _busy;
    private bool _active;
    private bool _updatingLists;
    private readonly bool _remoteWindow;
    private readonly RemotePairingPageState _pageState = new();
    private IReadOnlyList<RemotePairingInvitation> _invitationSnapshot = [];
    private IReadOnlyList<PendingRemoteDevice> _pendingSnapshot = [];
    private IReadOnlyList<RemoteDevice> _deviceSnapshot = [];
    private string? _displayedPairingUrl;
    private int _qrGeneration;
    private static RemoteAccessController? Controller => (Application.Current as App)?.RemoteAccess;
    private RemoteAccessController? HostController => _remoteWindow ? null : Controller;

    public RemoteConnectionsPanel(bool remoteWindow)
    {
        _remoteWindow = remoteWindow;
        InitializeComponent();
        GotFocus += OnChildGotFocus;
        HostControls.Visibility = remoteWindow ? Visibility.Collapsed : Visibility.Visible;
        DeviceName.Text = Environment.MachineName;
        _refresh = DispatcherQueue.CreateTimer();
        _refresh.Interval = TimeSpan.FromSeconds(2);
        _refresh.Tick += (_, _) => RefreshHostSafely();
    }

    private static void OnChildGotFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement element)
            element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.2 });
    }

    public void Activate()
    {
        _active = true;
        SshConnections.Activate();
        try
        {
            var selected = (ListenAddress.SelectedItem as RemoteNetworkAddress)?.Address;
            var addresses = RemoteNetworkAddress.Discover();
            ListenAddress.ItemsSource = addresses;
            ListenAddress.SelectedItem = addresses.FirstOrDefault(item => item.Address == selected);
            if (ListenAddress.SelectedIndex < 0) ListenAddress.SelectedIndex = 0;
            RefreshSaved();
            RefreshHost();
            if (Controller?.StartupError is { } error) Status.Text = error;
            _refresh.Start();
        }
        catch (Exception exception) { Status.Text = exception.Message; }
    }

    public void Deactivate()
    {
        _active = false;
        SshConnections.Deactivate();
        _refresh.Stop();
        _pairing?.Cancel();
        _pageState.Clear();
        ClearLinkDisplay();
        PairingShareHint.Text = string.Empty;
        IncomingLink.Text = string.Empty;
        PairingProgress.Text = string.Empty;
        VerificationConfirmed.IsChecked = false;
    }

    private void RefreshHostSafely()
    {
        try { RefreshHost(); }
        catch (Exception exception)
        {
            // Never keep presenting a secret when we cannot verify that its grant is still active.
            _pageState.Clear();
            ClearLinkDisplay();
            Status.Text = $"Could not refresh remote sharing: {exception.Message}";
        }
    }

    private void RefreshHost()
    {
        if (!_active) return;
        var controller = HostController;
        if (!_busy && AllowRemoteUpdates.IsOn != (controller?.AllowsRemoteUpdates == true))
            AllowRemoteUpdates.IsOn = controller?.AllowsRemoteUpdates == true;
        AllowRemoteUpdates.IsEnabled = !_busy && controller?.CanHost == true;
        StartSharing.IsEnabled = !_busy && controller is { CanHost: true, IsSharing: false };
        StopSharing.IsEnabled = !_busy && controller?.IsSharing == true;
        CreatePairingLink.IsEnabled = !_busy && controller?.IsSharing == true;
        PairingLabel.IsEnabled = PairingLifetime.IsEnabled = PairingAccess.IsEnabled = CreatePairingLink.IsEnabled;
        ListenAddress.IsEnabled = ListenPort.IsEnabled = controller?.IsSharing != true && !_busy;
        UpdateApprovalState();
        SharingState.Text = controller?.NeedsAddress == true ? "Sharing needs an address. The selected network address is unavailable. Stop sharing, then choose an active address."
            : controller?.IsSharing == true
            ? $"Sharing at {controller.Address}\nCertificate: {controller.Fingerprint}"
            : controller?.CanHost == true ? "Remote sharing is off." : "This window is attached to an existing host. Manage direct sharing from the desktop that owns it; SSH access remains available.";
        var invitations = controller?.Access?.ListInvitations() ?? [];
        var pending = controller?.Access?.ListPending() ?? [];
        var devices = controller?.Access?.ListDevices() ?? [];
        _pageState.Synchronize(invitations, controller?.IsSharing == true, DateTimeOffset.UtcNow);
        _updatingLists = true;
        try
        {
            if (!_invitationSnapshot.SequenceEqual(invitations))
            {
                var selectedId = (PairingInvitations.SelectedItem as RemoteInvitationRow)?.Invitation.Id;
                var rows = invitations.Select(item => new RemoteInvitationRow(item)).ToArray();
                PairingInvitations.ItemsSource = rows;
                PairingInvitations.SelectedItem = rows.FirstOrDefault(item => item.Invitation.Id == selectedId);
                if (PairingInvitations.SelectedIndex < 0 && rows.Length > 0) PairingInvitations.SelectedIndex = 0;
                _invitationSnapshot = invitations;
            }
            if (!_pendingSnapshot.SequenceEqual(pending))
            {
                var selectedId = (PendingDevices.SelectedItem as PendingRemoteDevice)?.RequestId;
                PendingDevices.ItemsSource = pending;
                PendingDevices.SelectedItem = pending.FirstOrDefault(item => item.RequestId == selectedId);
                if (PendingDevices.SelectedIndex < 0 && pending.Count > 0) PendingDevices.SelectedIndex = 0;
                _pendingSnapshot = pending;
            }
            if (!_deviceSnapshot.SequenceEqual(devices))
            {
                var selectedId = (ApprovedDevices.SelectedItem as RemoteSessionRow)?.Device.DeviceId;
                var rows = devices.Select(item => new RemoteSessionRow(item)).ToArray();
                ApprovedDevices.ItemsSource = rows;
                ApprovedDevices.SelectedItem = rows.FirstOrDefault(item => item.Device.DeviceId == selectedId);
                if (ApprovedDevices.SelectedIndex < 0 && rows.Length > 0) ApprovedDevices.SelectedIndex = 0;
                _deviceSnapshot = devices;
            }
        }
        finally { _updatingLists = false; }
        InvitationsEmpty.Visibility = invitations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SessionsEmpty.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateInvitationDisplay();
        UpdateSessionDetails();
        UpdateApprovalState();
    }

    private void RefreshSaved()
    {
        var id = (SavedEnvironments.SelectedItem as SavedRemoteEnvironment)?.EnvironmentId;
        var entries = Controller?.Connections.Load() ?? [];
        SavedEnvironments.ItemsSource = entries;
        SavedEnvironments.SelectedItem = entries.FirstOrDefault(e => e.EnvironmentId == id);
        if (SavedEnvironments.SelectedIndex < 0 && entries.Count > 0) SavedEnvironments.SelectedIndex = 0;
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (_busy || !_active) return;
        _busy = true;
        try { RefreshHost(); await operation(); }
        catch (OperationCanceledException) { Status.Text = "The operation was canceled or timed out."; }
        catch (Exception exception) { Status.Text = exception.Message; }
        finally { _busy = false; RefreshHostSafely(); }
    }

    private async void OnStartSharing(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (HostController is not { } controller) return;
        await controller.StartAsync((ListenAddress.SelectedItem as RemoteNetworkAddress)?.Address ?? string.Empty, checked((int)ListenPort.Value));
        Status.Text = "Sharing is enabled. Create a pairing link for your other device. Windows may ask you to allow network access.";
    });

    private async void OnStopSharing(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (HostController is not { } controller) return;
        try { await controller.StopAsync(); }
        finally
        {
            if (!controller.IsSharing) { _pageState.Clear(); ClearLinkDisplay(); }
        }
        Status.Text = "Sharing stopped. Local work continues; saved device approvals remain available when sharing resumes.";
    });

    private async void OnCreatePairingLink(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (HostController is not { } controller) return;
        var lifetime = RemotePairingPageState.LifetimeFromMinutes(PairingLifetime.Value);
        var label = RemotePairingPageState.NormalizeLabel(PairingLabel.Text);
        var generation = _pageState.Generation;
        var pairing = await controller.CreateInvitationAsync(
            PairingAccess.SelectedIndex == 1 ? RemoteAccessLevel.ReadOnly : RemoteAccessLevel.Operate, lifetime, label);
        if (!_active || !_pageState.Remember(pairing, generation)) return;
        RefreshHost();
        PairingInvitations.SelectedItem = (PairingInvitations.ItemsSource as IEnumerable<RemoteInvitationRow>)?
            .FirstOrDefault(item => item.Invitation.Id == pairing.Invitation.Id);
        UpdateInvitationDisplay();
        Status.Text = "Link created. Share it with one device, then compare the verification code before approving.";
    });

    private async void OnCopyPairingLink(object sender, RoutedEventArgs e)
    {
        var id = (PairingInvitations.SelectedItem as RemoteInvitationRow)?.Invitation.Id;
        await RunAsync(() =>
        {
            // RunAsync refreshes SQLite first. Keep the clicked ID even if that refresh changes selection.
            var url = _pageState.GetUrl(id, DateTimeOffset.UtcNow);
            if (url is null) throw new InvalidOperationException("This link is no longer available to copy. Create a new pairing link.");
            var data = new DataPackage();
            data.SetText(url);
            if (!Clipboard.SetContentWithOptions(data, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false }))
                throw new InvalidOperationException("The clipboard is unavailable. Select and copy the link manually.");
            Status.Text = "Pairing link copied.";
            return Task.CompletedTask;
        });
    }

    private async void OnRevokeInvitation(object sender, RoutedEventArgs e)
    {
        var row = PairingInvitations.SelectedItem as RemoteInvitationRow;
        await RunAsync(() =>
        {
            if (row is not null && HostController?.Access is { } access)
            {
                var revoked = access.RevokeInvitation(row.Invitation.Id);
                Status.Text = revoked ? "Pairing link revoked. Existing device sessions are unchanged." : "The link was already used, revoked, or expired.";
            }
            return Task.CompletedTask;
        });
    }

    private async void OnApproveDevice(object sender, RoutedEventArgs e)
    {
        var device = PendingDevices.SelectedItem as PendingRemoteDevice;
        var confirmed = VerificationConfirmed.IsChecked == true;
        await RunAsync(() =>
        {
            if (device is not null)
            {
                if (!confirmed || string.IsNullOrWhiteSpace(device.VerificationCode))
                    throw new InvalidOperationException("Compare the verification code on both devices before approving.");
                HostController?.Access?.Approve(device.RequestId, device.VerificationCode);
                VerificationConfirmed.IsChecked = false;
                Status.Text = $"Approved {device.DeviceName}.";
            }
            return Task.CompletedTask;
        });
    }

    private async void OnRejectDevice(object sender, RoutedEventArgs e)
    {
        var device = PendingDevices.SelectedItem as PendingRemoteDevice;
        await RunAsync(() =>
        {
            if (device is not null)
            { HostController?.Access?.Reject(device.RequestId); Status.Text = "Pairing request rejected."; }
            return Task.CompletedTask;
        });
    }

    private async void OnRevokeDevice(object sender, RoutedEventArgs e)
    {
        var row = ApprovedDevices.SelectedItem as RemoteSessionRow;
        await RunAsync(() =>
        {
            if (row is not null)
            { HostController?.Access?.Revoke(row.Device.DeviceId); Status.Text = $"Access revoked for {row.Name}."; }
            return Task.CompletedTask;
        });
    }

    private async void OnPairDevice(object sender, RoutedEventArgs e)
    {
        if (_pairing is not null) return;
        using var cancellation = new CancellationTokenSource();
        _pairing = cancellation;
        PairDevice.IsEnabled = false;
        CancelPairing.IsEnabled = true;
        PairingProgress.Text = string.Empty;
        try
        {
            var invitation = RemoteInvitation.Parse(IncomingLink.Text);
            Status.Text = $"Connecting to {invitation.Address.Host}. Certificate: {invitation.CertificateFingerprint}";
            var progress = new Progress<string>(message =>
            {
                if (_active && ReferenceEquals(_pairing, cancellation) && !cancellation.IsCancellationRequested)
                    PairingProgress.Text = message;
            });
            var environment = await RemotePairingClient.PairAsync(invitation, DeviceName.Text, progress, cancellation.Token);
            if (Controller?.Connections.Load().FirstOrDefault(saved => saved.EnvironmentId == environment.EnvironmentId) is { } previous)
                environment = environment with { ClientId = previous.ClientId };
            Controller?.Connections.Save(environment);
            IncomingLink.Text = string.Empty;
            RefreshSaved();
            Status.Text = $"Paired with {environment.Name}. Select Open to connect.";
        }
        catch (OperationCanceledException) { Status.Text = "Pairing canceled or expired. Create a fresh link to try again."; }
        catch (Exception exception) { Status.Text = $"Could not pair: {exception.Message}"; }
        finally { _pairing = null; PairDevice.IsEnabled = true; CancelPairing.IsEnabled = false; PairingProgress.Text = string.Empty; }
    }

    private void OnCancelPairing(object sender, RoutedEventArgs e) => _pairing?.Cancel();

    private void OnAllowRemoteUpdatesChanged(object sender, RoutedEventArgs e)
    {
        if (!_active || _busy || HostController is not { CanHost: true } controller) return;
        try { controller.AllowsRemoteUpdates = AllowRemoteUpdates.IsOn; }
        catch (Exception exception) { Status.Text = exception.Message; }
    }

    private async void OnCopyDiagnostics(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        if (SavedEnvironments.SelectedItem is SavedRemoteEnvironment saved && Application.Current is App app)
        {
            var data = new DataPackage();
            data.SetText(app.GetRemoteDiagnostics(saved));
            Clipboard.SetContent(data);
            Status.Text = "Connection diagnostics copied. Credentials and file contents are excluded.";
        }
        return Task.CompletedTask;
    });

    private async void OnTestEndpoint(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (SavedEnvironments.SelectedItem is not SavedRemoteEnvironment saved) return;
        var candidate = saved with { Address = new Uri(EndpointAddress.Text.Trim()) };
        RemoteEndpoint.Validate(candidate.Address);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _pairing = cancellation;
        try
        {
            await using var probe = new EnvironmentClient(candidate.CreateOptions());
            await probe.ConnectAsync(cancellation.Token);
            Status.Text = $"Verified {probe.Descriptor!.EnvironmentName} • host {probe.Descriptor.ServerVersion}. Address has not been saved.";
        }
        finally { _pairing = null; }
    });

    private async void OnSaveEndpoint(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (SavedEnvironments.SelectedItem is not SavedRemoteEnvironment saved || Application.Current is not App app) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _pairing = cancellation;
        try
        {
            Status.Text = "Verifying the new address and host identity…";
            await app.ReplaceRemoteAddressAsync(saved, new Uri(EndpointAddress.Text.Trim()), cancellation.Token);
            RefreshSaved();
            Status.Text = "Verified address saved. Open work and device identity are preserved.";
        }
        finally { _pairing = null; }
    });

    private async void OnOpenEnvironment(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (SavedEnvironments.SelectedItem is SavedRemoteEnvironment environment && Application.Current is App app)
            await app.OpenRemoteEnvironmentAsync(environment);
    });

    private async void OnInstallHostUpdate(object sender, RoutedEventArgs e) => await RunAsync(() => RunUpdateAsync(false));
    private async void OnCheckHostUpdate(object sender, RoutedEventArgs e) => await RunAsync(() => RunUpdateAsync(true));

    private async Task RunUpdateAsync(bool historyOnly)
    {
        if (SavedEnvironments.SelectedItem is not SavedRemoteEnvironment saved || Application.Current is not App app) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        _pairing = cancellation;
        var existing = app.FindRemoteClient(saved.EnvironmentId.ToString());
        var client = existing ?? new EnvironmentClient(saved.CreateOptions());
        try { await RemoteUpdateWorkflow.RunAsync(client, XamlRoot, new Progress<string>(message => Status.Text = message), historyOnly, cancellation.Token); }
        finally { _pairing = null; if (existing is null) await client.DisposeAsync(); }
    }

    private async void OnDisconnectEnvironment(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (SavedEnvironments.SelectedItem is SavedRemoteEnvironment environment && Application.Current is App app)
            await app.CloseRemoteEnvironmentAsync(environment.EnvironmentId.ToString());
    });

    private async void OnForgetEnvironment(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (SavedEnvironments.SelectedItem is SavedRemoteEnvironment environment)
        {
            if (Application.Current is App app) await app.CloseRemoteEnvironmentAsync(environment.EnvironmentId.ToString());
            Controller?.Connections.Forget(environment.EnvironmentId);
            RefreshSaved();
            Status.Text = "Saved connection removed. To remove its host authorization too, revoke the device on the host.";
        }
    });

    private void OnPendingSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PendingDetails.Text = PendingDevices.SelectedItem is PendingRemoteDevice device
            ? $"Verification code: {device.VerificationCode ?? "unavailable"}\n{device.AccessLevel} · expires {device.ExpiresAt.ToLocalTime():t}"
            : string.Empty;
        VerificationConfirmed.IsChecked = false;
        UpdateApprovalState();
    }

    private void OnVerificationChanged(object sender, RoutedEventArgs e) => UpdateApprovalState();

    private void UpdateApprovalState()
    {
        var hasCode = HostController?.Access is not null && PendingDevices.SelectedItem is PendingRemoteDevice { VerificationCode: { Length: > 0 } };
        VerificationConfirmed.IsEnabled = !_busy && hasCode;
        ApproveDevice.IsEnabled = !_busy && hasCode && VerificationConfirmed.IsChecked == true;
        RejectDevice.IsEnabled = !_busy && HostController?.Access is not null && PendingDevices.SelectedItem is PendingRemoteDevice;
    }
    private void OnApprovedSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingLists) UpdateSessionDetails();
    }

    private void UpdateSessionDetails()
    {
        ApprovedDetails.Text = (ApprovedDevices.SelectedItem as RemoteSessionRow)?.Details ?? string.Empty;
        RevokeDevice.IsEnabled = !_busy && HostController?.Access is not null && ApprovedDevices.SelectedItem is RemoteSessionRow;
    }

    private void OnInvitationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingLists) UpdateInvitationDisplay();
    }

    private void UpdateInvitationDisplay()
    {
        var row = PairingInvitations.SelectedItem as RemoteInvitationRow;
        InvitationDetails.Text = row?.Details ?? string.Empty;
        RevokeInvitation.IsEnabled = !_busy && HostController?.Access is not null && row is not null;
        var url = _active ? _pageState.GetUrl(row?.Invitation.Id, DateTimeOffset.UtcNow) : null;
        PairingShareHint.Text = row is null ? "Select or create a pairing link to share it."
            : url is null ? "The secret is unavailable on this page. Create a new link to share; this link can still be revoked."
            : "The link and QR code are secrets. They can be copied only during this Settings visit. Paste the link into another PiStation desktop to pair.";
        CopyPairingLink.IsEnabled = !_busy && url is not null;
        if (_displayedPairingUrl == url) return;
        ClearLinkDisplay();
        _displayedPairingUrl = url;
        PairingLink.Text = url ?? string.Empty;
        CopyPairingLink.IsEnabled = !_busy && url is not null;
        if (url is not null) _ = RenderPairingQrAsync(url, _qrGeneration);
    }

    private void ClearLinkDisplay()
    {
        _qrGeneration++;
        _displayedPairingUrl = null;
        PairingLink.Text = string.Empty;
        PairingQr.Source = null;
        PairingQrContainer.Visibility = Visibility.Collapsed;
        CopyPairingLink.IsEnabled = false;
    }

    private async Task RenderPairingQrAsync(string url, int generation)
    {
        byte[]? bytes = null;
        try
        {
            bytes = RemotePairingPageState.CreateQrPng(url);
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var source = new BitmapImage();
            await source.SetSourceAsync(stream);
            // An asynchronous image load must not restore an expired/revoked/hidden secret.
            if (!_active || generation != _qrGeneration || _pageState.GetUrl(
                (PairingInvitations.SelectedItem as RemoteInvitationRow)?.Invitation.Id, DateTimeOffset.UtcNow) != url) return;
            PairingQr.Source = source;
            PairingQrContainer.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            if (_active && generation == _qrGeneration)
                PairingShareHint.Text = "QR display unavailable. You can still copy the pairing link.";
        }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
    private void OnSavedSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SavedDetails.Text = SavedEnvironments.SelectedItem is SavedRemoteEnvironment environment ? environment.Address.AbsoluteUri : string.Empty;
        EndpointAddress.Text = SavedDetails.Text;
    }
}
