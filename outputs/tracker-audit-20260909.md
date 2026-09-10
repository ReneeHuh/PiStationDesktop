# Tracker audit — 2026-09-09

**Superseded backlog:** this report preserves the earlier `c3e7401` audit. The master [tracker](../tracking.md) now records the eleven completed terminal/icon/browser rows as Done and reconciles REM-19 as implementation Done with live acceptance in DEL-09. Current core inventory: 180 Done / 23 Partial / 16 Not started / 3 Review needed (42 open). Use the tracker's current completion section and [T3/Pi next-work comparison](../Docs/NEXT-IMPLEMENTATION-STEPS-2026-09-09.md) for planning; the counts and missing-feature lists below are historical.

**Implementation follow-up:** PROJ-07, TERM-05, and TERM-06 are now implemented in the working tree and marked Done with verification in [tracking.md](../tracking.md). Protocol is now 47. The final Debug build and default serial gate passed (960 tests, 21 optional skips); native acceptance remains pending. The audit and inventory below describe the earlier `c3e7401` baseline.

**Verdict: `tracking.md` is substantially current with the code at `c3e7401791b5d76132c7b6d9e036ae86d4d99f65`, but the project still has substantial implementation and acceptance work.** The tracker was updated in that same commit. Recent completed features are represented, and the checked unfinished areas still match their source limitations. This was a tracker, Git-history, focused source, and retained-test-evidence audit; no new build, application tests, native UI journey, or upstream parity certification was performed.

The working tree had no tracked modifications at audit start. An existing untracked `outputs/` directory was present. This report does not change feature statuses or product code.

## Inventory

| Inventory view | Done | Partial | Not started | Review needed | Total |
| --- | ---: | ---: | ---: | ---: | ---: |
| Core inventory, sections 1–14 | 168 | 31 | 20 | 3 | 222 |
| Delivery, section 15 | 1 | 9 | 0 | 0 | 10 |

There are **54 open core rows**, including **53 outside the remote section** and **one remote row, REM-19**. Delivery has nine open rows; DEL-02 is explicitly deferred until after the working-core milestone. The three core investigation rows are EXT-11, EXT-15, and LOOK-09.

Counts describe tracker rows, not effort, parity percentages, or independent work packages. PI-09 overlaps DEL-06; REM-19 overlaps DEL-09; required OPT-02/03 investigations overlap EXT-15/AGENT-08. Those dependencies must not be counted as extra missing features. Across all sections, all **267 IDs are unique** and all **197 local Markdown link occurrences resolve** in this checkout, including local reference repositories.

## Is the tracker current?

The following recent implementations are already represented as Done, with their remaining native/provider checks separated into delivery rows:

- UI-16/UI-17: quit policies and proactive panels.
- CHAT-03/CHAT-17/CHAT-18: HEIF conversion, resting-composer preferences, and slash-menu skill visibility.
- CHAT-16: external prompt editing and recovery.
- PI-18/PI-19: direct Pi shell execution and cancellation.
- SES-06/SES-07: branch navigation, bookmarks, and session filtering/search.
- TOOL-05/TOOL-07: PowerShell, tool policy, persistence, and effective inventory.
- REM-15/16/17: remote runtime settings, diagnostics delivery, host path selection, and icon transport.
- BUG-05/07/08/10: locator correction, honest hosting copy, remote-update scope cleanup, and corrected RPC test expectations.

Protocol **46** matches [ProtocolVersion.cs](../src/PiStation.Protocol/ProtocolVersion.cs). Older protocol numbers in historical verification paragraphs are explicitly qualified elsewhere in the tracker; they are not evidence of a current wire mismatch.

Three maintenance improvements would make the tracker clearer:

1. **REM-19 mixes implementation and qualification.** Its remaining-work text describes connected discovery/Serve controls and leaves live/native checks to DEL-09, yet its status stays Partial. Under the tracker's definition of Done, either name the remaining implementation gap or mark the implementation Done and retain pending qualification in DEL-09. This is a status-consistency issue, not evidence that Tailscale works on two physical PCs. See [tracking.md](../tracking.md#14-remote-access-windows-hosts-only-active-separate-workstream).
2. **Clarify the baseline summary.** The scope table still names the original `36dc464...` inventory baseline and emphasizes remote changes over `b10e550`. The evidence log covers the later main merge, shell/composer work, tool selection, and regression gate through the current commit. Preserve the historical baseline, but add the latest reconciliation commit/date and its review scope near the top. Do not relabel the entire product freshly verified.
3. **Make native gate maintenance explicit in DEL-10.** [Invoke-PullRequestTests.ps1](../Invoke-PullRequestTests.ps1) does not invoke the standalone Pi shell, external editor, session navigation, or session label journeys. Decide which belong in the automated gate and retain a named attended lane for checks requiring a usable desktop. Separately, the existing composer adaptation requirement is real: [Invoke-DraftSlice.ps1](../tests/PiStation.UiTests/Invoke-DraftSlice.ps1) and other journeys target `PromptInput` directly, while no UI test script references `ExpandComposerButton`. The [acceptance checklist](../Docs/SHELL-COMPOSER-ACCEPTANCE.md) and DEL-03 already acknowledge this work. These are source findings, not a newly reproduced UI failure.

