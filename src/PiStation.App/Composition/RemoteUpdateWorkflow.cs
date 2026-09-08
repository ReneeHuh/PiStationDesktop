using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PiStation.ClientRuntime;
using PiStation.Protocol.Models;
using Windows.Storage.Pickers;

namespace PiStation.App.Composition;

internal static class RemoteUpdateWorkflow
{
    internal static async Task RunAsync(EnvironmentClient client, XamlRoot root, IProgress<string> progress,
        bool historyOnly, CancellationToken cancellationToken)
    {
        if (client.ConnectionState != EnvironmentConnectionState.Connected) await client.ConnectAsync(cancellationToken);
        var descriptor = await client.GetRemoteUpdateDescriptorAsync(cancellationToken);
        if (historyOnly)
        {
            var history = await client.GetRemoteUpdateHistoryAsync(cancellationToken);
            if (history.Length == 0) { progress.Report("No update requests for this device."); return; }
            var latest = history[0];
            progress.Report(Describe(latest));
            if (latest.State is RemoteUpdateState.Ready or RemoteUpdateState.WaitingForIdle or RemoteUpdateState.Uploading)
            {
                var cancel = new ContentDialog { XamlRoot = root, Title = "Pending host update", Content = Describe(latest),
                    PrimaryButtonText = "Cancel update", CloseButtonText = "Keep", DefaultButton = ContentDialogButton.Close };
                using var registration = cancellationToken.Register(() => root.Content.DispatcherQueue.TryEnqueue(cancel.Hide));
                if (await cancel.ShowAsync() == ContentDialogResult.Primary)
                    progress.Report(Describe(await client.CancelRemoteUpdateAsync(latest.RequestId, cancellationToken)));
            }
            return;
        }
        if (!descriptor.Supported) throw new InvalidOperationException("This host has no update owner. For a standalone host, start PiStation.Server supervise. Connection-owned SSH hosts use Update and reconnect.");
        if (!descriptor.Enabled) throw new InvalidOperationException("The host owner must enable remote updates in local Connections settings, or start supervise with --enable-remote-updates true.");
        var window = (Application.Current as App)?.FindWindow(root) ?? throw new InvalidOperationException("The connection window is unavailable.");
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(descriptor.PackageKind);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var file = await picker.PickSingleFileAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (file is null) return;
        var id = Guid.NewGuid();
        progress.Report($"Uploading and validating {file.Name}. Request {id:D}…");
        RemoteUpdateReceipt receipt;
        try
        {
            receipt = await client.StageRemoteUpdateAsync(file.Path, id,
                new Progress<long>(bytes => progress.Report($"Uploading {bytes / (1024 * 1024)} MiB · {id:D}")), cancellationToken);
        }
        catch
        {
            progress.Report($"Upload interrupted. Check update status before retrying. Request {id:D}.");
            throw;
        }
        if (receipt.State != RemoteUpdateState.Ready) { progress.Report(Describe(receipt)); return; }
        var interrupt = new CheckBox { Content = "Interrupt active agents and terminals", IsChecked = false };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap,
            Text = $"{descriptor.HostKind}: {descriptor.CurrentVersion} → {receipt.TargetVersion}\n{descriptor.Trust}\nThe host will restart and clients will reconnect. By default, activation waits for running work to finish. Unsaved desktop edits must be saved first." });
        body.Children.Add(interrupt);
        var dialog = new ContentDialog { XamlRoot = root, Title = "Install verified host update?", Content = body,
            PrimaryButtonText = "Install", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        using var canceled = cancellationToken.Register(() => root.Content.DispatcherQueue.TryEnqueue(dialog.Hide));
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || cancellationToken.IsCancellationRequested)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.CancelRemoteUpdateAsync(id, cleanup.Token);
            progress.Report("Update canceled before activation.");
            return;
        }
        try { receipt = await client.CommitRemoteUpdateAsync(new(id, interrupt.IsChecked == true), cancellationToken); }
        catch
        {
            progress.Report($"Activation response was interrupted. Use Check update status to recover request {id:D}; do not submit a new update.");
            return;
        }
        progress.Report(Describe(receipt) + " Use Check update status after reconnecting; pending activation can be canceled there.");
    }

    private static string Describe(RemoteUpdateReceipt receipt) =>
        $"Update {receipt.RequestId:D}: {receipt.State} · version {receipt.TargetVersion ?? "pending validation"}\n{receipt.Message}";
}
