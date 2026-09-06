# Pi integration parity milestone

Started September 5, 2026; validation continued September 6. This implements the first Pi integration milestone from the [feature completion audit](FEATURE-COMPLETION-AUDIT-2026-09-05.md). It does not complete the 48-item T3 parity backlog.

## Delivered behavior

| Area | Result |
| --- | --- |
| Extension configuration | Settings → Pi / runtime exposes installed-extension discovery and explicit trusted paths. Configuration is stored atomically in `pi-runtime.json`, with migration from the legacy executable-path file. Invalid or oversized configuration does not replace working settings. |
| Extension launch policy | Discovery is off by default. Enabling it delegates discovery and project trust to Pi. Explicit paths and the bundled browser extension remain available with discovery off. PiStation does not set a project-trust override. |
| Runtime restart | Restart selected thread now restarts a healthy idle runtime and resumes its session. Active turns and pending questions/approvals block restart. Settings changes apply to new or restarted runtimes. |
| Skill invocation | `$skill:name` mentions resolve against Pi's effective discovered skills. Multiple/inline mentions include each skill body once, preserve arguments and attachments, and fail before host turn creation when a resource is unavailable. Code literals, escaped tokens and quoted context are excluded. |
| Command metadata | Current `sourceInfo` identity/path/scope/origin/base-directory metadata is retained, with fallback for older top-level metadata. FakePi fixtures now model current command names and metadata. |
| Extension commands | A command handled by an extension without starting a model turn can settle successfully instead of leaving a phantom running turn. The host checks ordered Pi idle state for discovered extension commands. |
| Extension UI | Notifications, keyed status, text widgets with removal and above/below placement, title updates and editor suggestions flow through typed per-thread state. Host and client share the same bounded reducer. Snapshots preserve state on reconnect; runtime restart clears it. |
| Draft protection | Extension titles label the extension panel. Editor suggestions require Insert into draft and append to existing text. Consumed suggestion identities are scoped to the thread and runtime epoch. No suggestion automatically replaces the draft. |

The application protocol is now **v23**. The embedded client and host are built together. Arbitrary TUI component factories, terminal overlays and custom editors are outside Pi's current text-based RPC UI contract.

## Validation

- Debug build: passed, zero warnings/errors.
- Debug code suites: 219 passed, zero failed — Protocol 25, PiRpc 56, Host 90, ClientRuntime 44, CommandSystem 4. Final TRX files are under `TestResults/pi-integration-debug/*-final.trx`.
- Installed **Pi 0.84.4**: the opt-in offline integration test passed. It uses an isolated agent home and deterministic provider fixture, without reading user credentials or contacting a model service. It verifies skill instructions at the provider boundary, an actual tool call, template arguments, extension commands/UI and removal, session resume, and discovery off/on/off across new runtimes. Evidence: `TestResults/pi-integration-debug/real-pi-offline-final.trx`.
- Native functional UI: the dedicated Pi integration slice passed using the packaged Debug app and FakePi. It covers extension content, explicit suggestion insertion, separate thread drafts, Settings save, idle restart and process relaunch. Final evidence: `TestResults/pi-integration-native/073dcf45e901415ca0a991465ff96734/`, including `result.json`, the final accessibility tree and isolated app data.
- Static visual contract: passed for 24 states, four responsive layouts and three text scales. This is a source/contract check, not screenshot acceptance.
- Release build: passed, zero warnings/errors. All 219 code tests passed with zero failures; final TRX files are under `TestResults/pi-integration-release/`.

The first concurrent Debug suite run exposed an incorrect new assertion (fixed) and an existing checkpoint test timeout under contention. The full Host suite subsequently passed in isolated runs. Final counts above come from the successful per-project runs, not the earlier failed artifacts.

Native screenshot capture remains **unverified**. Both desktop capture and the CLI screenshot path returned black images; the screenshot validator rejected them. Desktop activation also reported access denied. The functional slice was run without `-Capture`. The Pester scenario requests capture and will require a usable interactive desktop; no black screenshot is counted as a visual pass.

## Reproduce the focused checks

```powershell
dotnet build PiStationDesktop.slnx -c Debug -p:Platform=x64
dotnet test tests/PiStation.PiRpc.Tests/PiStation.PiRpc.Tests.csproj -c Debug --no-build --filter 'Category!=RealPiOffline'
dotnet test tests/PiStation.Host.Tests/PiStation.Host.Tests.csproj -c Debug --no-build
dotnet test tests/PiStation.ClientRuntime.Tests/PiStation.ClientRuntime.Tests.csproj -c Debug --no-build

$env:PISTATION_RUN_REAL_PI_OFFLINE = '1'
# Optional: $env:PISTATION_PI_PATH = 'C:/path/to/pi.ps1'
dotnet test tests/PiStation.PiRpc.Tests/PiStation.PiRpc.Tests.csproj -c Debug --no-build --filter 'Category=RealPiOffline'
Remove-Item Env:PISTATION_RUN_REAL_PI_OFFLINE

pwsh tests/PiStation.UiTests/Invoke-PiIntegrationSlice.ps1 -NoBuild
# On an interactive desktop, add -Capture to require a valid screenshot.
```

## Remaining work

Historical list at this milestone. The [September 6 resource/setup follow-up](PI-RESOURCES-AND-SETUP-2026-09-06.md) implements the core of item 1 and adds actual Pi 0.85 and authenticated skill/resume coverage from item 3.

1. Finish the native effective-resource/trust/error inventory and package/skill/prompt management. Configure project trust through Pi's CLI for now.
2. Complete real session import/fork/export, plan workflows and subagent controls, including a real subagent extension driving the Agents panel.
3. Validate the actual Pi 0.85.0 binary and authenticated providers. The reference source was inspected, but this milestone's installed-runtime execution used 0.84.4. Queue/compaction/browser/subagent/provider acceptance is not established by the offline fixture.
4. Complete PR hosted diffs and discussions, conversation artifacts, background/unread/settlement workflows, and the remaining local feature families in the audit.
5. Address additional agent runtimes/accounts, WSL/SSH/remote environments, pairing/relay and web/mobile clients as separate larger milestones.
6. Complete interactive visual and release acceptance. Production signing, publisher identity and update-feed setup remain explicitly deferred.

Usage instructions are in [Install and recovery](INSTALL-AND-RECOVERY.md#pi-extensions-and-skills). Validation covered the combined PiStation working tree, including earlier implementation work. No deployment was performed.
