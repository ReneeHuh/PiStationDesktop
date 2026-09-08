using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class SettingsViewModel
{
    private int _writingStyleIndex;
    private string _writingInstructions = "";
    private string _writerProvider = "";
    private string _writerModel = "";
    private string _writingStatus = "Uses the current Pi model unless a dedicated model is saved.";
    private string _pullRequestBaseBranch = "";
    private bool _isGeneratingText;
    private long _writingRevision;

    public int WritingStyleIndex { get => _writingStyleIndex; set => SetProperty(ref _writingStyleIndex, value); }
    public string WritingInstructions { get => _writingInstructions; set => SetProperty(ref _writingInstructions, value); }
    public string WriterProvider { get => _writerProvider; set => SetProperty(ref _writerProvider, value); }
    public string WriterModel { get => _writerModel; set => SetProperty(ref _writerModel, value); }
    public string WritingStatus { get => _writingStatus; internal set => SetProperty(ref _writingStatus, value); }
    public string PullRequestBaseBranch { get => _pullRequestBaseBranch; set => SetProperty(ref _pullRequestBaseBranch, value); }
    public bool IsGeneratingText
    {
        get => _isGeneratingText;
        internal set { if (SetProperty(ref _isGeneratingText, value)) OnPropertyChanged(nameof(CanGenerateText)); }
    }
    public bool CanGenerateText => !IsGeneratingText;

    internal void ApplyWritingSettings(SourceControlWritingSettings settings)
    {
        WritingStyleIndex = (int)settings.Style;
        WritingInstructions = settings.CustomInstructions;
        WriterProvider = settings.Model?.ProviderId ?? "";
        WriterModel = settings.Model?.ModelId ?? "";
        _writingRevision = settings.Revision;
        WritingStatus = "Writing preferences loaded. Generated text is available for review before committing or creating a PR.";
    }

    internal SourceControlWritingSettings CreateWritingSettings() => new((SourceControlWritingStyle)WritingStyleIndex,
        WritingInstructions, string.IsNullOrWhiteSpace(WriterProvider) && string.IsNullOrWhiteSpace(WriterModel)
            ? null : new(WriterProvider.Trim(), WriterModel.Trim()), _writingRevision);
}
