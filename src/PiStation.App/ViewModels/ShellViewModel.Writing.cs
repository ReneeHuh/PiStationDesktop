using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    private string? PullRequestTargetBranch()
    {
        var branch = Settings.PullRequestBaseBranch.Trim();
        foreach (var prefix in new[] { "refs/remotes/origin/", "refs/heads/", "origin/" })
            if (branch.StartsWith(prefix, StringComparison.Ordinal)) return branch[prefix.Length..];
        return branch.Length == 0 ? null : branch;
    }

    public async Task SaveWritingSettingsAsync()
    {
        try
        {
            var saved = await RequireClient().SaveSourceControlWritingSettingsAsync(Settings.CreateWritingSettings());
            Settings.ApplyWritingSettings(saved);
            Settings.WritingStatus = "Writing preferences saved.";
        }
        catch (Exception error) { Settings.WritingStatus = error.Message; }
    }

    public async Task<GeneratedSourceControlText?> GenerateSourceControlTextAsync(bool forPullRequest, CancellationToken cancellationToken = default)
    {
        var project = SelectedProject;
        var thread = SelectedThread;
        if (project is null || Settings.IsGeneratingText) return null;
        Settings.IsGeneratingText = true;
        Settings.WritingStatus = "Pi is generating text…";
        if (!forPullRequest) WorkbenchChanges.Status = Settings.WritingStatus;
        var model = PiConfiguration.SelectedModel?.Selection ?? project.DefaultModel;
        var paths = forPullRequest ? null : WorkbenchChanges.SelectedCommitPaths;
        try
        {
            var result = await RequireClient().GenerateSourceControlTextAsync(new GenerateSourceControlTextRequest(
                new WorkspaceTarget(project.ProjectId, thread?.ThreadId), forPullRequest,
                BaseBranch: forPullRequest ? Settings.PullRequestBaseBranch : null,
                FilePaths: paths, Model: model), cancellationToken);
            if (SelectedProject?.ProjectId != project.ProjectId || SelectedThread?.ThreadId != thread?.ThreadId)
            {
                Settings.WritingStatus = "The workspace changed during generation. Generate again in the selected workspace.";
                return null;
            }
            if (paths is not null && !paths.SequenceEqual(WorkbenchChanges.SelectedCommitPaths))
            {
                Settings.WritingStatus = "The commit file selection changed during generation. Generate again for the selected files.";
                WorkbenchChanges.Status = Settings.WritingStatus;
                return null;
            }
            Settings.WritingStatus = "Text generated. Review it before submitting.";
            return result;
        }
        catch (Exception error)
        {
            Settings.WritingStatus = error is OperationCanceledException ? "Text generation was cancelled." : error.Message;
            WorkbenchChanges.Status = Settings.WritingStatus;
            return null;
        }
        finally { Settings.IsGeneratingText = false; }
    }

    public async Task<bool> GenerateCommitMessageAsync(CancellationToken cancellationToken = default)
    {
        var original = WorkbenchChanges.CommitMessage;
        var result = await GenerateSourceControlTextAsync(false, cancellationToken);
        if (result is null) return false;
        if (WorkbenchChanges.CommitMessage != original)
        {
            Settings.WritingStatus = "Your commit message changed while Pi was generating. Your edits were kept.";
            WorkbenchChanges.Status = Settings.WritingStatus;
            return false;
        }
        WorkbenchChanges.CommitMessage = result.Title + (string.IsNullOrWhiteSpace(result.Body) ? "" : "\n\n" + result.Body);
        WorkbenchChanges.Status = "Pi generated a commit message. Review it before committing.";
        return true;
    }
}
