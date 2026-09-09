# PiStation master feature and issue tracker

Last updated: 2026-09-09 (PI-18/PI-19); inventory baseline reconciled 2026-09-08. Target: a C# / WinUI Windows application using **Pi as its only coding-agent runtime**.

This is the current tracking index for T3 parity, Pi integration, PiStation additions, defects, and delivery work. Keep completed features here so they are not repeatedly rediscovered as missing. Older documents are supporting history; their old scope decisions and missing-feature labels do not override this tracker.

## Project goal

**Bring the T3 Code features and subfeatures selected for PiStation into the app, and support the Pi coding agent completely within PiStation, including Pi-only features that T3 does not have.** Deliver the core application as native C# / WinUI on Windows, with Pi as the only coding-agent runtime. Features skipped to get PiStation working belong in the deferred implementation list at the end and will be implemented afterward.

The project has two equal requirements:

1. **Selected T3 features:** reproduce the T3 user workflows and subfeatures included in PiStation's scope, adapting agent-specific behavior to Pi and the interface to native Windows controls. A feature's presence in T3 does not automatically make it a PiStation requirement. Record skipped feature decisions and their reasons at the end, with implementation scheduled after the core app is working.
2. **Complete Pi support:** expose and preserve Pi's stable Windows-relevant capabilities, including runtime configuration, models/providers, tools, commands, skills, prompt templates, packages, extensions and supported extension interactions, turn/queue control, compaction/retry, sessions, branching, and import/export. Pi capabilities belong in the tracker even when T3 has no equivalent.

Track every missing or incomplete capability with its correct origin, implementation status, dependencies, and acceptance criteria. A limitation in the current Pi RPC transport is an integration gap to resolve through an SDK adapter, native bridge, or upstream protocol work; it is not by itself a reason to omit the feature or claim complete Pi support. Distinguish stable Pi capabilities from arbitrary third-party extension behavior and experimental APIs, and record compatibility requirements explicitly.

**Feature origin, delivery phase, and implementation status are separate decisions.** Pi-only features are first-class requirements for complete Pi support, even without a T3 equivalent. T3-only features may be included now, adapted, or deferred. A feature can also come from both projects or be a PiStation addition. Deferred features remain committed follow-up work; they do not block the first working milestone. Unresolved inclusion decisions must be recorded for review rather than silently included or excluded.

The first working milestone remains Windows-focused and Pi-only. Previously skipped platform expansion and remote installation/update automation are deferred to the final section. Until that work is delivered, users install and update software on the other Windows computer themselves. Pi remains the only coding-agent runtime; deferring features does not add other agent harnesses to the product.

The core milestone is achieved when its included T3 and Pi workflows are usable and their applicable acceptance checks pass. The overall project also includes implementing the deferred feature list after that milestone. Source presence alone does not establish complete support, and a working core does not mark deferred features Done.

## Scope and baselines

| Item | Decision |
| --- | --- |
| Client | Native Windows WinUI. Current packaging targets Windows x64. |
| Coding agent | Pi only. Multiple model providers through Pi remain in scope; additional agent harnesses do not. |
| T3 feature selection | Include applicable, selected T3 features now; retain skipped features and their reasons in the deferred implementation list. Product boundaries such as Pi-only remain explicit. |
| Pi-only features | Include stable Windows-relevant Pi capabilities even when T3 has no equivalent. Track native integration and compatibility gaps explicitly. |
| Remote access | Windows client to Windows host. Kept in a separate section because remote work is already underway. A local-only view excludes REM and remote issue rows. |
| Remote installation and updating | Deferred until the core app is working (SCOPE-08). The user installs/updates on the other computer for now. Compatibility checks and manual-update recovery remain core work. |
| Other platforms | Deferred until the core app is working: Linux/macOS, WSL, web, and mobile. WinUI remains the initial Windows client; later clients require an appropriate separate presentation layer. |
| Skipped feature decisions | Append each decision to the final deferred section with its reason, current progress, dependencies, and completion target. Implement these after the core milestone. |
| Optional additions | Additional product proposals remain optional. Stable Pi compatibility gaps required by the project goal are tracked as required investigations, including gaps in the current RPC transport. |
| PiStation source baseline | `36dc4641320a63b0e8a6969a98faede9860f8303` |
| T3 source baseline | Local `../t3code`, `0a590fa01af66ec135d2ebf2d5542b08a37dc275`, dated 2026-09-04. Not a claim about the newest upstream release. |
| Pi source baseline | Local `../Pi Agent`, `17de82d7bea18a6589677a9761baabc2060c9efb`, coding-agent package 0.85.0. |

The inventory reconciles the earlier source review, existing milestone reports, and focused source checks. It is not a new complete runtime certification. Newly discovered upstream subfeatures should receive new rows; do not infer exhaustive equivalence from a broad feature name.

## Labels and maintenance

**Origin describes the requested behavior, not which repository contains the implementing code or whether the feature is included.** Pi being able to run `git` in a shell does not make a native PR dashboard a Pi feature.

Inclusion and phase are recorded by the scope decisions and section placement: the main inventory is core work; the final deferred list is implementation work after the core milestone; the product-boundary register describes non-applicable harness/library work. Optional proposals still await a decision except for explicitly required compatibility investigations. A proposal the user has not selected is different from a selected feature they chose to skip temporarily. Keep phase separate from implementation progress.

| Origin | Meaning |
| --- | --- |
| T3 | A T3 application workflow to reproduce within the Windows/Pi scope. |
| Pi | A stable Pi coding-agent feature to expose or preserve. |
| Both | A comparable user capability exists in T3 and Pi; implementation and provider limits may differ. |
| Pi extension | A Pi SDK/extension capability or example, not a guaranteed built-in Pi feature. |
| PiStation | A native application behavior or adaptation specific to this product. |
| Delivery | Packaging, qualification, accessibility, performance verification, or engineering work. Not product parity. |

| Implementation status | Meaning |
| --- | --- |
| Done | A connected implementation exists for the behavior and limits stated in that row. This does not imply release qualification. |
| Partial | A useful subset exists; the remaining behavior is stated explicitly. |
| Not started | No connected implementation for that specific behavior was found in the reviewed baseline. |
| Review needed | The mapping or current implementation was not established well enough to rate. Do not silently treat it as missing. |
| Out of scope | Excluded by the product scope, even if old or current code implements part of it. |

**Deferred** is a delivery phase, not an implementation status. A deferred feature can still be Not started, Partial, or Review needed. Keep its actual progress and evidence; do not mark it Done when the core app becomes usable.

**Evidence:** `S` = source-reviewed assessment, without a fresh end-to-end run for this row; `H` = linked historical milestone records relevant validation, with its stated limits; `V` = current focused verification described beside the table; `P` = acceptance or investigation pending; `N/A` = excluded. Evidence links appear immediately before each group's table. H does not mean every subfeature was independently tested, and old results do not certify later changes.

Maintain stable IDs. Split a row when subfeatures have different statuses, origins, or scope. Update its remaining-work cell, evidence, and reconciliation date with the implementation. A checkbox or protocol enum alone is not completion. A release claim also requires the applicable DEL checks. Keep defects in the issue register and link them to feature IDs rather than duplicating the feature backlog. Do not calculate a parity percentage across differently sized features or count deferred work against the first working milestone. Keep the deferred section last when adding new material.

Useful views: outstanding core local work = core rows marked Partial or Not started outside REM; T3 parity = T3/Both rows with their provider qualifications and delivery phase; Pi integration = Pi/Pi extension/Both rows; release readiness = DEL plus unresolved defects; later implementation = the final deferred section. `Review needed` is its own investigation queue.

## 1. Native shell, navigation, and thread organization

Evidence: [shell/layout](src/PiStation.App/ViewModels/ShellLayoutViewModel.cs), [command rules](src/PiStation.App/Commands/CommandSystem.cs), [thread inbox](src/PiStation.ClientRuntime/ThreadInbox.cs), [sidebar behavior](src/PiStation.App/ViewModels/ShellViewModel.Sidebar.cs), [read state milestone](Docs/THREAD-READ-STATE-2026-09-07.md), [settlement milestone](Docs/AUTO-SETTLEMENT-2026-09-07.md), [T3 sidebar](../t3code/docs/user/thread-sidebar.md), [T3 settings](../t3code/packages/contracts/src/settings.ts).

