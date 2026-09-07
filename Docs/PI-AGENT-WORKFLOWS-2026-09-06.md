# Native Pi agent workflows

September 6, 2026. Follows [approved plan execution](PI-PLAN-WORKFLOW-2026-09-06.md). Protocol **v27** adds agent setup, durable workflow requests and child transcript/control fields.

September 7 integration review: these changes now follow the read-state/background-task/settlement commit. The integrated protocol is **v33**, so older v32 clients cannot silently omit agent-control fields. Focused host, settlement and client projection checks were rerun. Native acceptance below is historical evidence, not a new native run.

## Delivered behavior

Open the workbench's **Agents** tab and expand **Run workflow**. The bundled integration provides scout, planner, reviewer and worker presets, plus a native editor for custom presets. Enable/disable, refresh, save, delete and explicit editor reload use revision checks. Presets are shared across this PiStation data root; workflow drafts belong to the selected thread.

Single mode delegates one task. Parallel mode supports up to eight tasks with four running at once within the workflow. Chain mode runs tasks in order and substitutes `{previous}` with the preceding result. A failed or interrupted step prevents later chain steps from starting. Children share workspace files. Scout, planner and reviewer expose read/grep/find/ls; worker can also use bash/edit/write. Presets can specify a provider/model or inherit the parent's selected model. Thinking settings inherit and are clamped by Pi to the model's capabilities.

The launcher submits a parent turn through the existing durable command-receipt path. Pi receives the exact workflow as hidden tool instructions, while the conversation displays readable tasks. The parent model must invoke `pistation_subagent`; a requested workflow is reported as executed only when actual child activity arrives. The composer draft and attachments remain separate. The launcher collapses after acceptance so the activity list stays visible.

Each child has its own Pi SDK session, context, cancellation signal and saved JSONL file. SDK sessions run within the owned Pi process, so closing that runtime ends them. Parent provider registrations are copied through public Pi APIs. Children load their preset's built-in tools without recursively loading extensions, skills or prompt templates. This is context/tool isolation, not an OS sandbox.

## Controls and recovery

The activity list shows hierarchy, current work, state, tool/token metrics, result/failure summaries and **View transcript**. The dialog displays a bounded transcript snapshot and offers **Continue child** when saved context is available and the parent is idle. Continuation asks for a follow-up task and starts from a copy of the saved session, preserving the earlier activity's files and transcript. It uses the saved preset, even if that preset has since been edited or deleted.

**Stop child** targets one cancellation handle and leaves siblings running. The parent's Stop action aborts the workflow. External extensions without PiStation handles retain an explicitly labeled **Stop parent turn** action. Stale/finished controls are rejected. Children do not automatically restart after process recreation; interrupted activity is restored for inspection and explicit continuation when context was saved.

Activity projections and transcripts persist in the host database. Child files live beneath `agents/<thread-id>/<child-id>/`; configuration lives in `agent-presets.json`. Back up these with the database and parent sessions. Setup failures are displayed in the panel. Planning mode blocks delegation. A separate native workflow requires normal mode; an approved plan turn can delegate using its enabled tools.

Limits: 16 presets, 8,192 characters per preset instruction/task, 256 KiB preset configuration, eight tasks per workflow, a 30-minute limit per child run, a 32,768-character native transcript tail and a 16 MiB continuation source file. Individual message excerpts are limited to 8,192 characters; visible markers identify omitted text. Full child history remains in JSONL. Transient progress streams without writing duplicate transcripts to storage for each text delta. Continuation usage counts only the new run. Child usage appears in Agents; integration into the aggregate usage dashboard remains separate work.

Implementation: [bundled SDK extension](../src/PiStation.App/PiExtensions/pistation-agents.ts), [host commands](../src/PiStation.Host/Threads/PiThreadController.Agents.cs), [activity projection](../src/PiStation.Host/Threads/PiAgentActivityProjector.cs), [native state](../src/PiStation.App/ViewModels/PiAgentsViewModel.cs), [native actions](../src/PiStation.App/ViewModels/ShellViewModel.Agents.cs). Primary API references: [Pi SDK](../../Pi%20Agent/packages/coding-agent/src/core/sdk.ts), [upstream subprocess example](../../Pi%20Agent/packages/coding-agent/examples/extensions/subagent/README.md).

## Validation

Both Debug and Release solution builds passed with **zero warnings and errors**. All **241 distinct regression tests passed in each configuration**. Results are retained in `TestResults/pi-agents-debug/acceptance-*.trx` and `TestResults/pi-agents-release/acceptance-*.trx`, with each build log in `build-acceptance.log`. The bundled extensions in both application and test outputs match the source.

| Suite | Debug passed | Release passed |
| --- | ---: | ---: |
| Protocol | 25 | 25 |
| PiRpc | 65 | 65 |
| Host | 101 | 101 |
| ClientRuntime | 46 | 46 |
| CommandSystem | 4 | 4 |
| **Total** | **241** | **241** |

Actual Pi **0.84.4 and 0.85.0** tests passed with no skips. Evidence: `TestResults/pi-agents-debug/real-agents-084-acceptance.trx` and `real-agents-085-acceptance.trx`. Maximum Unicode preset/workflow payloads and continuation usage are covered alongside the behavior below.

All **five native checks** passed: preset creation, single delegation/transcript with the draft preserved, continuation, independent child Stop followed by parent Stop, and thread separation/restart recovery. Screenshot inspection confirmed that the newest workflow appears first and interrupted children are distinguished from failures. Evidence: `TestResults/pi-agents-native/3fd37264b06d47a0952d954a5e4ad0a3/result.json`, `pi-agents-active.png` and `pi-agents.png`. The native runner is included in PR checks. The visual contract also passed: 24 states, four responsive layouts and three text scales.

The first broader Host pass exposed a parser defect when optional numeric fields were explicitly `null`; both new workflow tests failed because updates were dropped. Numeric readers now check the JSON kind, regression coverage includes null step/tool/cache metrics, and the full selected Host group passed afterward. The initial failure is retained in `TestResults/pi-agents-debug/before-null-metrics-host.trx`. Existing timing-sensitive idle restart, checkpoint and one-second startup/abort tests ran separately with their original thresholds.

The actual-runtime fixture uses a deterministic local provider with no billed model calls. It tests real Pi SDK sessions, file reading, all modes, chain failure, independent cancellation, parent Stop, saved-context continuation, preset revisions and planning restrictions. Host tests exercise authenticated commands, replay, drafts, thread separation and restart. Native acceptance uses isolated FakePi for repeatable UI interaction; it does not replace the real-Pi tests.

```powershell
dotnet build PiStationDesktop.slnx -c Debug -p:Platform=x64
dotnet test tests/PiStation.Host.Tests/PiStation.Host.Tests.csproj --filter FullyQualifiedName~PiAgent
$env:PISTATION_RUN_REAL_PI_OFFLINE = '1'
$env:PISTATION_PI_PATH = 'C:/path/to/pi.ps1'
dotnet test tests/PiStation.PiRpc.Tests/PiStation.PiRpc.Tests.csproj --filter FullyQualifiedName~RealPiAgentTests
pwsh tests/PiStation.UiTests/Invoke-PiAgentsSlice.ps1 -NoBuild -Capture
```

Next: hosted PR diffs and inline discussions. This implements the core PI-10 workflow milestone; arbitrary external-extension child controls, process/remote isolation, aggregate child billing, broader authenticated providers and full T3 parity remain separate acceptance or implementation work.
