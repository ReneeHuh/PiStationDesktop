# Tracker audit at cea6c8f — 2026-09-09

**Reconciliation follow-up:** the user requested that completed implementation be marked Done. REM-19 is now Done under the tracker definition, with live/native/two-PC acceptance retained in DEL-09. Current core counts are 180 Done / 23 Partial / 16 Not started / 3 Review needed (42 open). The audit inventory below preserves the pre-reconciliation snapshot; use [tracking.md](../tracking.md) and [the T3/Pi next-work comparison](../Docs/NEXT-IMPLEMENTATION-STEPS-2026-09-09.md) for current planning.

**Verdict: [tracking.md](../tracking.md) is substantially current with the checked code at `cea6c8f182035fbe7d006d1be874d7631173aaa5`. It accurately retains substantial implementation and acceptance work.** The latest four implementation commits each updated the tracker, and the current protocol is 50 in both its header and [ProtocolVersion.cs](../src/PiStation.Protocol/ProtocolVersion.cs). I found no checked open row whose entire remaining boundary should now be marked Done.

This audit read the tracker, recent Git history, focused implementation paths, test entry points, and retained JSON/TRX evidence. It did not run a new build, application test suite, native journey, or exhaustive upstream parity review. The local T3 and Pi repository HEADs match the tracker baselines. Tracked files were unchanged at audit start; `outputs/` already contained an untracked earlier audit. This report preserves that file and does not change product code or tracker statuses.

## Current inventory

| View | Done | Partial | Not started | Review needed | Total |
| --- | ---: | ---: | ---: | ---: | ---: |
| Core, sections 1–14 | 179 | 24 | 16 | 3 | 222 |
| Delivery, section 15 | 1 | 9 | 0 | 0 | 10 |
| Deferred, section 19 | 0 | 0 | 6 | 0 | 6 |

There are **43 open core rows**, of which 42 are outside the remote section. PI-09 and REM-19 primarily retain acceptance work, overlapping DEL-06 and DEL-09. The other 41 rows describe implementation or compatibility investigations. The nine open delivery rows and linked OPT-02/03 investigations should not be added as independent missing features without accounting for these overlaps. Counts describe rows, not effort or parity percentages.

All **267 tracker IDs are unique**, and all **223 local Markdown link occurrences resolve** in this checkout. Link checking established file existence, not anchor validity or the accuracy of every linked document.

## Currentness findings and cleanup recommendations

1. **The earlier audit is stale for backlog planning.** [tracker-audit-20260909.md](tracker-audit-20260909.md) describes `c3e7401` and counts 54 open core rows. Its follow-up note covers protocol 47, but its main inventory predates later browser work. Eleven formerly open rows are now Done: PROJ-07; TERM-05/06; WEB-02/06/13/14/15/16/18/19. The master tracker already reflects these changes. Use the present 43-row inventory; retain the earlier report as history.

2. **Make the latest reconciliation baseline easier to find.** The scope table still leads with inventory baseline `36dc464...` and remote work over `b10e550`. Those are explicitly historical and are not false, but they are much older than the latest feature rows. Add the reviewed HEAD and review scope near the top, while retaining the original inventory baseline. Historical protocol 42–49 statements in verification paragraphs should remain labeled as historical; the current connection requirement is protocol 50.

3. **Clarify REM-19's status boundary.** Its remaining-work cell describes implemented discovery, direct connections, Serve lifecycle, probing and process tests, followed by live/native qualification. Most comparable rows use Done for connected implementation and retain qualification in DEL. Either state the specific unimplemented REM-19 behavior, or explicitly explain why live qualification is required before this row becomes Done. Do not promote it solely on this source audit; live Windows Tailscale acceptance remains outstanding.

4. **Make native regression-gate maintenance explicit under DEL-10.** [Invoke-PullRequestTests.ps1](../Invoke-PullRequestTests.ps1) does not invoke the standalone Pi shell, external prompt editor, session navigation, session label, or browser automation slices. Their existence and milestone passes do not mean the PR entry point runs them. Select appropriate automated/attended lanes. Separately, native scripts still target `PromptInput` directly and no UI test `.ps1` references `ExpandComposerButton`; DEL-03 and [the shell/composer checklist](../Docs/SHELL-COMPOSER-ACCEPTANCE.md) already acknowledge the required resting-composer adaptation. This is a source-level coverage finding, not a newly reproduced native failure.

5. **Resolve one scope ambiguity in WEB-15.** Its Done boundary explicitly omits T3's full named-device catalog/mobile emulation as “outside this slice.” That describes the implemented limit, but does not say whether the remainder is excluded, deferred, or still awaiting selection. Record that decision under the tracker's existing scope rules; this audit does not assume those capabilities were selected or add them to the backlog count.

## Every open core item

