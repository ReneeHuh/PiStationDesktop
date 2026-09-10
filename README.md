# Pi Station Desktop

[Install and recovery](Docs/INSTALL-AND-RECOVERY.md) · [Current implementation status](Docs/IMPLEMENTATION-STATUS-2026-09-05.md) · [Distribution TODOs](Docs/RELEASE-TODO.md)

PiStation is a Windows desktop tool for Pi only. Model providers and subagent workflows run through Pi; additional coding-agent runtimes are outside the product scope.

The [GitHub PR review milestone](Docs/PI-PR-REVIEW-2026-09-06.md) adds hosted patches, persistent inline drafts, review submission, discussion replies and resolve/reopen with stale-head guards and durable operation recovery. Native visual and authenticated GitHub acceptance remain pending.

[Sent attachments and citations](Docs/SENT-CONTENT-2026-09-07.md) retain files and source metadata after draft clearing and restart, with image preview, Windows open/save/copy actions and clickable citation sources. Native UI acceptance remains pending.

The [first Pi integration parity milestone](Docs/PI-INTEGRATION-MILESTONE-2026-09-05.md) adds configurable extension discovery and explicit paths, working inline skill invocation, current command metadata, supported extension UI, and idle runtime restart. The [resource and provider setup follow-up](Docs/PI-RESOURCES-AND-SETUP-2026-09-06.md) adds native resource toggles, project trust, provider status, custom models and guided Pi terminals. Real Pi 0.84.4 and 0.85.0 have passed offline and authenticated skill/resume checks. The [session management follow-up](Docs/PI-SESSION-MANAGEMENT-2026-09-06.md) adds independent session import/copy/fork, branch inspection and JSONL/HTML export. The [plan workflow follow-up](Docs/PI-PLAN-WORKFLOW-2026-09-06.md) adds enforced planning tools, native plan editing/approval, progress and recovery. The [agent workflow follow-up](Docs/PI-AGENT-WORKFLOWS-2026-09-06.md) adds native presets, single/parallel/chain delegation, child transcripts, targeted Stop and saved-context continuation. The [48-item completion audit](Docs/FEATURE-COMPLETION-AUDIT-2026-09-05.md) tracks the remaining local and full-product parity work.

The T3-inspired implementation now includes durable thread context and complete draft stashes, a project inbox, structured diff review, Markdown navigation, Pi setup/retry, recoverable hosting operations, and Release packaging tools. Production publisher/signing/feed configuration is deferred. Current visual and authenticated-provider acceptance remains outstanding; see the status document before treating older completion claims below as release certification.


Pi Station Desktop is a packaged, x64 WinUI 3 client for Pi. This folder is the application
solution; the parent workspace holds planning documents and reference source checkouts.

The implemented foundation includes the project boundaries, UI Automation contract, Pi RPC
runtime, a versioned application protocol, SQLite host metadata, authoritative ordered thread
projections, replay-safe command receipts, an authenticated loopback host, reconnectable client
runtime, approvals and structured questions, durable composer drafts, and host-owned draft
attachments carried into Pi turns. An authenticated, host-owned workspace filename search returns
only project-relative paths as the backend for composer file mentions. The WinUI workflow keeps
transport failures, Pi crashes, and uncertain commands as separate recovery states.

