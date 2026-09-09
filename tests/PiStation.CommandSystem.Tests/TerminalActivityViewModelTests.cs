using PiStation.App.ViewModels;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.CommandSystem.Tests;

public sealed class TerminalActivityViewModelTests
{
    [Fact]
    public void ActivityUpdatesTabNameAndAccessibilityWhileRestoredSessionsRequireRestart()
    {
        var descriptor = new TerminalSessionDescriptor(TerminalSessionId.New(), ProjectId.New(), "PowerShell 1",
            TerminalShellKind.PowerShell, "PowerShell", TerminalSessionState.Running, 100, 30, null, null,
            DateTimeOffset.UtcNow, Sequence.Initial);
        var model = new WorkbenchTerminalViewModel { AllowOperations = true, InputText = "echo hello" };
        model.ApplySessions([descriptor]);
        var tab = Assert.Single(model.Sessions);
        Assert.Contains("checking activity", model.SessionSummary);
        var properties = new List<string?>();
        tab.PropertyChanged += (_, args) => properties.Add(args.PropertyName);
        model.ApplyDescriptor(descriptor with { HasRunningSubprocess = true, ForegroundCommand = "node" });
        Assert.Equal("node", tab.Name); Assert.Contains("child process running", tab.AccessibleName);
        Assert.Contains(nameof(tab.Name), properties); Assert.Contains(nameof(tab.AccessibleName), properties);
        Assert.Contains("node", model.SessionSummary);
        model.ApplyDescriptor(descriptor with { HasRunningSubprocess = false });
        Assert.Equal("PowerShell 1", tab.Name);
        Assert.Contains("no child process detected", model.SessionSummary);
        model.ApplyDescriptor(descriptor with { State = TerminalSessionState.Interrupted, HasRunningSubprocess = false });
        model.InputText = "echo hello";
        Assert.False(model.CanSend); Assert.False(model.CanStop); Assert.True(model.CanRestart);
        Assert.Contains("restart", model.SessionSummary);
    }
}
