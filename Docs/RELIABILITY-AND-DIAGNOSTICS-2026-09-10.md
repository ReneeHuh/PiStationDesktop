# Reliability and diagnostics — protocol 53

Implementation against the `cea6c8f` working tree. This batch implements REL-03, REL-04, DIAG-02 and DIAG-03. BUG-09 remains an investigation: a passing slow-start test does not establish the cause of the historical heartbeat failure.

## T3 review

Reviewed the local T3 checkout at `0a590fa01`, concentrating on the behavior needed by the Windows/Pi application:

| T3 source | Behavior carried into PiStation |
| --- | --- |
| `packages/client-runtime/src/state/threads.ts`, `apps/web/src/components/chat/MessagesTimeline.tsx` | A recent transcript window; explicit earlier pages; reject replaced-history replies; wait for the live sequence before merging; preserve newer live metadata and avoid duplicate items. |
| `packages/contracts/src/background.ts`, `packages/shared/src/backgroundActivitySettings.ts`, `apps/server/src/background/BackgroundPolicy.ts`, `HostPowerMonitor.ts` | Expiring client activity reports, distinct visible/focused state, unknown power signals, background profiles, host constraints and renewed work on resume. |
| `apps/server/src/diagnostics/ProcessDiagnostics.ts`, `packages/contracts/src/resourceTelemetry.ts`, `apps/web/src/components/settings/ResourceTelemetryDiagnostics.tsx` | Owned process relationships, CPU/memory history, protected host process, and PID plus start-time validation before an action. |
| `packages/shared/src/observability.ts`, `apps/server/src/observability/Layers/Observability.ts`, `RpcInstrumentation.ts` | Bounded collection, structural operation metadata, duration/outcome reporting, and independently configured OTLP trace/metric destinations. |

The implementation uses native WinUI controls, .NET activities/metrics, Windows process identities and the existing authenticated transports. It does not add T3's Node runtime or provider adapters.

## Implemented behavior

### REL-03: long conversations

The hub initially sends the latest ten turns, capped at 400 timeline items. Pending interactions and mutable output extend that window when needed so subsequent live events can still update their targets. Earlier pages contain up to twenty turns or 400 items. Cursor requests carry the thread and projection epoch; a replaced branch/session rejects an old request.

The client waits until its live stream reaches the page's sequence, then prepends distinct item IDs while retaining newer live state. A newer snapshot in the same epoch retains already loaded immutable history when its window overlaps. Cached pending items that may have completed during missed events are discarded with the fresh window. A changed epoch or non-overlapping window starts a fresh window. The native button preserves the existing reading anchor and draft; citation navigation can load earlier pages until its message is found. Relaunch recovers a recent window and allows earlier history to be loaded again. Stale cursor and conflicting settings errors give the client explicit refresh instructions.

Hydration now constructs one private list, eliminating a whole-list copy for every message and turn boundary. Duplicate-entry compatibility remains. A 4,000-message allocation regression test covers this path and thinking/message order.

**Limit:** the host and Pi still hydrate the active branch. This change bounds initial transcript transfer/rendering and individual pages; it does not claim that host memory is independent of session size. Loaded pages remain in the active client's projection. A still-mutable early item can deliberately exceed the normal recent-window cap.

### REL-04: background policy

Clients report visibility, focus, diagnostics demand, battery and battery-saver state every ten seconds and on activation changes. Reports expire after 45 seconds and are removed on disconnect. Native host power is sampled every five seconds. Unknown or stale power remains unknown instead of becoming a synthetic locked/battery condition.

Balanced requires a focused visible client. Performance also permits visible unfocused clients. Battery saver additionally pauses on battery. Native settings expose the lock, host/client battery-saver and battery overrides. A locked client reports itself hidden. Each client checks its own eligibility, so another active client cannot enable refreshes in a hidden window.

The effective policy gates opportunistic terminal process inspection, inbox timer refreshes, remote workspace refreshes and periodic process sampling. Diagnostics demand is required for periodic resource sampling. Resume refreshes the workspace and project/thread lists. Active Pi turns, streaming output, browser heartbeats, cancellation and explicit user refreshes continue independently.

Windows signals use [`GetSystemPowerStatus`](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getsystempowerstatus) and the public [session flags](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/ns-wtsapi32-wtsinfoex_level1_w) in `WTSQuerySessionInformation(WTSSessionInfoEx)`. The reader collects no user/domain names. Lock/battery/saver combinations and lease expiry are tested with controlled signals; physical suspend/resume and battery transitions remain delivery qualification.

### DIAG-02: owned processes

Diagnostics displays the application host, registered Pi/terminal roots and their Windows descendants, with depth/parent identity, first-sample unknown CPU, normalized CPU, working-set and private memory. Collection is bounded to 256 processes, depth 32 and 120 resource samples. Combined CPU/memory graphs use sample order; the text explicitly describes omitted background samples and unknown initial CPU.

Stopping a process opens a native review dialog, defaults to Cancel, and captures the selected environment. The server takes a fresh ownership snapshot, matches PID and process start time, pins the kernel process handle and rechecks identity before stopping only that process. It refuses the host, unrelated processes and stale identities. Tests stop only a purpose-created child and verify another purpose-created process survives. Periodic native enumeration does not hold the policy/operation telemetry lock.

### DIAG-03: observability

