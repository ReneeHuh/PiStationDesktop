# Native plan, approval and execution

September 6, 2026. Follows the [session management milestone](PI-SESSION-MANAGEMENT-2026-09-06.md). Protocol **v26** adds plan state and durable plan commands.

## Delivered workflow

The expandable **Plan** panel above the composer provides planning mode, a numbered-plan editor, save/reload, Markdown export, reported step progress, **Approve and execute remaining steps**, and return to normal tools. Choose planning mode and send the task through the composer. Pi's completed response proposes numbered steps under a `Plan:` heading. The user can edit and save them before approval. The editor retains unsaved changes across thread switches and rejects stale saves; execution requires the saved revision.

Approval starts a normal, host-owned turn against the saved plan without consuming the composer's draft or attachments. The existing Stop action interrupts it. Completed-step markers are accepted only from completed assistant responses; progress is agent-reported, not independent verification. A finished run with remaining steps becomes paused. Approving again continues those remaining steps. Completion restores the planning restrictions until the user explicitly returns to normal tools.

The bundled [plan extension](../src/PiStation.App/PiExtensions/pistation-plan.ts) is loaded independently of installed-extension discovery. Planning exposes four dedicated wrappers around Pi's built-in read, grep, find and ls tools. A dispatch hook blocks every other tool, including shell commands, writes, browser actions and extension tools. The policy is also re-applied before model turns. User shell execution through Pi is blocked while restricted. This is an enforced Pi tool policy; trusted extensions still execute Node code with process permissions. It is not an OS sandbox, a restriction on the user's Files/Terminal workbench, or T3's full set of permission modes.

## Persistence and recovery

The extension atomically stores state in `plans/<thread-id>.json` under PiStation's data root and appends portable custom entries to the Pi session. The separate state file preserves planning choices even before Pi has saved its first assistant response. Session entries carry plan state into copied/forked sessions. Checkpoint rewind, Pi fork and new-session transitions rebind state to the resulting session and revoke execution permission.

Each approval applies to one agent run. Process recreation changes an executing plan to paused and requires a new approval. Extension reload preserves the normal tool selection and re-applies the saved planning policy; an executing plan becomes paused. A failed dispatch closes the owned runtime so an uncertain approval cannot leave it available for an unrelated prompt. Missing or failed bundled policy blocks runtime readiness. A malformed/unwritable saved state fails closed; restore the state from a backup with PiStation closed before restarting the runtime.

Plan actions use the existing authenticated, durable command-receipt path. Identical command replay returns its recorded outcome; it does not execute again. The native controls disable changes during disconnection or an unresolved command. Host/client projections carry revisioned, session-scoped plan events through reconnect; older or foreign-session events are ignored. Plan text is limited to 32 KiB and 100 steps.

Implementation: [host workflow](../src/PiStation.Host/Threads/PiThreadController.Plan.cs), [command routing](../src/PiStation.Host/EnvironmentService.cs), [protocol state](../src/PiStation.Protocol/Models/PiPlan.cs), [native model](../src/PiStation.App/ViewModels/PiPlanViewModel.cs), [native actions](../src/PiStation.App/ViewModels/ShellViewModel.Plan.cs).

## Validation

The final Debug and Release solution builds succeeded with zero warnings and zero errors. Build logs are retained as `TestResults/pi-plan-debug/build-final.log` and `TestResults/pi-plan-release/build-final.log`.

All **237 distinct regression tests** have passing results in both Debug and Release, including the isolated timing-sensitive cases described below. Results are retained in `TestResults/pi-plan-debug` and `TestResults/pi-plan-release`.

| Suite | Debug passed | Release passed |
| --- | ---: | ---: |
| Protocol | 25 | 25 |
| PiRpc | 65 | 65 |
| Host | 98 | 98 |
| ClientRuntime | 45 | 45 |
| CommandSystem | 4 | 4 |
| **Total** | **237** | **237** |

- Actual Pi 0.84.4 and 0.85.0 offline tests passed with no skips. They exercised planning persistence before the first response, file reading, plan extraction/editing with Windows line endings, stale approval rejection, approved mutation, progress, process restart, Stop, fork and new-session transitions. A competing fixture extension deliberately re-enabled write/bash/custom mutation tools; the dispatch policy still blocked all three, and no mutation files were created. These use a deterministic local provider, without billed model calls.
- The final runtime test also reloads extensions during planning and verifies that a subsequent approved run can use the normal edit tool. It reproduced a lost-tool-selection defect before the fix and passed afterward on both Pi versions. Evidence: `TestResults/pi-plan-debug/real-plan-085-reload-before.trx`, `real-plan-085-reload-fixed.trx` and `real-plan-084-reload-fixed.trx`.
- Host tests cover authenticated approval, duplicate execution replay, retained composer drafts, separate threads, restart/progress and missing-policy rejection.
- Host/client projection tests cover serialization, reconnect and stale/foreign-session events.
- All five native checks passed: plan creation/editing and approval with the original draft preserved, thread separation, restart recovery, Markdown export through the Windows save dialog, and completion after approving the remaining step. Screenshot validation and visual inspection passed. Evidence: `TestResults/pi-plan-native/ede0c8b9e9a0468dada8f8b93bdd3d07/result.json`, `pi-plan.png` and `native-export.md`. The native runner is included in PR checks with required capture validation. Plan editing normalizes Windows line endings, and the expander's accessibility name tracks its current mode and progress.

The broader Debug Host run passed 96 of its 97 selected cases; the existing idle-runtime restart case exceeded its shared 20-second deadline. It passed in isolation in four seconds. The checkpoint case also passed separately. These timing-sensitive tests, and PiRpc's one-second startup/abort case, are run separately in the final regression grouping. No timeout thresholds were changed. The failed initial result is retained alongside the focused result in `TestResults/pi-plan-debug`.

## Reproduce

```powershell
dotnet build PiStationDesktop.slnx -c Debug -p:Platform=x64
dotnet test tests/PiStation.Host.Tests/PiStation.Host.Tests.csproj --filter FullyQualifiedName~PiPlanIntegrationTests

$env:PISTATION_RUN_REAL_PI_OFFLINE = '1'
$env:PISTATION_PI_PATH = 'C:/path/to/pi.ps1'
dotnet test tests/PiStation.PiRpc.Tests/PiStation.PiRpc.Tests.csproj --filter FullyQualifiedName~RealPiPlanTests

pwsh tests/PiStation.UiTests/Invoke-PiPlanSlice.ps1 -NoBuild -Capture
```

Next: actual subagent extension setup, workflows and child detail/control support (PI-10), followed by hosted PR diffs and inline discussions. Full permission-mode parity, arbitrary extension UI, and broader provider/physical-DPI/accessibility acceptance remain separate work.
