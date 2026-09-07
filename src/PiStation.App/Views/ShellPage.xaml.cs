using System.Collections.ObjectModel;
using System.Globalization;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PiStation.App.Commands;
using PiStation.App.Composition;
using PiStation.App.ViewModels;
using PiStation.App.Views.Controls;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using Windows.System;
using Windows.Storage.Pickers;

namespace PiStation.App.Views;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "WinUI owns Page lifetime; MainWindow calls Release when the window closes.")]
public sealed partial class ShellPage : Page
{
    private readonly AppSidebar _sidebar;
    private readonly ComposerSurface _composerSurface;
    private readonly ConversationTimeline _conversationTimeline;
    private readonly RightPanelHost _rightPanel;
    private readonly CommandRegistry _commands = new();
    private readonly CommandKeybindingManager _keybindings;
    private readonly List<KeyboardAccelerator> _commandAccelerators = [];
    private CancellationTokenSource? _paletteSearchCancellation;
    private bool _paletteOpen;
    private bool _settingsOpen;
    private bool _disposed;

    public ShellPage()
        : this(AppBootstrapper.CreateShellViewModel(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()))
    {
    }

    public ShellPage(ShellViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        _sidebar = new AppSidebar(ViewModel);
        _sidebar.AddProjectRequested += OnAddProjectRequested;
        _sidebar.SettingsRequested += OnSettingsRequested;
        _sidebar.CollapsedChanged += OnSidebarCollapsedChanged;
        _rightPanel = new RightPanelHost(ViewModel);
        _rightPanel.HostingReviewRequested += OnHostingReviewRequested;
        _conversationTimeline = new ConversationTimeline(ViewModel);
        _composerSurface = new ComposerSurface(ViewModel);
        ShellLayout.Sidebar = _sidebar;
        ShellLayout.RightPanel = _rightPanel;
        ConversationTimelineHost.Content = _conversationTimeline;
        RecoveryBannerHost.Content = new RecoveryBannerSurface(ViewModel);
        ComposerSurfaceHost.Content = _composerSurface;
        WorkspaceStatusBarHost.Content = new WorkspaceStatusBar(ViewModel);

        RegisterCommands();
        _keybindings = new CommandKeybindingManager(_commands);
        _keybindings.Changed += OnCommandBindingsChanged;
        _rightPanel.CommandGestureRequested += OnTerminalCommandGestureRequested;
        ViewModel.Layout.PropertyChanged += OnCommandContextPropertyChanged;
        ViewModel.Workspace.PropertyChanged += OnCommandContextPropertyChanged;
        ViewModel.Thread.PropertyChanged += OnCommandContextPropertyChanged;
        ViewModel.WorkbenchTerminal.PropertyChanged += OnCommandContextPropertyChanged;
        _keybindings.Load(ViewModel.Layout.CommandKeybindings.Select(static binding =>
            (binding.CommandId, binding.Gesture, binding.When)));
        CommandPaletteResults.ItemsSource = PaletteItems;
        KeybindingCommandSelector.ItemsSource = _commands.Commands;
        KeybindingOverridesList.ItemsSource = CustomKeybindingRows;
        InstallCommandAccelerators();
        UpdateTerminalCommandGestures();
        RefreshKeybindingRows();
        SynchronizeThemeSelection();
        SynchronizeTerminalAppearanceSelection();
    }

    public ShellViewModel ViewModel { get; }

    public ObservableCollection<CommandPaletteItemViewModel> PaletteItems { get; } = [];

    public ObservableCollection<CommandKeybindingRowViewModel> CustomKeybindingRows { get; } = [];

    public bool IsSidebarCollapsed => _sidebar.IsCollapsed;

    public event EventHandler? SidebarCollapsedChanged;

    public Task OpenCommandPaletteAsync() =>
        _commands.ExecuteAsync("commandPalette.toggle", BuildCommandContext());