Settings are revision checked and atomically persisted per host in `runtime-health.json`. Metrics are enabled locally by default; traces and external export are disabled by default. Tracing supports a sample rate from zero to one. Collection retains 512 traces, 256 operation metric names and a 256-span delivery queue. The native page displays process resources, effective policy, operation totals/failures/mean/max duration and the latest fifty traces. Clear removes collected telemetry without changing saved settings.

The hub filter instruments RPC execution on local, HTTPS and SSH listeners. Long-lived streaming methods record setup invocation, not the lifetime of every stream item. `.NET ActivitySource` and `Meter` use `PiStation.Host`; local operation metadata includes names, timestamps, durations, outcomes and trace/span IDs. It excludes RPC arguments, prompts, tool output and exception bodies.

Separate optional OTLP/HTTP JSON endpoints export spans to `/v1/traces` and current metric gauges to `/v1/metrics` when given a root URL. Explicit paths are respected. The four gauges report collected calls, failures/cancellations, total duration and maximum duration by operation. Gauges deliberately allow reset when the user clears collection. HTTP is allowed only on loopback; other endpoints require HTTPS. URL credentials/query/fragment are rejected. This supports unauthenticated local collectors or an appropriately configured HTTPS collector/proxy; custom authorization headers are not exposed.

Delivery disables redirects/cookies, uses a three-second timeout, limits replies to 16 KiB, reports HTTP/invalid/partial responses structurally, and cancels old pending export on settings changes. Trace batches contain at most 64 spans and failed batches are dropped with status, without retries. Metric snapshots refresh on subsequent collection cycles. Diagnostics JSON includes bounded local telemetry and redacts configured collector addresses. External sending requires an explicitly saved endpoint.

## Verification

- First focused gate: **33 passed, zero failed**, `TestResults/code-Debug-20260910-052456-c3635b7c/summary.json`.
- Expanded focused gate: **37 passed, zero failed**, `TestResults/code-Debug-20260910-053035-beeca774/summary.json`. This includes actual 45-turn imports/paged reconstruction over local, HTTPS and SSH, separate trace/metric fixture collectors, process ownership/action checks and read-only authorization.
- First native journey: **four grouped checks passed**, `TestResults/reliability-diagnostics-native/f391a910365447a5b336b94b93a62c2e/result.json`: 40-turn import/paging without draft loss; owned-process view and stop-review cancellation; settings save; relaunch with transcript/draft/settings recovery. The original import remained byte-identical. Its valid screenshot revealed the narrow settings layout; the subsequent change widens the panel and adapts navigation to small windows.
- Static visual contract passed: 24 states, four layouts, three text scales. This is structural verification, not physical accessibility acceptance.
- Final native layout journey: **four grouped checks passed**, `TestResults/reliability-diagnostics-native/50e64e1f41a54598b7388a3518b1ac85/result.json`. The validated screenshot was inspected; the wider panel gives process rows and resource graphs room to render without the earlier excessive wrapping.
- The first full-gate build overlapped the final shared eligibility-helper edit and compiled mismatched intermediate source, failing before any tests ran. The complete failure remains in `TestResults/code-Debug-20260910-053629-4040367c/summary.json` and `TestResults/reliability-full-gate.log`. The subsequent stable build passed with zero warnings/errors. Development compile failures are retained in the `runtime-health-build*` and `reliability-build-3` logs.
- Full managed gate: **1,088 passed, zero failed, 27 optional skips**, `TestResults/code-Debug-20260910-054012-4acab209/summary.json`. ClientRuntime 478; CommandSystem 86 / two skips; Host 390 / one skip; PiRpc 75 / 24 skips; Protocol 59. This run overlapped the passing native journey.
- Final focused verification after the stale-pending-history guard, explicit refresh errors, collector-address redaction assertions and stalled-response deadlines: **50 passed, zero failed, zero skips** (24 ClientRuntime, 26 Host), `TestResults/code-Debug-20260910-055357-b3378c3a/summary.json`. The collector tests hold a response body open after sending headers; both trace and metric requests end within the explicit deadline.
- The final solution/WinUI build in that follow-up gate passed with **zero warnings and zero errors**. No source changes followed it; only evidence/tracker documentation was finalized. `git diff --check` passed.

## BUG-09 evidence

The slow-start regression now records both host operation timings and completed browser poll count, maximum reply time and maximum gap between polls. On failure, the browser diagnostic also includes queued thread-pool work, thread count, available workers/I/O and cumulative GC pause during the session. No heartbeat, request, startup or test deadline was increased.

In the first focused run, all three transports retained browser control and cancellation while composer startup took approximately 6.8–8.9 seconds. The maximum completed browser replies were approximately 7.6–82.4 ms, with maximum gaps of 265–286 ms. These measurements demonstrate the tested independent transport behavior; they do not establish the cause of the older local timeout. The full-gate cases also passed with startup taking approximately 6.9–8.3 seconds, maximum replies of 10.3–17.7 ms, and maximum gaps of 264–274 ms. Keep the original failure and shared-load evidence in the [earlier investigation](REGRESSION-GATE-2026-09-09.md) and [browser milestone](BROWSER-EVALUATION-AND-SNAPSHOTS-2026-09-09.md).

Live provider authentication remains skipped by user request. No gist was published and no user-owned process was terminated during verification.
