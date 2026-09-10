namespace PiStation.Protocol.Models;

// All ranges are half-open. The time zone only controls chart bucket labels.
public sealed record UsageQuery(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string TimeZoneId = "UTC",
    bool Hourly = false, string? Provider = null, string? Model = null, string? HistoryDirectory = null);
public sealed record RefreshUsageRequest(UsageQuery Query, bool Rescan = false, bool RefreshPricing = false);
public sealed record UsageTotals(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens,
    long ReasoningTokens, long TotalTokens, decimal KnownCostUsd, decimal CacheSavingsUsd, int Records,
    int UnpricedRecords, int ReportedCostRecords, int SavingsPricedRecords, int ChildRecords);
public sealed record UsageBucket(string Label, UsageTotals Totals);
public sealed record UsageModelBreakdown(string Provider, string Model, UsageTotals Totals);
public sealed record UsageScanStatus(DateTimeOffset ScannedUtc, int Files, int CachedFiles, int MalformedLines,
    int SkippedFiles, int DuplicateRecords, int LegacyRecords, int UnreconciledChildren, IReadOnlyList<string> Roots,
    IReadOnlyList<string> Warnings);
public sealed record UsagePricingStatus(DateTimeOffset? UpdatedUtc, bool Stale, int Models, string Detail);
public sealed record UsageDashboard(UsageQuery Query, UsageTotals Totals, IReadOnlyList<UsageBucket> Buckets,
    IReadOnlyList<UsageModelBreakdown> Breakdown, IReadOnlyList<string> Providers, IReadOnlyList<string> Models,
    UsageScanStatus Scan, UsagePricingStatus Pricing);
