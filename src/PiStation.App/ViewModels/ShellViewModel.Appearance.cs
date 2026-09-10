using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    private GetThreadCheckpointDiffRequest? _appearanceCheckpoint;
    public Task RefreshAppearanceDiffAsync()
    {
        if (WorkbenchChanges.SelectedChange is { } change) return SelectWorkbenchChangeAsync(change);
        if (_appearanceCheckpoint is { } checkpoint && checkpoint.ThreadId == SelectedThread?.ThreadId &&
            WorkbenchChanges.IsCheckpointDiff)
            return OpenCheckpointDiffAsync(checkpoint.TurnCount, checkpoint.Scope, checkpoint.RelativePath);
        return Task.CompletedTask;
    }
}