| ID | Feature / subfeature | Origin | Status | Evidence | Remaining work / completion boundary |
| --- | --- | --- | --- | --- | --- |
| UI-01 | Native Windows shell, title bar, project/thread hierarchy | PiStation | Done | S | WinUI implementation; release acceptance is DEL. |
| UI-02 | Collapsible/resizable sidebar and workbench; narrow overlay layout | T3 | Done | H | Persisted layout; physical monitor/scaling checks remain DEL-04. |
| UI-03 | Thread rename and generated titles | Both | Done | S | Retain both explicit and generated title actions. |
| UI-04 | Pin, unpin, reorder threads | T3 | Done | S | Persist ordering and state. |
| UI-05 | Archive, restore, delete | T3 | Done | S | Preserve distinct lifecycle actions. |
| UI-06 | Explicit settle/unsettle | T3 | Done | S | Separate task settlement from archive. |
| UI-07 | Snooze and wake | T3 | Done | S | Timed wake and shelves implemented. |
| UI-08 | Persistent unread state and mark unread | T3 | Done | H | Read watermark and newer activity remain distinct. |
| UI-09 | Automatic inactivity and merged-PR settlement | T3 | Done | H | Policy respects active/pending work. |
| UI-10 | Bulk thread actions | T3 | Done | S | Apply supported lifecycle operations to selected threads. |
| UI-11 | Link and unlink PR metadata | T3 | Done | S | Hosting operations remain in HOST. |
| UI-12 | Sidebar grouping/sorting, previews, timestamps, confirmations | T3 | Done | S | Retain implemented preferences; identify any new upstream setting separately. |
| UI-13 | Search projects, branches, threads, and messages; reveal result | T3 | Done | S | Environment scope is REM-03. |
| UI-14 | Command palette and editable shortcuts | Both | Done | S | Keyboard/accessibility qualification is DEL-03. |
| UI-15 | Context-aware shortcut conditions and conflict handling | T3 | Done | S | Native command rules support focus/open-state conditions; compare new upstream context keys separately. |
| UI-16 | Configurable quit gesture/confirmation behavior | T3 | Review needed | P | T3 offers direct/hold/double-press policies; establish equivalent current WinUI behavior before rating the gap. |
| UI-17 | Proactive workbench panel preference | T3 | Review needed | P | Map T3's optional automatic panel behavior to current WinUI panel ownership. |

## 2. Composer, conversation, and artifacts

Evidence: [composer](src/PiStation.App/ViewModels/ComposerViewModel.cs), [power features](src/PiStation.App/ViewModels/ComposerPowerViewModel.cs), [sent content milestone](Docs/SENT-CONTENT-2026-09-07.md), [background tasks](Docs/BACKGROUND-TASKS-2026-09-07.md), [artifact routing](src/PiStation.App/ViewModels/ShellViewModel.Artifacts.cs), [T3 composer](../t3code/docs/user/composer.md), [Pi interactive features](<../Pi Agent/packages/coding-agent/README.md>).

| ID | Feature / subfeature | Origin | Status | Evidence | Remaining work / completion boundary |
| --- | --- | --- | --- | --- | --- |
| CHAT-01 | Durable per-thread drafts and revision conflicts | T3 | Done | H | Preserve unsent input across navigation/restart. |
| CHAT-02 | Paste/drop images; add/remove file attachments | Both | Done | S | Respect model and transport capabilities/limits. |
| CHAT-03 | HEIC/HEIF conversion for composer input | T3 | Not started | S | Convert supported inputs and show useful conversion failures. |
| CHAT-04 | Workspace file mentions and path search | Both | Done | S | Ranked picker and thread-owned context. |
| CHAT-05 | Quote/comment on assistant selections | T3 | Done | S | Preserve quoted source and user comment. |
| CHAT-06 | File/diff line ranges as context | T3 | Done | S | Preserve range and source identity. |
| CHAT-07 | Stash/restore complete prompts with attachments | T3 | Done | S | Preserve attachment ownership and retention behavior. |
| CHAT-08 | Submit an independent background task while continuing a draft | T3 | Done | H | New task has independent thread/workflow identity. |
| CHAT-09 | Stream assistant text, reasoning, and tool progress | Both | Done | S | Ordered events and interrupted/error states remain visible. |
| CHAT-10 | Markdown, code, tables, task lists, images | Both | Done | S | Rich-content rendering acceptance remains DEL-04. |
| CHAT-11 | Expand/collapse reasoning and tool output; copy content | Both | Done | S | Keep full source available within documented bounds. |
| CHAT-12 | Follow output, jump to latest, per-thread scroll position | T3 | Done | S | Preserve reading position while background output arrives. |
| CHAT-13 | Sent attachment previews and save/copy/download | T3 | Done | H | Remote transfer path is REM-12. |
| CHAT-14 | Durable sent citations, comments, source navigation | T3 | Done | H | Navigation resolves original source scope. |
| CHAT-15 | Workspace file/line links and outside-workspace read-only artifacts | T3 | Done | S | Remote path handling is REM-12. |
| CHAT-16 | Edit a prompt in an external editor | Pi | Not started | S | Round-trip prompt text without losing draft/attachments; Pi TUI has this workflow. |
| CHAT-17 | Resting composer collapse on blur/scroll preferences | T3 | Review needed | P | Compare behavior and independent controls against T3 client settings. |
| CHAT-18 | Control skill visibility in the slash menu | T3 | Review needed | P | Skill discovery works; establish whether the display preference is exposed. |

## 3. Pi runtime, models, authentication, and automation

Evidence: [runtime configuration](src/PiStation.App/ViewModels/ShellViewModel.Setup.cs), [host setup](src/PiStation.Host/EnvironmentService.Runtime.cs), [models](src/PiStation.App/ViewModels/PiConfigurationViewModel.cs), [resources extension](src/PiStation.App/PiExtensions/pistation-resources.ts), [resource/setup milestone](Docs/PI-RESOURCES-AND-SETUP-2026-09-06.md), [automation milestone](Docs/PI-AUTOMATION-SETTINGS-2026-09-07.md), [Pi RPC](<../Pi Agent/packages/coding-agent/docs/rpc.md>), [T3 provider maintenance](../t3code/apps/server/src/provider/providerMaintenance.ts).

PI-18/PI-19 verification, 2026-09-09: [host execution/recovery tests](tests/PiStation.Host.Tests/PiShellTests.cs), [real Pi offline shell/context test](tests/PiStation.PiRpc.Tests/PiShellTests.cs), [projection replay](tests/PiStation.ClientRuntime.Tests/ProjectionStoreTests.cs), [wire serialization](tests/PiStation.Protocol.Tests/ProtocolSerializationTests.cs), and [native WinUI acceptance](tests/PiStation.UiTests/Invoke-PiShellSlice.ps1). The x64 solution builds without warnings; 106 focused protocol, projection, host/lifecycle/authorization, transport, and shell tests passed, including the installed-Pi test. Native UI Automation verified streaming, context selection, closing/reopening the dialog, cancellation, nonzero exit, preserved prompt draft, and app restart recovery. Screenshot capture and its fallback produced blank images, so visual review remains pending under DEL-04. Protocol version is 42; clients and hosts must use matching builds.

