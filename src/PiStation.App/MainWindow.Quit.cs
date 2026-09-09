using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using PiStation.App.Composition;
using Windows.System;
using Windows.UI.Core;

namespace PiStation.App;

public sealed partial class MainWindow
{
    private readonly QuitGesture _quitGesture = new();
    private DispatcherTimer? _quitTimer;
    private bool _quitRequested;

    private void InitializeQuitGesture()
    {
        RootGrid.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnQuitKeyDown), true);
        RootGrid.AddHandler(UIElement.PreviewKeyUpEvent, new KeyEventHandler(OnQuitKeyUp), true);
        _quitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _quitTimer.Tick += OnQuitTick;
    }

    private static bool IsQuitKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnQuitKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Q || !IsQuitKeyDown(VirtualKey.Control) ||
            IsQuitKeyDown(VirtualKey.Menu) || IsQuitKeyDown(VirtualKey.Shift))
        {
            if (e.Key != VirtualKey.Control) ResetQuitGesture();
            return;
        }
        e.Handled = true;
        if (_quitRequested || _closeDialogOpen) return;
        if (_quitGesture.KeyDown(_viewModel.Layout.QuitConfirmationModeIndex, Environment.TickCount64, e.KeyStatus.WasKeyDown))
        {
            RequestQuitAsync();
            return;
        }
        QuitShortcutHint.Message = _quitGesture.Hint(Environment.TickCount64);
        QuitShortcutHint.IsOpen = _quitGesture.IsPending;
        _quitTimer?.Start();
    }

    private void OnQuitKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Q && _quitGesture.IsPending)
        {
            e.Handled = true;
            if (IsQuitKeyDown(VirtualKey.Control) && _quitGesture.KeyUp(Environment.TickCount64)) RequestQuitAsync();
            else if (!IsQuitKeyDown(VirtualKey.Control)) ResetQuitGesture();
            else QuitShortcutHint.IsOpen = _quitGesture.IsPending;
        }
        else if (e.Key == VirtualKey.Control && _quitGesture.IsHolding) ResetQuitGesture();
    }

    private void OnQuitTick(object? sender, object e)
    {
        if (_quitGesture.IsHolding && (!IsQuitKeyDown(VirtualKey.Control) || !IsQuitKeyDown(VirtualKey.Q)))
        {
            ResetQuitGesture();
            return;
        }
        _quitGesture.Expire(Environment.TickCount64);
        QuitShortcutHint.IsOpen = _quitGesture.IsPending;
        if (!_quitGesture.IsPending) _quitTimer?.Stop();
        else QuitShortcutHint.Message = _quitGesture.Hint(Environment.TickCount64);
    }

    private void ResetQuitGesture()
    {
        _quitGesture.Reset();
        _quitTimer?.Stop();
        QuitShortcutHint.IsOpen = false;
    }

    private async void RequestQuitAsync()
    {
        ResetQuitGesture();
        _quitRequested = true;
        try { if (await ConfirmPlanDiscardAsync()) await CloseWithRecoveryAsync(); }
        catch (Exception exception) { _viewModel.ReportRuntimeError(exception); }
        finally { _quitRequested = false; }
    }
}
