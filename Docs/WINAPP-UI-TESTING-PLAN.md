# Pi Station Desktop — WinApp UI Testing Plan

Status: local packaged-app suite implemented; CI, nightly matrix, and remote coverage pending  
Target: **Pi Station Desktop**, the C# WinUI 3 `PiStation.App` described in `PI-AGENT-CALLING-PLAN.md`  
Primary driver: Microsoft `winapp ui`  
Last reviewed: 2026-09-02

## 1. Decision and intended outcome

Adopt `winapp ui` as the primary black-box driver for the Windows desktop application. It will
serve two related but distinct workflows:

1. **Agent-assisted exploration:** a developer or coding agent launches the app, inspects its UI
   Automation tree, invokes controls, reads state, and captures screenshots while diagnosing a
   feature or bug.
2. **Repeatable end-to-end tests:** checked-in PowerShell/Pester scenarios launch an isolated app
   instance against the deterministic fake Pi runtime, perform user-visible actions, and make
   semantic assertions suitable for pull-request and nightly CI runs.

The first target is the local vertical slice from `PI-AGENT-CALLING-PLAN.md`: launch the app,
connect through the embedded loopback host, create a project and thread, send one prompt, observe
streaming, reach settlement, reopen the thread without duplication, and resume the session after a
restart.

The suite must validate behavior through the public UI and Windows UI Automation (UIA) contract.
It must not reach into ViewModels, invoke internal services, query the host database for assertions,
or call Pi directly to make a UI test pass. Test setup may provision deterministic files, launch the
fake Pi fixture, and assign an isolated application data directory.

`winapp` is currently a public-preview tool. The project must pin and explicitly upgrade the tested
CLI version rather than following nightly builds or the repository's `main` branch. If a blocking
driver defect remains after a reasonable investigation, keep the scenario and selector contracts
driver-neutral enough that a small FlaUI UIA3 adapter can replace the affected command. Do not add
Appium or WinAppDriver preemptively.

## 2. Scope

### In scope

- The top-level WinUI window and all application-owned dialogs, flyouts, menus, and secondary
  windows.
- Windows file/folder dialogs opened by the application.
- Application startup, initial loading, shutdown, notification-area lifetime, and restoration.
- Project selection, thread creation and selection, prompt submission, streaming transcript,
  tool-status presentation, stop/abort, errors, and recovery.
- Local and, when Phase 4 is implemented, remote environment connection state visible in the UI.
- Keyboard navigation and a focused set of pointer interactions that cannot be exercised through a
  UIA control pattern.
- Accessibility/testability checks for stable Automation IDs, accessible names, control types,
  focus, enabled state, and supported UIA patterns.
- Screenshots and UI-tree captures as diagnostic evidence.

### Out of scope

- Unit testing ViewModels, protocol DTOs, `PiRpcConnection`, SignalR ordering, persistence, or
  authorization. Those remain in their existing unit/integration suites.
- Pixel-perfect screenshot regression testing. Screenshots are failure evidence unless a later,
  separately approved visual-regression system is added.
- Automating UAC, the Windows sign-in screen, the secure desktop, or the lock screen.
- Testing the real Pi/provider path on every pull request. Real-provider testing remains opt-in.
- Load generation through the UI. Large-output and slow-client load tests belong at the host and
  protocol layers; the UI suite verifies only their user-visible outcomes.
- Driving a future custom-drawn terminal or embedded WebView solely through coordinates. If such a
  surface does not expose a useful UIA tree, test its container with `winapp ui` and its content with
  the technology-native driver.

## 3. Core testing rules

1. **Target the launched process, not a title string.** Capture the process ID returned by
   `winapp run --detach --json` and pass it to `-a`. Window titles are localized and may change with
   project or thread state.
2. **Prefer stable Automation IDs.** Text selectors are acceptable only for initial discovery or
   when the visible text itself is the behavior under test. Generated semantic slugs are useful for
   exploration but are not a committed selector contract.
3. **Prefer UIA patterns over injected input.** Use `invoke`, `set-value`, `scroll --direction` and
   `wait-for` before `click`, `send-keys`, wheel input, drag, touch, or pen.
4. **Wait for state, never for a guessed duration.** Use `wait-for` against an element, property, or
   value. A small bounded delay is allowed only when testing a time-based behavior and must be
   explained in the scenario.
5. **Assert externally visible state.** Read names, values, toggle states, enabled state, and the
   appearance/disappearance of elements. Do not treat a successful click as proof that the app
   completed an operation.
