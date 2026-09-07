using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PiStation.App.ViewModels;
using PiStation.Protocol.Models;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace PiStation.App.Views.Controls;

public sealed partial class ComposerSurface : UserControl
{
    private FileMentionToken? _activeFileMentionToken;
    private bool _isApplyingFileMention;
    private bool _isPromptShiftKeyDown;

    public ComposerSurface(ShellViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    public ShellViewModel ViewModel { get; }

    public void FocusPrompt() => PromptInput.Focus(FocusState.Programmatic);

    private async void OnSendPromptClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.SendPromptAsync();

    private void OnPromptInputChanged(object sender, RoutedEventArgs e)
    {
        UpdatePromptHeight();
        if (_isApplyingFileMention)
        {
            return;
        }

        if (TryReadFileMentionToken(PromptInput.Text, PromptInput.SelectionStart, out var token))
        {
            _activeFileMentionToken = token;
            ViewModel.UpdateFileMentionQuery(token.Query);
        }
        else
        {
            _activeFileMentionToken = null;
            ViewModel.CloseFileMentionSuggestions();
        }
    }

    private void OnPromptInputSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePromptHeight();

    private void UpdatePromptHeight()
    {
        const double estimatedCharacterWidth = 7.4;
        const double additionalLineHeight = 20;
        var availableWidth = Math.Max(estimatedCharacterWidth * 24, PromptInput.ActualWidth - 16);
        var charactersPerLine = Math.Max(24, (int)(availableWidth / estimatedCharacterWidth));
        var visualLineCount = PromptInput.Text
            .Split('\n')
            .Sum(line => Math.Max(
                1,
                (int)Math.Ceiling(line.TrimEnd('\r').Length / (double)charactersPerLine)));
        PromptInput.Height = Math.Clamp(
            PromptInput.MinHeight + ((visualLineCount - 1) * additionalLineHeight),
            PromptInput.MinHeight,
            PromptInput.MaxHeight);
    }

    private async void OnPromptInputPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Shift)
        {
            _isPromptShiftKeyDown = true;
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
        if (IsShiftKeyDown())
        {
            InsertPromptLineBreak();
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
        foreach (var file in files)
        {
            if (!ViewModel.Composer.CanAttach)
            {
                break;
            }

            var properties = await file.GetBasicPropertiesAsync();
            await using var content = await file.OpenStreamForReadAsync();
            await ViewModel.AddAttachmentAsync(
                file.Name,
                file.ContentType,
                content,
                checked((long)properties.Size));
        }
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
        if (e.DataView.Contains(RightPanelHost.WorkspaceFileDragFormat))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Mention workspace file";
            e.DragUIOverride.IsCaptionVisible = true;
            e.Handled = true;
        }
    }

    private async void OnComposerDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(RightPanelHost.WorkspaceFileDragFormat))
        {
            return;
        }

        e.Handled = true;
        var value = await e.DataView.GetDataAsync(RightPanelHost.WorkspaceFileDragFormat);
        if (value is string relativePath && !string.IsNullOrWhiteSpace(relativePath))
        {
            InsertFileMentionAtCaret(relativePath);
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

    private async void OnPiModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PiModelSelector.SelectedItem is PiModelOptionViewModel model)
        {
            await ViewModel.SelectPiModelAsync(model);
        }
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
