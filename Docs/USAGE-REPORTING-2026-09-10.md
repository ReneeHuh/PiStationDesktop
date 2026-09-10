# Usage reporting — protocol 54

Implementation against the `cea6c8f` working tree for USE-02, USE-03, USE-04 and USE-07.

## T3 review

Reviewed the local T3 checkout at `0a590fa01`:

| T3 source | Behavior adapted to PiStation |
| --- | --- |
| `apps/server/src/usage/UsageService.ts`, `usageTranscriptReader.ts`, `usageScanCache.ts` | Discover transcript files on the host, cache by file metadata, explicitly rescan, retain usable cached pricing when a refresh fails, and report incomplete history. |
| `usageTranscripts.ts`, `usageAggregation.ts` | Deduplicate copied/resumed history before date/model aggregation; keep reasoning as a subset of output; separate known cost from unpriced records. |
| `usagePricing.ts` | Prefer reported cost, otherwise apply provider-qualified model rates. Calculate cache-read savings from the difference between ordinary input and cached input rates. Missing cache prices use the ordinary input rate. |
| `packages/contracts/src/usage.ts`, `apps/web/src/components/usage/UsagePage.tsx`, `UsageProviderChart.tsx`, `apps/web/src/state/usage.ts` | Explicit time windows, daily/hourly charts, provider/model breakdowns, pricing coverage and source status. |

T3 also has provider-specific transcript readers, incremental append offsets, multi-environment merging and quota/hub adapters. PiStation uses its existing Pi JSONL format and WinUI shell. Its file cache reuses unchanged files and reparses changed files; the dashboard covers the selected environment. Quotas and pooled account hubs remain USE-05/06.

## Native workflow

Open **Settings → Usage** or **Open Usage Dashboard** in the command palette.

- The initial report covers the past 30 calendar days. Presets include the past 24 hours, 7/30/90 days, and custom inclusive local dates. Hourly labels include the UTC offset so repeated daylight-saving hours remain distinct; RPC ranges are half-open UTC intervals.
- Expand **Date range, filters and refresh** to select a provider/model, enter an additional absolute history directory on the host, apply filters, force a full rescan, or refresh pricing.
- Charts switch between tokens and known USD cost. Model rows show input/output/cache-read/cache-write tokens, cost, cache-read savings, and unpriced counts. Zero-usage time buckets remain visible.
- History status distinguishes cached files, duplicate records removed, malformed/incomplete lines, skipped files, legacy aggregates and unresolved child summaries. Costs are API-equivalent estimates, not subscription invoices. Reasoning is not added to output a second time.
- Existing PiStation session files, saved child sessions and the configured `PI_CODING_AGENT_DIR/sessions` (or the standard `~/.pi/agent/sessions`) are discovered automatically. The optional directory is on the selected host, including for remote connections. Temporary-history hosts scan only their owned roots and do not persist scan/pricing caches.

## Collection and reconciliation

`UsageService` reads newline-complete message records. It does not load transcript text into the client or cache. A partial final line is retried after the file changes. The scan cache contains only file metadata and usage records; a corrupt cache causes a fresh scan. Deleted or rewritten files replace their cached contribution on the next query; explicit rescan also handles an unchanged size/timestamp. Cache replacement is atomic and cancellation leaves the previous cache intact.

Every completed live assistant message also enters the host's canonical usage ledger. A SHA-256 fingerprint includes provider, model, billing timestamp, canonicalized message content and usage counters, allowing live events and saved/copy/fork records to reconcile without relying on the containing file's identity. Records without valid timestamps or usage counters are reported as malformed. All billed branches are considered, not only the currently selected branch.

Older turn aggregates are used only when canonical message history does not supersede them, matched by thread, session ID or saved session path. The report warns when legacy aggregates are superseded because those records cannot recover per-message or separate cache-write details. Newly recorded manual compaction totals retain separate attribution. The older diagnostics export summary remains a legacy aggregate view; Settings → Usage is the detailed history report.

Saved child transcripts are counted as child usage. A resumed child's copied prefix is removed by the same global message deduplication. Workflow totals and repeated progress snapshots are never summed into the child total again. Activity summaries are reduced to the latest usage-bearing snapshot per activity/control identity.

For external extensions, result items may supply:

```ts
{
  provider: "provider-id", model: "model-id",
  usageSessionId: "Pi session header ID", // reconcile with discovered JSONL
  usageSource: "session",
  usage: { input: 100, output: 20, cacheRead: 30, cacheWrite: 10,
           totalTokens: 160, cost: 0.12 }
}
```

