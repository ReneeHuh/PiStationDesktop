using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PiStation.Protocol.Models;
using Windows.ApplicationModel.DataTransfer;

namespace PiStation.App.Views.Controls;

public sealed partial class AppSidebar
{
    private ThreadDescriptor? _draggedPin;
    private void OnPinnedDragStarting(object sender, DragItemsStartingEventArgs args)
    {
        _draggedPin = args.Items.Count == 1 ? args.Items[0] as ThreadDescriptor : null;
        if (_draggedPin is not { IsPinned: true }) { args.Cancel = true; _draggedPin = null; return; }
        args.Data.SetText(_draggedPin.Title);
        args.Data.RequestedOperation = DataPackageOperation.Move;
    }
    private void OnPinnedDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => _draggedPin = null;
    private void OnPinnedDragOver(object sender, DragEventArgs args)
    {
        args.AcceptedOperation = _draggedPin is null ? DataPackageOperation.None : DataPackageOperation.Move;
        args.Handled = true;
    }
    private async void OnPinnedDrop(object sender, DragEventArgs args)
    {
        var moving = _draggedPin;
        _draggedPin = null;
        if (moving is null) return;
        var element = args.OriginalSource as DependencyObject;
        while (element is not null && element is not FrameworkElement { DataContext: ThreadDescriptor })
            element = VisualTreeHelper.GetParent(element);
        if (element is not FrameworkElement { DataContext: ThreadDescriptor { IsPinned: true } target } || target.ProjectId != moving.ProjectId) return;
        await ViewModel.SelectGroupedThreadAsync(moving);
        var pinned = ViewModel.Workspace.Threads.Where(thread => thread.IsPinned).ToList();
        var sourceIndex = pinned.FindIndex(thread => thread.ThreadId == moving.ThreadId);
        var targetIndex = pinned.FindIndex(thread => thread.ThreadId == target.ThreadId);
        if (sourceIndex < 0 || targetIndex < 0) return;
        args.Handled = true;
        await ViewModel.MovePinnedThreadAsync(moving, targetIndex - sourceIndex);
    }
}