6. **Use JSON for automation.** Checked-in helpers call `winapp ui ... --json`, capture the native
   exit code separately, parse stdout even on an expected nonzero result, and keep stdout free of
   unrelated logging.
7. **Run desktop scenarios serially.** A desktop, keyboard focus, pointer, test data root, and app
   registration are shared resources. Parallel UI scenarios would create nondeterministic failures.
8. **Capture evidence before teardown.** On failure, collect a screenshot and UI tree while the bad
   state is still present, then stop only processes created by that test run.
9. **Keep tests deterministic and private.** Pull-request tests use the fake Pi executable, fixed
   scripted responses, synthetic project content, and no developer credentials or conversation
   history.
10. **Treat accessibility as the automation API.** A control that cannot be addressed or read
    semantically is an app defect to fix, not a reason to make a coordinate-based test permanent.

## 4. Proposed repository layout

Create this structure when `PiStation.App` is scaffolded:

```text
tests/
  PiStation.UiTests/
    README.md
    Invoke-UiTests.ps1              # developer/CI entry point
    Bootstrap-UiTestTools.ps1       # verifies pinned tools; no silent upgrades
    PiStation.UiTests.psd1          # pinned Pester/module requirements
    tool-versions.json              # tested winapp and PowerShell/Pester versions
    lib/
      WinAppUi.psm1                 # all native CLI/process wrappers
      UiTestEnvironment.psm1        # isolated paths, fixtures, app lifecycle
    scenarios/
      00.DriverContract.Tests.ps1
      10.Launch.Tests.ps1
      20.LocalVerticalSlice.Tests.ps1
      30.ThreadLifecycle.Tests.ps1
      40.ErrorRecovery.Tests.ps1
      50.KeyboardAccessibility.Tests.ps1
      60.RemoteEnvironment.Tests.ps1
    fixtures/
      projects/
        minimal-project/
      fake-pi/
        scripts/
    artifacts/
      .gitignore
```

`artifacts/` is always ignored except for its `.gitignore`. CI uploads its contents on failure and
may upload a compact summary on success. No generated image, recording, UI-tree dump, temporary
profile, Pi session, or test transcript is committed.

The PowerShell entry point is the stable interface:

```powershell
pwsh ./tests/PiStation.UiTests/Invoke-UiTests.ps1 `
  -Configuration Debug `
  -Platform x64 `
  -Suite PullRequest `
  -KeepArtifacts OnFailure
```

Supported suites should be:

| Suite | Purpose | Expected environment |
| --- | --- | --- |
| `Smoke` | Driver contract, launch, one prompt/response | Local or CI |
| `PullRequest` | Deterministic critical paths using UIA patterns | Windows hosted runner |
| `Nightly` | Full deterministic suite plus injected keyboard/pointer cases | Dedicated interactive runner |
| `RealPi` | One opt-in configured-provider smoke test | Explicit local/manual run |

Do not make developers remember raw build-output paths, AUMIDs, process names, or artifact
locations. `Invoke-UiTests.ps1` owns build, isolated setup, launch, targeting, collection, and
cleanup.

## 5. Tool installation and version policy

### Developer bootstrap

The documented local installation is:

```powershell
winget install Microsoft.winappcli --source winget
winapp --version
winapp ui --help
```

The bootstrap script must then:

1. Verify Windows 11 x64 for the first supported matrix.
2. Verify PowerShell 7 and the pinned Pester major/minor version.
3. Find `winapp` on `PATH` and compare `winapp --version` with `tool-versions.json`.
4. Fail with an exact installation/update command when the version is absent or unsupported.
5. Print the resolved executable paths and versions into the run manifest.

The script must not install or upgrade machine-wide tools without an explicit `-InstallTools`
switch. Normal test execution is read-only with respect to machine tooling.

### Pinning and upgrades

- Pin the first version only after the driver-contract spike passes on the app's supported Windows
  build. Record that exact version in `tool-versions.json`.
- Pin the CI setup action by immutable commit SHA or a reviewed stable tag, and separately verify the
  installed `winapp --version`.
- Never consume a nightly `main` artifact in gating CI.
- Upgrade `winapp` in a dedicated pull request. Run the complete deterministic suite, compare JSON
  schemas and exit-code behavior, and record any required helper changes.
- Keep the previous known-good binary available to confirm whether a failure is an application
  regression or a CLI regression.

## 6. Application testability contract

The UI must deliberately expose a stable UIA surface. Add these attributes while each view is built,
not later as test-only cleanup.

### Automation ID convention

Use globally unique, semantic, PascalCase IDs. The suggested pattern is
`<Area><Purpose><ControlType>`; do not encode layout position or translated text.