    public void Release()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _paletteSearchCancellation?.Cancel();
        _paletteSearchCancellation?.Dispose();
        _paletteSearchCancellation = null;
        _keybindings.Changed -= OnCommandBindingsChanged;
        _rightPanel.CommandGestureRequested -= OnTerminalCommandGestureRequested;
        ViewModel.Layout.PropertyChanged -= OnCommandContextPropertyChanged;
        ViewModel.Workspace.PropertyChanged -= OnCommandContextPropertyChanged;
        ViewModel.Thread.PropertyChanged -= OnCommandContextPropertyChanged;
        ViewModel.WorkbenchTerminal.PropertyChanged -= OnCommandContextPropertyChanged;
        foreach (var accelerator in _commandAccelerators)
        {
            accelerator.Invoked -= OnCommandAcceleratorInvoked;
        }
    }

    private void RegisterCommands()
    {
        Register(
            "commandPalette.toggle", "Show Command Palette", "Navigation",
            "Search commands, projects, branches, messages, and threads.",
            ToggleCommandPaletteAsync, defaultShortcut: "Ctrl+K",
            searchTerms: ["global search", "quick open"]);
        Register(
            "project.add", "Add Local Project", "Project", "Add another local project folder.",
            OpenAddProjectAsync, enableWhen: "connected", defaultShortcut: "Ctrl+Shift+O",
            disabledReason: "Connect to the local environment first.");
        Register("sessions.manage", "Manage Pi Sessions", "Conversation", "Import, fork and export Pi conversations.", OpenPiSessionsAsync);
        Register(
            "settings.open", "Open Settings", "Application",
            "Configure appearance, terminal, and keyboard shortcuts.", OpenSettingsAsync,
            searchTerms: ["preferences", "keybindings"]);
        Register(
            "theme.cycle", "Cycle Color Theme", "Application",
            "Switch between dark, system, and light themes.",
            () =>
            {
                ViewModel.Layout.ThemePreference = ViewModel.Layout.ThemePreference switch
                {
                    AppThemePreference.Dark => AppThemePreference.System,
                    AppThemePreference.System => AppThemePreference.Light,
                    _ => AppThemePreference.Dark,
                };
                return Task.CompletedTask;
            }, searchTerms: ["dark", "light", "system"]);
        Register(
            "sidebar.toggle", "Toggle Sidebar", "View", "Show or hide projects and threads.",
            () =>
            {
                _sidebar.ToggleCollapsed();
                return Task.CompletedTask;
            }, defaultShortcut: "Ctrl+B");
        Register(
            "rightPanel.toggle", "Toggle Workbench", "View", "Show or hide the right workbench.",
            () =>
            {
                ViewModel.Layout.ToggleRightPanel();
                return Task.CompletedTask;
            }, defaultShortcut: "Ctrl+J", searchTerms: ["right panel"]);
        Register(
            "rightPanel.maximize", "Maximize Workbench", "View",
            "Expand the workbench to its maximum width.",
            () =>
            {
                ViewModel.Layout.IsRightPanelOpen = true;
                ViewModel.Layout.ResizeRightPanel(ShellLayoutViewModel.MaximumRightPanelWidth);
                ViewModel.Layout.CommitRightPanelWidth();
                return Task.CompletedTask;
            });
        Register(
            "thread.new", "New Thread", "Thread", "Create a thread in the selected project.",
            () => ViewModel.CreateThreadAsync(), enableWhen: "connected && projectOpen",
            defaultShortcut: "Ctrl+N", disabledReason: "Select a connected project first.");
        Register(
            "thread.previous", "Previous Thread", "Thread", "Select the previous visible thread.",
            () => SelectAdjacentThreadAsync(-1), enableWhen: "threadOpen", defaultShortcut: "Ctrl+PageUp");
        Register(
            "thread.next", "Next Thread", "Thread", "Select the next visible thread.",
            () => SelectAdjacentThreadAsync(1), enableWhen: "threadOpen", defaultShortcut: "Ctrl+PageDown");
        Register(
            "composer.focus", "Focus Composer", "Thread", "Move keyboard focus to the prompt editor.",
            () =>
            {
                _composerSurface.FocusPrompt();
                return Task.CompletedTask;
            }, enableWhen: "threadOpen", defaultShortcut: "Ctrl+Shift+L");
        RegisterPanelCommand("workbench.changes", "Show Changes", WorkbenchPanelKind.Changes, "Ctrl+Shift+G", ["git", "diff"]);
        RegisterPanelCommand("workbench.files", "Show Files", WorkbenchPanelKind.Files, "Ctrl+Shift+E", ["workspace", "editor"]);
        RegisterPanelCommand("workbench.terminal", "Show Terminal", WorkbenchPanelKind.Terminal, "Ctrl+Shift+T", ["shell", "console"]);
        RegisterPanelCommand("workbench.preview", "Show Browser Preview", WorkbenchPanelKind.Preview, "Ctrl+Shift+B", ["browser", "webview"]);
        Register(
            "changes.refresh", "Refresh Git Changes", "Git", "Reload repository status and branches.",
            () => ViewModel.RefreshWorkbenchChangesAsync(), enableWhen: "changesOpen", defaultShortcut: "F5");
        Register("git.pull", "Pull Current Branch", "Git", "Fast-forward the current branch.", () => ViewModel.PullGitBranchAsync(), "changesOpen");
        Register("git.commit", "Commit Changes", "Git", "Commit the selected workspace changes.", () => ViewModel.CommitGitChangesAsync(), "changesOpen");
        Register("git.push", "Push Current Branch", "Git", "Push the current branch.", () => ViewModel.PushGitBranchAsync(), "changesOpen");
        Register(
            "files.refresh", "Refresh Workspace Files", "Files", "Reload the directory tree or active search.",
            () => ViewModel.RefreshWorkbenchFilesAsync(), enableWhen: "filesOpen", defaultShortcut: "F6");
        Register(
            "file.save", "Save Active File", "Files", "Save changes in the active file tab.",
            () => ViewModel.SaveWorkbenchFileAsync(), enableWhen: "filesOpen && editorFocus", defaultShortcut: "Ctrl+S",
            canExecute: () => ViewModel.WorkbenchFiles.ActiveDocument?.CanSave == true,
            disabledReason: "Focus an editable file with unsaved changes.");
        Register(
            "terminal.new", "New Terminal", "Terminal", "Start another terminal session.",
            () => ViewModel.StartWorkbenchTerminalAsync(), enableWhen: "terminalOpen");
        Register(
            "terminal.splitRight", "Split Terminal Right", "Terminal", "Split the active terminal horizontally.",
            _rightPanel.SplitTerminalRightAsync, enableWhen: "terminalOpen", defaultShortcut: "Ctrl+Shift+5",
            canExecute: () => ViewModel.WorkbenchTerminal.CanSplit);
        Register(
            "terminal.splitDown", "Split Terminal Down", "Terminal", "Split the active terminal vertically.",
            _rightPanel.SplitTerminalDownAsync, enableWhen: "terminalOpen", defaultShortcut: "Ctrl+Shift+D",
            canExecute: () => ViewModel.WorkbenchTerminal.CanSplit);
        Register(
            "terminal.closePane", "Close Terminal Pane", "Terminal", "Close the active terminal pane.",
            _rightPanel.CloseTerminalPaneFromCommandAsync, enableWhen: "terminalOpen", defaultShortcut: "Ctrl+Shift+W",
            canExecute: () => ViewModel.WorkbenchTerminal.CanClosePane);
        Register(
            "terminal.find", "Find in Terminal", "Terminal", "Search the active terminal scrollback.",
            () =>
            {
                _rightPanel.OpenTerminalSearch();
                return Task.CompletedTask;
            }, enableWhen: "terminalOpen && terminalFocus", defaultShortcut: "Ctrl+F");
        Register(
            "terminal.focusPreviousPane", "Focus Previous Terminal Pane", "Terminal",
            "Move focus to the previous terminal pane.", _rightPanel.FocusPreviousTerminalPaneAsync,
            enableWhen: "terminalOpen", defaultShortcut: "Alt+Left",
            canExecute: () => ViewModel.WorkbenchTerminal.PaneCount > 1);
        Register(
            "terminal.focusNextPane", "Focus Next Terminal Pane", "Terminal",
            "Move focus to the next terminal pane.", _rightPanel.FocusNextTerminalPaneAsync,
            enableWhen: "terminalOpen", defaultShortcut: "Alt+Right",
            canExecute: () => ViewModel.WorkbenchTerminal.PaneCount > 1);
        Register(
            "preview.refresh", "Reload Browser Preview", "Preview", "Reload the active preview tab.",
            () =>
            {
                _rightPanel.ReloadPreview();
                return Task.CompletedTask;
            }, enableWhen: "previewOpen", defaultShortcut: "Ctrl+R");
        Register(
            "preview.focusAddress", "Focus Preview Address", "Preview", "Move focus to the active preview URL.",
            () =>
            {
                _rightPanel.FocusPreviewAddress();
                return Task.CompletedTask;
            }, enableWhen: "previewOpen", defaultShortcut: "Ctrl+L");
    }

    private void RegisterPanelCommand(
        string id,
        string title,
        WorkbenchPanelKind panel,
        string shortcut,
        IReadOnlyList<string> searchTerms) =>
        Register(
            id, title, "Workbench", $"Open the {title[5..].ToLowerInvariant()} workbench.",
            () => _rightPanel.ActivatePanelAsync(panel), enableWhen: "projectOpen",
            defaultShortcut: shortcut, searchTerms: searchTerms, disabledReason: "Select a project first.");

    private void Register(
        string id,
        string title,
        string category,
        string description,
        Func<Task> execute,
        string? enableWhen = null,
        string? defaultShortcut = null,
        IReadOnlyList<string>? searchTerms = null,
        Func<bool>? canExecute = null,
        string? disabledReason = null) =>
        _commands.Register(new CommandDefinition(
            id, title, category, description, searchTerms ?? [], enableWhen, execute,
            defaultShortcut is null ? [] : [defaultShortcut], canExecute, disabledReason));

    private CommandContext BuildCommandContext()
    {
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot);
        var selectedPanel = ViewModel.Layout.IsRightPanelOpen
            ? ViewModel.Layout.SelectedPanel
            : (WorkbenchPanelKind?)null;
        return new CommandContext(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["connected"] = ViewModel.IsConnected,
            ["projectOpen"] = ViewModel.Workspace.SelectedProject is not null,
            ["threadOpen"] = ViewModel.Workspace.SelectedThread is not null,
            ["threadReady"] = ViewModel.Thread.Projection?.RuntimeState == ThreadRuntimeState.Ready,
            ["turnRunning"] = ViewModel.Thread.Projection?.RuntimeState == ThreadRuntimeState.Running,
            ["rightPanelOpen"] = ViewModel.Layout.IsRightPanelOpen,
            ["changesOpen"] = selectedPanel == WorkbenchPanelKind.Changes,
            ["filesOpen"] = selectedPanel == WorkbenchPanelKind.Files,
            ["terminalOpen"] = selectedPanel == WorkbenchPanelKind.Terminal,
            ["previewOpen"] = selectedPanel == WorkbenchPanelKind.Preview,
            ["composerFocus"] = HasNamedAncestor(focused, "PromptInput"),
            ["editorFocus"] = HasNamedAncestor(focused, "WorkbenchFileEditor"),
            ["terminalFocus"] = HasAncestor<TerminalWebViewSurface>(focused),
            ["previewFocus"] = HasAncestor<PreviewWebViewSurface>(focused),
            ["paletteOpen"] = _paletteOpen,
        });
    }

    private void InstallCommandAccelerators()
    {
        foreach (var accelerator in _commandAccelerators)
        {
            accelerator.Invoked -= OnCommandAcceleratorInvoked;
            KeyboardAccelerators.Remove(accelerator);
        }

        _commandAccelerators.Clear();
        foreach (var gesture in _keybindings.EffectiveBindings.Select(static binding => binding.Gesture).Distinct())
        {
            var accelerator = new KeyboardAccelerator { Key = gesture.Key, Modifiers = gesture.Modifiers };
            accelerator.Invoked += OnCommandAcceleratorInvoked;
            KeyboardAccelerators.Add(accelerator);
            _commandAccelerators.Add(accelerator);
        }
    }

    private async void OnCommandAcceleratorInvoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        var context = BuildCommandContext();
        var commandId = _keybindings.Resolve(new KeyGesture(sender.Key, sender.Modifiers), context);
        if (commandId is null || !_commands.CanExecute(commandId, context, out _))
        {
            return;
        }

        args.Handled = true;
        await _commands.ExecuteAsync(commandId, context);
    }

    private void OnCommandBindingsChanged(object? sender, EventArgs e)
    {
        InstallCommandAccelerators();
        UpdateTerminalCommandGestures();
        RefreshKeybindingRows();
        if (_paletteOpen)
        {
            _ = RefreshPaletteAsync();
        }
    }

    private async void OnTerminalCommandGestureRequested(object? sender, TerminalWebShortcutEventArgs e)
    {
        if (!KeyGesture.TryParse(e.Gesture, out var gesture, out _))
        {
            return;
        }

        var context = BuildTerminalCommandContext();
        var commandId = _keybindings.Resolve(gesture, context);
        if (commandId is not null)
        {
            await _commands.ExecuteAsync(commandId, context);
        }
    }

    private void OnCommandContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ViewModel.WorkbenchTerminal) &&
            e.PropertyName is not (nameof(WorkbenchTerminalViewModel.PaneCount) or
                                   nameof(WorkbenchTerminalViewModel.CanSplit) or
                                   nameof(WorkbenchTerminalViewModel.CanClosePane)))
        {
            return;
        }

        UpdateTerminalCommandGestures();
    }

    private void UpdateTerminalCommandGestures()
    {
        if (_disposed)
        {
            return;
        }

        var context = BuildTerminalCommandContext();
        _rightPanel.SetTerminalCommandGestures(_keybindings.EffectiveBindings
            .Where(binding => binding.WhenExpression.Evaluate(context) &&
                              _commands.CanExecute(binding.CommandId, context, out _))
            .Select(static binding => binding.Gesture.ToString()));
    }

    private CommandContext BuildTerminalCommandContext() => new(new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["connected"] = ViewModel.IsConnected,
        ["projectOpen"] = ViewModel.Workspace.SelectedProject is not null,
        ["threadOpen"] = ViewModel.Workspace.SelectedThread is not null,
        ["threadReady"] = ViewModel.Thread.Projection?.RuntimeState == ThreadRuntimeState.Ready,
        ["turnRunning"] = ViewModel.Thread.Projection?.RuntimeState == ThreadRuntimeState.Running,
        ["rightPanelOpen"] = true,
        ["changesOpen"] = false,
        ["filesOpen"] = false,
        ["terminalOpen"] = true,
        ["previewOpen"] = false,
        ["composerFocus"] = false,
        ["editorFocus"] = false,
        ["terminalFocus"] = true,
        ["previewFocus"] = false,
        ["paletteOpen"] = _paletteOpen,
    });

    private async Task ToggleCommandPaletteAsync()
    {
        if (_paletteOpen)
        {
            CommandPaletteDialog.Hide();
            return;
        }

        if (_settingsOpen)
        {
            SettingsDialog.Hide();
        }

        _paletteOpen = true;
        UpdateTerminalCommandGestures();
        CommandPaletteQuery.Text = string.Empty;
        await RefreshPaletteAsync();
        CommandPaletteQuery.Focus(FocusState.Programmatic);
        await CommandPaletteDialog.ShowAsync();
    }

    private async Task RefreshPaletteAsync()
    {
        _paletteSearchCancellation?.Cancel();
        _paletteSearchCancellation?.Dispose();
        _paletteSearchCancellation = new CancellationTokenSource();
        var cancellationToken = _paletteSearchCancellation.Token;
        var rawQuery = CommandPaletteQuery.Text ?? string.Empty;
        var actionsOnly = rawQuery.StartsWith('>');
        var commandQuery = actionsOnly ? rawQuery[1..].TrimStart() : rawQuery;
        var context = BuildCommandContext();
        var local = _commands.Search(commandQuery, context)
            .Select(match => new CommandPaletteItemViewModel(
                $"command:{match.Definition.Id}", match.Definition.Category, match.Definition.Title,
                match.IsEnabled ? match.Definition.Description : match.DisabledReason ?? match.Definition.Description,
                _keybindings.ShortcutLabel(match.Definition.Id, context) ?? string.Empty,
                match.IsEnabled, commandId: match.Definition.Id))
            .ToArray();
        Replace(PaletteItems, local);
        SelectFirstPaletteItem();
        CommandPaletteStatusText.Text = local.Length == 1 ? "1 command" : $"{local.Length} commands";

        var globalQuery = rawQuery.Trim();
        if (actionsOnly || globalQuery.Length == 0 || !ViewModel.IsConnected)
        {
            return;
        }

        try
        {
            CommandPaletteProgress.Visibility = Visibility.Visible;
            await Task.Delay(180, cancellationToken);
            var result = await ViewModel.SearchGlobalAsync(globalQuery, cancellationToken);
            if (cancellationToken.IsCancellationRequested ||
                !string.Equals(CommandPaletteQuery.Text, rawQuery, StringComparison.Ordinal))
            {
                return;
            }

            foreach (var item in result.Items)
            {
                PaletteItems.Add(new CommandPaletteItemViewModel(
                    $"search:{item.Kind}:{item.ProjectId}:{item.ThreadId}:{item.BranchName}:{item.MessageId}",
                    item.Kind.ToString(), item.Title, item.Snippet ?? item.Description, string.Empty, true,
                    searchItem: item));
            }

            SelectFirstPaletteItem();
            CommandPaletteStatusText.Text = result.IsTruncated
                ? $"{PaletteItems.Count} results • refine your search for more"
                : $"{PaletteItems.Count} results";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            CommandPaletteStatusText.Text = $"Commands available • global search failed: {exception.Message}";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                CommandPaletteProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void SelectFirstPaletteItem()
    {
        CommandPaletteResults.SelectedIndex = PaletteItems.Count == 0 ? -1 : 0;
        if (CommandPaletteResults.SelectedItem is not null)
        {
            CommandPaletteResults.ScrollIntoView(CommandPaletteResults.SelectedItem);
        }
    }

    private async Task ExecutePaletteItemAsync(CommandPaletteItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (!item.IsEnabled)
        {
            CommandPaletteStatusText.Text = item.Description;
            return;
        }

        CommandPaletteDialog.Hide();
        if (item.CommandId is { } commandId)
        {
            await _commands.ExecuteAsync(commandId, BuildCommandContext());
            return;
        }

        if (item.SearchItem is { } searchItem)
        {
            await ViewModel.ActivateGlobalSearchItemAsync(searchItem);
            _sidebar.SynchronizeSelection();
            if (searchItem.MessageId is { Length: > 0 } messageId)
            {
                DispatcherQueue.TryEnqueue(() => _conversationTimeline.RevealMessage(messageId));
            }
        }
    }

    private async void OnCommandPaletteQueryChanged(object sender, TextChangedEventArgs e) =>
        await RefreshPaletteAsync();

    private async void OnCommandPaletteResultClicked(object sender, ItemClickEventArgs e) =>
        await ExecutePaletteItemAsync(e.ClickedItem as CommandPaletteItemViewModel);

    private async void OnCommandPaletteQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Down && PaletteItems.Count > 0)
        {
            CommandPaletteResults.SelectedIndex = Math.Min(PaletteItems.Count - 1, CommandPaletteResults.SelectedIndex + 1);
            CommandPaletteResults.ScrollIntoView(CommandPaletteResults.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Up && PaletteItems.Count > 0)
        {
            CommandPaletteResults.SelectedIndex = Math.Max(0, CommandPaletteResults.SelectedIndex - 1);
            CommandPaletteResults.ScrollIntoView(CommandPaletteResults.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await ExecutePaletteItemAsync(CommandPaletteResults.SelectedItem as CommandPaletteItemViewModel);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            CommandPaletteDialog.Hide();
            e.Handled = true;
        }
    }

    private void OnCommandPaletteClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _paletteOpen = false;
        UpdateTerminalCommandGestures();
        _paletteSearchCancellation?.Cancel();
        CommandPaletteProgress.Visibility = Visibility.Collapsed;
    }

    private async Task SelectAdjacentThreadAsync(int direction)
    {
        var threads = ViewModel.Workspace.Threads;
        if (threads.Count == 0)
        {
            return;
        }

        var current = ViewModel.Workspace.SelectedThread;
        var index = current is null ? -1 : threads.ToList().FindIndex(thread => thread.ThreadId == current.ThreadId);
        var next = (index + direction + threads.Count) % threads.Count;
        await ViewModel.SelectThreadAsync(threads[next]);
        _sidebar.SynchronizeSelection();
    }

    private void RefreshKeybindingRows()
    {
        Replace(CustomKeybindingRows, _keybindings.CustomBindings.Select(binding =>
            new CommandKeybindingRowViewModel(
                binding.CommandId,
                _commands.TryGet(binding.CommandId, out var command) ? command.Title : binding.CommandId,
                binding.Gesture.ToString(), binding.When ?? string.Empty)));
        KeybindingSummaryText.Text = $"{_keybindings.EffectiveBindings.Count} active shortcuts • " +
            $"{_keybindings.CustomBindings.Count} custom overrides";
        KeybindingEmptyText.Visibility = CustomKeybindingRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnKeybindingCommandSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KeybindingCommandSelector.SelectedItem is not CommandDefinition command)
        {
            return;
        }

        var custom = _keybindings.CustomBindings.FirstOrDefault(binding => binding.CommandId == command.Id);
        KeybindingGestureInput.Text = custom?.Gesture.ToString() ??
            _keybindings.ShortcutLabel(command.Id, BuildCommandContext()) ?? string.Empty;
        KeybindingWhenInput.Text = custom?.When ?? command.EnableWhen ?? string.Empty;
        KeybindingValidationText.Text = string.Empty;
    }

    private void OnSaveKeybindingClicked(object sender, RoutedEventArgs e)
    {
        if (KeybindingCommandSelector.SelectedItem is not CommandDefinition command)
        {
            KeybindingValidationText.Text = "Select a command.";
            return;
        }

        if (!_keybindings.TrySet(
                command.Id, KeybindingGestureInput.Text, KeybindingWhenInput.Text,
                out _, out var error))
        {
            KeybindingValidationText.Text = error;
            return;
        }

        var binding = _keybindings.CustomBindings.Single(candidate => candidate.CommandId == command.Id);
        ViewModel.Layout.SaveCommandKeybinding(new CommandKeybindingPreference(
            binding.CommandId, binding.Gesture.ToString(), binding.When));
        KeybindingValidationText.Text = $"Saved {binding.Gesture} for {command.Title}.";
    }

    private void OnResetKeybindingClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CommandKeybindingRowViewModel row })
        {
            return;
        }

        _keybindings.Reset(row.CommandId);
        ViewModel.Layout.RemoveCommandKeybinding(row.CommandId);
        KeybindingValidationText.Text = $"Restored the default for {row.CommandTitle}.";
    }

    private void OnResetAllKeybindingsClicked(object sender, RoutedEventArgs e)
    {
        foreach (var binding in _keybindings.CustomBindings.ToArray())
        {
            _keybindings.Reset(binding.CommandId);
        }

        ViewModel.Layout.ResetCommandKeybindings();
        KeybindingValidationText.Text = "Restored all default shortcuts.";
    }

    private void OnSidebarCollapsedChanged(object? sender, EventArgs e) =>
        SidebarCollapsedChanged?.Invoke(this, EventArgs.Empty);

    private async void OnAddProjectRequested(object? sender, EventArgs e) => await OpenAddProjectAsync();

    private async Task OpenAddProjectAsync()
    {
        ProjectPathInput.Text = string.Empty;
        await AddProjectDialog.ShowAsync();
    }

    private async void OnSettingsRequested(object? sender, EventArgs e) => await OpenSettingsAsync();

    private async void OnSaveSettlementSettingsClicked(object sender, RoutedEventArgs e) => await ViewModel.SaveSettlementSettingsAsync();

    private async Task OpenSettingsAsync()
    {
        SynchronizeThemeSelection();
        SynchronizeTerminalAppearanceSelection();
        RefreshKeybindingRows();
        if (KeybindingCommandSelector.SelectedIndex < 0 && _commands.Commands.Count > 0)
        {
            KeybindingCommandSelector.SelectedIndex = 0;
        }

        SynchronizeProjectSettings();
        PublishProviderSelector.SelectedIndex = Math.Max(0, PublishProviderSelector.SelectedIndex);
        if (SettingsNavigation.SelectedItem is null && SettingsNavigation.MenuItems.Count != 0)
        {
            SettingsNavigation.SelectedItem = SettingsNavigation.MenuItems[0];
        }

        ApplySettingsSection((SettingsNavigation.SelectedItem as NavigationViewItem)?.Tag as string ?? "Projects");

        _settingsOpen = true;
        _ = ViewModel.RefreshSettingsAsync();
        await SettingsDialog.ShowAsync();
    }

    private void OnSettingsNavigationSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string section)
        {
            ApplySettingsSection(section);
        }
    }

    private void ApplySettingsSection(string section)
    {
        foreach (var element in SettingsShell.Children.OfType<FrameworkElement>())
        {
            element.Visibility = string.Equals(element.Tag as string, section, StringComparison.Ordinal)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void SynchronizeProjectSettings()
    {
        var project = ViewModel.Workspace.SelectedProject;
        ProjectDefaultWorkspaceModeSelector.SelectedIndex = project?.DefaultWorkspaceMode == ThreadWorkspaceMode.Worktree
            ? 1
            : 0;
        ProjectAutoPullToggle.IsOn = project?.AutoPullDefaultBranch == true;
        ProjectDefaultModelProviderInput.Text = project?.DefaultModel?.ProviderId ?? string.Empty;
        ProjectDefaultModelIdInput.Text = project?.DefaultModel?.ModelId ?? string.Empty;
        ProjectDefaultRuntimeModeInput.Text = project?.DefaultRuntimeModeId ?? string.Empty;
        ProjectDefaultThinkingSelector.SelectedIndex = project?.DefaultThinkingLevel switch
        {
            PiThinkingLevel.Off => 1,
            PiThinkingLevel.Minimal => 2,
            PiThinkingLevel.Low => 3,
            PiThinkingLevel.Medium => 4,
            PiThinkingLevel.High => 5,
            _ => 0,
        };
        if (project is not null && string.IsNullOrWhiteSpace(PublishRepositoryInput.Text))
        {
            PublishRepositoryInput.Text = project.DisplayName;
        }
    }

    private async void OnSaveProjectDefaultsClicked(object sender, RoutedEventArgs e)
    {
        var mode = ProjectDefaultWorkspaceModeSelector.SelectedItem is ComboBoxItem { Tag: string value } &&
            Enum.TryParse<ThreadWorkspaceMode>(value, out var parsed)
                ? parsed
                : ThreadWorkspaceMode.Local;
        var defaultModel = string.IsNullOrWhiteSpace(ProjectDefaultModelProviderInput.Text) ||
            string.IsNullOrWhiteSpace(ProjectDefaultModelIdInput.Text)
                ? null
                : new PiModelSelection(
                    ProjectDefaultModelProviderInput.Text.Trim(),
                    ProjectDefaultModelIdInput.Text.Trim());
        var defaultThinking = ProjectDefaultThinkingSelector.SelectedItem is ComboBoxItem { Tag: string thinkingText } &&
            Enum.TryParse<PiThinkingLevel>(thinkingText, out var thinking)
                ? thinking
                : (PiThinkingLevel?)null;
        await ViewModel.UpdateSelectedProjectDefaultsAsync(
            mode,
            ProjectAutoPullToggle.IsOn,
            defaultModel,
            defaultThinking,
            ProjectDefaultRuntimeModeInput.Text);
    }

    private async void OnRemoveProjectClicked(object sender, RoutedEventArgs e)
    {
        var project = ViewModel.Workspace.SelectedProject;
        if (project is null)
        {
            return;
        }

        SettingsDialog.Hide();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Remove {project.DisplayName}?",
            Content = "Pi Station threads and local metadata for this project will be removed. The project folder and Git repository will not be deleted.",
            PrimaryButtonText = "Remove project",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.RemoveSelectedProjectAsync();
            _sidebar.SynchronizeSelection();
        }

        await OpenSettingsAsync();
    }

    private async void OnRefreshSettingsClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshSettingsAsync();

    private async void OnExportDiagnosticsClicked(object sender, RoutedEventArgs e)
    {
        var window = (Application.Current as App)?.MainWindow;
        if (window is null)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedFileName = $"pistation-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}",
        };
        picker.FileTypeChoices.Add("JSON", [".json"]);
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(window));
        if (await picker.PickSaveFileAsync() is { } file)
        {
            await ViewModel.ExportDiagnosticsAsync(file.Path);
        }
    }

    private async void OnCloneRepositoryClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.CloneHostedRepositoryAsync(CloneRemoteInput.Text, CloneDestinationInput.Text);

    private async void OnPublishRepositoryClicked(object sender, RoutedEventArgs e)
    {
        if (PublishProviderSelector.SelectedItem is ComboBoxItem { Tag: string providerText } &&
            Enum.TryParse<SourceControlProvider>(providerText, out var provider))
        {
            await ViewModel.PublishSelectedProjectAsync(
                provider,
                PublishOwnerInput.Text,
                PublishRepositoryInput.Text,
                PublishPrivateCheckBox.IsChecked == true);
        }
    }

    private async void OnGeneratePullRequestTextClicked(object sender, RoutedEventArgs e)
    {
        var generated = await ViewModel.GenerateSourceControlTextAsync(forPullRequest: true);
        if (generated is not null)
        {
            PullRequestTitleInput.Text = generated.Title;
            PullRequestBodyInput.Text = generated.Body;
        }
    }

    private async void OnCreatePullRequestClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.CreatePullRequestAsync(
            PullRequestTitleInput.Text,
            PullRequestBodyInput.Text,
            PullRequestDraftCheckBox.IsChecked == true);

    private async void OnLinkPullRequestClicked(object sender, RoutedEventArgs e)
    {
        if (ResolvePullRequest(sender) is { } pullRequest)
        {
            await ViewModel.LinkPullRequestAsync(pullRequest);
        }
    }

    private async void OnCommentPullRequestClicked(object sender, RoutedEventArgs e) =>
        await PromptAndMutatePullRequestAsync(sender, PullRequestMutationKind.Comment, "Comment on pull request", "Comment");

    private async void OnLabelPullRequestClicked(object sender, RoutedEventArgs e) =>
        await PromptAndMutatePullRequestAsync(sender, PullRequestMutationKind.AddLabel, "Add pull-request label", "Label");

    private async void OnReviewerPullRequestClicked(object sender, RoutedEventArgs e) =>
        await PromptAndMutatePullRequestAsync(sender, PullRequestMutationKind.AddReviewer, "Add reviewer", "Username or email");

    private async void OnRequestChangesPullRequestClicked(object sender, RoutedEventArgs e) =>
        await PromptAndMutatePullRequestAsync(sender, PullRequestMutationKind.RequestChanges, "Request changes", "Review summary");

    private async void OnApprovePullRequestClicked(object sender, RoutedEventArgs e)
    {
        if (ResolvePullRequest(sender) is { } pullRequest)
        {
            await ViewModel.MutatePullRequestAsync(pullRequest, PullRequestMutationKind.Approve);
        }
    }

    private async void OnMergePullRequestClicked(object sender, RoutedEventArgs e)
    {
        if (ResolvePullRequest(sender) is not { } pullRequest)
        {
            return;
        }

        SettingsDialog.Hide();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Merge PR {pullRequest.Number}?",
            Content = pullRequest.Title,
            PrimaryButtonText = "Merge",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.MutatePullRequestAsync(pullRequest, PullRequestMutationKind.Merge);
        }

        await OpenSettingsAsync();
    }

    private async void OnClosePullRequestClicked(object sender, RoutedEventArgs e)
    {
        if (ResolvePullRequest(sender) is { } pullRequest)
        {
            await ViewModel.MutatePullRequestAsync(
                pullRequest,
                pullRequest.State == PullRequestState.Closed
                    ? PullRequestMutationKind.Reopen
                    : PullRequestMutationKind.Close);
        }
    }

    private async Task PromptAndMutatePullRequestAsync(
        object sender,
        PullRequestMutationKind mutation,
        string title,
        string placeholder)
    {
        if (ResolvePullRequest(sender) is not { } pullRequest)
        {
            return;
        }

        SettingsDialog.Hide();
        var input = new TextBox
        {
            PlaceholderText = placeholder,
            AcceptsReturn = mutation is PullRequestMutationKind.Comment or PullRequestMutationKind.RequestChanges,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 360,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = input,
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
        {
            await ViewModel.MutatePullRequestAsync(pullRequest, mutation, input.Text.Trim());
        }

        await OpenSettingsAsync();
    }

    private PullRequestDescriptor? ResolvePullRequest(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is PullRequestDescriptor pullRequest)
        {
            return pullRequest;
        }

        var number = (sender as FrameworkElement)?.Tag as string;
        return string.IsNullOrWhiteSpace(number)
            ? null
            : ViewModel.Settings.PullRequests.FirstOrDefault(
                candidate => candidate.Number.Equals(number, StringComparison.Ordinal));
    }

    private async void OnAddProjectConfirmed(object sender, RoutedEventArgs e)
    {
        var path = ProjectPathInput.Text;
        AddProjectDialog.Hide();
        await ViewModel.AddProjectAsync(path);
        _sidebar.SynchronizeSelection();
    }

    private void OnResetLayoutClicked(object sender, RoutedEventArgs e) => ViewModel.Layout.Reset();

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PiThemeSelector.SelectedItem is ComboBoxItem { Tag: string themeName } &&
            Enum.TryParse<AppThemePreference>(themeName, ignoreCase: true, out var theme))
        {
            ViewModel.Layout.ThemePreference = theme;
        }
    }

    private void OnTerminalFontFamilySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TerminalFontFamilySelector.SelectedItem is ComboBoxItem { Tag: string family })
        {
            ViewModel.Layout.TerminalFontFamily = family;
        }
    }

    private void OnTerminalFontSizeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TerminalFontSizeSelector.SelectedItem is ComboBoxItem { Tag: string sizeText } &&
            double.TryParse(sizeText, NumberStyles.Number, CultureInfo.InvariantCulture, out var size))
        {
            ViewModel.Layout.TerminalFontSize = size;
        }
    }

    private void OnResetTerminalAppearanceClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.Layout.ResetTerminalAppearance();
        SynchronizeTerminalAppearanceSelection();
    }

    private void OnSettingsDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _settingsOpen = false;
        _sidebar.FocusSettingsButton();
    }

    private void OnAddProjectDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args) =>
        _sidebar.FocusNewProjectButton();

    private async void OnSimulateTransportDropClicked(object sender, RoutedEventArgs e)
    {
        SettingsDialog.Hide();
        await ViewModel.DisconnectForUiTestAsync();
    }

    private void SynchronizeThemeSelection()
    {
        PiThemeSelector.SelectedIndex = ViewModel.Layout.ThemePreference switch
        {
            AppThemePreference.Dark => 0,
            AppThemePreference.System => 1,
            AppThemePreference.Light => 2,
            _ => 0,
        };
    }

    private void SynchronizeTerminalAppearanceSelection()
    {
        TerminalFontFamilySelector.SelectedIndex = FindTaggedItem(TerminalFontFamilySelector, ViewModel.Layout.TerminalFontFamily);
        TerminalFontSizeSelector.SelectedIndex = FindTaggedItem(
            TerminalFontSizeSelector,
            Math.Round(ViewModel.Layout.TerminalFontSize).ToString(CultureInfo.InvariantCulture));
    }

    private static bool HasNamedAncestor(object? focused, string name)
    {
        for (var current = focused as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement element && string.Equals(element.Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAncestor<T>(object? focused) where T : DependencyObject
    {
        for (var current = focused as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T)
            {
                return true;
            }
        }

        return false;
    }

    private static int FindTaggedItem(ComboBox comboBox, string tag)
    {
        for (var index = 0; index < comboBox.Items.Count; index++)
        {
            if (comboBox.Items[index] is ComboBoxItem { Tag: string candidate } &&
                string.Equals(candidate, tag, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return 0;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
