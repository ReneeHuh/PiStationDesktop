# Pi Station Desktop MVP Plan

Status: MVP implemented and validated; automated suite and real-Pi smoke pass  
Last reviewed: 2026-09-02

This plan turns `PI-AGENT-CALLING-PLAN.md` and `WINAPP-UI-TESTING-PLAN.md` into the
smallest useful, testable Windows application. The two larger plans remain the source of truth for
the eventual product. This document defines only the first shippable vertical slice.

### Product naming

- Product and window title: **Pi Station Desktop**.
- Application root and solution: `PiStationDesktop/PiStationDesktop.slnx`.
- Project and root namespace prefix: `PiStation`.
- Packaged executable and assembly name: `PiStationDesktop` (`PiStationDesktop.exe`).
- Package identity: reserve a stable reverse-DNS-style identifier before external distribution;
  do not derive persistence paths from the display name.

## 1. MVP decision

Build a **packaged, x64 WinUI 3 application on .NET 10**. The desktop process contains an embedded
ASP.NET Core/Kestrel host, and the WinUI client reaches it through an authenticated loopback
HTTP/SignalR connection. The host launches Pi as a child Node process in `--mode rpc` and
exchanges strict JSONL over redirected standard input and output.

Loopback HTTP is a deliberate MVP-only transport choice. The host binds only to `127.0.0.1` on an
ephemeral port and requires a high-entropy per-process bearer credential supplied directly to
ClientRuntime in memory. The credential never appears in a URL or log. Any future non-loopback
endpoint requires HTTPS and the stable certificate/pairing design from the parent plan.

Use the five production projects already identified by the architecture plan. They are small at the
start, but their separate project references make the important boundaries enforceable:

```text
PiStation.App ─────────────► PiStation.ClientRuntime ───► PiStation.Protocol
    │
    └──────────────────────► PiStation.Host ────────────► PiStation.Protocol
                                      │
                                      └───────────────► PiStation.PiRpc
```

This is a modular monolith: one installed desktop application and one OS process, except for the
child Pi process. It is not a Windows service and does not have a separately deployed server.

## 2. Options considered

### Option A — direct Pi process from the WinUI ViewModel

Projects: `PiStation.App` and perhaps `PiStation.PiRpc`.

Advantages:

- Fastest way to put streamed Pi text in a window.
- Few projects and little initial hosting code.

Costs:

- Couples UI lifetime to Pi lifetime.
- Creates a second implementation path when remote environments are added.
- Makes reconnect, restart, idempotency, and deterministic host tests difficult.
- Must be substantially rewritten after the demo.

Use this only for a disposable one-day spike, not for the repository MVP.

### Option B — in-process modular host through loopback SignalR (recommended)

Projects: the five production projects in this plan.

Advantages:

- Proves the same client/host boundary needed for a remote environment.
- Keeps Pi JSON, processes, persistence, and UI concerns isolated.
- Supports fake-Pi integration tests without UI and black-box UI tests without a real provider.
- Remains one application to build, launch, debug, and distribute.

Costs:

- Requires the protocol and embedded-host seams before the first complete UI flow.
- Has more project scaffolding than a direct prototype.

This is the best tradeoff because it is still an MVP but is not throwaway work.

### Option C — separate host executable or Windows service

Projects: the five projects plus a host executable/service and IPC/service-management code.

Advantages:

- Host can survive a desktop UI process exit independently.
- Cleaner isolation for an always-on remote environment.

Costs:

- Installation, updates, service identity, permissions, logging, debugging, and lifecycle become
  first-release problems.
- Significantly expands UI-test setup and cleanup.

Defer this until there is evidence that notification-area lifetime in the desktop process is
insufficient.

### Pi integration alternatives

The supported MVP boundary is a child process using `pi --mode rpc`. Embedding Pi's TypeScript API
would require a Node sidecar API of our own and would duplicate the RPC boundary. Reimplementing Pi
in C# is out of scope. The child-process RPC protocol is already designed for custom UIs and is the
least risky integration surface.

## 3. Exact MVP scope

The user can:

1. Launch the app and see whether the local environment and Pi are ready.
2. Add one local project directory.
3. Create and select a thread in that project.
4. Enter a multiline prompt and send it.
5. See assistant text appear incrementally.
6. See a compact tool activity row move from running to completed or failed.
7. Stop the current turn.
8. Close and reopen the thread without losing or duplicating the transcript.
9. Close and relaunch the app, then resume the same durable Pi session.
10. See actionable states for Pi-not-found, unsupported-version, process-crash, and connection-loss
    failures.

The implementation may store several projects and threads, but the acceptance journey needs only
one project, one thread, and one active Pi process.

### Explicitly excluded

- Remote computers, TLS pairing UX, device management, and authorization scopes.
- Multiple simultaneous clients.
- Model/provider setup, model switching, thinking controls, and credential management.
- Steering, follow-up queues, compaction controls, branching, cloning, and session export.
- Extension dialogs, permission interception, and custom Pi extensions. The launcher disables
  discovered user/project extensions with `--no-extensions`; an unexpected blocking extension UI
  request is cancelled defensively instead of being ignored.
- Rich Markdown, syntax highlighting, diff rendering, terminal emulation, images, and attachments.
- Notification-area hosting, automatic updates, Store publishing, and ARM64.
- Full load, slow-client, accessibility matrix, and visual-regression suites.

The underlying contracts should remain extensible for these features, but the MVP must not build
unused UI or speculative infrastructure for them.

Historical-scope note (2026-09-02): the list above records what was excluded when the MVP was
accepted. Post-MVP work has since added typed extension approvals/questions, durable image and
generic file attachments, and composer file mentions. Those additions do not retroactively change
the MVP acceptance boundary.

## 4. Technology baseline

- .NET SDK: `10.0.400`, pinned by `global.json` for the initial build.
- Target framework: `net10.0-windows10.0.26100.0` for the WinUI project.
- Minimum product OS for the MVP: Windows 11 x64; choose the precise minimum build in the app
  manifest before packaging outside the developer machine.
- Windows App SDK: stable channel, initially `2.4.0`, centrally pinned.
- Packaging: the single-project packaged WinUI template; do not add a separate packaging project.
- Hosting: ASP.NET Core 10 Kestrel and SignalR, JSON protocol first.
- Minimum supported Pi version: `0.84.4` for the MVP compatibility range.
- Persistence: `Microsoft.Data.Sqlite`; use direct, explicit SQL for the small MVP schema.
- UI pattern: MVVM with `CommunityToolkit.Mvvm`; no UI framework abstraction layer.
- JSON: `System.Text.Json` with source-generated contexts for network and known Pi records.
- .NET tests: xUnit with the normal `dotnet test` runner.
- UI tests: PowerShell 7, Pester, and a pinned `winapp ui` release after the driver spike passes.

All NuGet versions belong in `Directory.Packages.props`. Common compiler and analyzer settings
belong in `Directory.Build.props`.

### Current workstation readiness

- .NET SDK `10.0.400` and the ASP.NET Core 10 runtime are installed.
- A self-contained Pi `0.84.1` installation with compatible bundled Node `22.23.2` is installed,
  but that Pi version is below the MVP compatibility floor and must be upgraded before the real-Pi
  smoke test.
- The checked-in Pi source is `0.84.4` and requires Node `>=22.19.0`.
- The global Node is `22.14.0`, so it is not compatible with the checked-in Pi source.
- The WinUI `dotnet new` templates and `winapp` CLI are not installed yet.

Increment 0 must install the official stable WinUI template pack and a reviewed `winapp` build
before scaffolding or attempting the driver-contract spike. Machine-wide installation remains an
explicit developer step, not a side effect of a normal build or test command.

## 5. Solution and repository layout

The application is isolated under `PiStationDesktop/`. The parent workspace keeps the planning
documents and `Core/` reference checkouts outside the buildable application tree.

