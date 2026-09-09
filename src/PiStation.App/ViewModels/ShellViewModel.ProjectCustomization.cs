using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    public async Task UpdateProjectCustomizationAsync(ProjectDescriptor project, IReadOnlyList<ProjectScript> scripts, string? icon, ProjectIconUpload? upload = null)
    {
        try
        {
            var client = RequireClient();
            var members = ProjectGroups.FirstOrDefault(group => group.Members.Any(member => member.ProjectId == project.ProjectId))?
                .Members.ToArray() ?? [project];
            var updateIcons = upload is not null || project.Icon != icon;
            var updated = project;
            if (!scripts.SequenceEqual(project.Scripts ?? []))
                updated = await client.UpdateProjectDefaultsAsync(new(project.ProjectId, project.DefaultWorkspaceMode,
                    project.DefaultModel, project.DefaultThinkingLevel, project.DefaultRuntimeModeId, project.AutoPullDefaultBranch,
                    scripts, project.Icon, true, UpdateIcon: false)).ConfigureAwait(false);
            var changed = updateIcons
                ? await client.UpdateProjectIconsAsync(new(members.Select(member => member.ProjectId).ToArray(), icon, upload)).ConfigureAwait(false)
                : [updated];
            await RunOnUiThreadAsync(() =>
            {
                foreach (var item in changed)
                {
                    var index = Projects.ToList().FindIndex(existing => existing.ProjectId == item.ProjectId);
                    if (index >= 0) Projects[index] = item;
                    if (SelectedProject?.ProjectId == item.ProjectId) SelectedProject = item;
                }
                Settings.Status = updateIcons && members.Length > 1
                    ? $"Icon saved for {members.Length} checkouts. Scripts apply only to {project.DisplayName}."
                    : "Project customization saved.";
            }).ConfigureAwait(false);
            await RefreshProjectGroupsAsync().ConfigureAwait(false);
        }
        catch (Exception error) { ReportRuntimeError(error); }
    }
}