Protocol v9 introduced the authoritative thread-lifecycle backend: revisioned titles, archived and
pinned state, receipt-backed rename/archive/unarchive/pin/unpin commands, active-by-default lists,
and bounded title search. SQLite migrations preserve existing thread and Pi-session identities;
ClientRuntime exposes typed lifecycle/search operations, maintains a revision-safe metadata cache,
and refreshes tracked projects after reconnect. The WinUI app now has a T3-inspired, dark-first
semantic design-token layer with light and high-contrast variants plus reusable compact styles for
surfaces, controls, sidebar/transcript rows, status indicators, and the composer. Its integrated
T3-inspired frame replaces the generic `NavigationView` with a compact custom title bar, extracted
workspace shell, collapsible 260/52px project rail, centered 720px reading column, unified
project/thread hierarchy, and bottom settings/connection state. The custom title bar hosts the
single-line `ChatHeader` with project/thread context and live turn status, removing a duplicate
workspace bar. The extracted `ConversationTimeline` reads as a continuous document:
assistant messages are unboxed, user turns use restrained right-aligned bubbles, reasoning and
tools use compact disclosures, interactions remain semantically bounded, and metrics sit in quiet
footers. The extracted `ComposerSurface` combines the bounded expanding editor, file mentions,
horizontal attachment chips, compact model/reasoning selectors, accessible icon-led attachment and
Send actions, visible Stop state, and quiet configuration/draft status in one rounded shell aligned
with the transcript. A resizable workbench now docks beside the conversation on desktop widths and
overlays only the conversation on compact windows. Its Changes tab now shows the active Git branch,
upstream divergence, staged/working-tree/untracked file states, line totals, init/pull/branch/commit/
push workflows, managed worktree controls, refresh, and bounded diffs.
Its Files tab exposes the active thread workspace as a directory tree, supports ranked path and
case/whole-word/regex content search, opens multiple editable file tabs, reveals content-search
lines, saves through revision-conflict checks, renders Markdown, sandboxed HTML, PDF, image, audio,
and video previews, creates composer mentions by drag-and-drop, and launches workspace files in an
available external editor. A file picker can also open bounded files outside the workspace in a
strictly read-only preview. Selected source or diff ranges become bounded review-comment context
chips in the composer. Unsupported binary content, oversized files, missing files, and escaping
workspace paths are handled explicitly.
Its Terminal tab now manages host-owned PowerShell and Command
Prompt sessions with saved scrollback across host restart, subprocess labels, session switching,
start/stop/restart/close actions, durable clear, command and focused-viewport keyboard input, native copy/paste/select-all/clear context actions,
responsive size updates, and recursive split-right/split-down layouts for up to four independently
focused sessions. The complete pane tree, split orientations and ratios, active pane, and pane sessions
persist per project; every divider supports pointer, keyboard, and UI Automation resizing, and
terminal-native shortcuts create, cycle focus between, and close panes. Its viewport uses
the T3-derived Ghostty WebAssembly canvas renderer in a locked-down WebView2 surface. Its Preview
tab discovers browser-ready loopback development servers and opens HTTP/HTTPS addresses in a
separately locked-down WebView2 surface with address, back/forward, reload/stop, external-open,
loading, and recoverable failure controls. Preview sessions are scoped per project/thread and retain multiple
live WebView2 tabs with independent history plus responsive, desktop, tablet, phone, and freeform viewports.
Each tab persists a zoom level, system/light/dark page-color emulation, recent addresses, and an
isolated browser profile. Cookie import is explicit and bounded. Human-triggered screenshot,
recording, picture-in-picture, and element-annotation actions produce local artifacts; annotations
append bounded DOM context and upload their screenshot through the normal attachment path. DevTools
remain disabled until the user enables the explicit policy. Pi receives browser tools through a
trusted explicit extension. Each thread requires inspect-only or inspect-and-interact access;
its controller and browser documents remain available while another thread is selected. Pi can
create/reuse tabs, resize their CSS viewport, and set system/light/dark appearance. New tabs use
the shared defaults; agent targets stay pinned independently of human tab selection. Resize and
appearance operations confirm rendered state. Closing a tab, revoking permission, deleting its
thread or disconnecting cancels pending work. Agents with Interact access can evaluate JavaScript
with bounded JSON results and execution deadlines. Inspect access provides snapshots with page text,
element selectors, accessibility, console/network failures, action history and a PNG image.
See [agent browser behavior](Docs/BROWSER-AUTOMATION-LIFETIME-2026-09-09.md) and
[evaluation and snapshots](Docs/BROWSER-EVALUATION-AND-SNAPSHOTS-2026-09-09.md). Web messages
remain limited to one-use element-picker tokens; page permissions, downloads, host objects, and
implicit browser access stay blocked. The Agents tab now projects Pi structured-subagent
and workflow tools as a persisted hierarchy with live state, current activity, elapsed time,
tool/token usage, model, result/failure summaries, and an interrupt action where the parent Pi turn
can propagate cancellation. The
selected workbench tab persists locally. The header's Add action
menu creates real threads, Open launches the selected local folder, and the workspace status rail
shows local-project identity with a live Git summary. Sidebar collapse, workbench
visibility, selected tab, and preferred width survive relaunch. The sidebar includes visible stable
project paths, thread lifecycle state and relative time, debounced title search, active and archived shelves, inline rename, pin/unpin and
archive/restore actions, keyboard and screen-reader status, and conflict-safe refresh. Assistant
messages use native
CommonMark rendering with formatted prose, safe links, inert raw HTML, selectable text, and
syntax-highlighted code blocks with language labels and accessible copy actions. Protocol v10
introduced bounded tool arguments in the ordered projection. Native WinUI expanders keep active
reasoning and failed/running tools open, collapse completed work, and group adjacent tool calls per
turn into one compact activity surface with accessible state, arguments, and output. Packaged-app
journeys verify these UI slices and persistence. `ShellViewModel` now acts as the cross-feature
coordinator while focused workspace, thread, composer, Pi-configuration, connection/recovery, and
file-mention ViewModels own their observable presentation state; `ShellPage` binds to those child
models directly.

Protocol v21 completes the next T3-inspired productivity slice. The composer discovers Pi slash
commands and `$` skills, stashes and restores prompts, compacts context through Pi's native RPC,
quotes/cites responses, attaches selected diff or terminal output as visible context chips, previews
pasted/dropped images, and can submit steering or follow-up work in the background. The sidebar is
now an inbox with automatic settlement, manual reactivation, snooze, delete, extended multi-select,
explicit pinned ordering, unsent-draft and PR indicators, and generated titles that preserve manual
names. Project metadata includes confined icons, model/reasoning/runtime/workspace defaults, clean
default-branch auto-pull, removal, and runnable trusted `t3.json` scripts. Host-owned source-control
adapters use `gh`, `glab`, `bb`, and `az` for clone/publish and pull-request listing, creation,
comments, labels, reviewers, checks, reviews, merge/close, generated text, and thread linking.
Settings now routes across projects, Pi/runtime, source control, appearance, integrations,
diagnostics, usage, and updates, including bounded logs, resource telemetry, a native usage dashboard
with date/model/provider filters, token and cost charts, cache savings, historical rescan, public pricing
refresh, reconciled child-agent usage, and redacted JSON export. Open **Settings → Usage** or the
**Open Usage Dashboard** command. [Usage reporting and limits](Docs/USAGE-REPORTING-2026-09-10.md).

Settings is now a first-class sidebar destination with workspace-layout summary/reset, local
environment details, and About information. Transport, Pi-crash, uncertain-command, and runtime
notices share one recovery stack immediately above the composer. The visible transport test button
has moved into a diagnostics section created only for Debug FakePi UI-test runs, and the normal
driver contract verifies it cannot appear in ordinary launches. Intentional no-project, no-thread,
and empty-thread states guide the user instead of leaving the conversation blank; native project,
approval, question, and lifecycle interactions use the shared compact control treatment.
The shell now has explicit Narrow, Compact, Standard, and Wide layouts with synchronized page,
header, composer, and workbench behavior. A persisted Dark/Light/System appearance preference
applies immediately; System follows Windows high contrast. Dialogs restore focus to their invoking
sidebar control, while a DPI-aware compatibility journey verifies responsive bounds, minimum and
maximized windows, composer growth, and 100/150/200 percent typography profiles.