```text
PiStationDesktop/
  PiStationDesktop.slnx
  global.json
  Directory.Build.props
  Directory.Packages.props
  README.md

  src/
    PiStation.App/
      PiStation.App.csproj
      Package.appxmanifest
      App.xaml
      App.xaml.cs
      MainWindow.xaml
      MainWindow.xaml.cs
      Composition/
        AppBootstrapper.cs
      Views/
        ShellPage.xaml
        ShellPage.xaml.cs
      ViewModels/
        ShellViewModel.cs
        ProjectListViewModel.cs
        ThreadViewModel.cs
      Controls/
        TranscriptItemControl.xaml
        ToolActivityControl.xaml
      Converters/
      Assets/

    PiStation.ClientRuntime/
      PiStation.ClientRuntime.csproj
      IEnvironmentClient.cs
      EnvironmentClient.cs
      ConnectionSupervisor.cs
      ThreadSubscription.cs
      ProjectionStore.cs
      ClientRuntimeOptions.cs

    PiStation.Protocol/
      PiStation.Protocol.csproj
      Identifiers/
      Commands/
      Receipts/
      Projections/
      Streaming/
      Errors/
      Serialization/
        ProtocolJsonContext.cs
      ProtocolVersion.cs

    PiStation.Host/
      PiStation.Host.csproj
      Hosting/
        EmbeddedEnvironmentHost.cs
      Hubs/
        EnvironmentHub.cs
      Threads/
        PiThreadController.cs
        PiThreadRegistry.cs
        ThreadProjectionReducer.cs
      Projects/
        ProjectService.cs
      Persistence/
        HostDatabase.cs
        Migrations/
      Security/
        LoopbackAuthentication.cs
      HostOptions.cs

    PiStation.PiRpc/
      PiStation.PiRpc.csproj
      Discovery/
        PiLocator.cs
        PiInstallation.cs
      Process/
        PiProcess.cs
        PiProcessLauncher.cs
      Transport/
        PiRpcConnection.cs
        JsonlRecordReader.cs
      Wire/
        Commands/
        Responses/
        Events/
        PiJsonContext.cs
      Decoding/
        PiEventDecoder.cs
        PiStreamAssembler.cs
      Diagnostics/

  tests/
    PiStation.Protocol.Tests/
      PiStation.Protocol.Tests.csproj
    PiStation.PiRpc.Tests/
      PiStation.PiRpc.Tests.csproj
    PiStation.Host.Tests/
      PiStation.Host.Tests.csproj
    PiStation.ClientRuntime.Tests/
      PiStation.ClientRuntime.Tests.csproj

    PiStation.FakePi/
      PiStation.FakePi.csproj
      Program.cs
      Scenarios/

    PiStation.UiTests/
      README.md
      Invoke-UiTests.ps1
      Bootstrap-UiTestTools.ps1
      PiStation.UiTests.psd1
      tool-versions.json
      lib/
        WinAppUi.psm1
        UiTestEnvironment.psm1
      scenarios/
        00.DriverContract.Tests.ps1
        10.Launch.Tests.ps1
        20.LocalVerticalSlice.Tests.ps1
        30.ThreadLifecycle.Tests.ps1
        40.ErrorRecovery.Tests.ps1
      fixtures/
        projects/minimal-project/
        fake-pi/scripts/
      artifacts/
        .gitignore
```

`PiStation.FakePi` is a deterministic console executable, not a mock hidden inside a unit-test
assembly. Both host integration tests and black-box UI tests launch the same fixture through the
real `PiStation.PiRpc` process boundary.

## 6. Project responsibilities and allowed references

| Project | Owns | May reference | Must not reference |
| --- | --- | --- | --- |
| `PiStation.App` | XAML, windows, ViewModels, composition | `ClientRuntime`, `Host` | Pi wire types, SQLite |
| `PiStation.ClientRuntime` | connection, subscription, client projection cache | `Protocol` | WinUI, PiRpc, host storage |
| `PiStation.Protocol` | versioned application DTOs | BCL only | WinUI, SignalR implementation, Pi wire DTOs |
| `PiStation.Host` | projects, threads, projection, Kestrel hub, persistence | `Protocol`, `PiRpc` | WinUI |
| `PiStation.PiRpc` | Pi discovery, child process, JSONL, Pi decoding | BCL only | WinUI, SignalR, SQLite, application DTOs |

Test projects reference only the production layers they exercise. `PiStation.UiTests` references no
production assembly; it observes the built app through Windows UI Automation.

## 7. Initial WinUI layout

Use ordinary WinUI controls with good UI Automation support. The prompt editor should be a
multiline `TextBox`, not a `RichEditBox`, for the MVP.

