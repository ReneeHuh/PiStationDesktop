using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    public async Task UpdateProjectCustomizationAsync(ProjectDescriptor project, IReadOnlyList<ProjectScript> scripts, string? icon)
    {
        try
        {
            var updated = await RequireClient().UpdateProjectDefaultsAsync(new(project.ProjectId, project.DefaultWorkspaceMode,
                project.DefaultModel, project.DefaultThinkingLevel, project.DefaultRuntimeModeId, project.AutoPullDefaultBranch,
                scripts, icon, true)).ConfigureAwait(false);
            await RunOnUiThreadAsync(() =>
            {
                var index = Projects.ToList().FindIndex(item => item.ProjectId == updated.ProjectId);
                if (index >= 0) Projects[index] = updated;
                if (SelectedProject?.ProjectId == updated.ProjectId) SelectedProject = updated;
                Settings.Status = "Project icon and scripts saved.";
            }).ConfigureAwait(false);
            await RefreshProjectGroupsAsync().ConfigureAwait(false);
        }
        catch (Exception error) { ReportRuntimeError(error); }
    }
}
