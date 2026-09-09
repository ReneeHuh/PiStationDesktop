# Browser automation lifetime and tab operations

Implements WEB-13, WEB-14 and WEB-15 against local T3 reference `0a590fa01af66ec135d2ebf2d5542b08a37dc275`. This slice introduced wire protocol **49**; [evaluation and snapshots](BROWSER-EVALUATION-AND-SNAPSHOTS-2026-09-09.md) advance it to **50**, requiring matching host and desktop builds.

## Ownership and presentation

The desktop owns browser workspaces by project/thread within its environment window. Changing the selected thread retains the original tab objects, WebView2 documents and inspect/interact permission. A page-owned container keeps background surfaces attached when the workbench closes; only a presented tab or a tab executing an operation needs rendering. No hidden native browser window is created. The visible panel presents the selected tab from that registry.

Each thread has an independent host lease. A dedicated browser control connection is shared by the environment window's controllers. It uses the same bearer credential, TLS pin and environment/protocol checks as the workspace connection. Another desktop cannot take a thread over until its controller closes or expires. Disconnect closes the control connection and cancels its work; reconnect creates fresh leases and does not replay claimed commands. The complete catalog restores previously granted background threads and releases deleted projects/threads. Remote navigation captures the owning workspace's project, so switching projects cannot redirect a background tab's host-local route. Permissions and browser storage remain isolated per environment.

Separating browser control prevents slow or queued workspace calls, including thread startup, from blocking heartbeats, completion and permission changes. Each heartbeat still originates at the client: the three-second client deadline, five-second host expiry and thirty-second request lifetime are unchanged. SignalR's ordinary per-connection invocation limit is documented in [ASP.NET Core HubOptions](https://github.com/dotnet/aspnetcore/blob/main/src/SignalR/server/Core/src/HubOptions.cs).

An omitted tabId initially uses that thread's selected tab. After an operation succeeds, the agent's target stays pinned independently of human selection. Explicit IDs are resolved only inside the owning thread. A closed pinned tab returns an error rather than using another tab. Thread/project deletion, tab closure, permission revocation, disconnect and page shutdown cancel work. Initializing a blank browser also locks its profile, since WebView2 cannot switch storage after initialization.

## Pi tool operations

The existing `pistation_browser` tool gains:

- `open`: optional HTTP/HTTPS `url`, optional existing `tabId`, `reuseExistingTab` (default true), and `open` (default true). Setting reuseExistingTab=false creates another tab; combining it with tabId is rejected. Setting open=false keeps the human's selection and panel state. A background thread does not switch the human's selected thread. New tabs use the shared profile, viewport, zoom and appearance defaults. The existing 12-tab limit returns an error; initialization failure/cancellation removes a newly created tab.
- `resize`: `mode: fill`; `mode: freeform` with width/height; or `mode: preset` with desktop/tablet/phone and optional portrait/landscape orientation. Freeform dimensions are bounded to 240–3840 CSS pixels and at most 8,294,400 pixels. Presets use the existing PiStation dimensions (1440×900, 768×1024, 390×844); this does not import T3's full named-device catalog or emulate a mobile user agent. Settings persist per tab, including freeform sizes.
- `set_appearance`: `colorScheme: system`, light or dark. Uses the existing prefers-color-scheme emulation; system clears the override.

All three operations require **interact** permission in the extension, host and desktop. Inputs and results retain existing bounds. Resize waits for two matching rendered innerWidth/innerHeight observations, allowing one CSS pixel of native rounding, as T3 does; appearance reads the rendered media-query result after the native override. They return actual state and fail on timeout or a competing change. Revision checks prevent rollback from overwriting a newer human action. Existing status/navigation/snapshot/click/type/keys/scroll/wait/screenshot operations use the same retained targets.