No checked open row was shown to have its complete remaining boundary implemented and verified. No broad promotion of Partial/Not started rows is justified by this audit.

## Remaining core work

Every currently open core ID is included below. “P / N / R” means Partial / Not started / Review needed.

| Area | IDs and current status | Remaining behavior |
| --- | --- | --- |
| Pi setup/preferences | PI-09, PI-20, PI-21 — P | Fresh authentication/provider acceptance; discoverable transport/cache-retention controls; telemetry/offline/version-check preferences and effective-state verification. PI-09 shares acceptance work with DEL-06. |
| Resources/extensions | EXT-07 — P; EXT-11, EXT-15 — R | Exact load/error attribution, including extensions registering no feature; establish an in-process reload route; define native compatibility for arbitrary Pi TUI components. |
| Sessions | SES-09, SES-13 — P; SES-11, SES-12 — N | Rich HTML/media export; fold/unfold, branch-point jumps, filtered-tree presentation, arbitrary entry copying, label-time display; explicit gist sharing; ephemeral-session semantics. |
| Tool execution | TOOL-08 — P | Native sequential/parallel tool-batch controls and the applicable runtime API. Tool allowlists and PowerShell are already implemented. |
| External subagents | AGENT-08 — P | Independent control of compatible third-party extension children; establish handles/adapters beyond parent Stop. Bundled child control already exists. |
| Project grouping | PROJ-07 — P | Shared icon editing/storage across grouped checkouts. |
| Hosting/reviews | HOST-01, HOST-06, HOST-08, HOST-09 — N; HOST-10, HOST-20, HOST-21, HOST-22 — P | Account/repository browser before clone; Bitbucket adapter; GitLab/Azure publication; cross-repository PR browsing; provider-specific detailed review and advanced actions. |
| Terminal | TERM-05, TERM-06 — P | Persist bounded scrollback/session metadata across host restart; foreground-process labels and idle-shell detection. |
| Browser | WEB-02, WEB-06, WEB-09, WEB-13, WEB-18 — P; WEB-07, WEB-14, WEB-15, WEB-16, WEB-17, WEB-19 — N | Terminal-process ownership of discovered listeners; defaults/profile management; installed-browser import; recording frame-rate parity; automation lifetime across thread switches; agent tab creation, viewport/appearance, JavaScript, recording; richer snapshots; system-browser/app-preview link preference. |
| Appearance | LOOK-02, LOOK-03, LOOK-04 — P; LOOK-05, LOOK-06, LOOK-07, LOOK-08, LOOK-10 — N; LOOK-09 — R | Independent fonts, contrast/opacity, wrap/diff preferences; palette editing/inspection and theme interchange; animation duration; decide native handling of Pi TUI themes. |
| Usage/diagnostics/reliability | USE-02, USE-03, USE-04, USE-07, DIAG-02, DIAG-03, REL-03 — P; USE-05, USE-06, REL-04 — N | Usage dashboard/breakdowns/cache savings/history rescan; child usage aggregation; quota and pooled-account adapters; process history/actions; tracing/metrics; paged transcript recovery; Windows background/power policy. |
| Remote qualification | REM-19 — P | Live Windows Tailscale and native acceptance, also tracked in DEL-09; clarify whether any implementation remains. |

Source checks supporting the remaining boundaries:

