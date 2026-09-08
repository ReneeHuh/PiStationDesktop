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
                    PrimaryButtonText = "Cancel update", SecondaryButtonText = latest.State == RemoteUpdateState.WaitingForIdle ? "Follow progress" : string.Empty,
                    CloseButtonText = "Keep", DefaultButton = ContentDialogButton.Close };
                using var registration = cancellationToken.Register(() => root.Content.DispatcherQueue.TryEnqueue(cancel.Hide));
                var choice = await ((App)Application.Current).ShowConnectionDialogAsync(cancel, cancellationToken);
                if (choice == ContentDialogResult.Primary)
                    progress.Report(Describe(await client.CancelRemoteUpdateAsync(latest.RequestId, cancellationToken)));
                else if (choice == ContentDialogResult.Secondary)
                    await FollowAsync(client, latest.RequestId, root, progress, cancellationToken);
            }
            else if (latest.State == RemoteUpdateState.Restarting)
                await FollowAsync(client, latest.RequestId, root, progress, cancellationToken);
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
            Text = $"{descriptor.HostKind}: {descriptor.CurrentVersion} → {receipt.TargetVersion}\n{descriptor.Trust}\nThe host will restart and clients will reconnect. By default, activation waits for agents to finish and all terminals to close, including idle shells. Unsaved host desktop edits must be saved first." });
        body.Children.Add(interrupt);
        var dialog = new ContentDialog { XamlRoot = root, Title = "Install verified host update?", Content = body,
            PrimaryButtonText = "Install", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        using var canceled = cancellationToken.Register(() => root.Content.DispatcherQueue.TryEnqueue(dialog.Hide));
        if (await ((App)Application.Current).ShowConnectionDialogAsync(dialog, cancellationToken) != ContentDialogResult.Primary || cancellationToken.IsCancellationRequested)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.CancelRemoteUpdateAsync(id, cleanup.Token);
            progress.Report("Update canceled before activation.");
            return;
        }
        try { receipt = await client.CommitRemoteUpdateAsync(new(id, interrupt.IsChecked == true), cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            progress.Report($"Stopped checking. Activation may already have been accepted; check request {id:D} before retrying.");
            return;
        }
        catch
        {
            progress.Report($"Activation response was interrupted. Recovering request {id:D}…");
        }
        await FollowAsync(client, id, root, progress, cancellationToken);
    }

    private static async Task FollowAsync(EnvironmentClient client, Guid id, XamlRoot root,
        IProgress<string> progress, CancellationToken cancellationToken)
    {
        var text = new TextBlock { Text = $"Checking update {id:D}…", TextWrapping = TextWrapping.Wrap };
        var dialog = new ContentDialog { XamlRoot = root, Title = "Host update progress", Content = text,
            PrimaryButtonText = "Cancel pending update", CloseButtonText = "Stop checking", DefaultButton = ContentDialogButton.Close };
        using var monitor = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        monitor.CancelAfter(TimeSpan.FromMinutes(10));
        using var registration = monitor.Token.Register(() => root.Content.DispatcherQueue.TryEnqueue(dialog.Hide));
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Opened += (_, _) => opened.TrySetResult();
        var shown = ((App)Application.Current).ShowConnectionDialogAsync(dialog, monitor.Token);
        if (await Task.WhenAny(opened.Task, shown) == shown) { await shown; return; }
        async Task<RemoteUpdateReceipt?> ReadAsync(CancellationToken token)
        {
            try
            {
                if (client.ConnectionState != EnvironmentConnectionState.Connected) await client.ConnectAsync(token);
                return await client.GetRemoteUpdateReceiptAsync(id, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch when (client.ConnectionState is not (EnvironmentConnectionState.AuthenticationRequired or EnvironmentConnectionState.TrustRequired or EnvironmentConnectionState.Incompatible))
            {
                return null;
            }
        }
        var observed = new Progress<RemoteUpdateReceipt>(current =>
        {
            text.Text = Describe(current);
            progress.Report(text.Text);
            dialog.IsPrimaryButtonEnabled = current.State is RemoteUpdateState.WaitingForIdle or RemoteUpdateState.Uploading or RemoteUpdateState.Ready;
        });
        var follow = RemoteUpdateMonitor.FollowAsync(id, ReadAsync, observed, TimeSpan.FromSeconds(1), monitor.Token);
        try
        {
            if (await Task.WhenAny(follow, shown) == follow)
            {
                var final = await follow;
                progress.Report(Describe(final) + (final.State == RemoteUpdateState.Ready ? " Activation was not confirmed; the verified package remains staged." : string.Empty));
                dialog.Hide();
            }
            else if (await shown == ContentDialogResult.Primary)
                progress.Report(Describe(await client.CancelRemoteUpdateAsync(id, cancellationToken)));
            else progress.Report($"Stopped checking update {id:D}. The host keeps processing it; use Check update status to resume.");
        }
        catch (OperationCanceledException) when (monitor.IsCancellationRequested)
        {
            progress.Report($"Stopped checking update {id:D}. Use Check update status to recover its outcome.");
        }
        finally
        {
            await monitor.CancelAsync();
            dialog.Hide();
            try { await follow; } catch (OperationCanceledException) when (monitor.IsCancellationRequested) { }
            await shown;
        }
    }

    private static string Describe(RemoteUpdateReceipt receipt) =>
        $"Update {receipt.RequestId:D}: {receipt.State} · version {receipt.TargetVersion ?? "pending validation"}\n{receipt.Message}";
}
