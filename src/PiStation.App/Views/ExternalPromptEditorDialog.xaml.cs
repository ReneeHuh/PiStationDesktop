using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PiStation.App.ViewModels;
using PiStation.ClientRuntime;
using Windows.ApplicationModel.DataTransfer;

namespace PiStation.App.Views;

public sealed partial class ExternalPromptEditorDialog : ContentDialog, IDisposable
{
    private readonly ShellViewModel _shell;
    private ExternalPromptEdit? _edit;
    private bool _closed;
    private int _launchGeneration;
    private readonly CancellationTokenSource _lifetime = new();
    public ExternalPromptEditorViewModel Model { get; } = new();
    public ExternalPromptEditorDialog(ShellViewModel shell)
    {
        _shell = shell;
        InitializeComponent();
        Opened += OnOpened;
        Closed += (_, _) => Dispose();
    }
    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) => await RunAsync(async () =>
    {
        var preferences = _shell.ExternalEditor.LoadPreferences();
        Model.Executable = preferences.Executable;
        Model.Arguments = preferences.Arguments;
        var draft = await _shell.Composer.PrepareTurnAsync(_lifetime.Token);
        if (_closed) return;
        if (draft is null) throw new InvalidOperationException("Wait for a draft to load.");
        _edit = _shell.ExternalEditor.Load(draft.EnvironmentId, draft.ThreadId);
        if (_edit is not null)
        {
            BindEdit();
            await ReadAsync();
            Model.Status = "Recovered external edit. Save in the editor before using its text, or open it again.";
        }
        else await LaunchAsync();
    });
    private void BindEdit()
    {
        Model.HasEdit = _edit is not null;
        Model.CanApply = _edit is not null && _shell.CanEditPromptExternally;
        Model.FilePath = _edit is null ? "" : _shell.ExternalEditor.GetFilePath(_edit);
    }
    private async Task LaunchAsync()
    {
        _edit ??= await _shell.PrepareExternalPromptAsync(_lifetime.Token);
        if (_closed) return;
        BindEdit();
        var process = _shell.ExternalEditor.Launch(_edit, Model.Preferences);
        Model.Status = "Editor launched. Save your changes, then choose Use saved text. Nothing is sent to Pi.";
        _ = ObserveExitAsync(process, ++_launchGeneration, _lifetime.Token);
        await ReadAsync();
    }
    private async Task ObserveExitAsync(System.Diagnostics.Process process, int generation, CancellationToken cancellationToken)
    {
        // Launchers may exit immediately or reuse a process; never import on exit.
        using (process)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var code = process.ExitCode;
                if (code != 0) DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_closed && generation == _launchGeneration) Model.Status = $"Editor exited with code {code}. Your draft is unchanged; the editor file is retained.";
                });
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }
    private async Task ReadAsync()
    {
        if (_edit is not null) Model.EditedText = await _shell.ExternalEditor.ReadTextAsync(_edit, _lifetime.Token);
    }
    private async void OnLaunch(object sender, RoutedEventArgs args) => await RunAsync(LaunchAsync);
    private async void OnRefresh(object sender, RoutedEventArgs args) => await RunAsync(async () => { await ReadAsync(); Model.Status = "Saved text refreshed."; });
    private async void OnCopy(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        await ReadAsync();
        var package = new DataPackage();
        package.SetText(Model.EditedText);
        Clipboard.SetContent(package);
        Model.Status = "Edited text copied. Close this dialog to merge it into the composer.";
    });
    private async void OnDiscard(object sender, RoutedEventArgs args) => await RunAsync(() =>
    {
        if (_edit is not null) _shell.ExternalEditor.Discard(_edit);
        _launchGeneration++;
        _edit = null;
        BindEdit();
        Model.EditedText = "";
        Model.Status = "External edit discarded. The composer draft was kept.";
        return Task.CompletedTask;
    });
    private async void OnApply(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (!_shell.CanEditPromptExternally || _edit is null) throw new InvalidOperationException("Reconnect to the editable conversation before applying. Your editor file is retained.");
        await ReadAsync();
        var result = await _shell.Composer.ApplyExternalPromptAsync(_edit, Model.EditedText, _lifetime.Token);
        if (!result.Applied) { Model.Status = result.Message ?? "The draft changed; the editor file was retained."; return; }
        _edit = _shell.ExternalEditor.RecordApplied(_edit, result.CurrentDraft.Text);
        _launchGeneration++;
        BindEdit();
        Model.Status = "Saved to the composer. The working copy remains available until Discard edit; nothing was sent.";
    });
    private async Task RunAsync(Func<Task> action)
    {
        if (Model.IsBusy) return;
        Model.IsBusy = true;
        try { await action(); }
        catch (Exception error) { Model.Status = error.Message; }
        finally { Model.IsBusy = false; }
    }
}
