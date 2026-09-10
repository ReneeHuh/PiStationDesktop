using PiStation.App.ViewModels;
using PiStation.Protocol.Models;

namespace PiStation.CommandSystem.Tests;

public sealed class UsageDashboardViewModelTests
{
    [Fact]
    public void DatePresetsAndCustomDatesUseHalfOpenLocalDaysAcrossDst()
    {
        var vm = new UsageDashboardViewModel { Preset = "Custom dates", FromDate = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), ThroughDate = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero) };
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); var query = vm.CreateQuery(timeZone: zone);
        Assert.Equal(TimeSpan.FromHours(25), query.ToUtc - query.FromUtc);
        vm.Preset = "Past 24 hours"; query = vm.CreateQuery(timeZone: zone); Assert.True(query.Hourly); Assert.Equal(TimeSpan.FromHours(24), query.ToUtc - query.FromUtc);
        vm.Preset = "Custom dates"; vm.ThroughDate = vm.FromDate?.AddDays(-1); Assert.Throws<ArgumentException>(() => vm.CreateQuery(timeZone: zone));
    }
    [Fact]
    public void ModelProviderFiltersResetAndClearDoesNotLeakAnotherHostsData()
    {
        var vm = new UsageDashboardViewModel { Provider = "provider", Model = "model", HistoryDirectory = "C:\\fixture" };
        var query = vm.CreateQuery(); Assert.Equal("provider", query.Provider); Assert.Equal("model", query.Model);
        vm.Provider = "other"; Assert.Null(vm.CreateQuery().Model);
        vm.Clear(); Assert.Null(vm.CreateQuery().Provider); Assert.Null(vm.CreateQuery().HistoryDirectory); Assert.Empty(vm.Timeline);
    }
    [Fact]
    public void DashboardMarksUnknownCostsAndRebuildsTokenAndCostCharts()
    {
        var vm = new UsageDashboardViewModel(); var query = vm.CreateQuery();
        var totals = new UsageTotals(100, 20, 30, 10, 5, 160, 2, .2m, 2, 1, 1, 1, 1);
        vm.Apply(new(query, totals, [new("day", totals)], [new("p", "m", totals)], ["p"], ["m"], new(DateTimeOffset.UtcNow, 1, 0, 0, 0, 1, 0, 0, [], []), new(null, true, 0, "Unavailable")), "Test host");
        Assert.Contains("partial", vm.Summary); Assert.Contains("1 unpriced", vm.Coverage); Assert.Single(vm.ProviderChart); Assert.Contains("160", vm.Timeline[0].Value);
        vm.Metric = "Known cost (USD)"; Assert.Contains("2.0000", vm.Timeline[0].Value); Assert.Contains("Test host", vm.Scope);
    }
}