Examples for the first vertical slice:

| Element | Proposed Automation ID | State exposed through |
| --- | --- | --- |
| Main application root | `AppMainWindow` | Name/window patterns |
| Startup/loading surface | `AppLoadingView` | Presence |
| Environment selector | `EnvironmentSelector` | Selection/value |
| Project selector | `ProjectSelector` | Selection/value |
| New project action | `NewProjectButton` | Invoke |
| Thread tab container | `ThreadTabList` | Selection |
| New thread action | `NewThreadButton` | Invoke |
| Active thread view | `ActiveThreadView` | Presence/name |
| Prompt editor | `PromptInput` | Value or Text pattern |
| Send prompt action | `SendPromptButton` | Invoke/enabled |
| Stop action | `StopTurnButton` | Invoke/enabled |
| Connection indicator | `ConnectionStatusText` | Name/value |
| Turn status | `TurnStatusText` | Name/value |
| Transcript container | `TranscriptList` | Children/scroll |
| Latest assistant message | `LatestAssistantMessage` | Text/name |
| Tool activity group | `ToolActivityGroup` | Name/children |
| Tool activity row | `ToolActivityExpander` | Expand-collapse/name |
| Tool arguments | `ToolArgumentsText` | Text/name |
| Tool output | `ToolOutputText` | Text/name |
| Reasoning disclosure | `ReasoningExpander` | Expand-collapse/name |
| Reasoning body | `ReasoningText` | Text/name |
| Runtime error surface | `RuntimeErrorBanner` | Presence/name |
| Retry/reconnect action | `ReconnectButton` | Invoke/enabled |

Example XAML:

```xml
<Button
    AutomationProperties.AutomationId="SendPromptButton"
    AutomationProperties.Name="Send prompt"
    Content="Send" />
```

Rules:

- An Automation ID is stable across releases unless the control's semantic purpose changes.
- IDs are unique across the entire active UI tree, not merely among siblings. This lets `winapp`
  emit the Automation ID directly rather than a generated slug.
- `x:Name` is not treated as a test contract; explicitly set `AutomationProperties.AutomationId`.
- Accessible `Name` values describe user purpose and may be localized. IDs remain language-neutral.
- Repeated/virtualized items expose a useful accessible name and item control type. When an item
  needs direct selection, compose its Automation ID from a stable test-fixture identifier, never
  its current array index.
- Custom controls implement an `AutomationPeer` and the correct UIA patterns. Do not expose one
  opaque pane containing several visually interactive subcontrols.
- Status text must be exposed as one coherent value or name. Do not force tests or screen readers
  to concatenate unrelated decorative text nodes.
- Disabled and busy states must be reflected in UIA properties, not only by color or animation.

### Required test mode seam

Add a narrowly scoped test launch configuration available in non-production builds:

- `--ui-test` enables deterministic startup and disables first-run tours, update prompts, and other
  unrelated nondeterminism.
- `--data-root <absolute-path>` directs all client and host persistence to a test-owned temporary
  directory.
- `--pi-executable <absolute-path>` selects the fake Pi fixture already required by the architecture
  plan.
- `--log-file <absolute-path>` writes structured application logs to the test artifact directory.
- Optional fixture arguments select a scripted fake-Pi behavior such as normal streaming, delayed
  response, crash, malformed output, or resume.

These switches configure dependencies and storage; they must not mutate visual state or bypass the
same ClientRuntime/SignalR/PiRpc path used in production. Release builds should reject or omit test-
only fixture switches.

## 7. Driver wrapper design

All committed scenarios call helper functions rather than scattering native command construction
throughout the suite. The minimum helper API is:

```text
Start-PiStationUiTestApp
Stop-PiStationUiTestApp
Get-PiStationWindow
Get-UiTree
Find-UiElement
Invoke-UiElement
Set-UiValue
Send-UiKeys
Wait-UiElement
Wait-UiValue
Get-UiValue
Get-UiProperty
Save-UiScreenshot
Save-UiRecording
Invoke-WinAppJson
```

`Invoke-WinAppJson` is the only function that executes `winapp`. It returns a structured object
containing:

- Executable and argument list.
- Start/end timestamps and elapsed time.
- Native exit code.
- Parsed JSON, when present.
- Raw stdout and stderr.
- Target PID/HWND.

It must preserve parseable JSON when `search` finds nothing or `wait-for` times out; those commands
return a useful JSON envelope and a nonzero exit code. It must distinguish an expected assertion
miss from malformed output, a driver crash, or an app process exit.

