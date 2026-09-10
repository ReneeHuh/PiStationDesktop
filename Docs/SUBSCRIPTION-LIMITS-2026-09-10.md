# Subscription limits and quota hubs

Protocol 55 working tree, based on `cea6c8f`. This batch implements USE-05 and USE-06 within the source boundaries below. The tracker now has 202 Done / 10 Partial / 9 Not started / 1 Review needed across 222 core rows: 20 open.

## Use the panel

Open **Settings → Limits**, or **Open Subscription Limits** in the command palette. Quota windows show percentage used, reset countdowns, and consumption pace. The marker shows the elapsed share of the window. Usage more than five percentage points above that share is ahead of pace; more than five below it is under pace. Unknown reset times or durations have unknown pace. Expired windows await fresh data rather than pretending usage reset to zero.

**Manage CLIProxyAPI hubs** accepts a label, a hub origin, and a management key. The hub must support `GET /v0/management/quota-scheduler/status`. HTTPS is required outside literal loopback addresses/localhost. Use the management key, not a model API key. Save, then **Refresh quotas**. Existing hubs can be renamed, disabled, have their key replaced, or be removed. A blank key preserves the existing key only when the origin remains the same. Changing the origin requires entering a key again.

Hub keys and their configuration are stored together in `usage-limit-sources.protected`, encrypted with Windows DPAPI for the host's Windows user. They never appear in returned settings or diagnostics. The password field clears on save, editor changes and closing settings. Revision checks reject stale settings edits. Read-only remote clients can see quota readings but cannot obtain hub configuration, force upstream probes, or change sources. Local, HTTPS and SSH connections use the same contracts and authorization rules.

Hubs refresh about once a minute while the host's background activity policy permits it. Manual refresh is explicit. Each request has a ten-second deadline including response-body reads; redirects are disabled, cookies are disabled, and responses are capped at 1 MiB. Eight hubs maximum, four concurrent probes. Refresh failures retain last-good bars with a stale label and a bounded error. Readings older than five minutes are stale; pace is suppressed while stale. Successful full snapshots replace the previous account/window set. Removing or editing a hub during an in-flight probe cannot restore its old state.

## Sources and scope

The CLIProxyAPI adapter maps Claude and Codex pooled accounts: `five_hour`, `seven_day`, `weekly`, and `fable` windows. `known: false` omits an unknown window; `hard_limited: true` displays 100% even when the reported percentage lags. Other providers remain visible with an unsupported label. Account identifiers are displayed as supplied by the hub; no email or plan tier is guessed. These accounts are monitoring sources and do not affect model routing.

Pi has no generic stable subscription-quota RPC. PiStation does not launch Claude Code or Codex runtimes to probe accounts. A Pi extension can publish provider-specific quota metadata through the shipped event bridge below. Without a hub or publishing extension, subscription limits remain explicitly unsupported. The batch does not add built-in direct OAuth probes for individual provider accounts, authenticate users, infer subscription quotas from token counts, spend reset credits, or implement account pooling/routing.

## Pi extension contract

The desktop management extension subscribes to `pistation:usage-limits:v1` on Pi's event bus. An extension owns its supported upstream quota API, credentials, and polling. Publish only metadata, never credentials, request bodies, prompts or responses. One publisher owns each stable source ID. Desktop-managed Pi processes receive the host-specific `PISTATION_QUOTA_ROOT`; launch settings cannot override that reserved variable.

```ts
pi.events.emit("pistation:usage-limits:v1", {
  id: "my-quota-adapter",
  label: "My provider subscription",
  mode: "snapshot", // or "update" for sparse account/window updates
  checkedAt: new Date().toISOString(),
  accounts: [{
    id: "stable-account-id", provider: "my-provider", label: "Work account",
    plan: "Subscription",
    windows: [{
      id: "five_hour", kind: "session", label: "Session",
      usedPercent: 42, resetsAt: resetTimestamp, windowDurationMins: 300
    }]
  }],
  reply: result => { /* { success: boolean, error?: string } */ }
});
```

Window kinds are `session`, `weekly`, `monthly`, and `other`. Percentages must be finite and are clamped to 0–100. Reset and duration are optional. Source IDs/labels are limited to 128 characters; account IDs/labels to 256, providers to 80, plans to 100. There may be 128 accounts per source and 32 windows per account. IDs must be unique within their collection. Publications over 1 MiB, invalid dates or timestamps more than five minutes into the future are rejected.

A snapshot authoritatively replaces that source. Updates upsert accounts/windows by stable ID and retain omitted reset/duration fields. Full snapshots remove omitted accounts/windows. Set an account's `unavailable` to `probeFailed` with `windows: []` to retain its last good bars and timestamp; `unsupported` clears bars. A full successful snapshot restores an unsupported account. Failed probes never masquerade as zero usage. Omitted accounts in sparse updates retain their original checked time.