```text
┌──────────────────────────────────────────────────────────────────────┐
│ Pi Station Desktop                                  Local • Connected │
├──────────────────────┬───────────────────────────────────────────────┤
│ Projects             │ Project / Thread                    [Running] │
│  + Add project       ├───────────────────────────────────────────────┤
│  ▾ My Project        │                                               │
│     + New thread     │  You                                          │
│     Thread 1         │  Explain this project                          │
│                      │                                               │
│                      │  Pi                                           │
│                      │  Streaming response…                           │
│                      │                                               │
│                      │  ▸ bash                         Completed       │
│                      │                                               │
│                      ├───────────────────────────────────────────────┤
│                      │ [Ask Pi…                                  ]   │
│                      │                                  [Stop] [Send] │
└──────────────────────┴───────────────────────────────────────────────┘
```

Recommended control structure:

- `MainWindow` contains one `ShellPage`.
- A left `NavigationView` pane contains projects and threads.
- The content area uses a `Grid` with header, `InfoBar`, transcript `ListView`, and composer rows.
- Transcript items use a small typed template set: user message, assistant message, and tool row.
- `ProgressRing` indicates launch/hydration; text always states the same status accessibly.
- `InfoBar` presents actionable runtime and connection errors.
- Keep tool output collapsed and byte-bounded in the MVP.

Minimum automation contract:

| Element | Automation ID |
| --- | --- |
| Main window | `AppMainWindow` |
| Loading view | `AppLoadingView` |
| Local environment state | `ConnectionStatusText` |
| Project list | `ProjectSelector` |
| Add project | `NewProjectButton` |
| Project path in add dialog | `ProjectPathInput` |
| Confirm add project | `AddProjectConfirmButton` |
| Thread list | `ThreadTabList` |
| Thread search | `ThreadSearchInput` |
| Clear thread search | `ClearThreadSearchButton` |
| Active/archived shelf | `ArchivedThreadsToggle` |
| Thread-list status | `ThreadListStatusText` |
| Thread actions | `ThreadActionsButton` |
| Inline thread rename | `ThreadRenameInput` |
| Create thread | `NewThreadButton` |
| Active thread | `ActiveThreadView` |
| Transcript | `TranscriptList` |
| Markdown message body | `MarkdownMessageBody` |
| Markdown code block | `MarkdownCodeBlock` |
| Markdown code copy | `MarkdownCodeCopyButton` |
| Latest assistant content | `LatestAssistantMessage` |
| Tool activity | `ToolActivityList` |
| Prompt | `PromptInput` |
| Send | `SendPromptButton` |
| Stop | `StopTurnButton` |
| Turn state | `TurnStatusText` |
| Runtime error | `RuntimeErrorBanner` |
| Reconnect | `ReconnectButton` |

Add the IDs and accessible names when the controls are first created. Do not postpone this until
the UI test phase.

## 8. Runtime flow

### Startup

1. Parse normal settings and non-production test switches.
2. Create the application data root and initialize the SQLite schema.
3. Create a stable `EnvironmentId` if this is the first launch.
4. Start Kestrel on `127.0.0.1` and an available ephemeral port over HTTP. Generate a high-entropy
   per-process bearer credential and pass the resolved address and credential directly to
   ClientRuntime in memory. Reject unauthenticated requests, and never place the credential in a URL
   or log.
5. Start `ConnectionSupervisor`, authenticate, read the environment descriptor, and list projects
   and threads.
6. Display the ready state. Do not start Pi yet.

### First prompt

1. The user chooses a project, creates a thread, enters text, and invokes Send.
2. `IEnvironmentClient` sends `ThreadStartTurn` with a new `CommandId`.
3. The host persists the thread/session mapping and command receipt before launching or writing.
4. `PiLocator` resolves the Pi package's `package.json`, its `bin.pi` entry, and a compatible
   `node.exe` without invoking `cmd.exe`.
5. `PiProcessLauncher` starts
   `node.exe <resolved-cli> --mode rpc --session-dir <host-session-root> --session-id <id> --no-extensions`
   with the project as its working directory. The host session root is canonical, environment-owned,
   and below the application data root.
6. `PiRpcConnection` sends the prompt, correlates its acceptance response, and concurrently reads
   events.
7. The host normalizes Pi events into a `ThreadProjection` and sequenced envelopes.
8. ClientRuntime applies envelopes to its projection store; the ViewModel renders that state.
9. `agent_settled` completes the turn. Prompt acceptance alone does not complete it.