Never build a single shell command by concatenating untrusted prompt, path, or selector strings.
Pass native arguments as an array so quotes, spaces, backslashes, and Unicode are preserved.

### Launch and target flow

1. Create a unique run directory under the OS temporary directory and resolve it to an absolute
   path.
2. Copy or generate the minimal fixture project inside that directory.
3. Start the fake Pi fixture and prepare its script, if it is a separate process.
4. Build `PiStation.App` once for the requested configuration/platform.
5. Launch through project mode or the built output:

   ```powershell
   $launch = winapp run ./src/PiStation.App/PiStation.App.csproj --detach --json |
     ConvertFrom-Json
   $appPid = $launch.ProcessId
   ```

6. Pass the test-mode arguments supported by the final `winapp run` syntax. If project mode cannot
   forward them reliably, launch the known built executable and capture its PID directly.
7. Wait for `AppMainWindow`, then wait for the loading view to disappear and a ready-state element
   to appear.
8. Store PID, start time, executable path, data root, fixture paths, tool versions, and environment
   details in `run.json`.
9. Use `-a $appPid` for normal commands. Resolve and retain an HWND with `list-windows` when a
   secondary window or common dialog must be targeted consistently.
10. During cleanup, gracefully close the app if possible, then terminate only the exact process tree
    associated with the recorded test PID if it failed to exit. Validate the PID start time before
    forced cleanup to avoid acting on a reused PID.

## 8. Command-selection policy

Use this priority order for reliable tests:

| Need | Preferred command | Fallback | Notes |
| --- | --- | --- | --- |
| Discover controls | `inspect --interactive --json` | deeper `inspect` | Save the tree during contract work |
| Locate known control | Automation ID | text search | Text only when text is under test |
| Activate button/toggle | `invoke` | `click` | `invoke` works without pointer focus |
| Fill `TextBox` | `set-value` | focused `send-keys --via send-input` | Use keys when key events matter |
| Fill `RichEditBox` | `send-keys --via send-input` | none | WinUI exposes read-only TextPattern here |
| Read content | `get-value` | `get-property Name` | Account for the UIA pattern exposed |
| Await async state | `wait-for` | bounded helper polling | Never fixed sleeps for normal async work |
| Scroll container | `scroll --direction/--to` | `scroll --wheel` | Wheel requires interactive desktop |
| Context/double click | `click --right/--double` | keyboard equivalent | Nightly when injected input is required |
| Flyout/tooltip evidence | `screenshot --capture-screen` | normal screenshot | Screen capture foregrounds the target |
| App-window evidence | normal `screenshot` | `--capture-screen` | Default WGC can capture an occluded window |

For WinUI/XAML controls, do not use the default `post-message` transport to simulate typing. Use
`set-value` where possible or `send-keys --via send-input` when real keystrokes are required.
Injected mouse, keyboard, drag, wheel, touch, and pen commands require an unlocked interactive
desktop and foreground access. They run only in the interactive/nightly lane unless the chosen CI
runner is proven to provide that environment.

No committed critical-path test may depend on raw screen coordinates. A narrowly scoped test of a
canvas or spatial interaction may use coordinates relative to a semantically identified container,
must capture a screenshot, and belongs in the nightly suite.

## 9. Test scenarios and rollout order

### Stage A — driver contract spike

Implementation status (2026-09-02): **complete** for the packaged Debug workflow. The checked-in
driver-contract journey validates stable Automation IDs and app-owned dialog controls and retains
UI-tree and screenshot evidence.

Implement this as soon as the first WinUI window exists. It proves the tool before building a large
test harness.

1. Launch the packaged and unpackaged/debug form used by developers.
2. Locate the window by PID.
3. Inspect the complete UIA tree and save JSON.
4. Confirm every initial control exposes the intended Automation ID, name, control type, enabled
   state, and required pattern.
5. Invoke one button, set one `TextBox`, read one `TextBlock`, toggle one checkbox, select one list or
   tab item, and scroll one container.
6. Capture a screenshot while the window is partially occluded.
7. Open one flyout or dialog and capture it with `--capture-screen`.
8. Close and relaunch the app, proving selectors remain stable.

Exit criteria:

- No critical control requires a generated slug, XPath, or coordinate.
- The app can be targeted by PID and its principal HWND can be resolved.
- UIA-pattern actions work at the same integrity level without administrator privileges.
- Failures leave enough artifacts to diagnose selector versus application issues.

### Stage B — launch and isolation smoke tests

