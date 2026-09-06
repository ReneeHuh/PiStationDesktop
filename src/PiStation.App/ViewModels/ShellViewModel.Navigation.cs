using PiStation.ClientRuntime;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    public event EventHandler<string>? CitationRequested;

    public async Task OpenMarkdownLinkAsync(string value)
    {
        var root = SelectedThread?.WorktreePath ?? SelectedProject?.CanonicalPath;
        if (root is null || WorkspaceLink.Parse(value, root) is not { } link)
        {
            ComposerPower.Status = "This link does not point to a file in the active workspace.";
            return;
        }
        Layout.SelectedPanel = WorkbenchPanelKind.Files;
        Layout.IsRightPanelOpen = true;
        await OpenWorkbenchFileAsync(link.RelativePath, link.Line).ConfigureAwait(false);
    }

    public async Task RevealComposerContextAsync(ComposerContextChipViewModel chip)
    {
        ArgumentNullException.ThrowIfNull(chip);
        try
        {
        if (chip.SourceThreadId is { } threadId && SelectedThread?.ThreadId != threadId)
        {
            var thread = await RequireClient().GetThreadAsync(threadId).ConfigureAwait(false);
            var project = Workspace.Projects.FirstOrDefault(item => item.ProjectId == thread?.ProjectId);
            if (thread is null || project is null)
            {
                RunOnUiThread(() => ComposerPower.Status = "The original thread is unavailable. The saved citation text is still attached.");
                return;
            }
            if (SelectedProject?.ProjectId != project.ProjectId) await SelectProjectAsync(project).ConfigureAwait(false);
            await SelectThreadAsync(thread).ConfigureAwait(false);
        }
        if (chip.RelativePath is not null)
            await RunOnUiThreadAsync(() => { Layout.SelectedPanel = WorkbenchPanelKind.Files; Layout.IsRightPanelOpen = true; }).ConfigureAwait(false);
        if (chip.RelativePath is { } relativePath) await OpenWorkbenchFileAsync(relativePath, chip.StartLine).ConfigureAwait(false);
        else if (chip.MessageId is { } message) RunOnUiThread(() => CitationRequested?.Invoke(this, message));
        else RunOnUiThread(() => ComposerPower.Status = chip.Text);
        }
        catch (Exception exception) { ReportRuntimeError($"The citation source could not be opened: {exception.Message}"); }
    }
}
