using PiStation.ClientRuntime;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    public ExternalPromptEditor ExternalEditor { get; }
    public bool CanEditPromptExternally => CanOperate && IsConnected && Composer.HasDraft && !Composer.HasRecoveryConflict && !Connection.HasUncertainCommand;
    public async Task<ExternalPromptEdit> PrepareExternalPromptAsync(CancellationToken cancellationToken = default)
    {
        if (!CanEditPromptExternally || SelectedThread is not { } selected) throw new InvalidOperationException("Select a connected, editable draft first.");
        var draft = await Composer.PrepareTurnAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Wait for the draft to load.");
        if (draft.ThreadId != selected.ThreadId) throw new InvalidOperationException("The selected conversation changed. Open its editor again.");
        return ExternalEditor.Create(draft);
    }
}