Implementation status (2026-09-02): **partial**. Clean isolated launch, seeded state, PID-owned
cleanup, and write-manifest evidence are exercised. Single-instance activation and the deferred
notification-area lifetime policy are not covered.

Scenarios:

1. **Clean launch:** a new data root reaches the empty/ready screen without an error banner.
2. **Existing state launch:** a seeded test profile restores the expected environment and thread.
3. **Single-instance behavior:** a second launch produces the documented activation behavior and
   does not corrupt the first instance's state.
4. **Graceful close:** closing the main window follows the intended notification-area/host lifetime
   policy.
5. **No real data access:** the run manifest proves all writes stayed under the test data and fixture
   roots.

### Stage C — local vertical-slice gate

Implementation status (2026-09-02): **complete**. The deterministic packaged-app journey covers
project/thread creation, incremental text and tool state, completed reasoning disclosure,
consecutive-tool grouping, projected arguments/output, settlement, reopen, process relaunch, and
session hydration.

This is the first mandatory pull-request UI journey:

1. Launch with a clean data root and normal-streaming fake Pi script.
2. Add/select the temporary fixture project.
3. Create a thread.
4. Set `PromptInput` to a deterministic prompt.
5. Verify `SendPromptButton` becomes enabled, then invoke it.
6. Verify the UI acknowledges the turn and prevents an invalid duplicate send.
7. Wait for the first assistant content, then for at least one later value proving incremental
   streaming is visible rather than only the final response.
8. Wait for the scripted tool activity to appear and reach its final status.
9. Wait for `TurnStatusText` to report settlement and for `StopTurnButton` to become disabled or
   disappear according to the final design.
10. Assert the final transcript text exactly once.
11. Navigate away or close the thread tab, reopen it, and assert the transcript is neither missing
    nor duplicated.
12. Close and relaunch the application with the same isolated data root.
13. Reopen the thread and verify the session/projection is restored.

This journey proves only visible behavior. Lower-level tests remain responsible for exact event
ordering, cursor math, idempotency receipts, and JSONL framing.

### Stage D — thread lifecycle and recovery

Implementation status (2026-09-02): **complete locally**. The packaged recovery journey
distinguishes a Pi crash, restarts the runtime, and completes a subsequent prompt. The interaction
journey covers one-response-wins behavior. The hardening journey stops active turns, navigates away
from and back to live threads, proves two-thread isolation, disconnects and reconnects the real
SignalR client, recovers through a deliberately expired bounded journal, and presents an interrupted
dispatch as uncertain without replaying its prompt. Relaunch hydration remains covered by the
vertical journey.

Add one deterministic scenario per user-visible invariant:

- Stop a running turn and observe a clear stopped/settled state.
- Close a tab while a turn runs, reopen it, and observe continued streaming without duplication.
- Restart after a settled turn and restore the transcript.
- Simulate fake Pi exit and show runtime failure, distinct from transport disconnection.
- Simulate loopback connection loss and show disconnected/reconnecting state without declaring Pi
  crashed.
- Restore connection and prove the visible transcript has no gap or duplicate.
- Present `DispatchUncertain` without silently resending the prompt.
- Present an expired/resync condition and replace stale UI with the recovered snapshot.
- Run two threads and prove updates remain in the owning thread.
- Exercise an extension interaction and ensure one visible response wins when the lower-level race
  fixture is used.

### Stage E — project, dialog, and input behavior

Implementation status (2026-09-02): **substantially complete locally for the pull-request lane**.
The app-owned project dialog now has cancellation, confirmation, focus-order, and accessibility
coverage. The packaged input/accessibility journey verifies Enter-to-send, Shift+Enter multiline
input, exact Unicode/quoted/long prompt dispatch, busy/disconnected disabled states, long-transcript
scrolling, action-flyout identity, rename and file-mention Escape behavior, and critical accessible
names/control types. Native picker cancellation, pointer-opened context menus, and exhaustive focus
traversal remain for the interactive lane.

- Select and cancel a project through the app-owned path dialog without coordinate input.
- Exercise native picker cancellation by resolved dialog HWND in the interactive lane.
- Submit Unicode, quotes, multiline text, and a long but allowed prompt.
- Verify keyboard-only navigation order, visible focus, Enter-to-send behavior, and Escape behavior.
- Verify disabled controls cannot be invoked while disconnected or busy.
- Exercise scrolling and selection in a long transcript generated by the fake Pi fixture.
- Verify flyouts and context menus by element identity, using screen capture only for diagnostics.

### Stage F — remote environment UI

Implementation status (2026-09-02): **not started**; architecture Phase 4 is not implemented.

