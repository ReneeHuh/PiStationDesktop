using CommunityToolkit.Mvvm.ComponentModel;
using PiStation.ClientRuntime;

namespace PiStation.App.ViewModels;

public sealed class ExternalPromptEditorViewModel : ObservableObject
{
    private string _executable = "notepad.exe", _arguments = "", _status = "", _text = "", _filePath = "";
    private bool _busy, _hasEdit, _canApply;
    public string Executable { get => _executable; set => SetProperty(ref _executable, value); }
    public string Arguments { get => _arguments; set => SetProperty(ref _arguments, value); }
    public string Status { get => _status; internal set => SetProperty(ref _status, value); }
    public string EditedText { get => _text; internal set => SetProperty(ref _text, value); }
    public string FilePath { get => _filePath; internal set => SetProperty(ref _filePath, value); }
    public bool IsBusy { get => _busy; internal set { SetProperty(ref _busy, value); RaiseState(); } }
    public bool HasEdit { get => _hasEdit; internal set { SetProperty(ref _hasEdit, value); RaiseState(); } }
    public bool CanApply { get => !_busy && _hasEdit && _canApply; internal set { _canApply = value; RaiseState(); } }
    public bool CanLaunch => !IsBusy;
    public bool CanRead => !IsBusy && HasEdit;
    public ExternalEditorPreferences Preferences => new(Executable, Arguments);
    private void RaiseState()
    {
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(CanRead));
        OnPropertyChanged(nameof(CanApply));
    }
}
