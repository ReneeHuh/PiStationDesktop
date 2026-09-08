# PiStation Desktop — Preview Panel Plan

Status: First release implemented; optional stages 6–7 deferred  
Target: Protocol v15 and the packaged WinUI workbench  
Reference baseline: T3 Code `9159b808d35a88e74fc91e11070f3270cdb321f9`  
Last reviewed: 2026-09-04

**September 7 extension:** the first-release design below is retained as history. Protocol 20 now supports remote discovery and preview forwarding over direct HTTPS and SSH; see [the implementation and qualification notes](REMOTE-ACCESS-IMPLEMENTATION.md). WebView2 remains client-owned. A native proxy assigns each route an isolated temporary origin and forwards browser traffic through an authenticated host lease; browser scripts never receive the device credential. Host-local remote URLs open in embedded Preview. Remote forwarding keeps each tab's browser state client-owned.

## Implementation result

Stages 1–5 are complete:

- protocol v15 and the `preview.discover` capability;
- bounded, concurrent loopback HTTP/HTTPS discovery;
- typed ClientRuntime support and stale-project cancellation;
- per-project Preview URL persistence;
- a security-restricted WebView2 surface;
- T3-style browser chrome, local-server empty state, loading, and recoverable failure UI;
- packaged navigation and relaunch coverage plus a 24-state visual contract.

Stages 6–7 remain intentionally optional: viewport presets, zoom, page color preference, recent
URLs, multi-tab support, screenshots, recording, picture-in-picture, element annotations, and
permissioned agent browser automation.

## 1. Outcome

Add a useful, secure Preview panel to PiStationDesktop that follows the visual and interaction model of T3 Code:

- detect local development servers;
- open an HTTP or HTTPS URL in an embedded WebView2 browser;
- provide back, forward, reload/stop, address, and open-external controls;
- show clear empty, discovery, loading, loaded, and failure states;
- behave correctly in the existing docked and narrow overlay layouts;
- remember the last Preview URL for each project;
- keep browser automation, screenshots, recording, and element picking out of the first release.

The first release is a development preview, not a general-purpose web browser.

## 2. Architectural decision

Use a project-scoped, client-owned browser surface.

```text
Environment host
  └─ discovers browser-ready loopback servers
             │ protocol v15
             ▼
WorkbenchPreviewViewModel
  ├─ owns project Preview state and persistence
  └─ directs PreviewWebViewSurface
             │
             ▼
WebView2 browser process
```

The host discovers candidate local servers. The WinUI client owns WebView2 creation, navigation history, loading state, and rendering. No browser bytes or navigation commands need to travel through SignalR in this release.

This deliberately differs from T3's thread-scoped, multi-tab preview manager. PiStation currently has one local packaged client and project-scoped workbench panels, so server-owned tab synchronization would add complexity without a current consumer. The contracts can be extended to multi-tab or remote scenarios later.

## 3. First-release scope

### Included

- One retained Preview browser per selected project.
- Manual HTTP/HTTPS URL entry.
- URL normalization: an address without a scheme is treated as HTTP.
- Local development-server discovery from the environment host.
- A selectable list of discovered servers in the empty state.
- Back and forward navigation.
- Reload while idle and stop while loading.
- Open the current URL in the system browser.
- Browser title, current address, navigation status, and recoverable errors.
- Lazy WebView2 initialization when Preview is first selected.
- Per-project persistence of the last successfully requested URL.
- Cancellation and stale-result protection when the project changes or the host disconnects.
- Existing docked, compact, and overlay workbench behavior.
- Keyboard and accessibility metadata for all new controls.

### Explicitly deferred

- Multiple Preview tabs and server-owned Preview sessions.
- Remote preview proxying, port forwarding, or tunneling.
- Screenshots, video recording, and picture-in-picture.
- Element picking and element-to-prompt annotations.
- Agent-driven browser automation.
- Password storage, downloads, browser extensions, or a general browser history UI.
- Arbitrary URL schemes or local-file browsing.
- Terminal-process-to-port correlation. It can be added after basic discovery is reliable.

## 4. User experience

### Preview toolbar

The toolbar should use the same compact density as the current workbench and T3 Code:

```text
┌───────────────────────────────────────────────┐
│ ‹  ›  ↻/×  [ http://localhost:5173       ] ↗ │
├───────────────────────────────────────────────┤
│                                               │
│            embedded page or state             │
│                                               │
└───────────────────────────────────────────────┘
```