Begin only after architecture Phase 4 exists. Cover visible behavior while protocol/security suites
retain responsibility for cryptographic and authorization correctness:

- Add and pair a remote environment through a deterministic local test host.
- Distinguish local and remote environments with colliding thread IDs.
- Disconnect/reconnect a remote environment while local work continues.
- Display read-only permissions by disabling mutation controls.
- Display certificate mismatch, expired ticket, and revocation as distinct actionable states.
- Operate local and remote threads concurrently without cross-updating UI.

### Stage G — accessibility and compatibility matrix

Implementation status (2026-09-02): **not started as a matrix**. Stable Automation IDs, accessible
names, and keyboard operation exist for implemented flows, but DPI, theme, high-contrast, window
size, OS, and automated accessibility jobs remain.

Run after critical flows stabilize:

- Keyboard-only traversal and activation.
- 100%, 150%, and 200% display scale without coordinate selectors.
- Light, dark, and high-contrast themes.
- Minimum supported window size and maximized state.
- Supported Windows 11 versions and x64; add ARM64 only when it becomes a product target.
- Automated Accessibility Insights checks as a complementary job, not as a replacement for
  behavioral tests.

## 10. Assertions and flake prevention

### State-based synchronization

Each action must be followed by the state transition it is expected to cause. Examples:

```powershell
winapp ui invoke SendPromptButton -a $appPid --json
winapp ui wait-for TurnStatusText -a $appPid `
  --property Name --value 'Running' --timeout 5000 --json

winapp ui wait-for LatestAssistantMessage -a $appPid `
  --value 'Deterministic final response' --timeout 30000 --json
```

Exact user-facing strings above are placeholders until the UI copy contract is frozen. Prefer a
stable status element and machine-stable value where localization would otherwise make the test
brittle.

### Timeout policy

- Element reaction: 5 seconds.
- Normal deterministic turn completion: 30 seconds.
- App launch/build registration: 60 seconds.
- Recovery/relaunch: 60 seconds.
- Entire pull-request suite: 10 minutes initially.

Centralize these defaults and allow a CI multiplier for demonstrably slower machines. A timeout is a
failure with evidence, not a reason to automatically rerun a test. Track and fix flakes; do not hide
them with blanket retries. A single whole-scenario diagnostic retry may be enabled temporarily while
triaging an identified driver issue, and its first failure remains visible in reports.

### Deterministic fixture protocol

The fake Pi script should expose synchronization markers through the normal visible flow. For the
streaming test, it should wait for an explicit test-controlled release between the first and final
chunks so the test can reliably observe the intermediate state. The release mechanism is fixture
control, not an application backdoor.

## 11. Diagnostics and artifacts

Create one directory per run and scenario:

```text
artifacts/<run-id>/<scenario>/
  run.json
  app.log
  fake-pi.log
  commands.ndjson
  before-tree.json
  failure-tree.json
  failure.png
  failure-screen.png
  recording.mp4              # only when explicitly enabled
  pester-result.xml
```

On every failure:

1. Record the failed command, arguments with secrets redacted, exit code, duration, stdout, and
   stderr.
2. Check whether the recorded application PID is still alive and whether its start time matches.
3. Capture the focused element, active window list, and UI tree rooted at the failed element when
   possible.
4. Capture a normal app-window screenshot; add `--capture-screen` when the failure involves a flyout,
   dialog, tooltip, focus, or pointer.
5. Copy application and fake-Pi logs.
6. Write cleanup results, including any process that did not exit gracefully.

Record MP4 only for local repro, nightly interaction tests, or a targeted diagnostic retry. Routine
recordings are slower, large, and may retain more user-visible data than needed.

All test content is synthetic. Artifact upload must still use repository retention limits and must
not include credentials, pairing secrets, real project paths, or real Pi conversations. Redact the
command manifest before upload rather than relying on the UI not to display a secret.

## 12. CI plan

### Pull-request lane

- Windows 11 x64 runner.
- Pinned stable/reviewed `winapp` preview version.
- Deterministic fake Pi only.
- Serial execution.
- UIA-pattern commands only: `inspect`, `search`, `invoke`, `set-value`, `get-value`, property reads,
  directional scroll, `wait-for`, and window screenshots.
- Driver contract, clean launch, local vertical slice, stop, reopen, and one recovery/error journey.
- Upload diagnostics on failure and Pester/JUnit results always.

This lane should avoid injected input because `click`, `send-input`, wheel, drag, touch, and pen need
an unlocked interactive desktop. Microsoft documents the UIA-pattern commands used above as suitable
for locked/headless sessions, but this must still be proven on the selected CI image during Stage A.

