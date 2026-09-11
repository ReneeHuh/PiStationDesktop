using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PiStation.App.Composition;
using PiStation.App.ViewModels;

namespace PiStation.App.Views.Controls;

public sealed partial class ComposerSurface
{
    private bool _readingHistory;
    private bool _scrollCollapsed;
    private bool _resting;
    private readonly HashSet<object> _openComposerPopups = [];
    private FlyoutBase? _promptContextFlyout;

    private void OnRestingLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Layout.PropertyChanged += OnInteractionPreferenceChanged;
        ViewModel.Composer.PropertyChanged += OnRestingComposerChanged;
        _promptContextFlyout = PromptInput.ContextFlyout;
        if (_promptContextFlyout is not null)
        {
            _promptContextFlyout.Opening += OnComposerPopupOpened;
            _promptContextFlyout.Closed += OnComposerPopupClosed;
        }
        UpdateRestingState();
    }

    private void OnRestingUnloaded(object sender, RoutedEventArgs e)
    {
        _openComposerPopups.Clear();
        if (_promptContextFlyout is not null)
        {
            _promptContextFlyout.Opening -= OnComposerPopupOpened;
            _promptContextFlyout.Closed -= OnComposerPopupClosed;
            _promptContextFlyout = null;
        }
        ViewModel.Layout.PropertyChanged -= OnInteractionPreferenceChanged;
        ViewModel.Composer.PropertyChanged -= OnRestingComposerChanged;
    }

    public void SetReadingHistory(bool readingHistory)
    {
        if (_readingHistory == readingHistory) return;
        _readingHistory = readingHistory;
        _scrollCollapsed = readingHistory;
        UpdateRestingState();
    }

    private void OnInteractionPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellLayoutViewModel.ShowSkillsInSlashMenu))
            ViewModel.UpdateComposerDiscoveryQuery(PromptInput.Text, PromptInput.SelectionStart);
        UpdateRestingState();
    }

    private void OnRestingComposerChanged(object? sender, PropertyChangedEventArgs e) => UpdateRestingState();

    // ComboBox and flyout content has a separate visual root. Losing focus to it
    // is still interaction with the composer, not a request to hide its controls.
    private void OnComposerPopupOpened(object? sender, object e)
    {
        if (sender is null) return;
        _openComposerPopups.Add(sender);
        _scrollCollapsed = false;
        UpdateRestingState();
    }

    private void OnComposerPopupClosed(object? sender, object e)
    {
        if (sender is null) return;
        _openComposerPopups.Remove(sender);
        DispatcherQueue.TryEnqueue(UpdateRestingState);
    }

    private void OnComposerFocusChanged(object sender, RoutedEventArgs e)
    {
        if (!_resting && ContainsKeyboardFocus()) _scrollCollapsed = false;
        DispatcherQueue.TryEnqueue(UpdateRestingState);
    }

    private bool ContainsKeyboardFocus()
    {
        if (XamlRoot is null) return false;
        for (var element = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
             element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, RestingComposerSurface)) return false;
            if (ReferenceEquals(element, this)) return true;
        }
        return false;
    }

    private void UpdateRestingState()
    {
        if (!IsLoaded || XamlRoot is null) return;
        var hadFocus = ContainsKeyboardFocus();
        SetResting(ComposerPresentation.ShouldCollapse(hadFocus, _scrollCollapsed,
            ViewModel.Layout.ComposerCollapseOnBlur, ViewModel.Layout.ComposerCollapseOnScroll,
            ViewModel.Composer.HasRecoveryConflict, _openComposerPopups.Count != 0));
        // Compacting during history scrolling preserves the editing focus.
        RestingComposerPreview.Text = ComposerPresentation.Summary(ViewModel.PromptText,
            ViewModel.Composer.Attachments.Count, ViewModel.ComposerPower.ContextChips.Count);
    }

    private void SetResting(bool resting)
    {
        _resting = resting;
        // Keep the real editor in both presentations; focusing it expands it.
        ExpandedComposer.Visibility = Visibility.Visible;
        RestingComposerSurface.Visibility = Visibility.Collapsed;
        ComposerRoot.MinHeight = resting ? 76 : (double)Application.Current.Resources["PiComposerMinHeight"];
        PromptInput.MinHeight = resting ? 32 : 48;
        if (resting) PromptInput.Height = 32;
        ComposerFooterStatus.Visibility = resting ? Visibility.Collapsed : Visibility.Visible;
        if (!resting) UpdatePromptHeight();
    }

    private void OnExpandComposer(object sender, RoutedEventArgs e) => FocusPrompt();
}