| ID | Feature / subfeature | Origin | Status | Evidence | Remaining work / completion boundary |
| --- | --- | --- | --- | --- | --- |
| PI-01 | Discover/configure Pi executable; startup health, retry, idle restart | Pi | Done | H | Existing installation workflow; clean-machine checks are DEL-06. |
| PI-02 | Runtime arguments, environment variables, extension paths | Both | Done | S | Local UI implemented. Remote setup issue BUG-01 remains separate. |
| PI-03 | Multiple model providers through Pi | Both | Done | H | Does not add separate coding-agent harnesses. |
| PI-04 | Switch model and supported thinking level | Both | Done | H | Capabilities differ by model. |
| PI-05 | Project/new-thread model and reasoning defaults | Both | Done | S | Persist and apply defaults to the appropriate new session. |
| PI-06 | Search/group model picker; favorites, hidden models, ordering | Both | Done | S | Named Pi account-instance selection is deferred in SCOPE-04. |
| PI-07 | Refresh model catalog; custom endpoints/models and local inference | Both | Done | S | A configured compatible local endpoint works through Pi; no model-server installer implied. |
| PI-08 | Credential-source status and supported login/logout | Both | Done | S | Native supported auth paths plus guided Pi terminal fallback; do not collect secrets in transcript. |
| PI-09 | Fresh authentication and broad provider compatibility | Delivery | Partial | H | Existing-account smoke is recorded; fresh OAuth/API-key onboarding and other supported providers require DEL-06. |
| PI-10 | Context, token, cache, and reported cost indicators | Both | Done | S | Unknown usage/cost remains unknown. Dashboard gaps are USE. |
| PI-11 | Stop active turn; handle interruption and restart | Both | Done | H | Cancel the active Pi operation and retain recoverable state. |
| PI-12 | Steering while running | Both | Done | S | Uses Pi steering semantics. |
| PI-13 | Separate follow-up queue | Pi | Done | S | Distinct from steering. |
| PI-14 | Inspect/clear queues; all versus one-at-a-time delivery | Pi | Done | S | Apply separate Pi queue modes. |
| PI-15 | Manual compaction with optional instructions | Both | Done | H | Preserve useful failure/progress information. |
| PI-16 | Automatic compaction/retry and retry cancellation | Pi | Done | S | Runtime behavior and events are integrated. |
| PI-17 | Persist/manage auto-compaction and auto-retry overrides | Pi | Done | H | Saved/applied revisions and unmanaged defaults are explicit; native qualification is pending. |
| PI-18 | Direct user shell through Pi `bash` RPC | Pi | Done | V | Composer's Pi shell dialog runs in the thread workspace while Pi is idle and plan mode is off. Streams correlated output, preserves exit/truncation/full-output path, includes/excludes model context, and keeps the prompt draft separate. Displays at most 64K output characters; Pi session history and the latest result survive restart. Session entry advances before the next turn's checkpoint. Agent tool execution remains TOOL-04. |
| PI-19 | Cancel a direct Pi shell command with `abort_bash` | Pi | Done | V | Cancels the selected active shell execution independently of agent stop. Cancellation remains pending until Pi confirms its final result. Accepted execution belongs to the host; reconnect/retry does not rerun it. Unfinished saved commands become Interrupted after host restart and are never automatically replayed. |
| PI-20 | Additional provider-specific Pi options/service tiers | Pi | Review needed | P | Inventory stable Pi controls and applicable providers before specifying native settings. Do not copy Codex-only knobs. |
| PI-21 | Pi startup telemetry, offline, and version-check preferences | Pi | Partial | S | General arguments/environment support exists; dedicated discoverable preferences and effective-state verification are not established. |

## 4. Pi instructions, skills, packages, and extension UI