### Nightly interactive lane

- Dedicated Windows 11 VM or self-hosted runner with an unlocked, isolated interactive session.
- No concurrent jobs in that desktop session.
- Keyboard-only navigation, RichEditBox typing, pointer/context actions, drag/wheel cases, dialogs,
  DPI/theme matrix, and optional recording.
- Automatic screen locking, sleep, notifications, updates, and focus-stealing background apps
  disabled for the runner account.
- Runner and target app use the same non-elevated integrity level.

Do not rely on keeping an RDP client connected; disconnecting RDP can remove or lock the interactive
desktop. Use the runner technology's supported persistent console-session setup.

### Real Pi lane

- Manual dispatch or explicitly scheduled protected environment.
- Configured provider credential supplied through CI secrets.
- One deterministic prompt in a temporary project.
- Validate launch, acceptance, visible streaming, settlement, graceful stop, and session resume.
- Never block normal pull requests on provider availability, billing, rate limits, or model output
  wording.

### CI job sequence

1. Checkout and verify the tool-version lock.
2. Install/restore pinned .NET, PowerShell test modules, and `winapp`.
3. Build the app, fake Pi fixture, and any test host once.
4. Create the isolated data/artifact roots.
5. Run the selected suite serially with a job timeout.
6. Always collect the Pester report and artifact manifest.
7. On failure, upload screenshots, UI trees, and sanitized logs.
8. Stop only recorded test processes and remove test app registration if the launch method created
   one.

## 13. Known risks and mitigations

| Risk | Mitigation |
| --- | --- |
| Public-preview CLI changes behavior | Pin version; upgrade separately; retain known-good binary |
| Critical control is absent from UIA tree | Add AutomationProperties or a custom AutomationPeer |
| Selector changes with layout/localization | Use unique semantic Automation IDs |
| Generated slug becomes stale | Never commit slugs for stable app-owned controls |
| Async streaming produces timing flakes | Fixture-controlled checkpoints plus `wait-for` |
| Packaged app PID differs from visible process | Capture launch JSON, then resolve/record window PID and HWND |
| Dialog/flyout is a separate window | Use `list-windows`, target its HWND, capture screen overlays |
| WinUI text input ignores posted messages | Prefer `set-value`; otherwise use `send-input` interactively |
| `RichEditBox` cannot be set through ValuePattern | Use focused real keystrokes in the interactive lane |
| Focus or pointer lands in another app | Prefer `invoke`; isolate desktop; fail on foreground mismatch |
| Test accidentally uses developer state | Required unique `--data-root`; assert paths in run manifest |
| Screenshots leak sensitive content | Synthetic fixtures, redaction, limited retention |
| Tool regression blocks one command | Minimal driver wrapper allows a targeted FlaUI fallback |
| UI suite duplicates lower-level coverage | Keep UI assertions user-visible and map each journey to an invariant |
| Process cleanup kills unrelated work | Record PID/start time/process tree; validate ownership before cleanup |

## 14. Implementation milestones

### Milestone 0 — approve the contract

Implementation status (2026-09-02): **complete**.

- Confirm `winapp ui` as the primary driver.
- Confirm PowerShell 7 plus Pester as the scenario runner.
- Confirm the test-only launch arguments and isolated data-root design.
- Freeze the first vertical-slice Automation ID list.

Exit: the UI developer, runtime developer, and test owner agree on the selector and fixture seams.

### Milestone 1 — prove the driver

Implementation status (2026-09-02): **complete** for the packaged Debug workflow.

- Install a reviewed stable `winapp` preview release locally.
- Add Automation IDs to the first WinUI view.
- Complete Stage A manually and save an example artifact set.
- Record the tested tool version.

Exit: every critical initial control is semantically inspectable and operable.

### Milestone 2 — create the harness

Implementation status (2026-09-02): **complete**. Checked-in PowerShell entry points build, launch,
target by PID, retain diagnostics, and clean up owned processes.

- Add the proposed directory structure, bootstrap, wrapper module, lifecycle module, and Pester
  entry point.
- Implement safe argument handling, PID/HWND tracking, timeouts, and failure artifacts.
- Add driver-contract and clean-launch tests.

Exit: one command builds, launches, tests, reports, and cleans up on a developer machine.

### Milestone 3 — gate the local vertical slice

Implementation status (2026-09-02): **complete locally**. `Invoke-PullRequestTests.ps1` includes the
driver contract, draft, vertical-turn, crash-recovery, interaction, Pi-configuration,
thread-lifecycle, input/accessibility, and transport/thread-hardening journeys.