- Back and forward buttons expose disabled states.
- Reload changes to Stop while a navigation is active.
- Enter in the address box navigates.
- Escape while the address box is focused restores the current address.
- Open External is disabled until a valid HTTP/HTTPS URL exists.
- Browser content receives the remaining panel area.

### Empty state

When no URL has been opened, show:

- title: `No preview yet`;
- short guidance to enter a URL or run a local development server;
- a `Local servers` section;
- discovery progress, results, and a manual Refresh action;
- a compact server row containing the address and, when available, process details.

Selecting a discovered server opens it immediately.

### Failure state

Keep the address bar and browser surface intact. Overlay a friendly error card containing:

- `Preview unavailable`;
- the failed address;
- a concise mapped failure reason;
- Reload and Return to servers actions.

WebView2 process failure should replace the page with a recoverable surface and offer Recreate Preview.

### Responsive behavior

- At normal width, show all toolbar actions and the full address box.
- At narrow overlay width, preserve navigation, reload/stop, address, and overflow/open-external access.
- The page itself fills the panel; the first release does not emulate a fixed device width.
- Text scaling must not clip the empty or failure actions.

## 5. Protocol v15

Add `PiStation.Protocol/Models/Preview.cs` with small discovery-only contracts.

Suggested contract shape:

```csharp
public sealed record DiscoverProjectPreviewServersRequest(ProjectId ProjectId);

public sealed record DiscoveredPreviewServer(
    string Url,
    string Host,
    int Port,
    string Scheme,
    string? ProcessName,
    int? ProcessId);

public sealed record DiscoverProjectPreviewServersResult(
    ProjectId ProjectId,
    DateTimeOffset ScannedAtUtc,
    IReadOnlyList<DiscoveredPreviewServer> Servers,
    bool IsTruncated);
```

Required protocol work:

- bump the protocol version from 14 to 15;
- add capability `preview.discover` to the environment descriptor;
- add `DiscoverProjectPreviewServers` to the environment service, SignalR hub, client interface, and client implementation;
- register the new records in source-generated JSON serialization;
- add stable validation and unavailable error codes;
- cap returned results and all user-controlled string lengths;
- preserve v14 compatibility behavior according to the existing compatibility policy.

Discovery is project-authorized even though the listeners belong to the environment machine. A missing or inaccessible project must fail before the scan starts.

The host does not receive arbitrary browser navigation requests and must not become an HTTP proxy.

## 6. Host discovery service

Add a focused `PreviewPortDiscoveryService` behind an interface so the scan and probes can be tested independently.

### Candidate collection

- Read active TCP listeners with .NET networking APIs instead of starting PowerShell.
- Accept loopback and wildcard listeners that may serve the local machine.
- Add a short curated development-port fallback list when no listener information is available.
- Deduplicate by port and order deterministic results by port.
- Bound the number of candidates before probing.

Initial fallback ports:

`3000`, `3001`, `3333`, `4173`, `4200`, `4321`, `5000`, `5173`–`5175`, `5500`, `8000`, `8080`, `8081`, `8888`, and `9000`.

### HTTP probing

- Probe HTTP and, when appropriate, HTTPS with a cancellation-aware timeout of at most one second per attempt.
- Limit concurrent probes; start with 16 as the maximum.
- Avoid downloading response bodies.
- Accept successful HTML/XHTML responses and useful redirects.
- Reject a discovered redirect if its final destination is not loopback.
- Treat certificate, connection, timeout, and malformed-response failures as a missed candidate rather than a whole-scan failure.
- Return canonical loopback URLs and stable ordering.

### Refresh behavior

- Scan when Preview is selected for a project that has no current results.
- Provide explicit manual Refresh.
- Do not run a permanent background scanner.
- After the base feature is stable, optionally refresh every three seconds only while the empty Preview state is visible; stop immediately when hidden, disconnected, or disposed.

The first implementation should land with selection-triggered and manual scans. Visible-only polling is a separate, easy-to-remove follow-up.

## 7. Client state and persistence

Add `WorkbenchPreviewViewModel` alongside the existing Files, Changes, and Terminal workbench models.

Core state:

- `UrlDraft`
- `CurrentUri`
- `DocumentTitle`
- `NavigationStatus`
- `IsDiscovering`
- `IsLoading`
- `CanGoBack`
- `CanGoForward`
- `FailureKind` and `FailureMessage`
- `DiscoveredServers`
- `LastDiscoveryUtc`
- current project identity and a monotonically increasing operation generation

Core operations:

- activate and deactivate Preview;
- discover and refresh local servers;
- validate and normalize an address;
- request navigation;
- go back and forward;
- reload or stop;
- open externally;
- accept browser navigation and process events;
- clear or supersede stale asynchronous results.