Evidence: [Pi integration milestone](Docs/PI-INTEGRATION-MILESTONE-2026-09-05.md), [resources UI](src/PiStation.App/ViewModels/PiResourcesViewModel.cs), [package search](src/PiStation.Host/Projects/PiPackageCatalog.cs), [resource management](src/PiStation.App/PiExtensions/pistation-resources.ts), [extension UI](src/PiStation.App/ViewModels/PiExtensionUiViewModel.cs), [Pi resource docs](<../Pi Agent/packages/coding-agent/README.md>), [Pi RPC UI limits](<../Pi Agent/packages/coding-agent/docs/rpc.md#extension-ui-protocol>).

| ID | Feature / subfeature | Origin | Status | Evidence | Remaining work / completion boundary |
| --- | --- | --- | --- | --- | --- |
| EXT-01 | Load instruction files, overrides, system prompt and append prompt | Pi | Done | S | Follow Pi precedence/trust; native editor is Files, not a duplicate prompt engine. |
| EXT-02 | Discover commands, skills, and prompt templates with source metadata | Both | Done | H | Reflect active Pi resources. |
| EXT-03 | Native `/skill` and inline `$skill` expansion | Pi | Done | H | Preserve arguments/attachments and reject unresolved resources before dispatch. |
| EXT-04 | Prompt templates and arguments | Pi | Done | H | Delegate supported template semantics to Pi. |
| EXT-05 | Discover extensions and configure explicit trusted paths | Pi | Done | H | Discovery setting and bundled extensions are distinct. |
| EXT-06 | Enable/disable local and packaged skills/templates/extensions | Pi | Done | H | Keep saved configuration distinct from effective runtime. |
| EXT-07 | Inspect resources and exact load/failure attribution | Pi | Partial | S | Improve attribution for extensions registering no visible feature; do not label unconfirmed loads successful. |
| EXT-08 | Search/install/remove/update Pi resource packages | Pi | Done | S | Native package management now exists; older terminal-only descriptions are stale. This is not runtime installation. |
| EXT-09 | Saved/effective project resource trust | Pi | Done | H | Separate from script trust and tool permission modes. |
| EXT-10 | Apply resource changes through explicit idle runtime restart | Pi | Done | H | Preserve drafts and explain when restart is required. |
| EXT-11 | In-process resource reload matching applicable Pi behavior | Pi | Review needed | P | Establish safe supported RPC/SDK route; restarting Pi already provides a working apply path. |
| EXT-12 | Extension tools, hooks, and tool replacement | Pi extension | Done | H | Loaded extensions execute through Pi; custom terminal rendering is not transported. |
| EXT-13 | Confirm/select/input/editor dialogs with cancellation | Pi | Done | H | These RPC dialogs are supported; arbitrary custom editors are OPT-02. |
| EXT-14 | Notifications, keyed status, titles, text widgets, offered editor text | Pi | Done | H | Supported RPC text UI is implemented, including widget placement/removal. |
| EXT-15 | Arbitrary terminal components, overlays, headers/footers, loading indicators | Pi extension | Review needed | P | Required compatibility investigation for complete Pi support, linked to OPT-02. Pi RPC returns defaults/no-ops for several APIs; establish native equivalents and required SDK/protocol support. |

## 5. Sessions, branches, import, and export

Evidence: [session milestone](Docs/PI-SESSION-MANAGEMENT-2026-09-06.md), [session UI](src/PiStation.App/ViewModels/PiSessionsViewModel.cs), [session service](src/PiStation.Host/EnvironmentService.Sessions.cs), [transfer UI](src/PiStation.App/ViewModels/ShellViewModel.SessionTransfers.cs), [Pi sessions](<../Pi Agent/packages/coding-agent/docs/sessions.md>).

| ID | Feature / subfeature | Origin | Status | Evidence | Remaining work / completion boundary |
| --- | --- | --- | --- | --- | --- |
| SES-01 | Resume/hydrate persistent Pi sessions after restart | Both | Done | H | Keep Pi session and application thread identities consistent. |
| SES-02 | Browse/search/page external session files and import JSONL | Pi | Done | H | Validation and source ownership retained. |
| SES-03 | Independent whole-session tree copies | Pi | Done | H | New identities; original remains unchanged. |
| SES-04 | Fork at a completed assistant response, including alternate branches | Pi | Done | H | Copy selected ancestry; no implied workspace rewind. |
| SES-05 | Inspect/page branch tree and active-branch statistics | Pi | Done | H | Display bounded results and load-more controls. |
| SES-06 | Navigate an existing session in place between branches | Pi | Partial | S | Inspection and independent forks exist; native in-place switching remains. |
| SES-07 | Session tree labels/bookmarks and branch filtering/search | Pi | Partial | S | Tree inspection exists; label editing and Pi-equivalent tree search remain. |
| SES-08 | Export full validated JSONL | Pi | Done | H | Preserve full validated session, including embedded content. |
| SES-09 | Export readable HTML | Pi | Partial | H | Active-branch text export exists; richer Pi-equivalent media rendering remains. |
| SES-10 | Portable session bundle import/export | PiStation | Done | S | ZIP/session transfer workflow exists; retained references obey bundle boundaries. |
| SES-11 | Share a session through Pi's gist workflow | Pi | Not started | S | Provide explicit user-selected share/export workflow and report provider outcome. Never share automatically. |
| SES-12 | Ephemeral conversation without persistent Pi history | Pi | Not started | S | Define application draft/history semantics before exposing Pi `--no-session`. |

## 6. Planning, permissions, and agent tools

Evidence: [plan milestone](Docs/PI-PLAN-WORKFLOW-2026-09-06.md), [plan UI](src/PiStation.App/ViewModels/PiPlanViewModel.cs), [permissions extension](src/PiStation.App/PiExtensions/pistation-permissions.ts), [T3 modes](../t3code/docs/user/permission-modes.md), [Pi tools](<../Pi Agent/packages/coding-agent/src/core/tools/index.ts>), [Pi CLI flags](<../Pi Agent/packages/coding-agent/src/cli/args.ts>).

| ID | Feature / subfeature | Origin | Status | Evidence | Remaining work / completion boundary |
| --- | --- | --- | --- | --- | --- |
| PLAN-01 | Read-only plan generation with constrained tools | Pi extension | Done | H | Bundled policy uses read/search wrappers; no OS sandbox claim. |
| PLAN-02 | Native plan editing and approval of saved revision | PiStation | Done | H | This exact editor/approval workflow is an application addition. |
| PLAN-03 | Export saved plan as Markdown | T3 | Done | H | Export approved/saved artifact explicitly. |
| PLAN-04 | Step progress, pause, and resume remaining work | Pi extension | Done | H | Progress is agent-reported; restart requires renewed execution approval. |
| PLAN-05 | Supervised, auto-accept edits, and full-access modes | T3 | Done | S | Enforced through bundled Pi hooks; not built into Pi core and not an OS sandbox. |
| PLAN-06 | Auto mode with honest fallback to user approval | T3 | Done | S | Pi lacks the reviewer used by certain T3 providers; fallback is supported behavior, not a missing universal reviewer. |
| TOOL-01 | Agent reads text and supported images | Pi | Done | S | Built-in read tool subject to active policy. |
| TOOL-02 | Agent creates/writes files | Pi | Done | S | Built-in write tool subject to active policy. |
| TOOL-03 | Agent performs targeted edits | Pi | Done | S | Built-in edit tool subject to active policy. |
| TOOL-04 | Agent executes shell commands | Pi | Done | S | This is model-invoked bash, separate from direct user shell PI-18. |
| TOOL-05 | Windows PowerShell agent tool | Pi | Review needed | P | Verify enabled tool/version and permission behavior; PowerShell terminal support alone does not prove agent-tool support. |
| TOOL-06 | Dedicated grep/find/ls tools | Pi | Done | S | Available through supported Pi tool configuration and bundled planning tools. |
| TOOL-07 | General tool allowlist/exclusion | Pi | Partial | S | Launch arguments and planning constraints exist; a dedicated editor/effective inventory is not established. |
| TOOL-08 | Sequential/parallel tool batch execution controls | Pi | Partial | S | Pi engine behavior is inherited; native setting and applicable runtime API require review. |

## 7. Subagent workflows

Evidence: [agent milestone and limits](Docs/PI-AGENT-WORKFLOWS-2026-09-06.md), [bundled SDK extension](src/PiStation.App/PiExtensions/pistation-agents.ts), [agent UI](src/PiStation.App/ViewModels/PiAgentsViewModel.cs), [Pi optional subagent example](<../Pi Agent/packages/coding-agent/examples/extensions/subagent/README.md>).

| ID | Feature / subfeature | Origin | Status | Evidence | Remaining work / completion boundary |
| --- | --- | --- | --- | --- | --- |
| AGENT-01 | Delegate into separate Pi SDK child contexts | Pi extension | Done | H | Child contexts share the owned process/workspace; no process or OS isolation claim. |
| AGENT-02 | Single, parallel, and chained workflows | Pi extension | Done | H | Existing limits: eight tasks, four concurrent children; chain failures stop later steps. |
| AGENT-03 | Presets: create/edit/delete/enable; tools/model/instructions | PiStation | Done | H | Persist revisions and workflow drafts. |
| AGENT-04 | Hierarchy, activity, progress, results, and per-child usage | T3 | Done | H | Compatible activity is displayed; aggregate usage is USE-07. |
| AGENT-05 | Inspect bundled child transcripts | PiStation | Done | H | Bounded native transcript plus saved JSONL. |
| AGENT-06 | Stop one bundled child without stopping siblings | Pi extension | Done | H | Use owned cancellation handle; parent Stop cancels workflow. |
| AGENT-07 | Continue a bundled child from copied saved context | Pi extension | Done | H | Explicit continuation after interruption/restart, not automatic live-process restoration. |
| AGENT-08 | Targeted control of arbitrary external-extension children | Pi extension | Partial | S | Parent-stop fallback exists. Investigate compatibility with extensions exposing independent control; adapter/handle dependencies are tracked in OPT-03. Do not promise controls an extension does not provide. |

## 8. Projects, Git, and workspace changes

Evidence: [project customization](src/PiStation.App/Views/ShellPage.ProjectCustomization.cs), [configuration](src/PiStation.Host/Projects/ProjectConfigurationLoader.cs), [auto-pull](src/PiStation.Host/Projects/ProjectAutoPullService.cs), [Git service](src/PiStation.Host/Git/WorkspaceGitCommandService.cs), [checkpoints](src/PiStation.Host/Git/WorkspaceCheckpointService.cs), [T3 project settings](../t3code/docs/user/project-settings.md).

| ID | Feature / subfeature | Origin | Status | Evidence | Remaining work / completion boundary |
| --- | --- | --- | --- | --- | --- |
| PROJ-01 | Add/remove local project folders | T3 | Done | S | Preserve project/thread ownership. |
| PROJ-02 | Clone from a Git URL to selected destination | T3 | Done | S | Host/account discovery is HOST-01. |
| PROJ-03 | Local checkout versus managed worktree per thread | T3 | Done | S | Correct session working directory. |
| PROJ-04 | Setup/named scripts, trust, and worktree setup hooks | T3 | Done | S | Script execution and trust implemented. |
| PROJ-05 | Native script create/edit/remove controls | T3 | Done | S | Do not reclassify as missing based on older config-only reviews. |
| PROJ-06 | Project emoji/image/automatic icon selection | T3 | Done | S | Local editing exists; remote image paths are BUG-03. |
| PROJ-07 | Shared project icon behavior across grouped checkouts | T3 | Partial | S | Reconcile editing/storage across the whole repository group. Automatic script replication is not part of this row. |
| PROJ-08 | Safe automatic default-branch fast-forward pull | T3 | Done | S | Skip dirty/diverged/wrong-branch/no-upstream checkouts. |
| GIT-01 | Status, refs, branches, upstream divergence | T3 | Done | S | Structured workspace state. |
| GIT-02 | Initialize, pull, commit, push | T3 | Done | S | Preserve operation failures and recovery. |
| GIT-03 | Select files for commit | T3 | Done | S | Commit intended selection. |
| GIT-04 | Create/list/remove managed worktrees | T3 | Done | S | Protect active work and retain ownership. |
| GIT-05 | Unified/split structured diff, gutters, syntax, hunk navigation | T3 | Done | S | Rendering/keyboard qualification remains DEL. |
| GIT-06 | Collapse files/hunks and attach diff-line context | T3 | Done | S | Keep source coordinates accurate. |
| GIT-07 | Per-turn workspace checkpoints | T3 | Done | H | Historical timing issue tracked as BUG-04. |
| GIT-08 | Rewind workspace and conversation together | T3 | Done | H | Confirm selected recovery point and preserve failure state. |

## 9. Hosting, pull requests, and review

Evidence: [current capability gates](src/PiStation.Protocol/Models/HostingCapabilities.cs), [review UI](src/PiStation.App/ViewModels/PullRequestReviewViewModel.cs), [management](src/PiStation.App/ViewModels/PullRequestReviewViewModel.Management.cs), [advanced actions](src/PiStation.App/ViewModels/PullRequestReviewViewModel.Advanced.cs), [writing](src/PiStation.App/ViewModels/ShellViewModel.Writing.cs), [earlier review milestone](Docs/PI-PR-REVIEW-2026-09-06.md), [T3 host differences](../t3code/docs/user/source-control.md).

All rows in this group have T3 origin. PiStation uses Pi for generated text; CLI/API access alone is not a native review workflow. GitHub advanced controls were added after the earlier milestone's missing-feature list.

| ID | Feature / subfeature | Origin | Status | Evidence | Remaining work / completion boundary |
| --- | --- | --- | --- | --- | --- |
| HOST-01 | Browse hosting accounts/repositories before clone | T3 | Not started | S | Discover available repositories, choose one, and clone to selected destination. |
| HOST-02 | Source-control tool/authentication status and rescan | T3 | Done | S | Uses supported host tools/auth; fresh-account acceptance remains DEL-07. |
| HOST-03 | GitHub PR list/create/link | T3 | Done | S | Respect repository and credential scope. |
| HOST-04 | GitLab MR list/create and existing supported actions | T3 | Done | S | Does not imply detailed native review/publishing. |
| HOST-05 | Azure PR list/create and existing supported actions | T3 | Done | S | Does not imply detailed native review/publishing. |
| HOST-06 | Bitbucket account/repository and PR adapter | T3 | Not started | S | Add supported clone/publish/list/create/review/actions; do not require reopening declined PRs. |
| HOST-07 | Publish local repository to GitHub | T3 | Done | S | Retain empty-repository and push handling. |
| HOST-08 | Publish local repository to GitLab | T3 | Not started | S | Current publication capability is GitHub-only. |
| HOST-09 | Publish local repository to Azure | T3 | Not started | S | Add provider-specific repository creation/origin/push. |
| HOST-10 | Cross-repository PR browsing/filtering | T3 | Partial | S | Current review/filter behavior is narrower; combine repositories without mixing drafts or actions. |
| HOST-11 | GitHub PR body/metadata, commits, checks, comments, reviews | T3 | Done | S | Current native detail flow exists. |
| HOST-12 | GitHub hosted diffs and review checkout/thread/worktree flow | T3 | Done | S | Retain current-head/source validation and omitted/binary patch states. |
| HOST-13 | GitHub general comments, approvals, requested changes | T3 | Done | H | Actual authenticated acceptance is DEL-07. |
| HOST-14 | GitHub inline drafts, submit, replies, resolve/reopen | T3 | Done | H | Durable drafts and stale-head protection remain required. |
| HOST-15 | GitHub edit title/body and permitted own comments/reviews | T3 | Done | S | Respect permissions and revision conflicts. |
| HOST-16 | GitHub reactions and supported metadata/reviewer management | T3 | Done | S | Preserve capability-aware actions. |
| HOST-17 | GitHub merge methods, close/reopen, branch update | T3 | Done | S | Preserve head/check/permission guards. |
| HOST-18 | GitHub auto-merge enable/disable | T3 | Done | S | Live provider acceptance remains DEL-07. |
| HOST-19 | GitHub waiting fork-workflow approval and revert PR | T3 | Done | S | Capability/head/state validation applies. |
| HOST-20 | GitLab detailed review parity | T3 | Partial | S | Existing actions/list/create are present; add supported diff/discussion/edit/review flows. |
| HOST-21 | Azure detailed review parity | T3 | Partial | S | Match supported T3 metadata/activity/actions. T3 uses the website for Azure diffs and comment changes. |
| HOST-22 | GitLab/Azure supported auto-merge and advanced actions | T3 | Partial | S | Expand provider capability coverage; do not copy GitHub-only actions. |
| HOST-23 | Generate commit/PR text from actual changes | T3 | Done | S | Pi-based generation uses working/committed branch changes as appropriate. |
| HOST-24 | Writing model/style and repository conventions | T3 | Done | S | Retain selected generation preferences. |
| HOST-25 | Durable hosting receipts and uncertain-write recovery | T3 | Done | H | Reconcile outcome without automatically posting again. |

## 10. Files and integrated terminal

Evidence: [files UI](src/PiStation.App/ViewModels/WorkbenchFilesViewModel.cs), [file service](src/PiStation.Host/Files/WorkspaceFileReadService.cs), [paging](src/PiStation.App/ViewModels/ShellViewModel.Pagination.cs), [terminal UI](src/PiStation.App/ViewModels/WorkbenchTerminalViewModel.cs), [terminal registry](src/PiStation.Host/Terminals/TerminalSessionRegistry.cs), [output journal](src/PiStation.Host/Terminals/TerminalOutputJournal.cs), [T3 terminal](../t3code/apps/server/src/terminal/Manager.ts).

| ID | Feature / subfeature | Origin | Status | Evidence | Remaining work / completion boundary |
| --- | --- | --- | --- | --- | --- |
| FILE-01 | File tree, filename search, and pagination | T3 | Done | S | Show scan/truncation bounds and load-more behavior. |
| FILE-02 | Content search with case/whole-word/regex and result paging | T3 | Done | S | Preserve query/revision identity between pages. |
| FILE-03 | Editable tabs, revision-checked saves, conflict handling | T3 | Done | S | Remote supported payload checks are REM-11. |
| FILE-04 | Markdown/HTML/PDF/image/audio/video preview | T3 | Done | S | Actual formats and large/media cases are DEL-04. |
| FILE-05 | Open files in external editor/IDE | T3 | Done | S | Resolve path on the relevant machine; remote actions are REM-12. |
| FILE-06 | Outside-workspace read-only picker and artifact view | T3 | Done | S | Preserve read-only boundary and original source. |
| TERM-01 | Integrated PowerShell/CMD terminal with ConPTY/Ghostty | T3 | Done | H | Windows target; other shells/platforms are not required. |
| TERM-02 | Multiple terminals and persisted recursive split layout | T3 | Done | H | Current maximum four panes; geometry qualification is DEL-04. |
| TERM-03 | Input, resize, copy/paste, search, links | T3 | Done | H | Keyboard journey qualification is DEL-03. |
| TERM-04 | Start/stop/restart/close and replay within host lifetime | T3 | Done | H | Process and output ownership remain explicit. |
| TERM-05 | Restore scrollback and session metadata after host restart | T3 | Partial | S | Layout and in-memory replay exist; persist bounded terminal history. Does not mean running commands survive restart. |
| TERM-06 | Foreground command labels and idle-shell detection | T3 | Partial | S | Terminal lifecycle state exists; add Windows subprocess-aware labels/activity. |

## 11. Browser preview and automation

Evidence: [preview UI](src/PiStation.App/ViewModels/WorkbenchPreviewViewModel.cs), [WebView surface](src/PiStation.App/Views/Controls/PreviewWebViewSurface.xaml.cs), [surface ownership](src/PiStation.App/Views/Controls/RightPanelHost.xaml.cs), [discovery](src/PiStation.Host/Preview/PreviewDiscoveryService.cs), [automation milestone](Docs/BROWSER-AUTOMATION-2026-09-07.md), [Pi tool bridge](src/PiStation.App/PiExtensions/pistation-browser.ts), [T3 tools](../t3code/apps/server/src/mcp/toolkits/preview/tools.ts), [T3 background automation](../t3code/apps/web/src/components/preview/PreviewAutomationHosts.tsx), [T3 import limits](../t3code/docs/user/browser-import.md).

Browser capabilities have T3 origin; the PiStation implementation uses a bundled Pi extension. Pi core has no built-in browser. Remote localhost forwarding is REM-13, separate from local browser lifecycle.

| ID | Feature / subfeature | Origin | Status | Evidence | Remaining work / completion boundary |
| --- | --- | --- | --- | --- | --- |
| WEB-01 | Discover local listening development servers | T3 | Done | S | Existing loopback scanning/probing. |
| WEB-02 | Associate discovered listeners with owned terminal processes | T3 | Partial | S | Scanner exists; add registered terminal/process ownership. T3 does not guarantee universal project attribution. |
| WEB-03 | Human tab open/close/select, navigation/history/address | T3 | Done | S | Persist appropriate tab state. |
| WEB-04 | Human viewport/zoom/appearance controls | T3 | Done | S | Keep viewport and rendering state consistent. |
| WEB-05 | Isolated browser profiles and cookie-file import | T3 | Done | S | File import is separate from installed-browser import. |
| WEB-06 | Global browser defaults and full profile rename/remove/clear | T3 | Partial | S | Add missing defaults/profile management while retaining existing isolation. |
| WEB-07 | Import installed Windows browser sessions | T3 | Not started | S | Firefox and compatible Helium only at T3 baseline; report skipped/unavailable cookies. No general Chrome/Edge import promise. |
| WEB-08 | Screenshots, human recording, PiP, element annotations | T3 | Done | S | Human actions exist; capture quality qualification remains DEL. |
| WEB-09 | T3 recording frame-rate options | T3 | Partial | S | Current capture is capped at 12 FPS; T3 exposes 30/60 FPS. Measure actual achievable capture. |
| WEB-10 | DevTools availability and inspect/interact access policy | T3 | Done | S | Preserve policy gates for human and agent operations. |
| WEB-11 | Agent status/navigation/basic snapshot/click/type/screenshot | T3 | Done | H | Bundle exposes these through Pi. |
| WEB-12 | Agent target existing tabs; press keys, scroll, bounded wait | T3 | Done | H | Native hidden-tab/input acceptance remains DEL-08. |
| WEB-13 | Agent automation remains available across selected-thread changes | T3 | Partial | S | Move lifetime/ownership beyond selected desktop thread; preserve target and permission scope. |
| WEB-14 | Agent create/open browser tabs | T3 | Not started | S | Return stable tab identity and isolate target scope. |
| WEB-15 | Agent resize viewport and set appearance | T3 | Not started | S | Apply change and confirm rendered state. |
| WEB-16 | Agent evaluate JavaScript | T3 | Not started | S | Add policy-gated execution with bounded result/error handling. |
| WEB-17 | Agent start/stop recording and receive artifact | T3 | Not started | S | Owned recording lifecycle, cancellation, and artifact delivery. |
| WEB-18 | Richer semantic and diagnostic snapshots | T3 | Partial | S | Basic snapshots exist; enumerate and implement missing T3 snapshot fields/diagnostics. |
| WEB-19 | Browser-link default: system browser versus app preview | T3 | Review needed | P | Link opening exists; verify persistent target preference independently of tab/profile defaults. |

## 12. Appearance and personalization

Evidence: [layout preferences](src/PiStation.App/ViewModels/ShellLayoutViewModel.cs), [T3 appearance](../t3code/docs/user/appearance.md), [T3 setting definitions](../t3code/packages/contracts/src/settings.ts).

| ID | Feature / subfeature | Origin | Status | Evidence | Remaining work / completion boundary |
| --- | --- | --- | --- | --- | --- |
| LOOK-01 | Light/dark/system appearance | Both | Done | S | Native semantic palettes implemented. |
| LOOK-02 | Independent interface/composer/code/terminal fonts and sizes | T3 | Partial | S | Terminal options exist; add independent remaining surfaces. |
| LOOK-03 | Contrast and glass/opacity controls | T3 | Partial | S | Existing fixed visual treatment lacks equivalent adjustable controls. |
| LOOK-04 | Word-wrap and diff presentation/whitespace preferences | T3 | Partial | S | Rendering/diff modes exist; complete persistent preference coverage. |
| LOOK-05 | Custom palette creation/editing | T3 | Not started | S | Validate colors, preview safely, save/recover theme. |
| LOOK-06 | Palette inspection / identify themed UI area | T3 | Not started | S | Map inspected area to editable palette tokens. |
| LOOK-07 | T3 theme import/export | T3 | Not started | S | Preserve supported palette data and reject invalid input. |
| LOOK-08 | VS Code theme import/export compatibility | T3 | Not started | S | Match actual T3 conversion limits, not all VS Code extensions/settings. |
| LOOK-09 | Pi TUI theme resources | Pi | Review needed | P | Pi TUI themes do not directly skin WinUI; keep resource use separate from LOOK-05/08. |
| LOOK-10 | Configurable panel animation duration | T3 | Review needed | P | Inspect native transition settings and reduced-motion behavior against T3's preference. |

## 13. Usage, diagnostics, and reliability

Evidence: [settings/usage UI](src/PiStation.App/ViewModels/SettingsViewModel.cs), [host diagnostics](src/PiStation.Host/Diagnostics/HostDiagnosticsService.cs), [T3 usage](../t3code/docs/user/usage.md), [T3 process/trace UI](../t3code/apps/web/src/components/settings/DiagnosticsSettings.tsx), [T3 background policy](../t3code/packages/contracts/src/background.ts), [T3 history loading](../t3code/packages/client-runtime/src/state/threads.ts).

| ID | Feature / subfeature | Origin | Status | Evidence | Remaining work / completion boundary |
| --- | --- | --- | --- | --- | --- |
| USE-01 | Recorded token/cache/cost totals and session statistics | Both | Done | S | Unknown prices/usage must remain explicit. |
| USE-02 | Dedicated usage dashboard with date filters and charts | T3 | Partial | S | Existing summary/aggregation needs dashboard presentation. |
| USE-03 | Model/provider breakdowns and cache savings | T3 | Partial | S | Expose available breakdown data and savings consistently. |
| USE-04 | Rescan historical sessions and refresh pricing | T3 | Partial | S | Existing recorded totals are narrower than historical discovery/rescan. |
| USE-05 | Subscription quota, reset times, and consumption pace | T3 | Not started | S | Requires supported provider/account adapters; Pi has no generic stable quota RPC. Unsupported stays unknown. |
| USE-06 | CLIProxyAPI pooled-account usage hubs | T3 | Not started | S | Usage integration and credential settings; separate from model request routing. |
| USE-07 | Aggregate child-agent usage without double counting | PiStation | Partial | H | Per-child metrics exist; reconcile into aggregate totals. |
| DIAG-01 | Runtime/tool health, bounded logs, local redacted export | T3 | Done | S | Remote export defect is BUG-02. |
| DIAG-02 | Process trees, resource history, targeted process actions | T3 | Partial | S | Current diagnostics snapshots need history/process relationships and supported Windows actions. |
| DIAG-03 | Tracing/metrics and supported observability configuration | T3 | Partial | S | Logging exists; match selected tracing/metrics workflows and export controls. |
| REL-01 | Ordered projections, receipts, reconnect/resnapshot recovery | Delivery | Done | H | Do not replay uncertain mutations automatically. Remote qualification is DEL-09. |
| REL-02 | Bounded retained state, stream leases, idle Pi shutdown | Delivery | Done | H | Existing ownership/eviction paths; measure resource use in DEL-05. |
| REL-03 | Paged long-conversation recovery and load-earlier UX | T3 | Partial | S | Streaming/recovery and other collection pagination exist; complete windowed transcript loading/recovery. |
| REL-04 | Background activity policy for visibility/focus/host lock/battery/low power | T3 | Not started | S | Add Windows/client signals, effective policy, and resumed work behavior. |
| REL-05 | Application persistence and recovery across upgrade/restart | Delivery | Done | H | SQLite application data plus Pi JSONL; release migration checks are DEL-02. |

## 14. Remote access (Windows hosts only; active separate workstream)

Evidence: [remote implementation and qualification](Docs/REMOTE-ACCESS-IMPLEMENTATION.md), [earlier defect review](Docs/REMOTE-ACCESS-REVIEW-2026-09-07.md), [connection supervisor](src/PiStation.ClientRuntime/ConnectionSupervisor.cs), [remote window setup](src/PiStation.App/App.xaml.cs), [T3 remote workflows](../t3code/docs/user/remote-access.md).

Earlier remote docs include automatic updates and non-Windows possibilities. Those now belong to the deferred phase, after the Windows core is working. Old reproduced reconnect/payload defects were followed by implementation fixes; do not reopen them solely from the historical review. Physical two-machine acceptance remains DEL-09. This section records current work, not a request to replace changes the user is already making.

| ID | Feature / subfeature | Origin | Status | Evidence | Remaining work / completion boundary |
| --- | --- | --- | --- | --- | --- |
| REM-01 | Standalone Windows host independent of client window lifetime | T3 | Done | H | Manually installed/started host; no Windows service requirement. |
| REM-02 | Direct HTTPS pairing, machine identity, protected credentials | T3 | Done | H | Respect pinned identity/certificate and host approval. |
| REM-03 | Saved multiple environments with scoped windows/projects/threads | T3 | Done | H | Separate paths, drafts, credentials, receipts, and settings. |
| REM-04 | Managed SSH connection/tunnel to prepared Windows host | T3 | Done | H | Connection/process ownership exists; no remote bootstrap/install/update requirement. Real OpenSSH acceptance remains. |
| REM-05 | Automatic reconnect, backoff, retry, sleep/network recovery | T3 | Done | H | Physical machine/sleep/network checks are DEL-09. |
| REM-06 | Chat/terminal/catalog stream replay or resnapshot | T3 | Done | H | Resume synchronization without frozen waiters or duplicate application. |
| REM-07 | Subscription lifetime, bounded cache, passive clients | T3 | Done | H | Stop unused streams and respect read-only scope. |
| REM-08 | Multi-device catalog/live-session presentation | T3 | Done | H | Shared host identity/state; imported session copies are a different workflow. |
| REM-09 | Device sessions, read/operate scope, revocation and recovery guidance | T3 | Done | H | Distinguish revoked/auth/identity/version/connectivity states. |
| REM-10 | Verify/edit saved endpoint without losing environment identity | T3 | Done | H | Retain original working configuration on failure. |
| REM-11 | Remote file reads/writes and supported large payloads | T3 | Done | H | Revision/size handling across HTTPS and SSH. |
| REM-12 | Remote attachments, artifacts, and portable session transfer | T3 | Done | S | Use transport for content and resolve original host paths. Specialized path issues remain BUG-02/03. |
| REM-13 | Host-local preview forwarding, assets, SSE/WebSocket reload | T3 | Done | H | Local browser automation ownership is separately WEB-13. |
| REM-14 | Connection diagnostics and session activity | T3 | Done | H | Keep credentials redacted; diagnostics file delivery remains BUG-02. |
| REM-15 | Remote Pi runtime configuration round-trip | PiStation | Partial | S | BUG-01: initialize host launch settings before saving changes. |
| REM-16 | Remote diagnostic export to chosen client destination | PiStation | Partial | S | BUG-02: transfer bytes rather than sending a client-local destination to the host. |
| REM-17 | Remote project icon / executable picker path semantics | PiStation | Partial | S | BUG-03: distinguish client uploads from host paths and render through transport. |
| REM-18 | Version compatibility and recovery after manual host update | T3 | Done | H | Keep useful update instructions and reconnect behavior; automatic updating is deferred in SCOPE-08. |
| REM-19 | Tailscale HTTPS discovery/connection integration | T3 | Not started | S | Windows-relevant T3 connection workflow; no Linux/macOS host or automated remote installation. |
| REM-20 | Hosted relay/account linking and environment discovery | T3 | Not started | S | Requires a PiStation service/backend equivalent; T3 Connect itself is not a PiStation service. |

## 15. Delivery and qualification (not feature-origin claims)

Evidence: [release deferral](Docs/RELEASE-TODO.md), [release build](Build-Release.ps1), [UI testing plan](Docs/WINAPP-UI-TESTING-PLAN.md), [retained UI verification](Docs/UI-VERIFICATION-2026-09-05.md), [remote qualification limits](Docs/REMOTE-ACCESS-IMPLEMENTATION.md), [real-Pi acceptance history](Docs/PI-RESOURCES-AND-SETUP-2026-09-06.md), [browser acceptance limits](Docs/BROWSER-AUTOMATION-2026-09-07.md).

| ID | Feature / subfeature | Origin | Status | Evidence | Remaining work / completion boundary |
| --- | --- | --- | --- | --- | --- |
| DEL-01 | Build/package Windows x64 MSIX | Delivery | Done | H | Unsigned development packaging exists; not public release qualification. |
| DEL-02 | Production publisher, signing, feed, local app update/install migration | Delivery | Partial | H | Owner-deferred production inputs; complete after the working-core milestone and before public release. Remote update automation is SCOPE-08. |
| DEL-03 | Complete keyboard, focus, screen reader, and High Contrast journeys | Delivery | Partial | H | Existing UIA/keyboard coverage is not full accessibility acceptance. |
| DEL-04 | Rendered visual acceptance: scaling, mixed monitors, narrow layouts, rich content | Delivery | Partial | H | Capture/inspect actual populated states; static geometry or black captures do not certify appearance. |
| DEL-05 | Measured long-history/large-workspace/multi-terminal performance | Delivery | Partial | S | Record workloads and latency/CPU/memory; verify bounds/eviction/pagination. |
| DEL-06 | Clean-machine Pi setup and authenticated provider journeys | Delivery | Partial | H | Existing isolated/one-account tests do not establish fresh setup or all providers. |
| DEL-07 | Authenticated hosting acceptance across supported providers | Delivery | Partial | H | Verify actual list/clone/publish/review/action flows with controlled repositories and authorized writes. |
| DEL-08 | Native browser automation/profile/media acceptance | Delivery | Partial | H | Verify hidden/background tabs, keyboard/scroll/wait, media capture, policy denial, and new operations as delivered. |
| DEL-09 | Two physical Windows machines: direct HTTPS and actual OpenSSH | Delivery | Partial | H | Pair, revoke, restart, sleep/wake, change networks, transfer files, run terminal/preview, manually update host and reconnect. |
| DEL-10 | Regression/compatibility gates and evidence maintenance | Delivery | Partial | H | Keep focused tests and historical failures distinguishable; resolve BUG-04 status. No new application tests run for this documentation edit. |

## 16. Open defect / investigation register

Feature gaps above are not duplicated as bugs. These entries describe a specific incorrect or uncertain behavior. Source findings require focused reproduction before claiming a failing runtime test. No fixes are claimed by creating this file.

| ID | Related features | Scope | State | Finding / next check | Evidence |
| --- | --- | --- | --- | --- | --- |
| BUG-01 | PI-02, REM-15 | Remote | Source finding | Remote window setup does not hydrate saved host launch arguments/environment like local bootstrap. Saving another runtime setting can submit empty launch settings and replace host configuration. Reproduce with nonempty host settings, then load/round-trip them without loss. | [remote window](src/PiStation.App/App.xaml.cs), [setup save](src/PiStation.App/ViewModels/ShellViewModel.Setup.cs), [host replacement](src/PiStation.Host/EnvironmentService.Runtime.cs) |
| BUG-02 | DIAG-01, REM-16 | Remote | Source finding | Save picker supplies a client filesystem destination to a host export method that writes on the host. Reproduce with different client/host paths; return/download redacted content and save on the client. | [picker](src/PiStation.App/Views/ShellPage.xaml.cs), [client request](src/PiStation.App/ViewModels/ShellViewModel.cs), [host export](src/PiStation.Host/Diagnostics/HostDiagnosticsService.cs) |
| BUG-03 | PROJ-06, REM-17 | Remote | Source finding | Local image/executable picker paths can be passed as host paths; project icons use a local image URI. Check host paths and client-selected files separately; add upload/host selection/content transport where appropriate. | [project picker](src/PiStation.App/Views/ShellPage.ProjectCustomization.cs), [icon converter](src/PiStation.App/Views/ProjectIconSourceConverter.cs), [runtime setup](src/PiStation.App/ViewModels/ShellViewModel.Setup.cs) |
| BUG-04 | GIT-07/08, DEL-10 | Local/shared | Review needed | Historical checkpoint/rewind test exceeded its cancellation budget in some runs and passed isolated in others. Establish current reproducibility and contention cause before closing or labeling it a product failure. | [automation run limit](Docs/PI-AUTOMATION-SETTINGS-2026-09-07.md), [session test history](Docs/PI-SESSION-MANAGEMENT-2026-09-06.md) |
| BUG-05 | PI-01, DEL-06 | Local | Review needed | Historical npm-local `.bin/pi.ps1` discovery failure was separate from working global launcher/package-directory discovery. Verify current locator against that exact layout before retaining it as an open defect. | [session milestone](Docs/PI-SESSION-MANAGEMENT-2026-09-06.md) |

For a new defect, add a stable BUG ID, affected feature IDs, environment/scope, source or reproduction evidence, expected result, and the focused check needed to close it. Add severity/owner when triaged; do not invent assignments or deadlines.

## 17. Compatibility dependencies and optional proposals

Under the complete-Pi-support goal, OPT-02 and OPT-03 are required compatibility investigations linked to EXT-15 and AGENT-08. Their existing IDs are retained for continuity. Determine the supported native behavior and upstream/adapter dependencies before implementation; this does not promise compatibility with every arbitrary third-party extension. The other rows remain optional product additions until selected. Supported Pi RPC and current bundled extension workflows above remain in scope.

| ID | Proposal | Origin | Status | Dependency / decision |
| --- | --- | --- | --- | --- |
| OPT-01 | Install/update the local Pi runtime from PiStation | PiStation | Not started | Convenience adaptation of Pi CLI and T3 provider maintenance. T3 has provider-specific actions, not a universal unattended installer. Remote installation/updating is deferred in SCOPE-08. |
| OPT-02 | Native bridge for arbitrary Pi TUI UI/editor/loading components | Pi extension | Not started | Required compatibility investigation: Pi RPC omits/no-ops these methods. Define native equivalents and an explicit extension/SDK/UI contract. Links EXT-15. |
| OPT-03 | Adapters for independently controlling external subagent extensions | Pi extension | Partial | Required compatibility investigation: parent-stop fallback exists; determine supported extension-specific handles/protocol and adapters. Links AGENT-08. |
| OPT-04 | Built-in MCP client or packaged MCP-to-Pi bridge | Pi extension | Not started | Pi has no core MCP client. Existing loaded extensions remain usable; define supported bridge first. |
| OPT-05 | Packaged web search/fetch or image-generation tools | Pi extension | Not started | Possible via extensions/provider APIs; not universal core Pi tools or proven T3-independent requirements. |
| OPT-06 | Persistent agent-owned background shell jobs | Pi extension | Not started | Needs Windows-compatible ownership/control contract; normal terminals and background coding tasks already exist. |
| OPT-07 | Automatic AI approval reviewer for Pi | PiStation | Not started | Current Auto fallback is deliberate and matches unsupported-provider behavior. A new reviewer is an optional policy feature. |
| OPT-08 | OS/process sandbox or Windows service management | PiStation | Not started | Planning permissions are not a sandbox. T3 managed background service support at this baseline does not supply Windows service parity. |
| OPT-09 | Universal project ownership detection for development servers | PiStation | Not started | Beyond confirmed T3 terminal-process association. Do not confuse with WEB-02. |
| OPT-10 | Automatic script synchronization across every grouped checkout | PiStation | Not started | Not established as T3 parity. Shared icon behavior remains PROJ-07. |
| OPT-11 | Product usage analytics collection and opt-out | T3 | Not started | T3 has [optional product telemetry](../t3code/docs/user/telemetry.md). Adding collection to PiStation needs a product decision; Pi runtime telemetry preferences are separately PI-21. |

## 18. Product boundaries / not application parity

These describe the Pi-only product identity and non-applicable implementation mechanisms. They are distinct from user-facing features skipped to get the app working; those are in the final deferred section. Implementing deferred features does not require adding other coding-agent harnesses or rebuilding Pi's internal libraries.

| ID | Feature family | Origin | Status | Reason |
| --- | --- | --- | --- | --- |
| SCOPE-03 | Codex, Claude Code, Cursor, Grok Build, OpenCode, Antigravity agent harnesses | T3 | Out of scope | Pi is the only coding agent. Model access through Pi is still in scope. |
| SCOPE-05 | Codex-specific async app access, feedback, protocol-only controls | T3 | Out of scope | Do not map another harness's protocol to unsupported Pi requirements. |
| SCOPE-10 | Rebuild Pi TUI renderer, raw terminal editor, terminal keybinding engine | Pi | Out of scope | Reimplementing the terminal engine is not the product goal. Native support for the corresponding Pi user capabilities remains a compatibility investigation in EXT-15/OPT-02. |
| SCOPE-12 | Pi experimental services, replicated state, plugin facets, alternative session backend | Pi | Out of scope | Experimental/library mechanisms are not required stable desktop features. Existing app SQLite + Pi JSONL remains REL-05. |
| SCOPE-13 | Reimplement or distribute all Pi SDK/model/TUI libraries | Pi | Out of scope | Use supported integration APIs; library internals are not a separate feature backlog. |

## Evidence log and next maintenance step

- Earlier comparison in this conversation ran selected ClientRuntime tests (162 passed) and Host tests (171 passed), covering remote/transfer/catalog/browser and related cases. These counts describe the selected filters, not all features or a full release gate.
- This tracker creation changes documentation only. Source/milestone reconciliation and file/link/ID checks are the validation for this edit; no new paid provider calls, hosting writes, physical-machine checks, or UI certification are implied.
- Resolve Review needed rows with targeted source/protocol checks. When implementation starts, choose specific IDs and update them with the patch and relevant validation. Preserve Done, deferred, optional, and product-boundary decisions so scope and provenance remain visible.

## 19. Deferred decisions — implement after PiStation is working

**Decision recorded 2026-09-08:** features skipped to get PiStation working are deferred implementation work, not permanently discarded. Keep this section at the end of the tracker. Once the core milestone below passes, implement these features in dependency order, carrying forward their existing progress and acceptance evidence.

The first working milestone means the Windows/Pi app can complete its included coding workflows: configure Pi, create/open a project and conversation, send/stream/stop work, use the required tools and Pi features, inspect/edit files, review changes, and recover drafts/sessions after restart. Included Windows remote workflows must pass their applicable connection/recovery checks. Relevant focused tests and native acceptance must pass, with no unresolved defect preventing those workflows. Record the accepted commit and evidence before starting the deferred phase. This milestone does not require the deferred features or deferred production signing, and it is not a claim of public-release readiness or completion of the whole project.

The previous skip reasons are retained below as scheduling history. Existing SCOPE IDs are retained for continuity; their phase is now Deferred. Decompose broad rows into implementation subfeatures when that phase starts.

| ID | Deferred feature | Origin | Phase | Implementation | Previous skip reason / completion target |
| --- | --- | --- | --- | --- | --- |
| SCOPE-01 | Linux/macOS hosts and clients; non-Windows shell backends | T3 | Deferred | Not started | Previously skipped for Windows focus. After the core works, implement host/runtime support and suitable client presentation; WinUI itself remains Windows-only. |
| SCOPE-02 | WSL distribution discovery and Linux workspace connections | T3 | Deferred | Not started | Previously skipped with Linux scope. Deliver distribution discovery, path handling, host connection, Pi execution, and restart recovery after the core milestone. |
| SCOPE-04 | Multiple named Pi runtime/account instances | T3 | Deferred | Not started | Previously skipped for a simpler Pi setup. Add isolated named Pi configurations/account selection without introducing other agent harnesses. |
| SCOPE-06 | Web/hosted clients and iOS/Android clients | T3 | Deferred | Not started | Previously skipped for the native Windows client. Reuse host contracts with appropriate new clients and verify scoped state/recovery after the core milestone. |
| SCOPE-07 | Mobile offline queue, share sheet, voice, push/activity | T3 | Deferred | Not started | Depends on deferred mobile clients. Implement applicable platform capabilities with their actual device/provider limits. |
| SCOPE-08 | Remote installation/bootstrap and remote runtime/app/server updates | T3 | Deferred | Partial | Previously skipped because users would install/update on the other computer. Existing update code and local verification remain; finish setup, supported ownership/update paths, and real-machine qualification after the core works. |
| SCOPE-09 | Environment-published themes for served/hosted clients | T3 | Deferred | Not started | Previously skipped with web clients. Implement when those clients exist; local WinUI theme work remains in LOOK. |
| SCOPE-11 | PiStation one-shot/print/JSON task CLI | Pi | Deferred | Not started | Previously skipped because Pi already supplies these modes. Add an application-integrated task interface after the core milestone while preserving Pi behavior. |

Also carry forward the existing owner-deferred **DEL-02** production publisher/signing/feed/install-migration work after the core milestone and before public release; its implementation status and evidence remain in the delivery table.

For every future skip decision, append the feature ID, origin, date/reason, current implementation status, dependencies, and measurable completion target here. Deferred means **implement after the core is working**. Optional ideas that have never been selected remain in section 17 until selected.
