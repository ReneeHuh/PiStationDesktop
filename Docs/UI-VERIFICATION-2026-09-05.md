# Native UI verification — September 5, 2026

PiStation UI testing uses **Microsoft `winapp ui` 0.6.1** and the repository's PowerShell/Pester scenarios. Native interaction and readable captures work on the active Windows RDP desktop. The earlier Computer Use pipe/access-denied/black-capture blocker is historical.

## Completed fixes

Three Luna agents implemented and cross-reviewed the Files, theme, and inbox changes. Integration review and native testing added further corrections:

- **Files:** the default 420px panel stacks the tree above a full-width editor. Search and file actions use separate rows, with horizontal scrolling for overflowing actions. The maximum panel width is now 720px, allowing a side-by-side layout above the 560px breakpoint.
- **Preview/PiP:** app surfaces and the separate preview window retain the active theme. Browser content keeps its normal opaque white backing so unstyled HTML remains readable. The second-tab fixture tolerates idle/abandoned connections.
- **Theme and syntax:** brushes resolve inside each theme dictionary; programmatic lookups use the owning view's actual theme. Markdown and diff syntax share a readable dark palette. Screenshot review caught and corrected light diff backgrounds in Dark mode.
- **Inbox:** metadata changes update grouped rows/counts from the complete project snapshot, independently of search. Refreshes are serialized; detached-client callbacks are ignored. Deletion emits a notification; counts use singular/plural labels.
- **Rename:** saving closes the editor even when refresh replaces/recycles its row. A generation guard prevents an old async result from closing a newer edit.
- **Composer:** Send and Stop have a dedicated row; secondary controls scroll horizontally. Prompt growth accounts for the measured toolbar height and the existing 240px composer budget.
- **Tests:** thread selectors are scoped to the main list, expectations match current titles/statuses, and running-state fixtures use explicit gates. Blank captures are rejected and retried with focused screen capture. The compatibility runner avoids applying monitor scaling twice.

Earlier prompt-acknowledgment/Stop and empty-language diff fixes remain covered by regressions.

## Results

Native scenarios exercise the installed Debug app against isolated local hosts and FakePi fixtures. Release compilation and unsigned package contents are checked separately.

| Check | Result |
| --- | --- |
| Debug solution build | Passed, zero warnings/errors |
| Release publish / MSIX | Passed; self-contained payload and SHA-256 verified |
| Debug non–Real Pi tests | 199 passed: Host 85, ClientRuntime 43, PiRpc 42, Protocol 25, CommandSystem 4 |
| Release non–Real Pi tests | 199 passed across the same projects |
| Shell / driver contract | Passed |
| Conversation | Passed: streaming, tools, Markdown/code, copy, thread switching, restart/transcript restoration |
| Full workbench | Passed: Changes, Files, terminal/nested panes, Preview controls, overlay, restart/layout restoration |
| Recovery | Passed |
| Extension interaction | Passed |
| Draft persistence | Passed across threads and restart |
| Pi configuration | Passed: model/thinking changes and restart |
| Thread lifecycle | Passed: rename closes, pin/unpin, search, archive/unarchive, immediate grouped counts, restart |
| Hardening | Passed: Stop/thread isolation, reconnect/resync, uncertain dispatch; no duplicate prompts |
| Input/accessibility | Passed: focus/navigation, Unicode/multiline input, Enter dispatch, transcript scrolling, rename cancellation, file mentions, disconnect/reconnect draft preservation |
| Theme / responsive layout / text profiles | Passed: four widths, collapsed sidebar, maximized, bounded composer growth, Light/System/Dark, 100/150/200% profiles; Files edit/save at 150/200% |
| Focused visual workbench | Passed on the final build: dark diff palettes, 420px stacked/640px side-by-side Files, Send/Stop bounds, second-tab navigation, PiP open/close, dark app canvas and readable HTML backing |
| Static visual contract | Passed: 24 states, 4 layouts, 3 text profiles |
| Script parsing / whitespace | Passed |

