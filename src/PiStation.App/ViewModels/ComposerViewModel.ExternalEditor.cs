using PiStation.ClientRuntime;

namespace PiStation.App.ViewModels;

public sealed partial class ComposerViewModel
{
    public async Task<ExternalPromptApplyResult> ApplyExternalPromptAsync(ExternalPromptEdit edit, string text,
        CancellationToken cancellationToken = default)
    {
        using var lifetime = CreateLifetimeCancellation(cancellationToken);
        await _switchGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            await FlushAsync(lifetime.Token).ConfigureAwait(false);
            await _saveGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                var snapshot = await RunOnUiThreadAsync(() => new DraftSnapshot(_draft, _text, GetContext())).ConfigureAwait(false);
                var local = snapshot.Draft ?? throw new InvalidOperationException("The draft is unavailable. The editor file is retained.");
                var result = await ExternalPromptEditor.ApplyAsync(edit, text, local with { Text = snapshot.Text }, _loadDraft, _saveDraft, lifetime.Token).ConfigureAwait(false);
                if (!result.Applied) return result;
                return await RunOnUiThreadAsync(() =>
                {
                    // Extensions may update the composer while the host save is in flight.
                    var unchanged = _draft?.DraftId == local.DraftId && ExternalPromptEditor.SameText(_text, snapshot.Text);
                    var contextUnchanged = GetContext().SequenceEqual(snapshot.Context);
                    ApplySavedDraft(result.CurrentDraft);
                    if (unchanged) SetLoadedText(result.CurrentDraft.Text);
                    if (contextUnchanged) ReplaceContext(result.CurrentDraft.Context);
                    PersistRecovery();
                    if (HasUnsavedChanges) ScheduleSave(); else Status = "Saved";
                    return unchanged ? result : result with { Applied = false,
                        Message = "A newer composer edit was kept. Copy the external text to merge it; the editor file is retained." };
                }).ConfigureAwait(false);
            }
            finally { _saveGate.Release(); }
        }
        finally { _switchGate.Release(); }
    }
}
