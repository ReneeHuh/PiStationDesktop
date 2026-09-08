using System.Text;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    public async Task OpenArtifactFileAsync(string absolutePath, int? line = null)
    {
        var project = SelectedProject;
        var threadId = SelectedThread?.ThreadId;
        if (project is null) { ComposerPower.Status = "Select a project before opening an artifact."; return; }
        WorkbenchFileDocumentViewModel document = null!;
        long version = 0;
        await RunOnUiThreadAsync(() =>
        {
            Layout.SelectedPanel = WorkbenchPanelKind.Files;
            Layout.IsRightPanelOpen = true;
            document = WorkbenchFiles.OpenExternalDocument(absolutePath);
            version = document.BeginExternalRead();
        }).ConfigureAwait(false);
        try
        {
            var result = await RequireClient().ReadArtifactFileAsync(new(new(project.ProjectId, threadId), absolutePath)).ConfigureAwait(false);
            await RunOnUiThreadAsync(() =>
            {
                if (document.ExternalReadVersion != version || SelectedProject?.ProjectId != project.ProjectId || SelectedThread?.ThreadId != threadId || !WorkbenchFiles.OpenDocuments.Contains(document)) return;
                if (result.IsBinary) document.ApplyLoadFailure("This binary artifact has no supported in-app renderer.");
                else if (document.IsImage) document.ApplyExternalImage(result.Content, result.ByteLength);
                else if (document.IsPdf || document.IsMedia)
                {
                    document.LocalPreviewPath = null;
                    document.ApplyAsset(new(project.ProjectId, result.AbsolutePath, result.Content, result.ByteLength, result.MediaType, ""));
                    document.Status = $"External read-only file • {result.ByteLength:N0} bytes • {result.MediaType}";
                }
                else document.ApplyExternalText(Encoding.UTF8.GetString(result.Content).TrimStart('\uFEFF'), result.ByteLength, result.IsTruncated);
                document.RequestLineReveal(line);
            }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await RunOnUiThreadAsync(() =>
            {
                if (document.ExternalReadVersion == version && WorkbenchFiles.OpenDocuments.Contains(document)) document.ApplyLoadFailure($"Unable to open artifact read-only: {error.Message}");
            }).ConfigureAwait(false);
        }
    }
}