Keep WebView2 types out of the view model. Expose narrow commands/events between the view model and the browser surface so navigation logic can be unit tested.

Extend the current shell layout/settings model with a version-tolerant per-project Preview entry containing the last normalized URL. Persist a URL only after it passes validation and navigation is requested. Do not try to serialize WebView2's back/forward stack.

On project switch:

1. cancel the old discovery;
2. detach or hide its surface;
3. load the new project's saved address and state;
4. ignore late results using project identity plus operation generation;
5. discover servers if the new project has no saved/current preview.

## 8. WebView2 surface

Add a dedicated `PreviewWebViewSurface.xaml` and code-behind, following the encapsulation already used by the terminal WebView surface.

Responsibilities:

- lazily create and initialize `WebView2`;
- receive normalized navigation requests;
- publish source, title, navigation, history, and process-failure events;
- update `CanGoBack` and `CanGoForward` from the browser;
- map navigation completion errors into stable UI failure kinds;
- stop an active navigation;
- recover by recreating the environment/controller after a browser process failure;
- dispose handlers and browser resources deterministically.

Handle these WebView2 events at minimum:

- initialization completed;
- navigation starting and completed;
- source changed;
- document title changed;
- history changed;
- process failed;
- new window requested;
- permission requested;
- download starting.

Use a dedicated Preview user-data directory if supported cleanly by the installed WebView2 SDK. Keep it separate from the terminal surface and never place PiStation bearer tokens or host credentials into browser storage.

## 9. Security policy

Treat previewed pages as untrusted content.

- Allow only `http` and `https` navigation.
- Reject `file`, `javascript`, `data`, `about`, custom schemes, embedded credentials, and invalid/oversized addresses.
- Limit addresses to 2,048 characters.
- Discovery returns only loopback targets and does not follow discovery redirects off loopback.
- Manual navigation may target a non-loopback HTTP/HTTPS address because the user entered it explicitly.
- Do not expose host objects, SignalR credentials, privileged web messages, or virtual-host mappings to preview pages.
- Deny camera, microphone, location, notifications, MIDI, clipboard-read, and similar permission prompts in the first release.
- Cancel downloads in the first release and display a short explanation.
- Handle popup/new-window requests deterministically: ordinary HTTP/HTTPS targets navigate in the same Preview; unsupported targets are blocked.
- Open External must use the same normalized HTTP/HTTPS validation path.
- Disable DevTools in release builds until an explicit Preview developer-tools setting is implemented.

## 10. XAML integration

Update `RightPanelHost.xaml` and its code-behind/view-model bindings:

- add `PreviewPanelVisibility`;
- replace the generic Preview placeholder with a dedicated Preview root;
- add the compact navigation toolbar and semantic automation IDs;
- host `PreviewWebViewSurface` below the toolbar;
- layer empty, discovering, and failure UI over the browser area where appropriate;
- preserve the existing panel resize, dock, and overlay behavior;
- keep Preview initialization lazy so startup and non-Preview navigation remain unaffected.

Recommended automation IDs:

- `WorkbenchPreviewPanel`
- `PreviewBackButton`
- `PreviewForwardButton`
- `PreviewReloadStopButton`
- `PreviewAddressBox`
- `PreviewOpenExternalButton`
- `PreviewRefreshServersButton`
- `PreviewServerList`
- `PreviewBrowserSurface`
- `PreviewFailureOverlay`

Update `ShellLayoutViewModel` so Preview has a real panel description and is not routed through `WorkbenchEmptyStateVisibility`.

## 11. Implementation stages

### Stage 1 — Protocol and discovery

- Add v15 contracts, capability, JSON metadata, hub/client methods, and errors.
- Implement bounded loopback listener collection and HTTP probing.
- Add deterministic unit and host integration tests.

Exit condition: a client can request discovery for a valid project and receive a safe, ordered list of browser-ready local URLs.

### Stage 2 — Preview state and persistence

- Add `WorkbenchPreviewViewModel` and shell integration.
- Implement URL validation/normalization and operation-generation cancellation.
- Add version-tolerant per-project last-URL persistence.
- Add view-model unit tests before wiring WebView2.

Exit condition: Preview state survives project switches and app restart without requiring a browser surface.

### Stage 3 — Browser surface

- Add the lazy WebView2 surface and its narrow event bridge.
- Implement navigation, history, reload/stop, process recovery, and security handlers.
- Add injectable external-launch handling so tests do not start the system browser.

Exit condition: a known local test page can be navigated, reloaded, traversed backward/forward, and recovered after a simulated failure.