- Connect the existing fake Pi executable through test launch configuration.
- Implement Stage C and the smallest stop/reopen/restart scenarios.
- Remove all fixed sleeps and generated-slug selectors.

Exit: architecture Phase 3 cannot be considered complete unless this UI journey passes along with
its lower-level suites.

### Milestone 4 — add pull-request CI

Implementation status (2026-09-02): **partial**. The repeatable local pull-request entry point is
implemented and passing. A checked-in `windows-2025` GitHub Actions workflow provisions the pinned
toolchain, runs that entry point, and always uploads TRX and UI artifacts. Successful hosted-runner
execution and the time/repetition exit criteria are not yet demonstrated.

- Pin the runner and toolchain.
- Prove the UIA-pattern subset on the runner.
- Upload sanitized failure artifacts and test results.
- Track runtime and establish the 10-minute budget.

Exit: the suite passes repeatedly from a clean checkout and produces actionable evidence when a
deliberate UI defect is introduced.

### Milestone 5 — recovery and interactive coverage

Implementation status (2026-09-02): **partial**. Deterministic recovery, relaunch hydration,
draft/file-mention, approval/question, keyboard/dialog/input, long-transcript, flyout, and focused
accessibility journeys exist. The dedicated interactive/nightly runner and the remaining native
picker, pointer/context-menu, exhaustive focus, DPI, theme, and accessibility-matrix cases do not.

- Keep the completed Stage D recovery/error cases in the pull-request gate.
- Provision the dedicated interactive nightly runner.
- Keep the keyboard/dialog/input accessibility slice in the pull-request gate.
- Add native-picker, pointer/context-menu, DPI, theme, and accessibility-matrix cases.

Exit: the user-visible portions of architecture Phase 5 failure states are covered without a flaky
PR gate.

### Milestone 6 — remote environment coverage

Implementation status (2026-09-02): **not started**; it remains blocked on architecture Phase 4.

- Add a deterministic remote test host and Phase 4 UI scenarios.
- Keep security correctness in the protocol/security suites.

Exit: local and remote threads can be operated concurrently without visible cross-talk, gaps, or
duplicates.

## 15. Definition of done

The WinApp UI testing effort is established when all of the following are true:

- A clean machine can follow the checked-in bootstrap instructions and run `Smoke` with one command.
- The exact `winapp` version is checked before every run.
- Every critical app-owned control has a stable, unique Automation ID and meaningful accessible
  name.
- The suite targets the app by recorded PID/HWND and never relies on a mutable window title.
- The local vertical slice passes against deterministic fake Pi behavior.
- Stop, close/reopen, restart/resume, Pi failure, and transport disconnection have distinct visible
  assertions.
- Pull-request tests use semantic UIA actions, run serially, and have no unconditional sleeps or raw
  coordinates.
- Any failure yields a sanitized command log, app/fake-Pi logs, UI tree, screenshot, and test report.
- The pull-request lane completes within its agreed time budget on three consecutive clean runs.
- A deliberate accessibility/selector regression fails the driver-contract test clearly.
- The real Pi smoke test remains opt-in and cannot make ordinary development dependent on a
  provider.

## 16. Immediate next actions

The scaffold, driver contract, isolated launch seams, local harness, selected recovery scenarios,
and checked-in pull-request workflow now exist. Continue in this order:

1. Run the workflow on the selected Windows runner, resolve any runner-specific UIA issues, and
   demonstrate the runtime/repetition exit criteria.
2. Finish Stage E native-picker and pointer/context-menu coverage, then provision the Stage G
   interactive accessibility/compatibility matrix.
3. Add sustained large-output and slow-consumer coverage without expanding the PR lane beyond its
   agreed time budget.
4. Add Stage F only after architecture Phase 4 remote calling is implemented.

## 17. Primary references

- [Microsoft: UI Automation with `winapp ui`](https://learn.microsoft.com/en-us/windows/apps/dev-tools/winapp-cli/ui-automation)
- [Microsoft `winapp` CLI repository and installation](https://github.com/microsoft/winappcli)
- [Microsoft `winapp` WinUI 3 test sample](https://github.com/microsoft/winappCli/tree/main/samples/winui-app)
- [Microsoft: testing Windows App SDK and WinUI 3 apps](https://learn.microsoft.com/en-us/windows/apps/develop/testing/)
- [Microsoft: WinUI AutomationProperties](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.automationproperties)
- [Accessibility Insights for Windows: Inspect](https://accessibilityinsights.io/docs/windows/getstarted/inspect/)