For an activity with no saved transcript, use `usageSource: "activity"` and cumulative counters for that activity's own model calls, excluding its children and copied context history. Supply one provider/model per activity; saved transcripts preserve mixed-model attribution. A stable registered `controlId` also reconciles repeated reports across tool calls. Such summaries are attributed to their completion/latest update time, whereas saved transcript records retain per-message times. Unknown costs stay absent/null, including when newer counters replace an earlier priced snapshot. Extensions that supply neither discoverable session identity nor an explicit activity source are counted as unresolved and excluded from monetary/token totals; their unknown overlap is not guessed. Existing individual child activity remains visible in the conversation.

## Pricing and limits

Refresh downloads the public [LiteLLM model pricing table](https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json), the same source reviewed in T3. No transcript or account credential is sent. Base input/output/cache rates are stored locally; a table older than 24 hours is marked stale. Refresh is explicit. Provider-qualified lookup avoids borrowing another provider's price or resolving conflicting aliases arbitrarily. Pi-reported costs, including an explicit zero, take precedence. Savings coverage is separate from cost coverage, and legacy combined-cache records do not invent a cache-read saving.

Scan thresholds stop starting further files after 10,000 files or 500,000 unique messages; the final file can contribute up to another 100,000 usage records. Other limits are 128 MiB per source/cache file, 4 MiB per JSONL line, eight subdirectory levels, and a two-minute report deadline. Pricing downloads have a 15-second deadline and an 8 MiB body cap. The UI accepts up to 366 calendar days; hourly RPC requests are capped at seven days. Symlink/reparse-point recursion is skipped. Limits and inaccessible/corrupt sources produce explicit incomplete-source counts or errors.

Protocol 54 adds `GetUsageDashboard` (read access) and `RefreshUsageDashboard` (operate access) on local, HTTPS and SSH transports. Read-only clients cannot select arbitrary history directories or trigger forced rescans/pricing refreshes; host paths are omitted from their scan metadata.

## Validation

- Final solution/WinUI build: **zero warnings and errors** (`TestResults/usage-build-final.log`).
- Full default code gate: **1,112 passed / 1 failed / 27 explicit optional skips**. [Retained first-attempt report](../TestResults/code-Debug-20260910-151520-d754dbcd/summary.json). ClientRuntime 482 passed; CommandSystem 89 passed/2 skipped; Host 404 passed/1 skipped; PiRpc 78 passed/1 failed/24 skipped; Protocol 59 passed.
- The failure is the unchanged `PiShellTests.ShellStreamsByRequestIdAndWaitsForExplicitCancellation`: its two-second `get_state` readiness timeout expired before the shell operation began. The same startup failure was recorded in the September 9 tracker evidence. No startup/shell timeout was changed, and the historical BUG-09 heartbeat root cause remains open. A later pass is a follow-up result, not a replacement for this first failure.
- Static visual contract: **24 states, 4 responsive layouts and 3 text scales passed** (`TestResults/usage-visual-contract.log`).

- Final affected checks after the last child-cost/history-directory corrections: **37 passed / zero failed or skipped**, including the unchanged shell startup case. [Report](../TestResults/code-Debug-20260910-153056-6fef6fd6/summary.json). Coverage includes copied/live/history reconciliation, resumed children and repeated control reports, missing/stale pricing, malformed and partial JSONL, durable cache restart/rescan/deletion, explicit directory clearing, local/HTTPS/SSH RPCs, read-only restrictions, daylight-saving date boundaries and dashboard rendering data.
- Final native fixture journey: **all six grouped checks passed**. [Result](../TestResults/usage-dashboard-native/7412fa3ffec34b0f8d9cc99dd9791957/result.json), [overview](../TestResults/usage-dashboard-native/7412fa3ffec34b0f8d9cc99dd9791957/usage-overview.png), [cost chart and model breakdown](../TestResults/usage-dashboard-native/7412fa3ffec34b0f8d9cc99dd9791957/usage-charts.png). It covers parent/fork/resumed-child dedupe, cache savings, 90-day/provider/model filters, cost charts, explicit rescan, relaunch cache recovery, and actual public-price refresh with reported costs unchanged.
- The first native attempt used the launcher's inherited default CLI history because `winapp run` did not inherit the fixture environment variable. The harness now writes an isolated Pi runtime configuration before launch. This was a test isolation correction; the earlier failed result is retained under `TestResults/usage-dashboard-native/23ea85b5024842129e9803ed52db6f20`.

No provider sign-in, gist publication or real-account authentication was performed. The native pricing test downloads only the public pricing table.
