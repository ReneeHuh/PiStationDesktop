using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PiStation.App.Composition;
using PiStation.App.ViewModels;

namespace PiStation.App.Views.Controls;

public sealed partial class BrowserSettingsPanel : UserControl
{
    private bool _updating;
    private bool _busy;
    private string? _pendingProfile;
    private bool _remove;
    private CancellationTokenSource? _importCancellation;
    public ShellViewModel ViewModel { get; }

    public BrowserSettingsPanel(ShellViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += (_, _) => { ViewModel.Layout.PropertyChanged += OnSettingsChanged; RefreshProfiles(); };
        Unloaded += (_, _) => { ViewModel.Layout.PropertyChanged -= OnSettingsChanged; _importCancellation?.Cancel(); };
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ShellLayoutViewModel.BrowserProfiles) or nameof(ShellLayoutViewModel.DefaultBrowserProfileId))
            RefreshProfiles();
    }

    private void RefreshProfiles()
    {
        var selectedId = (ProfileSelector.SelectedItem as BrowserProfilePreference)?.Id;
        _updating = true;
        var profiles = ViewModel.Layout.BrowserProfiles;
        DefaultProfileSelector.ItemsSource = profiles;
        DefaultProfileSelector.SelectedItem = profiles.FirstOrDefault(profile => profile.Id == ViewModel.Layout.DefaultBrowserProfileId);
        ProfileSelector.ItemsSource = profiles;
        ProfileSelector.SelectedItem = profiles.FirstOrDefault(profile => profile.Id == selectedId) ?? profiles[0];
        _updating = false;
        UpdateProfileEditor();
    }

    private void OnDefaultProfileChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_updating && DefaultProfileSelector.SelectedItem is BrowserProfilePreference profile)
            ViewModel.Layout.SetDefaultBrowserProfile(profile.Id);
    }

    private void OnProfileChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_updating) { CancelConfirmation(); UpdateProfileEditor(); }
    }

    private void UpdateProfileEditor()
    {
        if (ProfileSelector.SelectedItem is not BrowserProfilePreference profile) return;
        ProfileName.Text = profile.Name;
        RenameButton.IsEnabled = RemoveButton.IsEnabled = profile.Id != "default";
    }

    private void OnCreate(object sender, RoutedEventArgs args)
    {
        try
        {
            var profile = ViewModel.Layout.AddBrowserProfile(ProfileName.Text);
            ProfileSelector.SelectedItem = ViewModel.Layout.BrowserProfiles.First(item => item.Id == profile.Id);
            Status.Text = $"Created {profile.Name}.";
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private void OnRename(object sender, RoutedEventArgs args)
    {
        if (ProfileSelector.SelectedItem is not BrowserProfilePreference profile) return;
        try { ViewModel.Layout.RenameBrowserProfile(profile.Id, ProfileName.Text); Status.Text = "Profile renamed."; }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private void OnClear(object sender, RoutedEventArgs args) => ConfirmAction(false);
    private void OnRemove(object sender, RoutedEventArgs args) => ConfirmAction(true);
    private void ConfirmAction(bool remove)
    {
        if (_busy || ProfileSelector.SelectedItem is not BrowserProfilePreference profile) return;
        _pendingProfile = profile.Id;
        _remove = remove;
        ConfirmationText.Text = $"{(remove ? "Remove" : "Clear data for")} {profile.Name}? This clears cookies, logins, site data and cache across saved environments on this computer. Existing tabs stay open and may create new data.";
        ConfirmButton.Content = remove ? "Remove profile" : "Clear data";
        Confirmation.Visibility = Visibility.Visible;
    }

    private void OnCancel(object sender, RoutedEventArgs args) => CancelConfirmation();
    private void CancelConfirmation() { _pendingProfile = null; Confirmation.Visibility = Visibility.Collapsed; }

    private async void OnConfirm(object sender, RoutedEventArgs args)
    {
        if (_busy || _pendingProfile is not { } profileId) return;
        _busy = true;
        ProfileEditor.IsEnabled = ConfirmButton.IsEnabled = CancelButton.IsEnabled = false;
        Status.Text = "Clearing browser data…";
        try
        {
            if (_remove) await ViewModel.Layout.RemoveBrowserProfileAsync(profileId, ClearDataAsync);
            else await ClearDataAsync(profileId);
            Status.Text = _remove ? "Profile removed. Existing tabs stay open until you close them." : "Browser data cleared.";
            CancelConfirmation();
        }
        catch (Exception error) { Status.Text = $"Could not complete the action: {error.Message}"; }
        finally
        {
            _busy = false;
            ProfileEditor.IsEnabled = ConfirmButton.IsEnabled = CancelButton.IsEnabled = true;
            UpdateProfileEditor();
        }
    }

    private async Task ClearDataAsync(string profileId)
    {
        var root = ViewModel.BrowserDataRoot ?? Path.GetDirectoryName(ViewModel.PreviewProfileRoot)!;
        foreach (var directory in BrowserProfilePaths.ExistingProfileDirectories(root, ViewModel.PreviewProfileRoot, profileId))
        {
            var browser = new WebView2 { Width = 1, Height = 1, IsTabStop = false };
            ClearBrowserHost.Content = browser;
            try { await PreviewWebViewSurface.ClearProfileDataAsync(directory, browser); }
            finally { browser.Close(); ClearBrowserHost.Content = null; }
        }
    }
}