Protocol v11 adds the compact turn-completion footer. It shows host-measured elapsed time, aggregates
Pi-reported input/output/cache/reasoning token usage for the user turn, and compares the latest
trustworthy response usage with the active model's reported context window. Missing values are not
estimated, and persisted Pi timestamps and usage restore the metadata when a session is hydrated.

Protocol v12 adds authenticated, project-confined file previews. The host accepts only relative
paths inside the selected project, rejects traversal and reparse-point escapes, caps returned bytes,
and identifies binary and truncated results without exposing absolute paths to the UI.

Protocol v13 adds authenticated, read-only Git status and diff contracts. Git runs without a shell,
pager, prompts, external diff drivers, or text-conversion hooks; command time and output are bounded,
and repository paths are normalized to the selected project before reaching the client.

Protocol v14 adds authenticated, host-owned terminal sessions. The host bounds sessions per project,
input length, and retained output; clients can reconnect from sequence cursors and safely resume a
session snapshot plus subsequent output/state events. The Windows host uses ConPTY and applies real
column/row resizes. The default viewport adapts T3 Code's Ghostty VT WebAssembly engine and Canvas2D
surface, including scrollback, selection, Unicode width, keyboard/IME input, mouse reporting, OSC
links, alternate screens, and SGR styling. WinUI owns lifecycle, theme, accessibility projection,
the authenticated SignalR stream, and exact grid resizes across a small JSON bridge. Ghostty is the
only client-side terminal engine. The native Ctrl+F overlay searches Ghostty-rendered scrollback,
highlights all visible matches, supports previous/next navigation plus case and whole-word filters,
and exposes the same action through the terminal context menu. Settings offers a validated terminal
font-family and size selector; changes remeasure open Ghostty surfaces immediately, persist across
relaunch, and fall back to the default monospace stack when a requested font is unavailable or
proportional. Nested pane layouts support up to the host's four-session-per-project limit and collapse
cleanly as panes or their backing sessions are closed.

Protocol v15 adds authenticated local preview-server discovery. The host validates the selected
project, collects bounded loopback TCP candidates, and performs cancellation-aware, concurrent
HTTP/HTTPS header probes. Only HTML/XHTML endpoints and loopback-safe redirects are returned; the
host never proxies page content or receives browser navigation. WinUI owns the embedded browser,
permits only credential-free HTTP/HTTPS addresses, denies web permissions and downloads, blocks
privileged host integration, and ignores stale discovery results after project switches.

Protocol v48 adds terminal/process ownership to discovered preview servers, including
descendants launched through npm or other shell commands. Preview can show all host
servers or just servers owned by the current thread. **Settings → Integrations → Browser**
provides the system-browser/app-preview link preference, defaults for new tabs, and
profile create/rename/default/clear/remove controls shared across environment windows.
Incognito is available per tab. Existing tabs keep their settings and profile identity.
See [browser behavior and acceptance](Docs/BROWSER-PREFERENCES-AND-OWNERSHIP-2026-09-09.md).

Protocol v16 adds T3-style per-turn checkpoints to the main coding loop. Immediately before a turn,
the host snapshots the selected project with an isolated temporary Git index; after Pi settles, it
writes the result to a hidden `refs/pistation/checkpoints/...` commit without moving `HEAD`, changing
the user's index, or adding history to the current branch. Each completed turn shows an inline file
summary with additions/deletions and opens either that turn's patch, a single-file patch, or the full
thread patch in the Changes workbench. Revert is always explicitly confirmed. Its policy is coupled:
the workspace is restored to the selected pre-turn snapshot and Pi is rewound to the matching session
entry (or a fresh session before turn one); only after both operations succeed are newer checkpoint
metadata and conversation projection removed. A temporary recovery ref restores the workspace if Pi
rejects or fails the rewind. Revert deliberately discards newer messages, turn diffs, and uncommitted
workspace edits, and the confirmation states that it cannot be undone. Non-Git projects continue to
run Pi normally without checkpoint cards.

Protocol v17 completes the writable Git/worktree loop. The authenticated host now owns repository
initialization, paged local/remote branch discovery, branch creation and safe switching, fetch plus
fast-forward-only pull, selected/all-file commits, first-push upstream setup, push, and combined
commit/push. Mutations use persisted idempotent receipts, expected HEAD/branch/status guards,
repository-wide operation locks, bounded non-interactive Git processes, conflict/authentication/
dirty/diverged error states, and active-turn exclusion. New threads can use the project checkout or
a durable managed worktree created from a named local ref or a freshly fetched `origin` ref. Pi,
files, diffs, checkpoints, and terminals all resolve through that thread workspace. Removal verifies
thread ownership and requires the exact server path before force-discarding dirty files. Checked-in
`t3.json` files can set the default thread mode and worktree setup script; scripts require persisted
project trust, run in a visible thread terminal with `T3CODE_PROJECT_ROOT` and
`T3CODE_WORKTREE_PATH`, and persist pending/running/succeeded/failed state. The Changes workbench
exposes these Git actions, branch choices, per-file and aggregate additions/deletions, and confirmed
managed-worktree removal.

Protocol v18 completes the T3-style file workspace. Flattened, bounded host listings become a native
directory tree; separate path and content searches stay scoped to the selected thread worktree.
Text documents carry SHA-256 revisions, so atomic saves reject stale tabs instead of overwriting Pi
or an external editor. Thread-scoped tabs preserve dirty buffers while navigating, Markdown can
switch between source and rendered views, supported images use a separate bounded binary contract,
content results reveal their source line, file drags insert composer mentions, and Open in Editor
passes the contained absolute target only to a host-owned launcher.

