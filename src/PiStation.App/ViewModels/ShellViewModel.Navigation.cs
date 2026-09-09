using PiStation.ClientRuntime;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    internal Func<Uri, Task>? OpenPreviewLink { get; set; }

    public async Task OpenBrowserLinkAsync(Uri uri, bool forceSystem = false)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https" or "mailto")) return;
        try
        {
            await Composition.BrowserLinkRouter.OpenAsync(uri, Layout.BrowserLinkTarget,
                SelectedThread is not null && OpenPreviewLink is not null, forceSystem,
                address => OpenPreviewLink!(address), async address =>
                {
                    if (!await Windows.System.Launcher.LaunchUriAsync(address))
                        throw new InvalidOperationException("Windows could not open this link.");
                });
        }
        catch (Exception error) { ReportRuntimeError(error); }
    }

    public event EventHandler<string>? CitationRequested;

    public async Task OpenMarkdownLinkAsync(string value)
    {
        var root = SelectedThread?.WorktreePath ?? SelectedProject?.CanonicalPath;
        if (root is not null && WorkspaceLink.Parse(value, root) is { } link)
        {
            Layout.SelectedPanel = WorkbenchPanelKind.Files;
            Layout.IsRightPanelOpen = true;
            await OpenWorkbenchFileAsync(link.RelativePath, link.Line).ConfigureAwait(false);
            return;
        }
        if (ArtifactLink.Parse(value) is { } artifact)
            await OpenArtifactFileAsync(artifact.AbsolutePath, artifact.Line).ConfigureAwait(false);
        else ComposerPower.Status = "This link does not point to a workspace file or an absolute artifact file.";
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
        else if (chip.MessageId is { } message)
        {
            await RunOnUiThreadAsync(() =>
            {
                var target = CitationSourceResolver.Resolve(chip.ToContext(),
                    Thread.Projection?.Timeline.OfType<PiStation.Protocol.Projections.MessageTimelineItem>() ?? []);
                if (target is not null) CitationRequested?.Invoke(this, target);
                else ComposerPower.Status = "The original message is unavailable or ambiguous. The saved citation text is still available.";
            }).ConfigureAwait(false);
        }
        else RunOnUiThread(() => ComposerPower.Status = chip.Text);
        }
        catch (Exception exception) { ReportRuntimeError($"The citation source could not be opened: {exception.Message}"); }
    }
}
