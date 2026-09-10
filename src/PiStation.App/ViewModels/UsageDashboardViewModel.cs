using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed record UsageChartRow(string Label, string Value, double BarWidth);
public sealed record UsageBreakdownRow(string Label, string Detail);

public sealed class UsageDashboardViewModel : ObservableObject
{
    private UsageDashboard? _report;
    public IReadOnlyList<string> Presets { get; } = ["Past 24 hours", "Past 7 days", "Past 30 days", "Past 90 days", "Custom dates"];
    public IReadOnlyList<string> Metrics { get; } = ["Tokens", "Known cost (USD)"];
    public ObservableCollection<string> Providers { get; } = ["All providers"];
    public ObservableCollection<string> Models { get; } = ["All models"];
    public ObservableCollection<UsageChartRow> Timeline { get; } = [];
    public ObservableCollection<UsageChartRow> ProviderChart { get; } = [];
    public ObservableCollection<UsageBreakdownRow> Breakdown { get; } = [];
    private string _preset = "Past 30 days", _metric = "Tokens", _provider = "All providers", _model = "All models", _directory = "";
    private DateTimeOffset? _from = DateTimeOffset.Now.Date.AddDays(-29), _through = DateTimeOffset.Now.Date;
    private string _status = "Open usage to scan this environment's history.", _summary = "No usage loaded.", _coverage = "", _pricing = "", _scope = "";
    private bool _busy;
    public string Preset { get => _preset; set { if (SetProperty(ref _preset, value)) OnPropertyChanged(nameof(CustomDates)); } }
    public bool CustomDates => Preset == "Custom dates";
    public string Metric { get => _metric; set { if (SetProperty(ref _metric, value)) RebuildCharts(); } }
    public string Provider { get => _provider; set { if (SetProperty(ref _provider, value)) Model = "All models"; } }
    public string Model { get => _model; set => SetProperty(ref _model, value); }
    public string HistoryDirectory { get => _directory; set => SetProperty(ref _directory, value); }
    public DateTimeOffset? FromDate { get => _from; set => SetProperty(ref _from, value); }
    public DateTimeOffset? ThroughDate { get => _through; set => SetProperty(ref _through, value); }
    public bool IsBusy { get => _busy; internal set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(IsIdle)); } }
    public bool IsIdle => !IsBusy;
    public string Status { get => _status; internal set => SetProperty(ref _status, value); }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string Coverage { get => _coverage; private set => SetProperty(ref _coverage, value); }
    public string Pricing { get => _pricing; private set => SetProperty(ref _pricing, value); }
    public string Scope { get => _scope; private set => SetProperty(ref _scope, value); }
    public UsageQuery CreateQuery(DateTimeOffset? clock = null, TimeZoneInfo? timeZone = null)
    {
        var now = clock ?? DateTimeOffset.UtcNow; var zone = timeZone ?? TimeZoneInfo.Local;
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var from = Preset == "Past 24 hours" ? now.AddHours(-24) : ToUtc(Preset == "Custom dates" ? FromDate?.Date ?? throw new ArgumentException("Choose a start date.") :
            today.AddDays(Preset == "Past 7 days" ? -6 : Preset == "Past 90 days" ? -89 : -29));
        var to = Preset == "Past 24 hours" ? now : ToUtc((Preset == "Custom dates" ? ThroughDate?.Date ?? throw new ArgumentException("Choose an end date.") : today).AddDays(1));
        if (from >= to || to - from > TimeSpan.FromDays(366)) throw new ArgumentException("Choose an end date on or after the start date, within 366 days.");
        return new(from, to, zone.Id, Preset == "Past 24 hours", Provider == "All providers" ? null : Provider,
            Model == "All models" ? null : Model, string.IsNullOrWhiteSpace(HistoryDirectory) ? null : HistoryDirectory.Trim());
        DateTimeOffset ToUtc(DateTime date) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), zone);
    }
    public void Clear()
    {
        _report = null; Timeline.Clear(); ProviderChart.Clear(); Breakdown.Clear(); Providers.Clear(); Providers.Add("All providers"); Models.Clear(); Models.Add("All models");
        Provider = "All providers"; Model = "All models"; HistoryDirectory = ""; Summary = "No usage loaded."; Coverage = ""; Pricing = ""; Scope = "";
    }
    public void Apply(UsageDashboard report, string environment)
    {
        _report = report; var totals = report.Totals;
        Scope = $"{environment} · {report.Query.FromUtc.ToLocalTime():g} – {report.Query.ToUtc.ToLocalTime():g} (end exclusive)";
        Summary = $"{totals.TotalTokens:N0} tokens · ${totals.KnownCostUsd:N4} known cost{(totals.UnpricedRecords > 0 ? " (partial)" : "")}\n" +
            $"Input {totals.InputTokens:N0} · output {totals.OutputTokens:N0} · cache read {totals.CacheReadTokens:N0} · cache write {totals.CacheWriteTokens:N0}\n" +
            $"Cache read savings ${totals.CacheSavingsUsd:N4} · pricing coverage {totals.SavingsPricedRecords:N0}/{totals.Records:N0} records";
        Coverage = $"{totals.Records:N0} records · {totals.ChildRecords:N0} child records · {totals.UnpricedRecords:N0} unpriced · {totals.ReportedCostRecords:N0} reported costs\n" +
            $"{report.Scan.Files:N0} files ({report.Scan.CachedFiles:N0} cached) · {report.Scan.DuplicateRecords:N0} duplicate records removed · {report.Scan.MalformedLines:N0} incomplete/invalid lines · {report.Scan.SkippedFiles:N0} skipped files · {report.Scan.LegacyRecords:N0} legacy aggregates";
        Pricing = $"{report.Pricing.Models:N0} rates · {(report.Pricing.Stale ? "stale or unavailable" : "current")} · updated {report.Pricing.UpdatedUtc?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? "never"}\n{report.Pricing.Detail}";
        var provider = Provider; var model = Model;
        Providers.Clear(); Providers.Add("All providers"); foreach (var item in report.Providers) Providers.Add(item);
        Models.Clear(); Models.Add("All models"); foreach (var item in report.Models) Models.Add(item);
        Provider = provider; Model = model;
        Breakdown.Clear(); foreach (var row in report.Breakdown)
            Breakdown.Add(new($"{row.Provider} / {row.Model}", $"{row.Totals.TotalTokens:N0} tokens · ${row.Totals.KnownCostUsd:N4} known cost · ${row.Totals.CacheSavingsUsd:N4} cache savings\n" +
                $"Input {row.Totals.InputTokens:N0} · output {row.Totals.OutputTokens:N0} · cache read {row.Totals.CacheReadTokens:N0} · write {row.Totals.CacheWriteTokens:N0} · unpriced {row.Totals.UnpricedRecords:N0}"));
        Status = string.Join("\n", new[] { totals.Records == 0 ? "No usage in this date range and filter." : $"History scanned {report.Scan.ScannedUtc.ToLocalTime():g}." }.Concat(report.Scan.Warnings));
        RebuildCharts();
    }
    private void RebuildCharts()
    {
        Timeline.Clear(); ProviderChart.Clear(); if (_report is null) return;
        Fill(Timeline, _report.Buckets.Select(b => (b.Label, Value(b.Totals))));
        Fill(ProviderChart, _report.Breakdown.GroupBy(b => b.Provider).Select(g => (g.Key, g.Sum(b => Value(b.Totals)))));
        decimal Value(UsageTotals totals) => Metric == "Tokens" ? totals.TotalTokens : totals.KnownCostUsd;
        void Fill(ObservableCollection<UsageChartRow> target, IEnumerable<(string Label, decimal Value)> values)
        {
            var rows = values.ToArray(); var max = rows.Select(r => r.Value).DefaultIfEmpty().Max(); if (max <= 0) max = 1;
            foreach (var row in rows) target.Add(new(row.Label, Metric == "Tokens" ? $"{row.Value:N0} tokens" : $"${row.Value:N4}", (double)(row.Value / max) * 180));
        }
    }
}