### Reopen and resume

1. Closing or navigating away from a thread only cancels that client subscription.
2. Reopening subscribes from the last `(ProjectionEpoch, Sequence)` cursor.
3. App restart reloads the thread-to-Pi-session mapping from SQLite.
4. Selecting the thread lazily launches Pi with the saved session and the same `--session-dir`,
   hydrates entries, publishes a reconciled snapshot, and only then enables Send. If an incremental
   `get_entries(since)` cursor is rejected as missing, discard it and perform a full hydration before
   publishing the replacement snapshot.

## 9. Minimal contracts

Implement only these network operations initially:

```text
GetEnvironmentDescriptor
ListProjects
AddProject
ListThreads
CreateThread
ExecuteThreadCommand
GetCommandReceipt
SubscribeThread
```

Implement only these application commands:

```text
ThreadStartTurn
ThreadStopTurn
```

Implement these normalized events/projection changes:

```text
RuntimeStateChanged
TurnStarted
MessageStarted
ContentDelta
MessageCompleted
ToolStarted
ToolOutputReplaced
ToolCompleted
TurnSettled
RuntimeFailed
```

The DTO shapes should remain discriminated unions so later command/event cases are additive. Do not
add a generic raw-command escape hatch.

## 10. Persistence for the MVP

SQLite tables:

```text
Environment(EnvironmentId, Name, CreatedUtc)
Projects(ProjectId, CanonicalPath, DisplayName, CreatedUtc)
Threads(ThreadId, ProjectId, PiSessionId, PiSessionFile, Title, CreatedUtc, UpdatedUtc)
CommandReceipts(ClientId, CommandId, ThreadId, BodyHash, State, ErrorCode, CreatedUtc, UpdatedUtc)
```

Pi's session JSONL remains the transcript authority. SQLite stores identity, mappings, and receipts;
it does not store every streamed token. An in-memory projection and bounded event journal are enough
for the first running process. Rebuild the projection from Pi entries after restart.

Every Pi process receives `--session-dir <host-session-root>`, where the canonical root is below the
environment's application data directory. UI tests therefore keep Pi session JSONL under their
unique `--data-root`, and production sessions remain owned by the environment host instead of being
written to the user's default Pi session directory. Persist the exact `sessionFile` returned by
`get_state`; do not derive Pi's timestamped filename.

## 11. Pi discovery rule for this machine and for production

The current installed Pi is `0.84.1` under
`%LOCALAPPDATA%\pi-node\current`, with its own Node `22.23.2`. The repository Pi checkout is
`0.84.4` and declares Node `>=22.19.0`, while the globally visible Node is older than that minimum.

The MVP compatibility floor is Pi `0.84.4`. Version `0.84.4` adds the `clear_queue` RPC operation
required by the MVP Stop behavior, fixes Windows shell-abort failure when `taskkill.exe` is
unavailable on `PATH`, and fixes session corruption when resuming a JSONL file without a trailing
newline. The application reports `PiVersionUnsupported` with upgrade guidance for older versions;
it never updates the user's Pi installation automatically.

The launcher must therefore:

1. Accept an explicit Pi path first, including the fake Pi test executable.
2. Launch an explicit or discovered native executable directly after validation.
3. Resolve a discovered `pi.cmd` to the adjacent package root.
4. Read and validate `package.json` and its current `bin.pi` value instead of assuming a fixed
   `dist/cli.js` path.
5. Prefer a `node.exe` adjacent to the shim/package installation when it satisfies `engines.node`.
6. Fall back to `PATH` only when the resolved Node version is compatible.
7. Start Node directly with `ProcessStartInfo.ArgumentList`, redirected UTF-8 pipes, and no shell.
8. Configure stdin with `new UTF8Encoding(false, true)`: no BOM and invalid-byte detection. Use an
   equally strict UTF-8 decoder for stdout and stderr.
9. Report a setup error containing the attempted locations and versions, but no secrets.

This logic supports both the installed package layout and the checked-out package layout without
hardcoding either one.

## 12. Test strategy

### Layer 1 — protocol tests

- Identifier and discriminated-union JSON round trips.
- Unknown event compatibility.
- Required-field and version rejection.

### Layer 2 — PiRpc tests with `PiStation.FakePi`

