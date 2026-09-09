using Microsoft.UI.Xaml;
using PiStation.App.ViewModels;

namespace PiStation.App.Views.Controls;

public sealed partial class RightPanelHost
{
    private readonly Dictionary<string, TaskCompletionSource> _previewLinkLoads = new(StringComparer.Ordinal);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _previewDiscoveryTimer;

    private void StartPreviewDiscoveryPolling()
    {
        _previewDiscoveryTimer ??= DispatcherQueue.CreateTimer();
        _previewDiscoveryTimer.Interval = TimeSpan.FromSeconds(3);
        _previewDiscoveryTimer.Tick -= OnPreviewDiscoveryTick;
        _previewDiscoveryTimer.Tick += OnPreviewDiscoveryTick;
        _previewDiscoveryTimer.Start();
    }

    private async void OnPreviewDiscoveryTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (IsLoaded && ViewModel.IsConnected && ViewModel.Layout.IsRightPanelOpen &&
            ViewModel.Layout.SelectedPanel == WorkbenchPanelKind.Preview &&
            ViewModel.WorkbenchPreview.EmptyStateVisibility == Visibility.Visible && ViewModel.WorkbenchPreview.CanDiscover)
            await ViewModel.RefreshWorkbenchPreviewServersAsync(background: true);
    }

    private async Task OpenBrowserLinkInPreviewAsync(Uri uri)
    {
        if (!ViewModel.WorkbenchPreview.CanAddTab) throw new InvalidOperationException("The preview tab limit was reached.");
        var tab = ViewModel.AddWorkbenchPreviewTab();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _previewLinkLoads[tab.TabId] = pending;
        try
        {
            ViewModel.PrepareWorkbenchPreviewNavigation(uri.AbsoluteUri);
            ViewModel.Layout.SelectedPanel = WorkbenchPanelKind.Preview;
            ViewModel.Layout.IsRightPanelOpen = true;
            if (_previewSurfaces.TryGetValue(tab.TabId, out var surface))
            {
                await NavigateTabIfNeededAsync(tab, surface);
                CompletePreviewLinkLoad(tab, surface);
            }
            await pending.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch
        {
            if (_previewSurfaces.TryGetValue(tab.TabId, out var surface)) ReleasePreviewSurface(tab.TabId, surface);
            if (ViewModel.WorkbenchPreview.Tabs.Contains(tab)) ViewModel.CloseWorkbenchPreviewTab(tab);
            throw;
        }
        finally { _previewLinkLoads.Remove(tab.TabId); }
    }

    private void CompletePreviewLinkLoad(WorkbenchPreviewTabViewModel tab, PreviewWebViewSurface surface)
    {
        if (!_previewLinkLoads.TryGetValue(tab.TabId, out var pending)) return;
        if (!ViewModel.WorkbenchPreview.Tabs.Contains(tab)) pending.TrySetCanceled();
        else if (!surface.IsInitialized || tab.FailureKind != PreviewFailureKind.None)
            pending.TrySetException(new InvalidOperationException(tab.FailureMessage));
        else pending.TrySetResult();
    }
}