- [HostingCapabilities.cs](../src/PiStation.Protocol/Models/HostingCapabilities.cs) restricts publication and detailed review to GitHub. [SourceControlHostingService.cs](../src/PiStation.Host/SourceControl/SourceControlHostingService.cs) implements GitHub publication and explicitly rejects Bitbucket list/create operations. Merely recognizing a Bitbucket URL is not an adapter.
- [pistation-browser.ts](../src/PiStation.App/PiExtensions/pistation-browser.ts) and [BrowserAutomation.cs](../src/PiStation.Protocol/Models/BrowserAutomation.cs) permit status, navigation, snapshot, click, type, screenshot, keys, scroll, and wait. They do not expose the missing agent operations. [RightPanelHost.xaml.cs](../src/PiStation.App/Views/Controls/RightPanelHost.xaml.cs) owns a thread-specific automation context. [PreviewWebViewSurface.xaml.cs](../src/PiStation.App/Views/Controls/PreviewWebViewSurface.xaml.cs) clamps recording to 1–12 FPS.
- [TerminalOutputJournal.cs](../src/PiStation.Host/Terminals/TerminalOutputJournal.cs) retains output and replay events in memory. [PreviewDiscoveryService.cs](../src/PiStation.Host/Preview/PreviewDiscoveryService.cs) scans listeners after validating the project; it does not consult terminal-process ownership.
- [PiSessionDocument.cs](../src/PiStation.PiRpc/Sessions/PiSessionDocument.cs) produces text-oriented active-branch HTML with image placeholders. [PiSessionsViewModel.cs](../src/PiStation.App/ViewModels/PiSessionsViewModel.cs) displays current tree rows/bookmarks without the remaining presentation controls. [PiRuntimeSettingsStore.cs](../src/PiStation.Host/PiRuntimeSettingsStore.cs) rejects `--no-session` in normal runtime arguments; one-shot setup/writing uses elsewhere do not supply an ephemeral conversation feature.
- [SettingsViewModel.cs](../src/PiStation.App/ViewModels/SettingsViewModel.cs), [ShellLayoutViewModel.cs](../src/PiStation.App/ViewModels/ShellLayoutViewModel.cs), and [HostDiagnosticsService.cs](../src/PiStation.Host/Diagnostics/HostDiagnosticsService.cs) retain summary usage/resource views and limited appearance preferences. [ShellViewModel.Pagination.cs](../src/PiStation.App/ViewModels/ShellViewModel.Pagination.cs) pages files, threads, refs, and PRs; those paths are not paged conversation history.
- [PiResourcesViewModel.cs](../src/PiStation.App/ViewModels/PiResourcesViewModel.cs) correctly keeps unconfirmed resources distinct from confirmed loads. [pistation-resources.ts](../src/PiStation.App/PiExtensions/pistation-resources.ts) infers extension confirmation from reported tools/commands, supporting the remaining attribution limitation. [ShellViewModel.ProjectCustomization.cs](../src/PiStation.App/ViewModels/ShellViewModel.ProjectCustomization.cs) updates the selected project rather than every grouped checkout.

## Defects and delivery still open

**BUG-09 is the clearest current gate blocker.** I read the retained [full-gate summary](../TestResults/code-Debug-20260909-153530-d6aff240/summary.json) and its TRX failure. They confirm **966 passed, 1 failed, 2 skipped**, with overall success false. The failing test is `ClientSendsDraftAttachmentsClearsOnAcceptanceAndHydratesAPathFreeTranscript`; its stack identifies the initial Ready wait at line 636 in [EnvironmentClientIntegrationTests.cs](../tests/PiStation.ClientRuntime.Tests/EnvironmentClientIntegrationTests.cs), before attachment work. The later [isolated run](../TestResults/code-Debug-20260909-155108-8a8d1865/summary.json) passes one test. It does not replace the failed full suite or establish the cause. Retained reports are local ignored artifacts and may not exist in another clone.

**BUG-04 remains a native-contention investigation.** Existing checkpoint passes reduce failure evidence but do not close the unexercised historical native-stress case. BUG-01/02/03/05/06/07/08 have source fixes with specific acceptance still pending; BUG-10's test correction is recorded as verified.

| Delivery ID | Remaining acceptance/work |
| --- | --- |
| DEL-02 | Production publisher, signing, update feed, installation/upgrade migration; explicitly deferred until after core, required before public release. |
| DEL-03 | Keyboard/focus, screen reader, High Contrast, new shell/composer interaction, and adaptation of old native journeys. |
| DEL-04 | Inspect actual populated nonblank renderings across scaling, mixed monitors, narrow layouts, rich content, and new UI states. |
| DEL-05 | Measured long-history, large-workspace, and multi-terminal latency/CPU/memory. |
| DEL-06 | Clean-machine Pi/codec setup and fresh authenticated provider journeys. |
| DEL-07 | Authenticated hosting list/clone/publish/review/actions on supported providers. |
| DEL-08 | Native browser/profile/media and automation acceptance. |
| DEL-09 | Two physical Windows machines, actual OpenSSH, live Tailscale, native pickers, reconnect/revoke/sleep/network/manual-update recovery. The tracker records the second-PC/Tailscale/native-access constraints; this audit did not recheck their availability. |
| DEL-10 | Clean full gate, timing/root-cause work, Release verification, regression coverage maintenance, and applicable native qualification. |

## Deferred work and optional decisions

After the core milestone, the six committed deferred rows remain: **SCOPE-02** WSL; **SCOPE-04** named Pi runtime/account instances; **SCOPE-06** web/mobile clients; **SCOPE-07** mobile offline/share/voice/push features; **SCOPE-09** served-client themes; **SCOPE-11** PiStation task CLI. Carry DEL-02 forward too.

**OPT-02 and OPT-03 are required compatibility investigations**, already linked to EXT-15 and AGENT-08. The other nine OPT rows remain unselected proposals: local Pi installer, MCP bridge, packaged web/image tools, persistent agent shell jobs, AI approval reviewer, sandbox/service management, universal server attribution, script synchronization, and product analytics. Explicit exclusions, including remote install/update automation, hosted relay/accounts, extra agent harnesses, and Linux/macOS app/host targets, are not missing implementation commitments.

Recommended order: resolve BUG-09 and obtain a clean full gate; adapt and run the native journeys and remaining acceptance; complete the selected core implementation backlog; then implement the deferred phase and production release work under the existing milestone rules. Keep BUG-04's outstanding stress evidence explicit throughout.