- Pi `0.84.1` is rejected, Pi `0.84.4` is accepted, and a compatible adjacent bundled Node wins
  over an incompatible global Node.
- Split and combined LF records, including split UTF-8. The reader splits only on LF, accepts CR
  only immediately before LF, rejects malformed UTF-8, and treats an unterminated EOF record as a
  connection fault; it does not use `StreamReader.ReadLine()`.
- BOM-less UTF-8 stdin writes, including proof that the first JSON record has no preamble.
- Out-of-order correlated responses.
- A Pi `command: "parse"` response without an ID faults the connection and fails every pending
  request; it is never assigned to the oldest request.
- Incremental text and cumulative tool-output assembly.
- Prompt acceptance followed later by settlement.
- Stop sends `clear_queue` and then `abort`. A compatibility test proves that supported Pi versions
  cancel an active auto-retry inside `abort`. If `abort` exceeds its ordinary-command timeout, the
  client sends `abort_retry` to release the retry delay and retries `abort`; the bounded fallback
  sequence is therefore `clear_queue`, `abort`, `abort_retry`, `abort`.
- Command-specific timeouts: ten minutes for `prompt` and `compact`, 30 seconds for ordinary
  requests, with cancellation for every wait.
- An unexpected blocking `extension_ui_request` receives a one-way
  `{"type":"extension_ui_response","id":"<request-id>","cancelled":true}` record;
  fire-and-forget presentations are ignored or retained only as bounded diagnostics.
- Timeout, cancellation, malformed output, stderr flood, and abrupt process exit.
- Start new durable session and resume it.

### Layer 3 — host/client integration tests

- Start the embedded host on `127.0.0.1`, reject an unauthenticated connection, and authenticate
  with the in-memory bearer credential without placing it in the URL or logs.
- Add a temporary project and create a thread.
- Send one prompt and receive snapshot plus ordered events.
- Reconnect from a retained cursor without a gap or duplicate.
- Reject a stale Pi entry cursor, fall back to full `get_entries`, and publish one reconciled
  replacement snapshot before accepting mutations.
- Repeat a `CommandId` without sending a second fake-Pi prompt.
- Restart the host and hydrate the saved fake-Pi session.
- Verify the reported `sessionFile` remains below the canonical host session root for every project
  and survives restart without writing to the user's default Pi session directory.

### Layer 4 — WinUI black-box tests

Run against a unique `--data-root`, the fake Pi path, and synthetic project files. The Add Project
flow should use an app-owned dialog with a path `TextBox` and an optional Browse action, so the
pull-request journey can set a path through UIA without requiring injected keyboard input or a
coordinate-driven system dialog:

1. Launch and wait for the ready state.
2. Add the fixture project and create a thread.
3. Set `PromptInput`, invoke Send, and observe an intermediate streaming value.
4. Observe tool running/completed state and the final assistant content exactly once.
5. Observe settlement and Stop becoming disabled.
6. Reopen the thread and verify no missing or duplicate content.
7. Relaunch with the same data root and verify the session is restored.
8. Verify the run manifest shows all client, host, and Pi session writes below the isolated data
   root.

Use UIA patterns and state waits, not coordinates or fixed sleeps. A small driver-contract test is
the first UI test written after the blank window exists.

### Layer 5 — optional real Pi smoke test

Use a temporary project and a Pi installation at version `0.84.4` or later with configured provider
credentials. Verify discovery, launch, prompt acceptance, visible streaming, settlement, graceful
shutdown, and session resume. This is manual or protected-CI only and never gates ordinary pull
requests. Upgrade the current `0.84.1` installation explicitly before running it; the application
does not perform that upgrade.

## 13. Non-production launch seams

Debug/test builds accept:

```text
--ui-test
--data-root <absolute-path>
--pi-executable <absolute-path>
--fake-pi-scenario <name>
--log-file <absolute-path>
```

These switches select dependencies and storage only. They do not bypass SignalR, the host,
`PiStation.PiRpc`, or ViewModels. Release builds reject fake-Pi scenario selection.

## 14. Implementation increments

### Increment 0 — scaffold and prove WinUI automation

Implementation status (2026-09-02): **complete**. The solution/project boundaries, packaged WinUI
shell, stable Automation IDs, pinned WinApp CLI contract, build settings, and driver-contract
journey are implemented and passing.