Protocol v19 adds the shared command system. Stable command IDs now drive the command palette and
application shortcuts instead of duplicating handlers across views. The palette ranks local commands
and performs bounded host search across projects, local/remote branches, thread titles, and persisted
user/assistant messages; selecting a result restores its project/thread context and reveals messages.
Settings persists per-command shortcut overrides, validates key syntax, evaluates `!`, `&&`, `||`,
and parenthesized context conditions, and rejects bindings whose contexts can overlap. File save,
workbench navigation, thread navigation, Git actions, terminal pane operations, and preview actions
all participate in the same registry; focused text-entry behavior remains local to its editor.

Protocol v20 adds active-turn steering, follow-up, and observability. While Pi is streaming, the
composer can deliver the next draft as an immediate steering message or an ordered follow-up, with
the same attachment resolution and epoch/turn guards as ordinary turns. Pi `queue_update` events
project steering/follow-up contents, pending count, cleared/delivering state, and independent
one-at-a-time/all delivery modes; the UI exposes explicit follow-up, refresh, clear, and mode
controls. Structured subagent tool details become bounded, persisted agent/workflow events with
hierarchy, live activity, elapsed time, tool/token metrics, model, and result/failure summaries.
Pi currently exposes abort at the parent-turn boundary, so an agent interrupt stops that turn and
lets the extension propagate its abort signal to child processes.

The per-thread Pi configuration layer introduced in protocol v8 reads model
and thinking-level capabilities from Pi RPC, validates receipt-backed updates, persists desired
settings with optimistic revisions in SQLite, and reapplies them when the thread runtime restarts.
ClientRuntime caches each thread's revisioned capability snapshot, refreshes tracked snapshots after
reconnect, and distinguishes configuration conflicts, unsupported values, disconnected clients, and
uncertain dispatch. WinUI renders only the reported model and reasoning options, persists selections
per thread, and automatically selects reasoning `Off` for models that cannot reason. Runtime/
permission controls remain hidden until Pi advertises concrete options.

## Prerequisites

- Windows 11 x64 with Developer Mode enabled
- .NET SDK `10.0.400`
- WinApp CLI `0.6.1`
- Git available on `PATH` for the Changes workbench and per-turn checkpoints
- Microsoft Edge WebView2 Runtime for the Terminal and Preview workbenches
- Pi `0.84.4` or later, plus the compatible Node version declared by its package, for the optional
  real-Pi smoke test

## Build and test

```powershell
cd PiStationDesktop
pwsh .\Invoke-CodeTests.ps1
```

The code gate builds first, then runs each code-test project sequentially with bounded
test concurrency, a hang cutoff, and separate first-attempt TRX/JSON evidence. Use
`-NoBuild` after a current build, or `-Suite Host -Filter 'FullyQualifiedName~PiResources'`
for a focused run. Code/PR gates reject another gate in the same checkout; ordinary
`dotnet` commands and IDE/native runs are not intercepted. See [regression runner and
BUG-09 evidence](Docs/REGRESSION-GATE-2026-09-09.md) for opt-in limits and remaining qualification.

The pull-request entry point runs restore, a zero-warning build, the pinned T3/PiStation visual
contract, all code tests with TRX output, the driver contract, and eleven packaged-app journeys
covering draft persistence, vertical turns, crash recovery, approvals/questions, Pi configuration,
thread lifecycle, input/accessibility, recovery hardening, and the workbench:

```powershell
pwsh .\Invoke-PullRequestTests.ps1
```

## Launch

```powershell
winapp run .\src\PiStation.App\PiStation.App.csproj --configuration Debug --arch x64
```

## Agent tools and Windows PowerShell

Settings → Pi / runtime → **Agent tools and Windows PowerShell** now provides Pi
defaults, an explicit allowlist, or no tools, with exclusions taking precedence.
The Windows coding preset includes PowerShell. Save and connect, restart each idle
thread, then refresh its registered/active tool inventory; existing runtimes retain
their launch policy. Dedicated controls require Pi 0.85.0+ and operate access on
the selected host. PiStation child presets also respect dedicated restrictions.
Planning/approval checks still apply; this is not an OS sandbox or a restriction
on user-run terminals. See [tool configuration and verification](Docs/PI-TOOL-CONFIGURATION-2026-09-09.md).

## Shell and composer preferences

Settings → Appearance also provides independent interface, composer and code fonts/sizes, contrast and surface opacity, file/diff word wrap, unified/split diff layout, Git whitespace filtering and panel animation timing. Changes apply immediately and persist on this PC; panel motion respects Windows reduced motion. Terminal keeps its independent font controls. See [appearance and editor preferences](Docs/APPEARANCE-AND-EDITOR-PREFERENCES-2026-09-10.md) for defaults, limits and verification.

**Custom palettes** adds live preview, color editing, inspection of native UI areas, and a saved theme library. Save commits a preview; Cancel restores the saved palette. Import T3 themes, standalone VS Code JSON/JSONC themes, or Pi terminal colors, then export as T3 JSON. Light/dark variants and unmapped T3 colors survive export. Imports do not install extensions or change Pi's runtime theme. See [theme formats, recovery and verification](Docs/CUSTOM-PALETTES-AND-THEME-INTERCHANGE-2026-09-10.md).

Settings → Appearance → **Shell and composer** saves these preferences on this PC:

- **Ctrl+Q:** close the current window immediately, hold for 1.2 seconds and release Q (default), or press twice within 0.5 seconds. The title-bar close button and Alt+F4 retain normal behavior. Unsaved plan confirmation and draft/file recovery still apply.
- **Proactive panels:** off by default. When enabled, newly completed turns with changed-file checkpoints open their diff, and a newly linked PR shows its summary in Changes. Existing Files, Terminal, Preview, or Agents panels are not taken over; old completions are not replayed when switching threads.
- **Resting composer:** independent collapse-on-blur and collapse-while-reading-history switches, both on by default. Click the compact preview, or focus it and press Enter, to expand. Text, attachments, context, and Stop remain available; open composer dropdowns/flyouts and draft recovery conflicts keep the editor expanded.
- **Slash-menu skills:** on by default. Hiding skills from `/` suggestions does not hide them from `$` search.

