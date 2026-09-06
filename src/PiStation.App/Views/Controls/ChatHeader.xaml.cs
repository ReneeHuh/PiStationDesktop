using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using PiStation.App.ViewModels;
using PiStation.Protocol.Models;
using Windows.Storage;
using Windows.System;

namespace PiStation.App.Views.Controls;

public sealed partial class ChatHeader : UserControl
{
    public ChatHeader(ShellViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    public ShellViewModel ViewModel { get; }

    public event EventHandler? CommandPaletteRequested;

    private async void OnNewThreadClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.CreateThreadAsync();

    private void OnCommandPaletteClicked(object sender, RoutedEventArgs e) =>
        CommandPaletteRequested?.Invoke(this, EventArgs.Empty);

    private void OnAddActionFlyoutOpening(object? sender, object e)
    {
        var markerIndex = AddActionFlyout.Items.IndexOf(ProjectScriptsSeparator);
        while (AddActionFlyout.Items.Count > markerIndex + 1)
        {
            AddActionFlyout.Items.RemoveAt(AddActionFlyout.Items.Count - 1);
        }

        var scripts = ViewModel.Workspace.SelectedProject?.Scripts ?? [];
        ProjectScriptsSeparator.Visibility = scripts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var script in scripts)
        {
            var item = new MenuFlyoutItem
            {
                Text = $"Run {script.Name}",
                Tag = script,
                Icon = new FontIcon { Glyph = "\uE768" },
            };
            AutomationProperties.SetAutomationId(item, $"RunProjectScript-{script.Id}");
            AutomationProperties.SetName(item, $"Run project script {script.Name}");
            item.Click += OnRunProjectScriptClicked;
            AddActionFlyout.Items.Add(item);
        }
    }

    private async void OnRunProjectScriptClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: ProjectScript script })
        {
            return;
        }

        var project = ViewModel.Workspace.SelectedProject;
        if (project is null)
        {
            return;
        }

        if (!project.AreRepositoryScriptsTrusted)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Trust and run {script.Name}?",
                Content = $"{script.Command}\n\nThis command comes from the repository's t3.json.",
                PrimaryButtonText = "Trust and run",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await ViewModel.TrustSelectedProjectScriptsAsync();
        }

        await ViewModel.RunProjectScriptAsync(script);
    }

    private async void OnNewLocalThreadClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.CreateThreadInWorkspaceAsync(ThreadWorkspaceMode.Local);

    private async void OnNewWorktreeThreadClicked(object sender, RoutedEventArgs e)
    {
        if (await ConfirmRepositorySetupScriptAsync())
        {
            await ViewModel.CreateThreadInWorkspaceAsync(ThreadWorkspaceMode.Worktree);
        }
    }

    private async void OnNewOriginWorktreeThreadClicked(object sender, RoutedEventArgs e)
    {
        if (await ConfirmRepositorySetupScriptAsync())
        {
            await ViewModel.CreateThreadInWorkspaceAsync(ThreadWorkspaceMode.Worktree, startFromOrigin: true);
        }
    }

    private async Task<bool> ConfirmRepositorySetupScriptAsync()
    {
        var project = ViewModel.Workspace.SelectedProject;
        var script = project?.Scripts?.FirstOrDefault(static candidate => candidate.RunOnWorktreeCreate);
        if (project is null || script is null || project.AreRepositoryScriptsTrusted)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Trust this repository setup script?",
            Content = $"{script.Name}\n\n{script.Command}\n\nThe command comes from t3.json and will run in each new worktree.",
            PrimaryButtonText = "Trust and continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return false;
        }

        try
        {
            await ViewModel.TrustSelectedProjectScriptsAsync();
            return true;
        }
        catch (Exception exception)
        {
            ViewModel.ReportRuntimeError($"Could not save script trust: {exception.Message}");
            return false;
        }
    }

    private async void OnOpenProjectClicked(object sender, RoutedEventArgs e)
    {
        var project = ViewModel.Workspace.SelectedProject;
        if (project is null)
        {
            return;
        }

        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(project.CanonicalPath);
            if (!await Launcher.LaunchFolderAsync(folder))
            {
                ViewModel.ReportRuntimeError("Windows could not open the selected project folder.");
            }
        }
        catch (Exception exception)
        {
            ViewModel.ReportRuntimeError($"Could not open the selected project folder: {exception.Message}");
        }
    }

    private void OnToggleWorkbenchClicked(object sender, RoutedEventArgs e) =>
        ViewModel.Layout.ToggleRightPanel();
}
