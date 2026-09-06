# T3-inspired implementation status — September 5, 2026

This follows the [repository comparison](PISTATION-T3-REVIEW-2026-09-05.md). It describes this implementation pass, separate from the earlier audit and the repository's preexisting changes.

**UI verification update:** Microsoft `winapp ui` works on the active desktop. Three Luna agents implemented and reviewed the Files layout, theme/contrast, and immediate inbox refresh fixes; integration review and native testing found additional rename and test-runner issues. See the [native UI verification report](UI-VERIFICATION-2026-09-05.md) for evidence and the precise coverage limits.

| Area | Implemented |
| --- | --- |
| Draft ownership | Context chips belong to a thread draft, save with its revision, reload after restart, and preserve source metadata. Sending clears the accepted snapshot while preserving newer edits. |
| Stashes | Complete draft snapshots, durable attachment references, transactional restore into an empty draft, and attachment cleanup that respects references. |
| Ordering and inbox | Shared pin/settlement ordering; completed responses no longer settle tasks; separate shelves, snooze expiry, expandable project groups with remembered expansion, runtime/attention indicators. |
| Accounting | Pi-reported cost flows through normal and compaction usage. Missing values stay unknown instead of becoming zero. |
| Hosting safety | Durable operation IDs and request hashes, replay without repeat dispatch, restart recovery to an explicit uncertain state, and operation history. Follow-up refresh failures do not erase a confirmed write outcome. |
| Provider capabilities | Corrected GitLab list filters and Azure draft arguments, authentication checks, supported-action gating, and removal of guessed Bitbucket commands. Publishing is enabled for GitHub's create/remote/push flow. |
| Diff and review | Native virtualized line rows, addition/deletion colors, old/new gutters, syntax coloring, unified/split layouts, hunk navigation, file collapse, and line-aware review context. A Pull requests entry point sits beside Changes. |
| Transcript | Tables, task lists, autolinks, remote image rendering, workspace file/line navigation, selected-text quote/cite actions, source chips, follow-output and jump-to-latest, per-thread scroll positions. |
| Runtime setup | The host starts without Pi. Settings can validate, save, and reconnect to an executable without restarting the app. |
| Responsiveness and lifetime | Selected-thread render updates are coalesced to approximately 30 per second. Idle Pi processes stop and restart on the next prompt; active turns and pending user interactions are protected. Completed timeline rows retain the existing equality-based reuse behavior. |
| Release tooling | Unsigned self-contained Windows x64 MSIX generation, configurable version/publisher/feed generation, Windows App Installer update checks, terminal source/bundle checks in CI, a Release CI gate, and blank screenshot rejection. |

Protocol version is **22**. SQLite schema changes are additive. Existing draft/usage/stash data remains readable; legacy usage without evidence of a reported cost is conservatively unknown. Release trimming is disabled because runtime WinUI bindings and syntax-language discovery require their metadata.

## Remaining acceptance work

- Extend manual visual acceptance to additional physical DPI/monitor combinations, Windows High Contrast, and detailed populated-content review such as tables, text selection, and context-source navigation. All 11 native scenario runners passed; the [UI verification report](UI-VERIFICATION-2026-09-05.md) records the exact screenshots, responsive layouts, and text-profile coverage.
- Run authenticated end-to-end hosting checks against disposable repositories. No remote PRs, comments, merges, or repositories were created during this implementation.
- Run a real-Pi/provider smoke test and record a long-session performance baseline on the supported machine. Coalescing and idle shutdown are implemented; startup, input latency, memory, and scaling budgets still need measurement.
- Further inbox polish: persistent unread markers, linked-PR state refresh, and any agreed inactivity-based settlement policy. Settlement is currently explicit.
- Further provider expansion: GitLab/Azure publishing, a verified Bitbucket adapter, and richer generated PR descriptions.
- [Production publisher, signing, update feed, and second-machine install/upgrade](RELEASE-TODO.md) are explicitly deferred by the owner.

The app has an implemented Windows/Pi coding workflow; production release and full visual/provider acceptance are not yet certified.

## Validation from this implementation

| Check | Result |
| --- | --- |
| Debug solution build | Passed, zero warnings/errors |
| Release build and publish | Passed, zero warnings/errors; full packaging script completed |
| Debug non–Real Pi tests | 199 passed: Host 85, ClientRuntime 43, PiRpc 42, Protocol 25, CommandSystem 4 |
| Release non–Real Pi tests | 199 passed across the same five projects |
| Terminal typecheck, tests, build | Passed; 5 tests, no tracked bundle drift |
| Static visual contract | Passed: 24 declared states, 4 layouts, 3 text profiles |
| Screenshot validity | Rejected the retained black capture; accepted the retained populated capture |
| PowerShell script parsing and git whitespace check | Passed |
| MSIX packaging | Unsigned self-contained package generated; executable, .NET runtime, WinUI runtime, manifest, and PRI resources present; SHA-256 verified |
| Installed app | Debug package launched and connected to isolated test hosts; native `winapp ui` interaction and readable captures work |
| Native UI scenario suite | All 11 runners passed, including recovery/hardening, lifecycle, keyboard/input, and compatibility; see the linked UI report for evidence |

Latest TRX results are retained under `TestResults/visual-polish-debug/` and `TestResults/visual-polish-release/`. Earlier implementation evidence remains under `TestResults/t3-implementation-*`. No existing user workspace was used for prompt execution or remote hosting writes.

The earlier Computer Use pipe/capture failure is historical. The native `winapp ui` runner is the selected test path. Blank captures are rejected, with a focused screen-capture retry when the normal window capture is blank.

Latest package: [PiStationDesktop_1.0.0.0_x64.msix](../artifacts/release/1.0.0.0-71361b2231134b88aac6984623e65522/PiStationDesktop_1.0.0.0_x64.msix), 128,140,048 bytes. SHA-256: `9EF0DA2D1417BD7755C39CCE803B6291E1E86FA557B499F8A8AA4CC6C74B0EEC`. The executable, .NET runtime, WinUI runtime, manifest, and PRI resources are present. It remains unsigned pending the deferred distribution configuration.