Adding, dropping, or pasting HEIC/HEIF files converts the first image to JPEG using
the Windows HEIF/HEVC codec. Inputs are limited to 50 MiB and 100 megapixels; output
respects image orientation, fits within 4096 pixels per edge, and must fit within
10 MiB. A fresh JPEG is created without copying source metadata. The original file
is unchanged. Unsupported codecs or damaged files produce a recoverable error;
export a JPEG/PNG in Windows Photos if necessary. Uploads retain the original draft
owner if selection changes during conversion.

Native keyboard/focus, rendering, and picker/drop/clipboard acceptance for this
slice is still pending. See [the acceptance checklist](Docs/SHELL-COMPOSER-ACCEPTANCE.md)
and [the tracker](tracking.md) for the verified implementation boundary.

## Remote access

[Pi extension and session controls](Docs/PI-EXTENSIONS-AND-SESSIONS-2026-09-10.md) add in-process reload, loader attribution, tool batch controls, secret provider prompts, component/child adapters, rich HTML, explicit gist sharing, temporary local sessions and tree navigation. Live provider sign-in remains unverified by request.

Remote access uses protocol 56, including workspace diff whitespace filtering, subscription limit feeds, the usage dashboard, historical/pricing refresh, paged conversation history, activity/power policy, owned-process diagnostics and trace/metric settings, owned browser recording/artifact transfer and revision-aware runtime preferences, agent JavaScript evaluation and rich snapshots, independent thread browser controllers, agent open/resize/appearance operations, preview-server terminal ownership, saved terminal history, subprocess labels, shared grouped-checkout icons, tool-selection settings/inventory, persistent recovery, live project/thread catalogs, verified address changes, full-size text saves, and scoped preview forwarding. Install and update PiStation and Pi on each Windows computer manually. See [terminal and grouped-icon behavior](Docs/TERMINAL-AND-GROUPED-ICONS-2026-09-09.md), [browser preferences](Docs/BROWSER-PREFERENCES-AND-OWNERSHIP-2026-09-09.md), [browser automation](Docs/BROWSER-AUTOMATION-LIFETIME-2026-09-09.md), and [evaluation and snapshots](Docs/BROWSER-EVALUATION-AND-SNAPSHOTS-2026-09-09.md) for usage and limits. See [recording, cookie import and Pi preferences](Docs/BROWSER-RECORDING-AND-RUNTIME-PREFERENCES-2026-09-09.md) for the new controls, limits and validation. The [master tracker](tracking.md) records current scope and acceptance; [earlier remote implementation notes](Docs/REMOTE-ACCESS-IMPLEMENTATION.md) retain historical package/update tooling and qualification evidence.

PiStation can connect Windows desktops over a reachable LAN or VPN address. Open
**Settings → Connections** on the computer that owns the projects and Pi runtime:

1. Choose its network address and a port (default `52740`), then select **Start sharing**.
2. Choose **Operate** or **Read only**, optionally add a label, and choose the link
   lifetime (default five minutes, up to one day). Select **Create pairing link**.
3. On the other desktop, paste the link into **Settings → Connections**, enter a device
   name, and select **Pair device**.
4. Compare the six-digit verification code on both desktops. On the host, confirm
   that the codes match, then approve the device. Never approve by device name alone.
   On the receiving desktop, select the saved
   environment and **Open**. It opens in a separate window alongside local work.

Links can be used once and expire after the lifetime you selected. The link carries the host's
certificate fingerprint; subsequent HTTP and WebSocket connections must match it.
Device credentials and the host certificate's private key are protected by Windows.
Approvals last 180 days and survive restarts. Use **Revoke selected session** on the host
to disconnect an existing device and prevent reconnection. **Stop sharing** closes the
remote listener while local work continues; the sharing preference persists across launches.
Re-pair with a fresh link after access expires or is revoked, then select **Open**.
This verifies the saved credentials and switches an existing remote window in place,
preserving its client identity, drafts and dirty file editors. There is no need to
forget the environment first. Changes to Host address use Test connection and
Verify and save address, with rollback if validation or persistence fails.

**Unused pairing links** shows active links from Settings and the CLI, with their
labels, access level, and expiration. Select a link created during this Settings
visit to copy it or display its QR code. Leaving Settings clears the link secrets
and QR display; the unused invitations remain listed and revocable. Links created
elsewhere cannot be recovered here—create a new link to share. The display also
clears when a link is used, revoked, or expires. **Copy pairing link** requests that
Windows exclude the secret from clipboard history and cross-device sync; manually
copying selected text does not apply those restrictions. Treat both links and QR
codes as passwords.

**Revoke selected link** prevents redemption of that unused invitation; it does
not remove an already approved device's access. **Device sessions** includes both
paired devices and CLI-issued sessions. Select a session to see its access level,
expiration, active connections, recent activity, subject (when supplied by the CLI),
and ID, or revoke it. The lists
refresh while Settings is open, including changes made by the CLI.

Read-only viewers can inspect saved conversations and observe live activity, but
viewing never starts or restarts Pi. Starting a stopped runtime requires the host
or an Operate device. Pairing approval results are retained briefly after approval
so a poll crossing the request deadline can still complete.

Redacted transport diagnostics are saved under the app data root as
`remote-access.log` (with one bounded `.previous` file). Logs retain event metadata,
not bearer credentials, pairing links, request contents, or exception messages.

Keep the host's local window open. Windows Firewall must allow the chosen address/port
on the intended network; PiStation does not change firewall or router rules. A VPN
such as Tailscale can provide a reachable address, but PiStation does not configure it.
There is no hosted relay or Windows background-service installer in this release.