Deliver:

- Solution-wide SDK, package, analyzer, and build settings.
- The five production projects and test projects with the allowed references.
- Packaged blank `PiStation.App` with assembly name `PiStationDesktop`, the static shell, and
  Automation IDs.
- Install/review `winapp`, perform the driver-contract spike, then pin its passing version.

Exit:

- `dotnet build PiStationDesktop.slnx` succeeds from a clean restore.
- `winapp run` launches the app and returns the correct PID.
- The UI tree exposes the critical controls by stable Automation ID.

### Increment 1 — prove Pi RPC independently of WinUI

Implementation status (2026-09-02): **complete**. The strict byte-framed transport, request
correlation, command-specific timeouts, dialog cancellation, discovery/version gates, owned process
lifetime, event decoding, authoritative stream assembly, deterministic FakePi scenarios, and
resume-capable opt-in real-Pi harness are implemented. The real-Pi category remains opt-in and
completed an isolated turn and session resume against Pi `0.84.4` on September 1, 2026.

Deliver:

- `PiStation.FakePi` scenarios for normal streaming, tool use, stop, crash, and resume.
- Pi discovery and safe bundled-Node resolution.
- Process lifetime, strict JSONL framing, correlation, event decoding, and stream assembly.

Exit:

- Automated PiRpc tests pass for success and failure cases.
- An opt-in console/test harness can run and resume the installed real Pi.

### Increment 2 — implement the authoritative local host

Implementation status (2026-09-02): **complete**. The versioned Protocol DTOs use strongly typed
identifiers, closed command/event/envelope unions, required-constructor validation, and a
source-generated JSON context. The Host now owns the SQLite schema, stable environment/project/
thread/session mappings, durable idempotent command receipts, lazy per-thread Pi controllers,
authoritative projection reduction, bounded cursor-aware journals, and authenticated loopback
Kestrel/SignalR. Integration tests cover bearer rejection, Start and Stop, ordered streaming,
retained and stale cursors, duplicate/conflicting command IDs, interrupted dispatch recovery, and
host restart with durable FakePi session hydration.

Deliver:

- Minimal Protocol DTOs and JSON contexts.
- SQLite schema and project/thread/session mappings.
- Thread controller, projection reducer, bounded journal, and command receipts.
- Embedded authenticated loopback Kestrel/SignalR surface.

Exit:

- A host integration test creates a thread, streams a fake turn, reconnects without duplication,
  and resumes after restart.

### Increment 3 — connect ClientRuntime and WinUI

Implementation status (2026-09-02): **complete**. ClientRuntime now owns authenticated SignalR
connection supervision, protocol negotiation, project/thread operations, cursor-resuming thread
subscriptions, ordered client projections, duplicate suppression, and resync fallback. The WinUI
shell starts and owns the embedded environment, accepts isolated Debug/test launch switches, and
binds project/thread navigation, streaming transcripts, tool activity, Send, Stop, connection
state, and runtime errors through `IEnvironmentClient`. The deterministic WinApp journey covers
intermediate tool/text streaming, settlement, thread reopen, process relaunch, and Pi session
hydration, while retaining structured logs, a UI tree, a screenshot, and a write manifest.

Deliver:

- Connection supervisor, environment client, thread subscription, and projection store.
- Project/thread navigation, transcript, tool row, composer, Send, Stop, and error states.
- Structured logging and isolated test launch switches.

Exit:

- The app reaches Pi only through ClientRuntime and SignalR.
- The deterministic local vertical-slice UI test passes.

### Increment 4 — recovery and MVP hardening

Implementation status (2026-09-02): **complete**. Distinct Pi-crash, transport, resync, and
uncertain-command states, bounded projections/tool previews, safe process cleanup, structured
failure artifacts, and the repeatable pull-request test entry point are implemented. The complete
solution currently passes 105 code tests. Packaged driver-contract, draft, vertical-turn,
crash-recovery, interaction, Pi-configuration, thread-lifecycle, and transport/thread-hardening
journeys pass from isolated data roots. The input/accessibility journey additionally covers keyboard
focus, project-dialog cancellation and confirmation, rich prompt dispatch, long-transcript scrolling,
flyout identity, Escape behavior, and connection-aware disabled states.

Deliver:

- Pi crash versus transport-disconnect UI states.
- Restart/resume reconciliation and uncertain-dispatch presentation.
- Bounded tool previews, output limits, cancellation, and safe owned-process cleanup.
- Pull-request test entry point and failure artifacts.

Exit:

- The complete definition of done below passes repeatedly from clean data roots.

## 15. MVP definition of done

- A new developer can restore, build, and launch the packaged x64 app with documented commands.
- The app discovers a compatible Pi installation or shows a precise setup error.
- Pi versions below `0.84.4` are rejected with upgrade guidance, and the app never silently updates
  Pi.
- The local WinUI-to-SignalR-to-host-to-Pi path is the only application call path.
- A user can create a local project/thread, send a prompt, see incremental text and tool status, and
  stop the turn.
- Closing/reopening a thread does not stop work or duplicate content.
- Relaunching the app restores the durable Pi session and transcript.
- Fake-Pi unit/integration tests cover strict LF/BOM-less UTF-8 framing, correlation faults,
  command-specific timeouts, defensive interaction cancellation, streaming, stop, crash, stale
  hydration cursors, and resume.
- The black-box UI journey passes with semantic selectors and no unconditional sleeps.
- Every UI-test failure leaves structured logs, a UI tree, a screenshot, and the command manifest.
- A real Pi smoke test passes once on the developer machine but remains opt-in.
- No remote access, rich rendering, extension bridge, or service-host work is required to call the
  MVP complete.

## 16. Next implementation action

The MVP definition of done remains complete. Post-MVP conversation-loop work now includes typed
timeline cards, approvals and structured questions, durable per-thread drafts, host-owned
attachments carried into Pi turns, bounded workspace filename search, and the composer `@` picker.
Continue to use `PiStationDesktop\Invoke-PullRequestTests.ps1` as the repeatable automated gate; it
restores and builds the solution, writes TRX results, runs all code tests, and exercises the
driver-contract, draft, vertical-turn, crash-recovery, approval/question, Pi-configuration,
thread-lifecycle, input/accessibility, and transport/thread-hardening packaged-app journeys from
isolated data roots.

Per-thread Pi configuration is complete for Pi's currently reported capabilities. Model/thinking
capabilities come from Pi RPC; updates are receipt-backed and revisioned; SQLite persists desired
settings; ClientRuntime refreshes snapshots after reconnect; and capability-driven WinUI selectors
preserve selections across relaunch. Permission/runtime controls remain hidden because Pi currently
advertises none.

The protocol-v9 thread lifecycle is complete end to end for rename, archive/unarchive, pin/unpin,
active-only listing, pinned ordering, and bounded title search. Lifecycle mutations use durable
receipts and optimistic metadata revisions, existing SQLite databases migrate in place, Pi sessions
remain intact, and the client maintains a revision-safe cache with reconnect refresh and typed
failure states. WinUI provides debounced search, active and archived shelves, inline rename,
pin/archive overflow and context actions, keyboard operation, and accessible live status. The
packaged lifecycle journey verifies those operations and persistence across relaunch.

The first T3-inspired WinUI design slice is complete. `T3DesignTokens.xaml` now provides dark-first
light/dark/high-contrast semantic color tokens, compact typography and layout metrics, and reusable
styles for surfaces, controls, pills, status indicators, sidebar rows, transcript rows, and the
composer; the current shell consumes those primitives. The integrated T3-inspired frame and unified
project/thread sidebar are also complete: a compact custom title bar aligns with the fixed workspace
rail, active-thread header, centered reading column, and bottom connection state, while always-visible
project rows preserve accessible selection across relaunch. Native assistant Markdown/code rendering
is now complete: an app-owned WinUI control walks Markdig's CommonMark tree into `RichTextBlock`
content, safe hyperlinks, native list/quote layouts, and syntax-highlighted code surfaces with
language labels and accessible copy actions. Raw HTML stays inert and user prompts remain plain.
Collapsible reasoning, grouped tool activity, compact turn metadata, and the first focused child
ViewModels are now implemented. The next T3-parity program is the GUI-first shell redesign defined
in `PISTATION-T3CODE-GUI-PARITY-PLAN.md`: rebuild the sidebar, header, continuous transcript,
integrated composer, and optional workbench panel architecture before adding Git, terminal, file,
or preview backends.
