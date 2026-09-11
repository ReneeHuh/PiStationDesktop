using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PiStation.App.ViewModels;
using PiStation.Protocol.Models;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Projections;
using Windows.Storage.Pickers;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace PiStation.App.Views.Controls;

public sealed partial class ComposerSurface : UserControl
{
    private bool _externalEditorOpen;
    public async Task OpenExternalEditorAsync()
    {
        if (_externalEditorOpen || !ViewModel.CanEditPromptExternally) return;
        ComposerOptionsFlyout.Hide();
        _externalEditorOpen = true;
        try { using var dialog = new ExternalPromptEditorDialog(ViewModel) { XamlRoot = XamlRoot }; await dialog.ShowAsync(); }
        finally { _externalEditorOpen = false; }
    }
    private async void OnExternalEditorClicked(object sender, RoutedEventArgs args) => await OpenExternalEditorAsync();

    private async void OnPiShellClicked(object sender, RoutedEventArgs e)
    {
        ComposerOptionsFlyout.Hide();
        await new PiShellDialog(ViewModel) { XamlRoot = XamlRoot }.ShowAsync();
    }

    private void OnNewBackgroundTaskClicked(object sender, RoutedEventArgs e) => ViewModel.SendPromptInBackground();
    private FileMentionToken? _activeFileMentionToken;
    private bool _isApplyingFileMention;
    private bool _isPromptShiftKeyDown;

    public ComposerSurface(ShellViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        Loaded += OnRestingLoaded;
        Unloaded += OnRestingUnloaded;
        GotFocus += OnComposerFocusChanged;
        LostFocus += OnComposerFocusChanged;
    }

    public ShellViewModel ViewModel { get; }

    public void FocusPrompt()
    {
        _scrollCollapsed = false;
        SetResting(false);
        PromptInput.Focus(FocusState.Programmatic);
    }

    private void OnRestoreRecoveredDraft(object sender, RoutedEventArgs e) => ViewModel.Composer.RestoreRecoveredDraft();

    private void OnKeepHostDraft(object sender, RoutedEventArgs e) => ViewModel.Composer.KeepHostDraft();

    private async void OnSendPromptClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.SendPromptAsync();

    private void OnPromptInputChanged(object sender, RoutedEventArgs e)
    {
        UpdatePromptHeight();
        if (_isApplyingFileMention)
        {
            return;
        }

        if (ComposerPowerViewModel.TryReadCommandToken(
                PromptInput.Text,
                PromptInput.SelectionStart,
                out _,
                out _,
                out _,
                out _))
        {
            _activeFileMentionToken = null;
            ViewModel.CloseFileMentionSuggestions();
            ViewModel.UpdateComposerDiscoveryQuery(PromptInput.Text, PromptInput.SelectionStart);
        }
        else if (TryReadFileMentionToken(PromptInput.Text, PromptInput.SelectionStart, out var token))
        {
            ViewModel.CloseComposerDiscovery();
            _activeFileMentionToken = token;
            ViewModel.UpdateFileMentionQuery(token.Query);
        }
        else
        {
            _activeFileMentionToken = null;
            ViewModel.CloseFileMentionSuggestions();
            ViewModel.CloseComposerDiscovery();
        }
    }

    private void OnPromptInputSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePromptHeight();

    private void OnComposerSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePromptHeight();

    private void UpdatePromptHeight()
    {
        if (_resting) return;
        const double estimatedCharacterWidth = 7.4;
        const double additionalLineHeight = 20;
        var availableWidth = Math.Max(estimatedCharacterWidth * 24, PromptInput.ActualWidth - 16);
        var charactersPerLine = Math.Max(24, (int)(availableWidth / estimatedCharacterWidth));
        var visualLineCount = PromptInput.Text
            .Split('\n')
            .Sum(line => Math.Max(
                1,
                (int)Math.Ceiling(line.TrimEnd('\r').Length / (double)charactersPerLine)));
        // Reserve the measured toolbar/rails before growing the input, including
        // the second toolbar row and larger text profiles.
        var maximumComposerHeight = (double)Application.Current.Resources["PiComposerMaxHeight"];
        var maximumInputHeight = ComposerRoot.ActualHeight > 0
            ? Math.Clamp(maximumComposerHeight - (ComposerRoot.ActualHeight - PromptInput.ActualHeight),
                PromptInput.MinHeight, PromptInput.MaxHeight)
            : PromptInput.MaxHeight;
        var desiredHeight = Math.Clamp(
            PromptInput.MinHeight + ((visualLineCount - 1) * additionalLineHeight),
            PromptInput.MinHeight,
            maximumInputHeight);
        if (Math.Abs(PromptInput.Height - desiredHeight) > 0.1)
        {
            PromptInput.Height = desiredHeight;
        }
    }