Projects, Pi, files, Git, and terminals run on the host. Attachments come from the
receiving computer and are uploaded. Folder/editor launch is local-only. Operate
devices can discover host-loopback preview servers and render them through a scoped
native forwarding route over direct HTTPS or SSH. Read-only devices can use manually
reachable preview URLs. **Forget** removes
the saved connection; host-side revocation removes authorization.

See [the implementation plan and T3 review](Docs/PISTATION-REMOTE-ACCESS-PLAN.md).

### SSH to a running Windows host

**Settings → Connections → SSH** connects to a Windows computer's already running
PiStation desktop/server. Install PiStation on that computer and start its desktop
or headless host first, under the Windows account used for SSH. The default data
directory is `%LOCALAPPDATA%\PiStationDesktop` for both. Attaching never takes
ownership of that host's lifetime.

Older saved SSH profiles keep their original `%LOCALAPPDATA%\PiStation\ssh-host`
directory. They are not silently switched to a different environment. Older
MSIX desktop data under the package's `LocalCache\Local\PiStationDesktop` is
reused in place by both hosts, without copying or merging databases. If multiple
desktop data directories exist, launch both with the same explicit `--data-root`.
The package disables file-write virtualization so shared data and project files
remain visible to the standalone host. New unvirtualized data persists after
uninstall; back it up and remove it separately when no longer needed.

On that computer, install/configure Windows OpenSSH Server and Pi separately.
The SSH account needs access to the projects and its own Pi/provider setup.
On the client, verify the host fingerprint,
and establish a working `ssh user@host` connection in a terminal first. PiStation
uses strict host-key checking and will not accept an unknown/changed key for you.
SSH config aliases, identity files, custom ports and jump hosts are handled by
the installed `ssh.exe`. The target picker reads named aliases (including config
Includes) and readable known-host entries; hashed entries cannot be listed.
Known-host ports are preserved, and an optional port field overrides SSH config.
Keys/agent are tried first; an authentication failure offers up to two in-app
password/passphrase attempts. The secret is held only for that connection and
passed to OpenSSH's password helper, never saved or placed in command arguments.

Enter the alias or `user@host` and, optionally, the running host's data directory. Select
**Connect and save**. The host is authenticated over SSH, and the forwarded
HTTPS/SignalR connection additionally checks its certificate and environment
identity. Profiles are Windows-protected; ephemeral ports and bearer credentials
are not saved on the client. No PiStation application port is exposed to the
network, but SSH itself must be reachable. Remote installation and update automation
are outside the current product scope.

To update, finish active work, update the software on each computer manually,
restart the host, then select **Open / retry**. Incompatible protocols are rejected;
both computers must run compatible PiStation builds. Preserve the host data directory
when updating. Database migrations are not automatically rolled back.

**Disconnect**, closing the remote window, or **Forget** closes the SSH connection
and leaves the host running. To run a headless host, start an installed server yourself:

```powershell
C:\Tools\PiStation\PiStation.Server.exe supervise
```

Such a separately running host is reused through a current-user Windows named
pipe and is never stopped by a client disconnect. Close it with Ctrl+C. This is
not a Windows service; it does not promise survival across host logout/reboot.
For a manual host deployment, publish and copy the entire output directory:

```powershell
dotnet publish .\src\PiStation.Server\PiStation.Server.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\ssh-host
```

Normal transport failures use persistent capped retries and re-establish
SSH at the same local endpoint. A forwarding-only failure preserves the healthy
control session and does not restart its host. **Open / retry** requests an immediate attempt;
explicit Disconnect stops automatic recovery.
Changed environment identity fails closed; check the target/data directory before
forgetting and re-adding it. An intentionally rotated host security identity
requires closing and reopening the window. Commands with uncertain outcomes are
not automatically resent.

### Pairing and authentication CLI

Run the matching `PiStation.Server.exe` as the Windows account that owns the host.
Every command accepts `--data-root PATH` (`--base-dir` is an alias). Omit it to use
the same default environment as the desktop. Commands operate on that environment
only; they do not administer another Windows account or an arbitrary network host.

With the desktop sharing, or a headless host running, create a single-use link and
terminal QR code:

```powershell
C:\Tools\PiStation\PiStation.Server.exe pair --label "Travel laptop" --access read-only
C:\Tools\PiStation\PiStation.Server.exe status --json
```

`pair` discovers the running host over its current-user pipe and performs a pinned,
authenticated readiness check. It uses the active sharing address when available.
A loopback-only link needs a separately configured tunnel to work on another
machine. For direct LAN/VPN access without the desktop, explicitly bind a local IP
and keep the process running:

```powershell
C:\Tools\PiStation\PiStation.Server.exe serve --host 192.168.1.20 --port 52740
```

Replace the example IP with an address assigned to the host. The default remains
loopback-only. Desktop and headless LAN sharing reuse the same protected certificate
for the same data root, preserving client pins when switching hosts at the same address.
This does not configure Windows Firewall, OpenSSH, Tailscale, a relay,
or a background service. Run `pair` in another terminal. On the receiving desktop,
paste the link into Remote Connections, then compare its verification code with
the host's pending request before approving:

```powershell
C:\Tools\PiStation\PiStation.Server.exe auth pairing pending
C:\Tools\PiStation\PiStation.Server.exe auth pairing approve REQUEST_ID --code 123456
```

Replace `REQUEST_ID` and the example code with the pending request and the code
shown by the receiving device. Desktop approval still works. Headless administration
also supports `auth pairing reject REQUEST_ID`.

The remaining T3-style commands work against the shared authentication database,
including while the environment is stopped:

```powershell
C:\Tools\PiStation\PiStation.Server.exe auth pairing create --ttl 10m --label "One-time setup" --json
C:\Tools\PiStation\PiStation.Server.exe auth pairing list --json
C:\Tools\PiStation\PiStation.Server.exe auth pairing revoke INVITATION_ID
C:\Tools\PiStation\PiStation.Server.exe auth session issue --ttl 1h --label "Automation" --subject "build-agent" --access read-only --token-only
C:\Tools\PiStation\PiStation.Server.exe auth session list --json
C:\Tools\PiStation\PiStation.Server.exe auth session revoke SESSION_ID
```

Pairing invitations default to five minutes (maximum one day). Issued sessions
default to 30 days (maximum 180 days); approved device pairings remain 180 days.
Both default to Operate unless `--access read-only` is supplied. PiStation retains
its ReadOnly/Operate policy rather than introducing a separate administrative
bearer scope. Authentication administration requires local filesystem access;
an issued token cannot call a remote authentication-administration API.

Tokens and pairing URLs are secrets, returned only on creation. List/status output
contains neither credentials nor their hashes. `--json` is available on all auth
commands; `--token-only` is exclusive to session issue and cannot be combined with
`--json`. `pair --no-qr` suppresses the QR. Offline invitation creation returns a
token without a URL unless both `--base-url HTTPS_ORIGIN` and `--certificate SHA256`
are supplied. For a running host, `--base-url` can specify a tunnel/reachable alias;
the discovered certificate pin is used unless explicitly overridden. A TLS-terminating
proxy requires its actual certificate fingerprint. Check the endpoint and pin out of band.

Session lists include both CLI-issued and paired devices. Revoking an unused
invitation does not revoke an already approved device. Revoke its session instead.
Revocation is checked against SQLite for each new authenticated request and hub
invocation; a one-second polling interval also disconnects idle/streaming connections
after another process revokes them. An operation already in progress is not rolled back.
Invitations and pending results survive normal restarts until expiry; explicitly
stopping desktop sharing clears invitations/pending requests, but keeps device grants.
CLI revocation does not revoke the Windows SSH login or its host bootstrap identity.
Use `--help` for the complete command reference.

## Verify the UI Automation contract

The driver check builds and launches the packaged app, verifies the baseline Automation IDs and
empty states, exercises the first-class Settings shell, proves test diagnostics are absent, opens
the app-owned Add Project dialog, verifies its controls, and writes diagnostic artifacts.

```powershell
pwsh .\tests\PiStation.UiTests\Invoke-DriverContract.ps1
```

The script stops only the exact process ID returned by `winapp run`.

## Verify the visual contract

The static visual gate pins the T3 reference screenshot and commit by SHA-256, records the three
review sizes and shell geometry, maps twenty-four named PiStation states to packaged-journey artifacts, checks
semantic resources across dark, light, and high-contrast themes, and rejects repeated hard-coded
colors in view XAML. The normal driver additionally fails if a test-only fault control is exposed.

```powershell
pwsh .\tests\PiStation.UiTests\Test-VisualContract.ps1
```

## Run the draft and file-mention journey

This packaged-app journey verifies the debounced project-file picker, inserts a relative `@` file
mention, preserves distinct drafts while switching threads, and restores the active draft after an
application relaunch. It retains artifacts beneath
`tests\PiStation.UiTests\artifacts\draft-runs`.

```powershell
pwsh .\tests\PiStation.UiTests\Invoke-DraftSlice.ps1
```

## Run the approval and question journey

This deterministic journey renders FakePi approval and structured-question requests inline,
submits one response for each request, disables the resolved controls, and verifies that FakePi
received the selected responses. It retains artifacts beneath
`tests\PiStation.UiTests\artifacts\interaction-runs`.

```powershell
pwsh .\tests\PiStation.UiTests\Invoke-InteractionSlice.ps1
```

## Run the deterministic desktop vertical slice

The black-box journey launches the packaged app with FakePi, adds an isolated fixture project,
creates threads, observes incremental tool and assistant output, verifies collapsible reasoning,
grouped tool arguments/output, and streamed native Markdown with highlighted code, accessible copy
feedback, and inert raw HTML. It then reopens a thread,
relaunches the process, and verifies session hydration. It writes a JSONL app log, UI tree,
screenshots, and a write manifest beneath `tests\PiStation.UiTests\artifacts\runs`.

```powershell
pwsh .\tests\PiStation.UiTests\Invoke-VerticalSlice.ps1
```

## Run the crash-recovery journey

This deterministic journey makes FakePi crash after accepting a prompt, verifies that the app shows
the Pi-specific recovery state instead of a transport error, restarts the same thread runtime, and
then completes a second prompt. Success and failure artifacts are retained beneath
`tests\PiStation.UiTests\artifacts\recovery-runs`.

```powershell
pwsh .\tests\PiStation.UiTests\Invoke-RecoverySlice.ps1
```

## Run the connection and thread hardening journey

This packaged-app journey runs two active FakePi threads without cross-talk, stops each turn,
disconnects and reconnects the real SignalR client while Pi continues, recovers through a bounded
journal snapshot, and interrupts an in-flight dispatch to verify that the UI presents its uncertain
receipt without resending the prompt. The transport-fault control and reduced journal limit are
available only to Debug FakePi UI-test launches.

```powershell
pwsh .\tests\PiStation.UiTests\Invoke-HardeningSlice.ps1
```

## Run the input and accessibility journey

This packaged-app journey verifies the app-owned project dialog's keyboard focus order and cancel
path, Enter-to-send and Shift+Enter multiline input, exact Unicode/quoted/long prompt dispatch,
disabled mutation controls while busy or disconnected, long-transcript scrolling, thread-action
flyout identities, Escape behavior, and accessible names/control types. It retains artifacts beneath
`tests\PiStation.UiTests\artifacts\input-accessibility-runs`.

