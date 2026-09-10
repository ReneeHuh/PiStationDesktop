using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using PiStation.App.ViewModels;
using Windows.UI.ViewManagement;

namespace PiStation.App.Views.Controls;

/// <summary>Retains a closing panel until its transition ends; interrupts obsolete transitions.</summary>
internal sealed class PanelMotion
{
    private readonly FrameworkElement _panel;
    private readonly ShellLayoutViewModel _settings;
    private readonly UISettings _system = new();
    private readonly TranslateTransform _offset = new();
    private Storyboard? _story;
    private bool _open;
    private bool _loaded;
    private bool _released;

    public PanelMotion(FrameworkElement panel, ShellLayoutViewModel settings, bool open)
    {
        _panel = panel; _settings = settings; _open = open;
        panel.RenderTransform = _offset;
        Settle();
        panel.Loaded += OnLoaded;
        panel.Unloaded += OnUnloaded;
    }
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded || _released) return;
        _loaded = true; _system.AnimationsEnabledChanged += OnAnimationsChanged; _settings.PropertyChanged += OnPreferencesChanged;
    }
    private void OnUnloaded(object sender, RoutedEventArgs args) => Detach();
    private void Detach()
    {
        if (_loaded) { _system.AnimationsEnabledChanged -= OnAnimationsChanged; _settings.PropertyChanged -= OnPreferencesChanged; }
        _loaded = false; Settle();
    }
    public void Release()
    {
        _released = true;
        _panel.Loaded -= OnLoaded; _panel.Unloaded -= OnUnloaded;
        Detach();
    }
    private void OnPreferencesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ShellLayoutViewModel.Appearance) && _settings.PanelAnimationDurationMs == 0) Settle();
    }
    private void OnAnimationsChanged(UISettings sender, object args) => _panel.DispatcherQueue.TryEnqueue(() => { if (!_released && !_system.AnimationsEnabled) Settle(); });

    public void SetOpen(bool open, bool reveal = false)
    {
        if (_released) return;
        if (open == _open && !reveal) return;
        var wasOpen = _open;
        _open = open;
        var duration = _settings.Appearance.EffectiveAnimationDuration(_system.AnimationsEnabled, _loaded);
        var opacity = _panel.Opacity;
        _story?.Stop(); _story = null;
        if (duration <= 0) { Settle(); return; }
        _panel.Visibility = Visibility.Visible;
        _panel.IsHitTestVisible = open;
        var story = new Storyboard();
        var fade = new DoubleAnimation { From = open && (!wasOpen || reveal) ? 0 : opacity, To = open ? 1 : 0, Duration = TimeSpan.FromMilliseconds(duration) };
        Storyboard.SetTarget(fade, _panel); Storyboard.SetTargetProperty(fade, "Opacity"); story.Children.Add(fade);
        var slide = new DoubleAnimation { From = open ? 12 : 0, To = open ? 0 : 12, Duration = TimeSpan.FromMilliseconds(duration), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(slide, _offset); Storyboard.SetTargetProperty(slide, "X"); story.Children.Add(slide);
        story.Completed += (_, _) => { if (ReferenceEquals(_story, story)) Settle(); };
        _story = story; story.Begin();
    }
    private void Settle()
    {
        _story?.Stop(); _story = null;
        _panel.Opacity = 1; _offset.X = 0;
        _panel.Visibility = _open ? Visibility.Visible : Visibility.Collapsed;
        _panel.IsHitTestVisible = _open;
    }
}