The full workbench functional run preceded the final literal-brush, browser-backing, and composer corrections. The final focused Visual run verified those changes explicitly; its wide Files and Preview screenshots were reviewed. The dark Markdown syntax screenshot was also reviewed and its keyword palette assertion passed. All 11 native scenario runners and the final focused Visual run passed.

## Evidence

Paths are relative to the repository root:

- Core TRX: `TestResults/visual-polish-debug/` and `TestResults/visual-polish-release/`.
- Native logs: `TestResults/visual-polish-ui-*.log`.
- Full workbench: `tests/PiStation.UiTests/artifacts/workbench-runs/20260905-140924-ee39014bc91049d291e263435df039c6/`.
- Final visual workbench: `tests/PiStation.UiTests/artifacts/workbench-runs/20260905-152351-9e818c03a63948a49ea81052da4f2451/`.
- Conversation: `tests/PiStation.UiTests/artifacts/runs/20260905-141839-665859e2f50c493e8c13c1349479a5d3/`.
- Recovery: `tests/PiStation.UiTests/artifacts/recovery-runs/20260905-142749-92f9be37929b4eed9f58e717cf994fb8/`.
- Interaction: `tests/PiStation.UiTests/artifacts/interaction-runs/20260905-143015-1e67dede586f4a4f87fe319696108cc1/`.
- Draft: `tests/PiStation.UiTests/artifacts/draft-runs/20260905-143040-280ef15b3bc04f2297f2cdf58edcd6c1/`.
- Pi configuration: `tests/PiStation.UiTests/artifacts/pi-configuration-runs/20260905-143619-04fac2ddbfd1436f83a59ba18112f6d0/`.
- Lifecycle: `tests/PiStation.UiTests/artifacts/thread-lifecycle-runs/20260905-145615-819734aba3b749cd86da259f3feb5520/`.
- Hardening: `tests/PiStation.UiTests/artifacts/hardening-runs/20260905-144612-6e1247d9dc32482a867041228a175c84/`.
- Compatibility: `tests/PiStation.UiTests/artifacts/compatibility-runs/20260905-151812-ea29c0f23a4149ed9bbaaad2b681595e/`.
- Input/accessibility: `tests/PiStation.UiTests/artifacts/input-accessibility-runs/20260905-152146-3577ab56d57d46d497fbd8b4e62ca92c/`.

## Coverage limits and release

The physical monitor scale is **125%**. The compatibility scenario exercises the app's **100%, 150%, and 200% text profiles**, Light/Dark/System choices, and Narrow/Compact/Standard/Wide/maximized layouts. Text profiles do not certify every physical Windows DPI setting. Additional physical-DPI/monitor combinations and Windows High Contrast remain manual acceptance work.

Authenticated hosting, real-Pi/provider smoke tests, and long-session performance measurements remain tracked in the [implementation status](IMPLEMENTATION-STATUS-2026-09-05.md). No remote hosting writes were performed.

The rebuilt unsigned MSIX/checksum are linked in the [implementation status](IMPLEMENTATION-STATUS-2026-09-05.md). Publisher identity, signing, HTTPS update feed, and second-machine install/upgrade remain explicitly deferred in [RELEASE-TODO.md](RELEASE-TODO.md).

## Run locally

Build first when sources change. Run desktop scenarios serially on an active, unlocked desktop; each scenario owns isolated data and local fixtures.

```powershell
dotnet build PiStationDesktop.slnx -c Debug -p:Platform=x64
pwsh ./tests/PiStation.UiTests/Invoke-ThreadLifecycleSlice.ps1 -NoBuild
pwsh ./tests/PiStation.UiTests/Invoke-InputAccessibilitySlice.ps1 -NoBuild
pwsh ./tests/PiStation.UiTests/Invoke-CompatibilitySlice.ps1 -NoBuild
pwsh ./tests/PiStation.UiTests/Invoke-WorkbenchSlice.ps1 -NoBuild -Scope Visual
```