```powershell
pwsh .\tests\PiStation.UiTests\Invoke-InputAccessibilitySlice.ps1
```

## Run the workbench journey

This packaged-app journey verifies Git branch/status/diff rendering; project-file discovery,
filtering, selection, and text previews; a real host-owned terminal command lifecycle; embedded
Preview navigation and the connected Agents empty state; terminal scrollback search, result navigation, and case filtering;
split-right/split-down terminal panes with isolated output and focus-driven session targeting; live
pointer/UI Automation divider resizing, per-project split persistence, stale-session fallback, and
terminal shortcut resolution; live terminal-font changes, fallback-safe sizing, and persistence; real header thread creation;
accessible width changes;
docked and compact-overlay geometry; sidebar containment; and persistence
of the collapsed sidebar, open panel, selected Agents tab, and preferred width across relaunch. It
retains default and compact screenshots beneath `tests\PiStation.UiTests\artifacts\workbench-runs`.

```powershell
pwsh .\tests\PiStation.UiTests\Invoke-WorkbenchSlice.ps1
```

The workbench journey exercises the Ghostty/WebView2 renderer and retains terminal-search,
terminal-font-settings, and both adjustable terminal split-orientation screenshots.

## Run the responsive compatibility journey

This DPI-aware packaged journey verifies Narrow, Compact, Standard, Wide, and maximized layouts;
sidebar and workbench containment; the 720 px reading column; bounded multiline composer growth;
focus restoration; persisted Dark/Light/System themes; and 100%, 150%, and 200% app-owned typography
profiles. It retains review screenshots beneath
`tests\PiStation.UiTests\artifacts\compatibility-runs`.

```powershell
pwsh .\tests\PiStation.UiTests\Invoke-CompatibilitySlice.ps1
```

## Run the Pi-configuration journey

This packaged-app journey selects a reasoning level, switches to a non-reasoning model, verifies
the capability-driven `Off` fallback, relaunches the app, and confirms the revisioned per-thread
selection persisted. Runtime-mode controls are also verified absent while FakePi reports none.

```powershell
pwsh .\tests\PiStation.UiTests\Invoke-PiConfigurationSlice.ps1
```

## Run the thread-lifecycle journey

This packaged-app journey renames and pins a thread, exercises matching and empty title searches,
archives and restores it through the archived shelf, relaunches the app to verify persistence, and
then unpins it. UI-tree, screenshot, log, and write-manifest artifacts are retained beneath
`tests\PiStation.UiTests\artifacts\thread-lifecycle-runs`.

```powershell
pwsh .\tests\PiStation.UiTests\Invoke-ThreadLifecycleSlice.ps1
```

## Local host behavior

The embedded host binds HTTP only to `127.0.0.1` on an ephemeral port and requires a high-entropy
per-process bearer credential supplied in memory. The credential is never placed in a URL. Host
metadata is stored in `host.db`, while Pi remains the transcript authority through session JSONL
under the configured application data root. Draft attachments stream to authenticated HTTP,
receive durable IDs, are SHA-256 checked, and are copied beneath the host `attachments` directory.
The default limits are eight attachments per draft, 10 MB per image, and 50 MB per generic file.
Turn commands reference the exact durable draft revision and attachment IDs. PNG, JPEG, GIF, and
WebP files are sent through Pi's native image blocks; every attachment also appears in a structured
host-path manifest so Pi can access generic files. After Pi accepts the turn, a receipt-led clear
atomically removes only the sent draft revision and its files. Transcript hydration replaces the
internal manifest with a filename-only attachment summary, so host paths are not shown in the UI.

Workspace filename search is exposed through the `file.search` capability. Searches are bounded,
deterministically ranked, confined to the selected project root, and skip reparse points plus common
generated/vendor trees. Results contain forward-slash relative paths only. Typing `@` in the
composer opens a debounced project-file picker with keyboard and mouse selection; the selected path
is inserted as an inline mention and quoted when it contains whitespace.

The host integration suite launches the real FakePi child process through Kestrel and SignalR and
verifies streaming, Stop, reconnect cursors, command idempotency, conflict rejection, and restart
hydration:

```powershell
dotnet test .\tests\PiStation.Host.Tests\PiStation.Host.Tests.csproj
```

## Run the optional real-Pi smoke test

The normal suite uses the deterministic FakePi executable. To exercise an installed Pi, including
session resume, opt in explicitly. The configured Pi provider must already be usable.

```powershell
$env:PISTATION_RUN_REAL_PI = '1'
$env:PISTATION_PI_PATH = 'C:\path\to\pi.cmd' # optional when discovery can find Pi
dotnet test .\tests\PiStation.PiRpc.Tests\PiStation.PiRpc.Tests.csproj --filter Category=RealPi
```

The smoke harness rejects Pi versions below `0.84.4` and keeps its session files under an isolated
temporary data root. The MVP baseline passed this test with Pi `0.84.4` on September 1, 2026.

## Continuous integration

`.github/workflows/pistationdesktop-pr.yml` provisions the pinned .NET, WinApp CLI, and Pester
versions on `windows-2025`, runs `Invoke-PullRequestTests.ps1`, and always uploads TRX results plus
the synthetic UI diagnostics. The workflow still needs successful repository runs before the
runner-specific UI Automation requirement can be considered proven.

Reliability and diagnostics implementation, T3 references, limits, and validation: [protocol-53 milestone](Docs/RELIABILITY-AND-DIAGNOSTICS-2026-09-10.md).

**Settings → Limits** shows subscription quota windows, reset countdowns, and consumption pace from CLIProxyAPI hubs and Pi extension quota feeds. Hub keys are encrypted for the Windows user running the host. Unsupported and stale quotas remain explicit. See [subscription limits, hub setup, and the Pi publisher contract](Docs/SUBSCRIPTION-LIMITS-2026-09-10.md).