JavaScript evaluation and richer snapshots are covered by the [protocol-50 follow-up](BROWSER-EVALUATION-AND-SNAPSHOTS-2026-09-09.md). Agent recording/artifact delivery, installed-browser session import and 30/60 FPS recording remain WEB-17, WEB-07 and WEB-09 respectively.

## Verification

The Debug solution build passed with zero warnings/errors. The final focused gate passed **64 tests / zero failures or skips**: ClientRuntime 43, CommandSystem 6, Host 14 and Protocol 1. [Report](../TestResults/code-Debug-20260909-200233-869b689e/summary.json). This includes local/HTTPS/SSH control, permission and deletion isolation, disconnect/reconnect without replay, input limits, profile/default inheritance, workspace identity and saved state. The slow-start regression holds composer discovery open for six seconds and queues another workspace RPC; browser request delivery, cancellation, completion and permission closure all succeed before those workspace calls are released.

The native WebView2 acceptance slice **passed**. [Summary](../TestResults/browser-native-20260909-160838-80dfb090ad8d46c799ea3a37826aa4d0/summary.json), [background screenshot](../TestResults/browser-native-20260909-160838-80dfb090ad8d46c799ea3a37826aa4d0/background-browser.png). The screenshot was visually checked: it contains the retained document and dark appearance at the requested freeform size. The flow also confirms light/system appearance, closed-panel operation and reopening the same document in the foreground. The native driver retries only stale read-only inspections, never applied actions.

Earlier reports remain retained. Native checks exposed the missing Freeform selector entry and regular-command queue starvation; both were corrected. The earlier single-response-stream attempt passed a one-blocked-call regression but failed the native flow with additional queued calls, so it was replaced by the dedicated browser control connection. A later driver-only stale-element failure is retained in `browser-native-20260909-160615-d100cfa761be4df5a7592e5ff61b4ced`. The initial slow-start fixture incorrectly waited at thread creation instead of runtime startup; its three timeouts remain in `code-Debug-20260909-194925-272e0da3`.

The full default gate passed **1,013 tests / zero failures / 21 optional skips**: ClientRuntime 438, CommandSystem 82, Host 360, PiRpc 74 and Protocol 59. [Report](../TestResults/code-Debug-20260909-201027-81bc16a6/summary.json). The isolated **Pi 0.85.0 browser bridge check passed** separately, with zero failures/skips: the bundled TypeScript tool forwards the new actions and explicit targets, delivers screenshot content, and rejects interaction under inspect-only permission. [Real-Pi report](../TestResults/code-Debug-20260909-202226-909cc00e/summary.json). This uses an isolated Pi home and a local extension probe; no model/API call is required.

The prior protocol-48 full-gate startup failure remains BUG-09; this green run does not establish its root cause. Physical two-computer acceptance and the other DEL-08 native profile, input and capture cases remain pending.

The existing static visual contract passed (24 states, four responsive layouts and three text scales), and `git diff --check` passed.

The native check is `tests/PiStation.UiTests/Invoke-BrowserAutomationSlice.ps1`. It uses a fresh data root, local HTML fixture, captured app PID and owned server job. It exercises background open, preset/freeform rendered dimensions, all appearance modes, document retention across thread changes and panel closure, pinned target, screenshot and inspect-only rejection. It requires a working native app/driver and a current Debug build; it never attaches to the user's app instance.

## T3 source comparison

T3 mounts PreviewAutomationHosts at the app root per environment and hosts browser documents separately from routed thread views (`apps/web/src/components/preview/PreviewAutomationHosts.tsx`, `apps/web/src/browser/ElectronBrowserHost.tsx`). Its preview_open contract controls reuse and presentation, and its broker remembers provider-session targets (`packages/contracts/src/previewAutomation.ts`, `apps/server/src/mcp/PreviewAutomationBroker.ts`). Resize awaits rendered viewport dimensions with guarded rollback; appearance goes through the desktop browser bridge. PiStation follows these ownership and confirmation behaviors using its existing authenticated Pi extension bridge and WinUI/WebView2 surfaces.