### Stage 4 — T3-style workbench UI

- Replace the Preview placeholder with the toolbar, empty state, discovered-server list, browser, and failure overlay.
- Polish normal, compact, and overlay widths.
- Add keyboard navigation, tooltips, accessible names, focus order, and high-contrast resources.

Exit condition: Preview is visually coherent with PiStation's T3-aligned workbench at supported widths and scale factors.

### Stage 5 — Packaged verification and documentation

- Exercise Preview in the packaged WinUI application against an in-process local test server.
- Add visual and workbench contract assertions.
- Run the complete existing regression suite.
- Update README architecture, capabilities, protocol version, and feature-status sections.

Exit condition: packaged verification passes with zero build warnings and Preview is no longer described as unavailable.

### Stage 6 — Small browser enhancements

After the base feature is stable, add independently:

- visible-empty-state discovery polling;
- responsive viewport presets and freeform sizing;
- the T3 zoom ladder;
- system/light/dark page color preference;
- a deliberate DevTools command;
- recently used URLs;
- multiple tabs, only if usage demonstrates the need.

### Stage 7 — Advanced preview tools

Plan and review these as separate security-sensitive features:

- screenshots;
- recording;
- picture-in-picture;
- element inspection and prompt annotations;
- agent browser automation through a permissioned host/client bridge.

None of these should be hidden inside the base Preview implementation.

## 12. Test plan

### Protocol and host

- v15 discovery request/result serialization.
- Project ID, URL, host, process-name, result-count, port, and scheme bounds.
- Missing project and absent capability behavior.
- Listener deduplication and stable ordering.
- HTTP and HTTPS probe success.
- HTML acceptance and non-HTML rejection.
- Redirect-to-loopback acceptance and redirect-off-loopback rejection.
- Timeout, cancellation, certificate error, refused connection, and truncated-result behavior.
- Concurrency remains within the configured bound.

Use injected listener and probe abstractions for deterministic unit tests. Use an ephemeral local HTTP server only for the focused integration path.

### View model

- HTTP/HTTPS normalization and invalid-scheme rejection.
- Navigate, reload/stop, back/forward, and external-open enablement.
- Address draft restoration on Escape.
- Discovery loading, success, empty, and failure states.
- Old-project and old-generation results cannot update current state.
- Saved URLs remain isolated per project and tolerate old settings files.
- Disconnect and reconnect behavior.

### Browser and packaged UI

Serve deterministic routes such as `/`, `/second`, and an intentionally failed target.

- Preview WebView initializes only after selection.
- Address entry navigates.
- A discovered-server row navigates.
- Title and source update.
- Back, forward, reload, and stop states track WebView2.
- Invalid schemes, permissions, downloads, and popups follow policy.
- Navigation and browser-process failures present recoverable UI.
- The system browser seam receives the expected validated URL without launching during tests.
- All automation IDs and accessible names exist.
- Empty, discovered, loaded, loading, and failure visual states remain correct at docked and overlay widths.
- High contrast and increased text scale remain usable.

### Regression gates

- solution build: zero warnings and zero errors;
- complete .NET test suite;
- protocol compatibility tests;
- packaged UI tests;
- visual contract tests;
- existing Changes, Files, Terminal, shell, update, and attachment workflows.

## 13. Acceptance criteria

The first Preview release is complete when all of the following are true:

- Selecting Preview shows the real Preview experience, not an unavailable placeholder.
- A browser-ready loopback development server appears after discovery.
- The user can open a discovered server or enter an HTTP/HTTPS address manually.
- Back, forward, reload/stop, title, address, and loading state accurately follow the browser.
- The current address can be opened externally.
- Empty, loading, loaded, navigation-failure, and browser-process-failure states are understandable and recoverable.
- The last valid Preview URL is restored independently for each project after relaunch.
- Project switching, cancellation, disconnects, and stale asynchronous results do not leak state across projects.
- Unsupported schemes, privileged WebView integrations, permission requests, and downloads are blocked by policy.
- The panel remains usable in docked and narrow overlay layouts.
- The packaged build and full regression suite pass with no warnings.

## 14. Recommended first implementation slice

Start with a vertical discovery slice:

1. land protocol v15 and `preview.discover`;
2. implement and test the bounded host scanner;
3. show real discovered-server rows in the existing Preview panel;
4. keep selection temporarily disabled or routed to a small callback seam until the WebView surface lands.

This proves the new host boundary and produces visible UI progress without mixing network discovery, browser lifetime, and persistence into one change. The second slice can then connect a selected server and manual address to the secure WebView2 surface.