P = Partial; N = Not started; R = Review needed.

| Area | IDs/status | Remaining work |
| --- | --- | --- |
| Pi setup/preferences | PI-09, PI-20, PI-21 — P | Fresh provider authentication/setup acceptance; discoverable transport/cache-retention controls; startup telemetry/offline/version-check preferences and effective-state verification. |
| Resources/extensions | EXT-07 — P; EXT-11, EXT-15 — R | Exact load/error attribution, including extensions with no visible registrations; establish a supported in-process reload route; define native support for arbitrary Pi terminal UI components. |
| Sessions | SES-09, SES-13 — P; SES-11, SES-12 — N | Rich HTML/media export; fold/unfold, branch-point jumps, filtered-tree layout, arbitrary selected-entry copying and label-time display; explicit gist sharing; ephemeral-session semantics. |
| Tools/children | TOOL-08, AGENT-08 — P | Native sequential/parallel tool-batch controls and runtime API; independent control of compatible external-extension children. Bundled child control and Windows PowerShell are already implemented. |
| Hosting | HOST-01, HOST-06, HOST-08, HOST-09 — N; HOST-10, HOST-20, HOST-21, HOST-22 — P | Account/repository discovery before clone; Bitbucket adapter; GitLab/Azure publication; cross-repository PR browsing; provider-specific detailed reviews and advanced actions. |
| Browser | WEB-07, WEB-17 — N; WEB-09 — P | Installed-browser session import; agent-owned recording and artifact delivery; 30/60 FPS recording options and measured achievable capture. |
| Appearance | LOOK-02, LOOK-03, LOOK-04 — P; LOOK-05, LOOK-06, LOOK-07, LOOK-08, LOOK-10 — N; LOOK-09 — R | Independent fonts, contrast/opacity, wrap/diff/whitespace preferences; palette editing/inspection, T3 and VS Code theme interchange, animation duration; native handling of Pi TUI theme resources. |
| Usage | USE-02, USE-03, USE-04, USE-07 — P; USE-05, USE-06 — N | Dashboard/date filters, model/provider breakdowns/cache savings, historical rescanning/pricing refresh, child-usage aggregation; provider quota/reset/pace adapters and CLIProxyAPI pooled-account usage. |
| Diagnostics/reliability | DIAG-02, DIAG-03, REL-03 — P; REL-04 — N | Process trees/history/actions, tracing/metrics/export controls, paged transcript recovery/load-earlier UI, and Windows focus/lock/battery/background policy. |
| Remote acceptance | REM-19 — P | Live Windows Tailscale/Serve and native qualification, overlapping DEL-09. |

Source checks support these boundaries:

- [HostingCapabilities.cs](../src/PiStation.Protocol/Models/HostingCapabilities.cs) restricts publication and detailed native review to GitHub. [SourceControlHostingService.cs](../src/PiStation.Host/SourceControl/SourceControlHostingService.cs) rejects Bitbucket operations; [PR refresh/paging](../src/PiStation.App/ViewModels/ShellViewModel.Pagination.cs) uses the selected project's workspace.
- [pistation-browser.ts](../src/PiStation.App/PiExtensions/pistation-browser.ts) now exposes open, resize, appearance, evaluation and rich snapshot operations. It still has no agent recording action. [The capture surface](../src/PiStation.App/Views/Controls/PreviewWebViewSurface.xaml.cs) clamps recording to 1–12 FPS. Browser preferences, profile operations and terminal ownership are connected through [layout preferences](../src/PiStation.App/ViewModels/ShellLayoutViewModel.Browser.cs), [profile UI](../src/PiStation.App/Views/Controls/BrowserSettingsPanel.xaml.cs), and [listener scanning](../src/PiStation.Host/Preview/PreviewPortScanner.cs).
- [TerminalSessionRegistry.cs](../src/PiStation.Host/Terminals/TerminalSessionRegistry.cs) restores disk history and polls native process activity. [Project customization](../src/PiStation.App/ViewModels/ShellViewModel.ProjectCustomization.cs) calls [the group icon operation](../src/PiStation.Host/EnvironmentService.ProjectIcons.cs). These substantiate the three completed rows that the earlier audit listed as missing.
- [PiSessionDocument.cs](../src/PiStation.PiRpc/Sessions/PiSessionDocument.cs) exports text-oriented active ancestry and image placeholders. [PiSessionsViewModel.cs](../src/PiStation.App/ViewModels/PiSessionsViewModel.cs) exposes filtering/bookmarks/paging but retains depth-based indentation and lacks the remaining presentation controls. [PiRuntimeSettingsStore.cs](../src/PiStation.Host/PiRuntimeSettingsStore.cs) prohibits `--no-session` in ordinary runtime arguments.
- [Resource discovery](../src/PiStation.App/PiExtensions/pistation-resources.ts) derives extension confirmation from registered tools/commands, while [its native view model](../src/PiStation.App/ViewModels/PiResourcesViewModel.cs) correctly distinguishes unconfirmed resources. Dedicated transport/cache/telemetry/tool-batch preferences were not found in the checked native settings and runtime contracts.
- [Host diagnostics](../src/PiStation.Host/Diagnostics/HostDiagnosticsService.cs) takes a current process snapshot and requests a fixed 30-day usage summary; [SettingsViewModel.cs](../src/PiStation.App/ViewModels/SettingsViewModel.cs) presents summary strings. Provider/model aggregation already exists in [HostDatabase.cs](../src/PiStation.Host/Persistence/HostDatabase.cs), so USE-03 is a presentation/completion gap rather than wholly absent data. [Layout settings](../src/PiStation.App/ViewModels/ShellLayoutViewModel.cs) retain theme selection and terminal font controls, without the broader appearance inventory.