The bridge writes one atomic, SHA-256-named JSON file per source under the host's `quota-feeds` directory. Its version-1 file contract matches the event metadata, with `version: 1` and a `checkedAt` field on every account; callbacks and extra properties are not serialized. The host independently validates and bounds every read. There are at most 32 feed files. Corrupt/incomplete reads retain the last good in-memory feed set with an error. Files survive runtime restarts, but age into stale status; a manual dashboard refresh rereads them rather than invoking an upstream provider probe. To remove a retired source and its file:

```ts
pi.events.emit("pistation:usage-limits:v1", { id: "my-quota-adapter", remove: true });
```

## T3 reference

Reviewed local `../t3code` at `0a590fa01`:

- `packages/contracts/src/providerUsageLimits.ts`: stable window identities, availability, and separate source/account snapshots.
- `apps/server/src/provider/providerUsageLimits.ts`: sparse updates, authoritative probes and retained good data after failures.
- `apps/server/src/usage/UsageLimitSources.ts`: background polling and authenticated quota-scheduler reads.
- `apps/server/src/usage/cliproxyUsageLimits.ts`: account/window mapping and hard-limit handling.
- `packages/shared/src/usageLimits.ts` and `apps/web/src/components/usage/UsageLimits.tsx`: elapsed-time markers and five-point pace tolerance.
- `apps/web/src/components/settings/AddUsageLimitSourceDialog.tsx`: configurable source UI.

T3's Codex `account/rateLimits/read`, Claude `get_usage`, and streamed `rate_limit_event` depend on those runtimes. PiStation adapts the shared model to CLIProxyAPI and a Pi-owned extension feed. DPAPI protection, bounded host reads, remote authorization, stale-pacing suppression and the native panel are PiStation implementation choices; this document does not claim T3 encrypts its source keys.

## Verification

- Final solution build: zero warnings/errors, `TestResults/quota-build-final.log`.
- Final affected gate: **33 passed, zero failures/skips** (ClientRuntime 4, CommandSystem 5, Host 22, PiRpc 1, Protocol 1), `TestResults/code-Debug-20260910-163450-83e060ca/summary.json`. Includes all three authenticated transports, read-only authorization, DPAPI restart/removal, stale revisions, origin validation, bounded bodies and deadlines, last-good recovery, in-flight removal, coalesced refreshes, redirect/cookie isolation, native view-model state, and real Pi 0.85 publication/reload with an isolated offline provider.
- Pi publisher Node checks: **3 passed**, `TestResults/quota-publisher-final.log`.
- Native acceptance: **six grouped checks passed**, `TestResults/usage-limits-native/5e0eacfe1e6b4042a5fa9e3af15c2a52/result.json`. Covers unsupported state; native settings, authenticated quota reads, encrypted keys, bars/reset/pace; stale fallback; relaunch; removal/relaunch; and the shipped Pi publisher feeding native bars. Captures `limits-overview.png`, `limits-stale.png`, and `limits-pi-extension.png` retain visual evidence.
- Static visual contract: 24 states, four responsive layouts, three text scales, `TestResults/quota-visual-contract-final.log`.
- Desktop and embedded SSH-host bundles contain a byte-for-byte match of the tested quota publisher: `TestResults/quota-packaging.log`.
- First affected run retained at `TestResults/code-Debug-20260910-162718-609bdb14/summary.json`: 31 passed / one failed. Discovery selected installed Pi 0.84.4: publishing worked, but the added reload check requires 0.85+. Selecting the existing isolated Pi 0.85 installation fixed the test environment; the test passed unchanged (`TestResults/code-Debug-20260910-163207-d42fc2f2/summary.json`) and passed again in the final affected run.
- First native evidence retained at `TestResults/usage-limits-native/07d1c4e1f93e48149cef28e3fcf4fac3/result.json`. Hub removal succeeded, but background polling replaced its confirmation before acceptance could read it. Save/remove feedback now has a separate persistent status; the final native run verifies the correction.
- Full first-attempt regression gate: **1,145 passed / zero failures / 28 optional skips**, `TestResults/code-Debug-20260910-163613-cccc8eec/summary.json` (ClientRuntime 485, CommandSystem 94, Host 428, PiRpc 79, Protocol 59 passed). Optional skips: two image-conversion checks, one installed-provider writer check, and 25 live/offline Pi opt-in checks. The new real-Pi quota test was explicitly enabled and passed separately in the final affected gate. The previously failing shell-startup test passed unchanged in this full run; that does not establish a historical root-cause fix.

Live provider sign-in remains skipped as requested. All hub credentials and accounts above are isolated fixtures. BUG-09 and the previously recorded two-second Pi shell startup timeout remain separate investigations.