    private async void OnPromptInputPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Shift)
        {
            _isPromptShiftKeyDown = true;
            return;
        }

        if (IsControlKeyDown() && e.Key == VirtualKey.V && await TryPasteImageOrFilesAsync())
        {
            e.Handled = true;
            return;
        }

        if (ViewModel.ComposerPower.SuggestionsVisibility == Visibility.Visible)
        {
            switch (e.Key)
            {
                case VirtualKey.Down:
                    ViewModel.MoveComposerDiscoverySelection(1);
                    ScrollSelectedComposerCommandIntoView();
                    e.Handled = true;
                    break;
                case VirtualKey.Up:
                    ViewModel.MoveComposerDiscoverySelection(-1);
                    ScrollSelectedComposerCommandIntoView();
                    e.Handled = true;
                    break;
                case VirtualKey.Enter:
                case VirtualKey.Tab:
                    await ApplySelectedComposerCommandAsync();
                    e.Handled = true;
                    break;
                case VirtualKey.Escape:
                    ViewModel.CloseComposerDiscovery();
                    e.Handled = true;
                    break;
            }

            return;
        }

        if (ViewModel.FileMentions.SuggestionsVisibility == Visibility.Visible)
        {
            switch (e.Key)
            {
                case VirtualKey.Down:
                    ViewModel.MoveFileMentionSelection(1);
                    ScrollSelectedFileMentionIntoView();
                    e.Handled = true;
                    break;
                case VirtualKey.Up:
                    ViewModel.MoveFileMentionSelection(-1);
                    ScrollSelectedFileMentionIntoView();
                    e.Handled = true;
                    break;
                case VirtualKey.Enter:
                case VirtualKey.Tab:
                    if (TryApplySelectedFileMention())
                    {
                        e.Handled = true;
                    }

                    break;
                case VirtualKey.Escape:
                    _activeFileMentionToken = null;
                    ViewModel.CloseFileMentionSuggestions();
                    e.Handled = true;
                    break;
            }

            return;
        }

        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        if (IsControlKeyDown() && IsShiftKeyDown())
        {
            ViewModel.SendPromptInBackground();
            return;
        }

        if (IsShiftKeyDown())
        {
            InsertPromptLineBreak();
            return;
        }

        if (IsControlKeyDown() && ViewModel.CanQueueFollowUp)
        {
            await ViewModel.QueueFollowUpAsync();
            return;
        }

        if (ViewModel.CanSend)
        {
            await ViewModel.SendPromptAsync();
        }
    }

    private void OnPromptInputPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Shift)
        {
            _isPromptShiftKeyDown = false;
        }
    }

    private bool IsShiftKeyDown() =>
        _isPromptShiftKeyDown ||
        (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;

    private static bool IsControlKeyDown() =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;

    private void InsertPromptLineBreak()
    {
        var text = PromptInput.Text;
        var selectionStart = PromptInput.SelectionStart;
        var selectionEnd = selectionStart + PromptInput.SelectionLength;
        PromptInput.Text = text[..selectionStart] + Environment.NewLine + text[selectionEnd..];
        PromptInput.SelectionStart = selectionStart + Environment.NewLine.Length;
        PromptInput.SelectionLength = 0;
    }

    private void OnFileMentionSuggestionClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ProjectFileMatch match)
        {
            ApplyFileMention(match);
        }
    }

    private void OnFileMentionSuggestionInvoked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProjectFileMatch match })
        {
            ApplyFileMention(match);
        }
    }

    private async void OnAttachFilesClicked(object sender, RoutedEventArgs e)
    {
        try { await PickAttachmentsAsync(); }
        catch (Exception exception) { ViewModel.ReportRuntimeError(exception); }
    }

    private async Task PickAttachmentsAsync()
    {
        var targetThread = ViewModel.Workspace.SelectedThread?.ThreadId;
        var window = (Application.Current as App)?.FindWindow(XamlRoot);
        if (window is null)
        {
            return;
        }

        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(window));
        var files = await picker.PickMultipleFilesAsync();
        await AddStorageFilesAsync(files.OfType<StorageFile>(), targetThread);
    }

    private async void OnRemoveAttachmentClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DraftAttachmentViewModel attachment })
        {
            await ViewModel.RemoveAttachmentAsync(attachment);
        }
    }

    private void OnComposerDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(RightPanelHost.WorkspaceFileDragFormat) ||
            e.DataView.Contains(StandardDataFormats.StorageItems) ||
            e.DataView.Contains(StandardDataFormats.Bitmap))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = e.DataView.Contains(RightPanelHost.WorkspaceFileDragFormat)
                ? "Mention workspace file"
                : "Attach to prompt";
            e.DragUIOverride.IsCaptionVisible = true;
            e.Handled = true;
        }
    }

    private async void OnComposerDrop(object sender, DragEventArgs e)
    {
        try { await HandleComposerDropAsync(e); }
        catch (Exception exception) { ViewModel.ReportRuntimeError(exception); }
    }

    private async Task HandleComposerDropAsync(DragEventArgs e)
    {
        var targetThread = ViewModel.Workspace.SelectedThread?.ThreadId;
        if (e.DataView.Contains(RightPanelHost.WorkspaceFileDragFormat))
        {
            e.Handled = true;
            var value = await e.DataView.GetDataAsync(RightPanelHost.WorkspaceFileDragFormat);
            if (IsAttachmentTargetCurrent(targetThread) && value is string relativePath && !string.IsNullOrWhiteSpace(relativePath))
            {
                InsertFileMentionAtCaret(relativePath);
            }

            return;
        }

        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.Handled = true;
            var items = await e.DataView.GetStorageItemsAsync();
            await AddStorageFilesAsync(items.OfType<StorageFile>(), targetThread);
            return;
        }

        if (e.DataView.Contains(StandardDataFormats.Bitmap))
        {
            e.Handled = true;
            await AddBitmapAsync(await e.DataView.GetBitmapAsync(), targetThread);
        }
    }

    private async Task<bool> TryPasteImageOrFilesAsync()
    {
        try { return await PasteImageOrFilesAsync(); }
        catch (Exception exception) { ViewModel.ReportRuntimeError(exception); return true; }
    }

    private async Task<bool> PasteImageOrFilesAsync()
    {
        var targetThread = ViewModel.Workspace.SelectedThread?.ThreadId;
        var content = Clipboard.GetContent();
        if (content.Contains(StandardDataFormats.StorageItems))
        {
            var items = await content.GetStorageItemsAsync();
            var files = items.OfType<StorageFile>().ToArray();
            if (files.Length != 0)
            {
                await AddStorageFilesAsync(files, targetThread);
                return true;
            }
        }

        if (content.Contains(StandardDataFormats.Bitmap))
        {
            await AddBitmapAsync(await content.GetBitmapAsync(), targetThread);
            return true;
        }

        return false;
    }

    private bool IsAttachmentTargetCurrent(ThreadId? targetThread)
    {
        if (targetThread is { } id && ViewModel.Workspace.SelectedThread?.ThreadId == id && ViewModel.Composer.OwnsDraft(id)) return true;
        ViewModel.ReportRuntimeError("The active draft changed while reading the attachment. Select the intended conversation and attach it again.");
        return false;
    }

    private async Task AddStorageFilesAsync(IEnumerable<StorageFile> files, ThreadId? targetThread)
    {
        foreach (var file in files)
        {
            if (!ViewModel.CanAttachFiles || !IsAttachmentTargetCurrent(targetThread))
            {
                break;
            }

            var properties = await file.GetBasicPropertiesAsync();
            await using var content = await file.OpenStreamForReadAsync();
            if (!IsAttachmentTargetCurrent(targetThread)) return;
            await ViewModel.AddAttachmentAsync(
                file.Name,
                file.ContentType,
                content,
                checked((long)properties.Size));
        }
    }

    private async Task AddBitmapAsync(RandomAccessStreamReference bitmap, ThreadId? targetThread)
    {
        if (!ViewModel.CanAttachFiles || !IsAttachmentTargetCurrent(targetThread))
        {
            return;
        }

        using var randomAccessStream = await bitmap.OpenReadAsync();
        await using var content = randomAccessStream.AsStreamForRead();
        if (!IsAttachmentTargetCurrent(targetThread)) return;
        var mediaType = string.IsNullOrWhiteSpace(randomAccessStream.ContentType)
            ? "image/png"
            : randomAccessStream.ContentType;
        await ViewModel.AddAttachmentAsync(
            $"pasted-image-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.png",
            mediaType,
            content,
            checked((long)randomAccessStream.Size));
    }

    private async void OnComposerCommandClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ComposerCommandDescriptor command)
        {
            await ApplyComposerCommandAsync(command);
        }
    }

    private async Task ApplySelectedComposerCommandAsync()
    {
        var index = ViewModel.ComposerPower.SelectedIndex;
        if (index >= 0 && index < ViewModel.ComposerPower.Suggestions.Count)
        {
            await ApplyComposerCommandAsync(ViewModel.ComposerPower.Suggestions[index]);
        }
    }

    private async Task ApplyComposerCommandAsync(ComposerCommandDescriptor command)
    {
        if (command.Source == ComposerCommandSource.BuiltIn)
        {
            RemoveActiveComposerToken();
            await ViewModel.InvokeComposerCommandAsync(command);
            return;
        }

        if (!ComposerPowerViewModel.TryReadCommandToken(
                PromptInput.Text,
                PromptInput.SelectionStart,
                out var prefix,
                out _,
                out var start,
                out var end))
        {
            return;
        }

        var invocationPrefix = command.Source == ComposerCommandSource.Skill ? '$' : prefix;
        var replacement = $"{invocationPrefix}{command.Name} ";
        PromptInput.Text = PromptInput.Text[..start] + replacement + PromptInput.Text[end..];
        PromptInput.SelectionStart = start + replacement.Length;
        PromptInput.SelectionLength = 0;
        ViewModel.CloseComposerDiscovery();
        PromptInput.Focus(FocusState.Programmatic);
    }

    private void RemoveActiveComposerToken()
    {
        if (!ComposerPowerViewModel.TryReadCommandToken(
                PromptInput.Text,
                PromptInput.SelectionStart,
                out _,
                out _,
                out var start,
                out var end))
        {
            return;
        }

        PromptInput.Text = PromptInput.Text.Remove(start, end - start).TrimStart();
        PromptInput.SelectionStart = Math.Min(start, PromptInput.Text.Length);
    }

    private void ScrollSelectedComposerCommandIntoView()
    {
        var index = ViewModel.ComposerPower.SelectedIndex;
        if (index >= 0 && index < ViewModel.ComposerPower.Suggestions.Count)
        {
            ComposerDiscoveryList.ScrollIntoView(ViewModel.ComposerPower.Suggestions[index]);
        }
    }

    private async void OnCompactContextClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.CompactContextAsync();

    private async void OnStashPromptClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.StashPromptAsync();

    private async void OnRestorePromptStashClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PromptStash stash })
        {
            await ViewModel.RestorePromptStashAsync(stash);
            PromptInput.Focus(FocusState.Programmatic);
        }
    }

    private async void OnDeletePromptStashClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PromptStash stash })
        {
            await ViewModel.DeletePromptStashAsync(stash);
        }
    }

    private void OnAddTerminalContextClicked(object sender, RoutedEventArgs e) => ViewModel.AddTerminalContext();

    private void OnAddDiffContextClicked(object sender, RoutedEventArgs e) => ViewModel.AddDiffContext();

    private async void OnContextSourceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ComposerContextChipViewModel chip })
            await ViewModel.RevealComposerContextAsync(chip);
    }

    private void OnRemoveContextClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ComposerContextChipViewModel chip })
        {
            ViewModel.RemoveComposerContext(chip);
        }
    }

    private void InsertFileMentionAtCaret(string relativePath)
    {
        var text = PromptInput.Text ?? string.Empty;
        var start = Math.Clamp(PromptInput.SelectionStart, 0, text.Length);
        var end = Math.Clamp(start + PromptInput.SelectionLength, start, text.Length);
        var mention = FormatFileMention(relativePath);
        var leading = start > 0 && !char.IsWhiteSpace(text[start - 1]) ? " " : string.Empty;
        var trailing = end < text.Length && char.IsWhiteSpace(text[end]) ? string.Empty : " ";
        var replacement = leading + mention + trailing;
        _isApplyingFileMention = true;
        try
        {
            PromptInput.Text = text[..start] + replacement + text[end..];
            PromptInput.SelectionStart = start + replacement.Length;
            PromptInput.SelectionLength = 0;
        }
        finally
        {
            _isApplyingFileMention = false;
        }

        _activeFileMentionToken = null;
        ViewModel.CloseFileMentionSuggestions();
        PromptInput.Focus(FocusState.Programmatic);
    }

    private async void OnStopTurnClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.StopTurnAsync();

    private async void OnFollowUpClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.QueueFollowUpAsync();

    private async void OnClearTurnQueueClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.ClearTurnQueueAsync();

    private async void OnRefreshTurnQueueClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshTurnQueueAsync();

    private async void OnToggleSteeringDeliveryModeClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.ToggleQueueDeliveryModeAsync(QueuedMessageKind.Steering);

    private async void OnToggleFollowUpDeliveryModeClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.ToggleQueueDeliveryModeAsync(QueuedMessageKind.FollowUp);

    private async void OnEditCitationComment(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ComposerContextChipViewModel chip }) return;
        var input = new TextBox { Text = chip.Comment ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 8192, MinHeight = 100 };
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Comment on " + chip.Label, Content = input,
            PrimaryButtonText = "Save comment", CloseButtonText = "Cancel" };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var index = ViewModel.ComposerPower.ContextChips.IndexOf(chip);
        if (index < 0) return;
        try
        {
            var replacement = chip with { Comment = input.Text };
            ComposerContextDefaults.Validate(ViewModel.ComposerPower.ContextChips.Select(item => item == chip ? replacement.ToContext() : item.ToContext()).ToArray());
            ViewModel.ComposerPower.ContextChips[index] = replacement;
        }
        catch (ArgumentException error) { ViewModel.ComposerPower.Status = error.Message; }
    }

    private async void OnPiModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PiModelSelector.SelectedItem is PiModelOptionViewModel model)
        {
            await ViewModel.SelectPiModelAsync(model);
        }
    }

    private async void OnPiPermissionModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PiPermissionSelector.SelectedItem is PiRuntimeModeCapability mode) await ViewModel.SelectPiRuntimeModeAsync(mode);
    }

    private async void OnPiThinkingLevelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PiThinkingLevelSelector.SelectedItem is PiThinkingLevelOptionViewModel thinkingLevel)
        {
            await ViewModel.SelectPiThinkingLevelAsync(thinkingLevel);
        }
    }

    private bool TryApplySelectedFileMention()
    {
        var index = ViewModel.FileMentions.SelectedIndex;
        if (index < 0 || index >= ViewModel.FileMentions.Suggestions.Count)
        {
            return false;
        }

        ApplyFileMention(ViewModel.FileMentions.Suggestions[index]);
        return true;
    }

    private void ApplyFileMention(ProjectFileMatch match)
    {
        if (_activeFileMentionToken is not { } token)
        {
            ViewModel.CloseFileMentionSuggestions();
            return;
        }

        var text = PromptInput.Text;
        if (token.Start < 0 || token.End > text.Length || token.Start >= token.End)
        {
            ViewModel.CloseFileMentionSuggestions();
            return;
        }

        var insertedMention = FormatFileMention(match.RelativePath);
        var replacement = insertedMention + " ";
        var updated = text[..token.Start] + replacement + text[token.End..];
        var caret = token.Start + replacement.Length;
        _isApplyingFileMention = true;
        try
        {
            PromptInput.Text = updated;
            PromptInput.SelectionStart = caret;
            PromptInput.SelectionLength = 0;
        }
        finally
        {
            _isApplyingFileMention = false;
        }

        _activeFileMentionToken = null;
        ViewModel.CloseFileMentionSuggestions();
        PromptInput.Focus(FocusState.Programmatic);
    }

    private void ScrollSelectedFileMentionIntoView()
    {
        var index = ViewModel.FileMentions.SelectedIndex;
        if (index >= 0 && index < ViewModel.FileMentions.Suggestions.Count)
        {
            FileMentionSuggestionsList.ScrollIntoView(ViewModel.FileMentions.Suggestions[index]);
        }
    }

    private static bool TryReadFileMentionToken(
        string text,
        int caret,
        out FileMentionToken token)
    {
        token = default;
        if (caret <= 0 || caret > text.Length)
        {
            return false;
        }

        var start = caret - 1;
        while (start >= 0 && !char.IsWhiteSpace(text[start]))
        {
            if (text[start] == '@')
            {
                break;
            }

            start--;
        }

        if (start < 0 || text[start] != '@' ||
            (start > 0 && !IsFileMentionBoundary(text[start - 1])))
        {
            return false;
        }

        var query = text[(start + 1)..caret];
        if (query.Any(char.IsWhiteSpace) || query.Contains('@'))
        {
            return false;
        }

        var end = caret;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        token = new FileMentionToken(start, end, query);
        return true;
    }

    private static bool IsFileMentionBoundary(char value) =>
        char.IsWhiteSpace(value) || value is '(' or '[' or '{';

    private static string FormatFileMention(string relativePath) =>
        relativePath.Any(char.IsWhiteSpace)
            ? $"@\"{relativePath.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : $"@{relativePath}";

    private readonly record struct FileMentionToken(int Start, int End, string Query);
}