## Defects and verification still outstanding

**BUG-09 remains the main unresolved regression-gate investigation.** The retained [full Debug report](../TestResults/code-Debug-20260909-224212-bdb21bf1/summary.json) confirms **1,033 passed, 1 failed, 21 skipped**, with overall success false. Its TRX identifies `BrowserAutomationTransportTests.SlowThreadStartupDoesNotStarveBrowserHeartbeatsOrCancellation(transport: "local")`, failing at the active-connection assertion after the six-second simulated slow startup with “Browser connection ended: A task was canceled.” The [later focused report](../TestResults/code-Debug-20260909-230031-6f27ecd1/summary.json) passes 79 checks. It does not replace the failed full run or establish the root cause.

The latest milestone also retains a successful [native browser slice](../TestResults/browser-native-20260909-190327-10ef7304bf98417fbf32a5580b2d6e6b/summary.json) and [isolated Pi 0.85.0 browser bridge test](../TestResults/code-Debug-20260909-230527-c23a61da/summary.json). The full attempt predates the final in-page result-type guard, so a complete green gate on the final code is still needed. These are existing reports inspected by this audit, not fresh runs. They are ignored local artifacts and may be absent from another checkout.

**BUG-04** still needs its historical native-contention scenario investigated; earlier checkpoint passes do not close that scenario. **BUG-01/02/03/05/06/07/08** have fixes with particular native, clean-machine or live acceptance pending. **BUG-10** is recorded as fixed with regression verification. The tracker correctly distinguishes these states.

| Delivery ID | Remaining work |
| --- | --- |
| DEL-02 | Production publisher/signing/feed and local installation/upgrade migration; deferred until after core, required before public release. |
| DEL-03 | Complete keyboard/focus/screen-reader/High Contrast journeys; shell/composer controls and adaptation of existing native scripts. |
| DEL-04 | Inspect populated rendered states across scaling, mixed monitors, narrow layouts and rich/media content. |
| DEL-05 | Measured long-history, large-workspace and multi-terminal latency/CPU/memory. |
| DEL-06 | Clean-machine Pi/codec setup and fresh authenticated provider journeys. |
| DEL-07 | Authenticated supported-provider hosting list/clone/publish/review/action acceptance. |
| DEL-08 | Remaining browser keyboard/scroll, recording, defaults/profile clearing/removal, Incognito, link modifiers/fallback and cross-environment storage acceptance. The focused browser slice is already recorded as passed. |
| DEL-09 | Two physical Windows PCs, actual OpenSSH and live Tailscale; native pickers/rendering, pairing/revocation, restart/sleep/network changes, files/terminal/preview and manual-update reconnect. The tracker records that a second PC was unavailable; this audit did not reassess hardware availability. |
| DEL-10 | Clean full gate on final code, BUG-09 investigation, BUG-04 stress evidence, broader opt-in compatibility and Release/native verification; maintain native gate coverage. |

## Deferred work and decisions

The six selected deferred rows remain **SCOPE-02** WSL connections, **SCOPE-04** named Pi runtime/account instances, **SCOPE-06** web/iOS/Android clients, **SCOPE-07** mobile offline/share/voice/push features, **SCOPE-09** served-client themes, and **SCOPE-11** the PiStation task CLI. Carry **DEL-02** production release work forward as well.

**OPT-02/03 are required compatibility investigations** already linked to EXT-15/AGENT-08. The other nine OPT proposals remain unselected. Explicit exclusions—including remote installation/update automation, hosted relay/accounts, extra agent harnesses, and Linux/macOS application/host targets—are not missing implementation commitments.

Recommended next steps: investigate BUG-09 and obtain a clean full gate on the final code; maintain/run the applicable native lanes; work through the selected core backlog with focused acceptance. Record the accepted core commit before beginning the deferred phase. Production signing remains scheduled after core and before public release.
