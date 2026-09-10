using PiStation.App.ViewModels;
using PiStation.Protocol.Models;

namespace PiStation.CommandSystem.Tests;

public sealed class UsageLimitsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
    [Theory]
    [InlineData(56, "Ahead of pace")]
    [InlineData(55, "On pace")]
    [InlineData(45, "On pace")]
    [InlineData(44, "Under pace")]
    public void PaceUsesElapsedWindowTimeWithFivePointTolerance(double used, string expected)
    {
        var window = new UsageLimitWindow("s", "session", "Session", used, Now.AddMinutes(150), 300);
        Assert.Equal(50, UsageLimitPacing.ElapsedPercent(window, Now)); Assert.Equal(expected, UsageLimitPacing.Pace(window, Now));
        Assert.Equal("Resets in 2h 30m", UsageLimitPacing.Reset(window, Now));
        Assert.Equal("Awaiting reset update", UsageLimitPacing.Pace(window, Now.AddMinutes(150)));
        Assert.Equal("Pace unknown", UsageLimitPacing.Pace(window with { WindowDurationMins = null }, Now));
    }
    [Fact]
    public void DashboardKeepsDraftAndRevisionWhileRefreshesAdvanceAndStalePaceIsSuppressed()
    {
        var model = new UsageLimitsViewModel();
        var configuration = new UsageLimitSourceConfiguration("hub", "Fixture", "http://localhost:8317/", true, true);
        var window = new UsageLimitWindow("s", "session", "Session", 60, Now.AddMinutes(150), 300);
        var snapshot = new UsageLimitsDashboard(new(1, [configuration]), [new("hub", "cliproxy", "Fixture", Now, [new("a", "codex", "Fixture account", Now, [window])])], Now, "Active");
        model.Apply(snapshot, Now); model.Edit(configuration); model.Label = "Draft edit";
        var row = model.Sources[0]; model.Apply(snapshot, Now); Assert.Same(row, model.Sources[0]);
        model.ActionStatus = "Hub saved.";
        model.Apply(snapshot with { Settings = new(2, [configuration]) }, Now);
        Assert.Equal("Hub saved.", model.ActionStatus);
        Assert.Equal("Draft edit", model.Label); Assert.Equal(1, model.SaveRequest("").Revision); Assert.Null(model.SaveRequest("").ManagementKey);
        Assert.Contains("Ahead of pace", Assert.Single(Assert.Single(Assert.Single(model.Sources).Accounts).Windows).Detail);
        model.Tick(Now.AddMinutes(6)); Assert.Contains("Pace unavailable while stale", Assert.Single(Assert.Single(Assert.Single(model.Sources).Accounts).Windows).Detail);
        model.Clear(); Assert.Empty(model.Sources); Assert.Empty(model.Configurations); Assert.False(model.CanRemove); Assert.Empty(model.BaseUrl);
    }
}
