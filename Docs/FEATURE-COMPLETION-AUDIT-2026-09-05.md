# PiStation feature completion audit

September 5, 2026. Target: the features in the supplied T3 Code checkout, adapted to PiStation, plus Pi capabilities that the desktop has not exposed.

PiStation has a substantial local coding workspace. It does **not** yet have full T3 feature parity. Following the first Pi integration milestone, the immediate gaps include advanced runtime setup, subagent workflows, richer conversation artifacts and full pull-request review. Remote environments, other agent runtimes, web/mobile clients, and custom themes are larger remaining product areas.

**Implementation follow-up:** the [first Pi integration milestone](PI-INTEGRATION-MILESTONE-2026-09-05.md) implements skill expansion/current metadata, configurable extension loading, supported text-based extension UI and idle restart. PI-01 through PI-03 below include that progress. The [resource and provider setup follow-up](PI-RESOURCES-AND-SETUP-2026-09-06.md) adds PI-04/05 implementation and Pi 0.85/authenticated acceptance. The [session management follow-up](PI-SESSION-MANAGEMENT-2026-09-06.md) adds session browsing/import, independent copies and response forks, tree/statistics inspection and JSONL/HTML export. The [plan workflow follow-up](PI-PLAN-WORKFLOW-2026-09-06.md) adds native planning, saved revision approval, enforced Pi tool restrictions, progress, Markdown export and recovery. The other feature families remain backlog; this is not a full-parity completion claim.

This audit includes the current uncommitted and untracked PiStation source. Several improvements from the earlier September 5 audit are already implemented. Do not reuse that older missing-feature list without its implementation follow-up.

## Scope and evidence

| Repository | Snapshot inspected |
| --- | --- |
| PiStation | `C:\Users\Bacon21\Workspace\PiStationDesktop1`, base `3f3bce95ec4c6fc7e90ff21fb5b603fa249a0cab`, including current working-tree changes |
| T3 Code | `C:\Users\Bacon21\Workspace\t3code`, `0a590fa01af66ec135d2ebf2d5542b08a37dc275` |
| Pi Agent | `C:\Users\Bacon21\Workspace\Pi Agent`, `17de82d7bea18a6589677a9761baabc2060c9efb`, coding-agent package `0.85.0` |

The original audit was based on source and retained evidence: no application code was changed, provider prompts sent, or fresh builds, UI or authenticated hosting tests run during that audit. Its retained Debug TRX files under `TestResults/visual-polish-debug` contain 199 passed and zero failed tests. Subsequent code changes and fresh validation are recorded in the [Pi integration milestone](PI-INTEGRATION-MILESTONE-2026-09-05.md). The earlier [UI verification report](UI-VERIFICATION-2026-09-05.md) retains its own limits.

**Status meanings:** Implemented = connected implementation found, subject to acceptance coverage. Partial = useful behavior exists but the listed part remains. Missing = no corresponding product path found in the inspected source. Pi-dependent = needs a Pi extension/adapter or a new protocol capability. Acceptance = implementation exists but real-world verification remains.

“All T3 features” here includes its remote and multi-client product scope. Those are included in the backlog, even though the earlier review treated them as optional for a smaller Windows/Pi 1.0. T3's own platform/provider limitations still apply; parity does not require inventing capabilities that T3 itself lacks. This is a feature-family inventory, not a percentage based on file counts or a claim that every provider-specific setting has been exhaustively exercised.

## Features already present

These should be extended or verified, rather than rebuilt.

| Area | Current implementation | Source |
| --- | --- | --- |
| Native shell | Project/thread hierarchy, collapsible sidebar, docked/overlay resizable workbench, persisted layout, dark/light/system appearance, responsive layouts | [Shell layout](../src/PiStation.App/ViewModels/ShellLayoutViewModel.cs), [workspace shell](../src/PiStation.App/Views/Controls/WorkspaceShell.xaml) |
| Conversation | Streaming replies, reasoning and tool disclosures, Markdown/code/tables/task lists, remote images, copy/quote/cite, follow-output, jump-to-latest, per-thread scroll state | [Timeline](../src/PiStation.App/Views/Controls/ConversationTimeline.xaml), [Markdown](../src/PiStation.App/Views/MarkdownView.xaml.cs) |
| Runtime reliability | Pi discovery/setup/retry, process restart, reconnect/resync, ordered projections, receipts, uncertain-command recovery, idle runtime shutdown | [Runtime setup](../src/PiStation.Host/EnvironmentService.Runtime.cs), [thread controller](../src/PiStation.Host/Threads/PiThreadController.cs), [registry](../src/PiStation.Host/Threads/PiThreadRegistry.cs) |
| Models and reasoning | Pi model/provider selection, supported thinking levels, persisted thread/project defaults; context and reported usage information | [Pi configuration](../src/PiStation.App/ViewModels/PiConfigurationViewModel.cs), [configuration contracts](../src/PiStation.Protocol/Models/PiConfiguration.cs) |
| Composer | Durable drafts, upload/remove attachments, image paste and file drag/drop, file mentions, thread-owned context chips, complete draft stashes | [Composer](../src/PiStation.App/ViewModels/ComposerViewModel.cs), [composer storage](../src/PiStation.Host/Persistence/HostDatabase.Composer.cs) |
| Commands and compaction | Current command/skill discovery, inline skill expansion and manual context compaction; focused invocation verified with installed Pi 0.84.4 | [Composer power features](../src/PiStation.App/ViewModels/ComposerPowerViewModel.cs), [skill expansion](../src/PiStation.PiRpc/Transport/PiSkillPromptExpander.cs) |
| Active-turn control | Send steering/follow-up messages, inspect/clear queue, all/one-at-a-time delivery, stop and retry interruption | [Queue](../src/PiStation.App/ViewModels/ThreadQueueViewModel.cs), [RPC connection](../src/PiStation.PiRpc/Transport/PiRpcConnection.cs) |
| Questions | Confirm, select, input, and editor extension dialogs with responses/cancellation | [Event decoder](../src/PiStation.PiRpc/Decoding/PiEventDecoder.cs) |
| Thread organization | Rename, title regeneration, pin/reorder, archive/restore/delete, explicit settle/unsettle, snooze/wake shelves, bulk actions, linked PR metadata | [Thread descriptors](../src/PiStation.Protocol/Models/Descriptors.cs), [inbox](../src/PiStation.ClientRuntime/ThreadInbox.cs) |
| Search/navigation | Command palette and editable shortcuts; cross-project project/branch/thread/message search with message reveal; workspace file/line links | [Shell commands/search](../src/PiStation.App/Views/ShellPage.xaml.cs), [global search](../src/PiStation.Host/Search/GlobalSearchService.cs) |
| Projects | Local folders, Git URL clone, defaults, icons loaded from project configuration, setup/named scripts and script trust, automatic safe default-branch pulls | [Project configuration](../src/PiStation.Host/Projects/ProjectConfigurationLoader.cs), [auto pull](../src/PiStation.Host/Projects/ProjectAutoPullService.cs) |
| Git/worktrees | Status, refs, branches, init/pull/commit/push, managed worktrees, selected-file commit workflow, per-turn checkpoints and workspace/conversation rewind | [Git commands](../src/PiStation.Host/Git/WorkspaceGitCommandService.cs), [checkpoints](../src/PiStation.Host/Git/WorkspaceCheckpointService.cs) |
| Changes review | Structured unified/split diff rows, colors/gutters/syntax, hunk navigation, collapse, source/diff-range context | [Diff view](../src/PiStation.App/Views/Controls/DiffView.cs), [diff document](../src/PiStation.ClientRuntime/DiffDocument.cs) |
| Files | Tree, filename and content search, regex/case/whole-word options, editable tabs, revision-checked save, external editor launch, rendered Markdown/HTML/PDF/images/audio/video, external read-only file picker | [Files](../src/PiStation.App/ViewModels/WorkbenchFilesViewModel.cs), [file service](../src/PiStation.Host/Files/WorkspaceFileReadService.cs) |
| Terminal | PowerShell/CMD, ConPTY/Ghostty, input/copy/paste/search/links, resize, start/stop/restart/close, output replay, persisted recursive split layouts up to four panes | [Terminal](../src/PiStation.App/ViewModels/WorkbenchTerminalViewModel.cs), [terminal host](../src/PiStation.Host/Terminals/TerminalSessionRegistry.cs) |
| Local preview | Server discovery, live tabs/history, address/navigation, viewport/zoom/color settings, isolated profiles, cookie-file import, DevTools policy, screenshots, MP4 capture, PiP, element annotations | [Preview](../src/PiStation.App/ViewModels/WorkbenchPreviewViewModel.cs), [browser surface](../src/PiStation.App/Views/Controls/PreviewWebViewSurface.xaml.cs) |
| Browser tool | Pi can inspect/navigate/click/type/screenshot the selected permitted browser; inspect/interact access modes | [Bundled extension](../src/PiStation.App/PiExtensions/pistation-browser.ts), [automation inbox](../src/PiStation.App/Services/BrowserAutomationInbox.cs) |
| Agents panel | Structured subagent/workflow hierarchy, progress, result/failure summaries, tool/token metrics and parent-turn interruption; actual extension availability is unfinished | [Agent projection](../src/PiStation.Host/Threads/PiAgentActivityProjector.cs), [Agents UI](../src/PiStation.App/ViewModels/WorkbenchAgentsViewModel.cs) |
| Hosting foundation | GitHub/GitLab/Azure PR listing/creation and supported mutations, GitHub publish, durable hosting-operation records and uncertain-write recovery | [Hosting](../src/PiStation.Host/SourceControl/SourceControlHostingService.cs), [capabilities](../src/PiStation.Protocol/Models/HostingCapabilities.cs) |
| Diagnostics/distribution foundation | Resource/log export, usage aggregation, update check, configurable unsigned self-contained x64 MSIX tooling and CI gates | [Diagnostics](../src/PiStation.Host/Diagnostics/HostDiagnosticsService.cs), [release build](../Build-Release.ps1) |

## First integration milestone and its remaining acceptance

### PI-01 — Make installed extensions available

**Implemented loading/configuration; acceptance partial.** [PiProcessLauncher](../src/PiStation.PiRpc/Process/PiProcessLauncher.cs) now adds `--no-extensions` only while discovery is disabled. Settings exposes discovery and explicit trusted extension paths. The [process factory](../src/PiStation.Host/Threads/IPiProcessFactory.cs) supplies those paths alongside `pistation-browser.ts`.

Discovery remains off by default; enabling it uses Pi's own project trust decisions. Configuration persists atomically and applies to new or explicitly restarted idle runtimes. A real Pi 0.84.4 offline extension registered and ran a tool and commands; a discovered extension appeared and disappeared across off/on/off launches. Host tests cover command completion without a model turn and idle restart. Native checks cover settings and draft persistence.

Remaining: a real subagent extension producing Agents activity and more precise per-resource failure attribution. Native resource/trust inventory, Pi 0.85 execution and authenticated-provider smoke coverage are now recorded in the September 6 follow-up. An Agents panel that recognizes subagent output alone does not establish subagent workflow parity.

**Acceptance:** a configured user extension registers a tool and command inside PiStation; disabling it removes both after the documented reload/restart; a subagent example produces real activity in Agents.

Pi reference: [CLI flags](../../Pi%20Agent/packages/coding-agent/src/cli/args.ts), [resource loader](../../Pi%20Agent/packages/coding-agent/src/core/resource-loader.ts).

### PI-02 — Correct skill invocation and current command metadata

**Implemented; real Pi 0.84.4 verified.** [PiCommandReader](../src/PiStation.PiRpc/Decoding/PiCommandReader.cs) reads current `sourceInfo` metadata with legacy fallback. The desktop retains `$skill:name` mentions and [expands their discovered resource contents](../src/PiStation.PiRpc/Transport/PiSkillPromptExpander.cs) before sending, including inline/multiple mentions with deduplication. Prefix `/skill:name` still follows Pi's native behavior.

Arguments, attachments and visible user text are preserved; generated skill bodies are removed when sessions hydrate the transcript. Missing/ambiguous/oversized resources fail before a host turn is created. Code literals, escaped tokens and quoted context are excluded. [FakePi](../tests/PiStation.FakePi/FakePiServer.cs) now returns current metadata and actual fixture skill files.

The [real Pi offline test](../tests/PiStation.PiRpc.Tests/RealPiOfflineTests.cs) proves skill body arrival at a provider, template argument expansion and extension command execution. The September 6 follow-up adds actual 0.85.0 and authenticated skill/resume smoke coverage; broader runtime acceptance remains under QA-01.

**Acceptance:** select a real skill in the picker and prove its content reaches the request; exercise a prompt template and extension command too; source metadata resolves to the correct resource.

Pi reference: [RPC types](../../Pi%20Agent/packages/coding-agent/src/modes/rpc/rpc-types.ts), [command discovery](../../Pi%20Agent/packages/coding-agent/src/modes/rpc/rpc-mode.ts), [prompt expansion](../../Pi%20Agent/packages/coding-agent/src/core/agent-session.ts).

### PI-03 — Surface the rest of Pi's supported extension UI

**Supported text-based RPC methods implemented.** `notify`, keyed `setStatus`, text-array `setWidget` with removal and placement, `setTitle`, and `set_editor_text` now flow through typed events and the shared host/client state reducer to the selected thread's extension panel. Existing confirm/select/input/editor dialogs remain supported.

State is bounded, survives client snapshot/reconnect, and clears on runtime restart. Titles label the extension panel; editor text is offered through **Insert into draft** and appends without overwriting user edits. Tests cover removal, host/client agreement, foreign-thread snapshot rejection and restart. Native checks exercise separate drafts across thread switches and process relaunch. Screenshot-based visual acceptance remains blocked by the current desktop capture environment.

**Acceptance:** each supported event updates the correct thread, including when another thread is selected, and removal/restart does not leave stale state.

Pi limitation: arbitrary TUI component factories, overlays, custom editors/footers and raw terminal input are not transported by the current RPC mode. Those require a native extension UI contract or another integration; they cannot be completed just by adding event cases. See [RPC mode](../../Pi%20Agent/packages/coding-agent/src/modes/rpc/rpc-mode.ts).

## Remaining Pi capabilities

| ID | Status | Work to finish | Completion check |
| --- | --- | --- | --- |
| PI-04 | Implemented core; partial acceptance | Native effective context/resource paths and sources, saved enable/disable settings including package filters, effective/saved project trust, startup/configuration messages and explicit idle restart. Pi config/package terminal actions cover additional resource management. Remaining: exact extension-load/failure attribution where Pi exposes no registered feature, and broader package/trust-extension acceptance. Repository script trust stays separate. | Real Pi 0.84.4/0.85 tests cover local/package skills, project trust and restart. Native controls and draft preservation pass. |
| PI-05 | Partial | Guided Pi login/logout terminal, credential-source status, native custom endpoints/models with environment-variable references, and package CLI actions are implemented. Still needed: general runtime arguments/environment editing, automatic runtime install/update and clean-machine onboarding acceptance. | Authenticated skill invocation and resume passed with an existing configured account on 0.84.4 and 0.85.0. A fresh login/provider setup journey is not yet certified. |
| PI-06 | Implemented core; acceptance limits | Native session browser and JSONL import, independent whole-session copy or fork after a completed assistant response, branch-tree inspection, active-branch statistics and JSONL/readable HTML export. New identities and model/thinking settings persist; original files/drafts remain separate. | Import/fork/resume verified on actual Pi 0.84.4 and 0.85.0. Native import, fork, relaunch and export exercised. Limits: v3 files only, bounded tree listing, HTML text for the active branch, external linked files and unchanged workspace files. |
| PI-07 | Partial | Settings for `set_auto_compaction` and `set_auto_retry`; preserve effective values across process recreation. Manual compaction and retry status/abort already exist. | Toggle each option and verify Pi reports/uses the persisted choice after idle restart. |
| PI-08 | Missing | Direct Pi shell execution (`bash`/`abort_bash`, analogous to `!`/`!!`): stream output and control whether it becomes model context. The integrated terminal is a separate process/session. | Run a command without an LLM call, stream its output, then demonstrate include/exclude context and cancellation. |
| PI-09 | Implemented core; partial parity/acceptance | Native plan editor/export, approved execution, agent-reported progress, durable receipts and paused recovery. A bundled extension enforces dedicated read/search tools during planning and blocks other tool dispatch. Trusted Node extensions are not sandboxed; broader T3 permission modes remain. | Real Pi 0.84.4/0.85.0 tests cover blocked mutations, approved writes, progress, Stop and restart. Native workflow and session-transition acceptance are recorded in the plan follow-up. |
| PI-10 | Partial/Pi-dependent | Subagent setup/presets, child transcript/detail navigation and, where supported, independent child interruption/resume. The current panel is an observer and its interrupt operation stops the parent turn. | Run actual single/parallel/chain workflows. Child-specific controls require stable child identities/handles; otherwise explain the parent scope. |

References: [Pi RPC commands](../../Pi%20Agent/packages/coding-agent/src/modes/rpc/rpc-types.ts), [Pi feature/trust/package behavior](../../Pi%20Agent/packages/coding-agent/README.md), [plan example](../../Pi%20Agent/packages/coding-agent/examples/extensions/plan-mode/README.md), [subagent example](../../Pi%20Agent/packages/coding-agent/examples/extensions/subagent/README.md), [PiStation client API](../src/PiStation.ClientRuntime/IEnvironmentClient.cs), [Pi configuration application](../src/PiStation.Host/Threads/PiThreadController.cs).

The Pi checkout also contains experimental `pi-server`, `pi-client`, Chord services, and durable Session/Harness work. They are separate from the coding-agent JSONL RPC PiStation currently uses. They may support a future architecture experiment; their existence does not mean remote hosting or shared-session attachment already works in PiStation. Evaluate them behind an adapter before committing to a migration. See [Pi server](../../Pi%20Agent/packages/server/README.md) and [Pi client](../../Pi%20Agent/packages/client/README.md).

## Remaining desktop workflow parity

| ID | Status | Work to finish | Completion check |
| --- | --- | --- | --- |
| DESK-01 | Missing | Persist read/unread state and add mark-unread actions. Current running/attention/draft badges are different states. | An unseen completion is marked, visiting clears it, manual unread works, and restart preserves it. |
| DESK-02 | Partial | Host-owned automatic settlement rules and linked-PR state refresh: inactivity, merged/closed PRs, activity/approval/background-work exclusions, and unsettle override. Explicit settle and timed snooze shelves already exist. | Match T3's documented cases, including work resumed after a PR closed; rules run without the UI. |
| DESK-03 | Partial | T3's background **new task** submission: start the task and immediately present another draft while retaining project/model/base-branch/workspace defaults; create an independent worktree for each worktree submission. Current background method just asynchronously sends the selected thread. | Submit three new tasks successively and get three independent threads/worktrees without manual navigation. |
| DESK-04 | Partial | Sent attachment identities now survive draft clearing and restart, with image preview/zoom, confirmed Windows open, Save as and Copy path; missing/modified files are reported. See [sent content](SENT-CONTENT-2026-09-07.md). Native UI acceptance, richer video behavior and HEIC/HEIF conversion remain. | Send an image/video/file, restart, and reopen/save it from the original message. Convert an HEIC photo before provider dispatch. |
| DESK-05 | Partial | Sent citations now retain source metadata and quoted text, with file/line navigation and response ID/fingerprint resolution after restart. Missing or ambiguous responses preserve the quote without guessing. Native interaction acceptance and citation comment editing remain. | Send a citation, reopen the conversation, and navigate back to its original response/file range; a missing source preserves the quote. |
| DESK-06 | Partial | Open an agent's outside-workspace file link directly in the existing read-only viewer. The external picker works, but `OpenMarkdownLinkAsync` rejects paths outside the active workspace. | Follow an absolute report/PDF link outside the project and read it without enabling edits or neighboring HTML resources. |
| DESK-07 | Partial | Project settings depth: native icon/emoji/image choice and script editing, rather than only loading configuration and exposing defaults/run actions. Extend grouping across checkouts as remote environments arrive. | Change the icon/script through the UI and retain the intended settings across restart/checkouts. |
| DESK-08 | Partial | Full model picker and provider options: searchable/grouped instances, applicable effort/service-tier/agent options, and remembered new-task selections equivalent to T3. Current Pi model/thinking selectors are narrower. | Only supported options appear, values persist, and project defaults predictably override global defaults. |

Sources: [T3 thread behavior](../../t3code/docs/user/thread-sidebar.md), [T3 composer](../../t3code/docs/user/composer.md), [T3 unread state](../../t3code/apps/web/src/uiStateStore.ts), [T3 project settings](../../t3code/docs/user/project-settings.md), [T3 provider option picker](../../t3code/apps/web/src/components/chat/TraitsPicker.tsx). PiStation: [inbox](../src/PiStation.App/ViewModels/ShellViewModel.Inbox.cs), [submission](../src/PiStation.App/ViewModels/ShellViewModel.cs), [message model](../src/PiStation.App/ViewModels/ThreadViewModel.cs), [attachment formatting](../src/PiStation.PiRpc/Transport/PiPromptAttachment.cs), [link navigation](../src/PiStation.App/ViewModels/ShellViewModel.Navigation.cs).

## Remaining Git hosting and review parity

| ID | Status | Work to finish | Completion check |
| --- | --- | --- | --- |
| GIT-01 | Partial | GitHub details/body, commits/latest-head checks, conversations and hosted patches are implemented in the [PR review milestone](PI-PR-REVIEW-2026-09-06.md). Remaining: cross-project/provider filtering, checkout/review-thread creation/branch linkage, additional detailed-review adapters and authenticated/native acceptance. | Review a PR that was not created by the selected local thread without leaving PiStation. |
| GIT-02 | Partial | GitHub persistent inline drafts/submission, replies and permission-aware resolve/reopen are implemented with head guards and durable recovery. Remaining: PR title/body and own-comment editing, reactions, additional providers and authenticated/native acceptance. | Complete an inline review with replies and resolved threads; reopening displays the server's authoritative state. |
| GIT-03 | Partial | Auto-merge while checks run, check/workflow details, GitHub fork-workflow approval and revert PR flow, with provider capability gates. | Verify each supported operation against disposable repos and clearly retain uncertain outcomes after disconnection. |
| GIT-04 | Missing/partial | Implement a verified Bitbucket adapter and GitLab/Azure repository publishing. Current capabilities permit publishing only to GitHub; Bitbucket operations reject. | Clone/list/create/publish/review work to each host's supported extent; unsupported operations stay unavailable. |
| GIT-05 | Partial | Model-generated commit messages and PR titles/descriptions using actual changes, branch-vs-base context, writing style and repository conventions. Current generator uses `git status`, recent subjects and filename templates; a clean committed branch can yield “No uncommitted file changes detected.” | Generate a useful PR description after committing the branch; it describes the committed diff and obeys selected writing preferences. |
| GIT-06 | Partial | Account/repository discovery in Add Project and source-control setup/rescan, beyond pasting a Git URL and probing local CLI authentication. | Browse repositories available to the selected host/account and clone into the chosen destination. |

Sources: [T3 source control guide](../../t3code/docs/user/source-control.md), [T3 PR contracts](../../t3code/packages/contracts/src/pullRequest.ts), [T3 writing settings](../../t3code/apps/web/src/components/settings/SourceControlWritingSettings.tsx). PiStation: [hosting contract](../src/PiStation.Protocol/Models/SourceControlHosting.cs), [capabilities](../src/PiStation.Protocol/Models/HostingCapabilities.cs), [hosting service](../src/PiStation.Host/SourceControl/SourceControlHostingService.cs), [PR dialog](../src/PiStation.App/Views/ShellPage.Review.cs).

## Remaining browser, appearance and usage parity

| ID | Status | Work to finish | Completion check |
| --- | --- | --- | --- |
| BROWSER-01 | Partial | Import sessions directly from supported installed browser profiles. Current import accepts a selected cookie text/JSON file. T3 on Windows supports Firefox and compatible Helium profiles, not arbitrary app-bound-encrypted Chromium profiles. | Discover a supported profile, import a one-time cookie copy and report skipped/unavailable data. |
| BROWSER-02 | Partial | Expand Pi browser automation: explicit tab targeting/open, resize/color controls, key presses, scrolling, evaluation, condition waits, agent-controlled recording and richer semantic/diagnostic snapshots. Current tool has status/navigate/snapshot/click/type/screenshot and targets the selected visible browser. | An agent operates a specific tab while the user views another, handles keyboard/scroll/wait steps, and records the intended tab. |
| BROWSER-03 | Missing | Environment-owned preview sessions and remote localhost proxying, with ownership transfer/reconnect and automation that does not depend on the currently selected desktop thread. | A remote dev server is previewable from another machine; switching UI threads does not strand the original browser request. |
| LOOK-01 | Partial | Custom theme editor, palette inspection, T3/VS Code theme import/export, and environment-published themes/defaults. Current app offers fixed light/dark/system themes. | Import/edit/export a palette and recover cleanly from invalid or removed themes. |
| LOOK-02 | Partial | Independently configurable interface/composer/code fonts and sizes, beyond current terminal font options and text-profile compatibility testing. | Adjust each text surface independently without breaking navigation or compact layouts. |
| USAGE-01 | Partial | Dedicated usage dashboard with date filters, model/provider breakdowns, cache savings and historical session scanning/refresh. PiStation aggregates its recorded usage but mainly presents a summary. | Totals reconcile to stored sessions with missing usage/cost explicitly represented. |
| USAGE-02 | Pi/provider-dependent | Subscription quota/reset/pace panels and pooled CLIProxyAPI usage hubs. Current code explicitly reports portable Pi quota information as unavailable. | Authenticated adapters report supported account limits and reset times; unsupported accounts remain unknown. |

Sources: [T3 browser import](../../t3code/docs/user/browser-import.md), [T3 automation tools](../../t3code/apps/server/src/mcp/toolkits/preview/tools.ts), [T3 themes](../../t3code/docs/user/appearance.md), [T3 appearance settings](../../t3code/apps/web/src/components/settings/SettingsPanels.tsx), [T3 usage](../../t3code/docs/user/usage.md). PiStation: [browser tool](../src/PiStation.App/PiExtensions/pistation-browser.ts), [browser import handler](../src/PiStation.App/Views/Controls/RightPanelHost.xaml.cs), [layout settings](../src/PiStation.App/ViewModels/ShellLayoutViewModel.cs), [usage presentation](../src/PiStation.App/ViewModels/SettingsViewModel.cs), [usage storage](../src/PiStation.Host/Persistence/HostDatabase.cs).

## Larger features required for the full T3 product scope

| ID | Status | Work to finish | Dependency / completion check |
| --- | --- | --- | --- |
| RUNTIME-01 | Missing | Additional agent harness adapters: Codex, Claude Code, Cursor, Grok Build, OpenCode and Antigravity alongside Pi. | Pi's ability to call different model providers is not equivalent to running those agents. Introduce a capability-aware runtime abstraction; validate streaming, resume, approvals, questions, skills, tools, usage and stop for each adapter. |
| RUNTIME-02 | Missing | Named runtime/account instances, separate binaries/config homes/environment variables, account status and compatible account switching in existing threads. | Persist runtime instance identity on threads. Separate session-compatible account switches from moves between incompatible homes/harnesses. |
| REMOTE-01 | Missing | Standalone environment host/CLI and independently managed lifetime. Current host is embedded in the WinUI app and disposed with it. | Tasks/terminals survive closing a client. Include start/status/stop/update/recovery. T3 has managed background services on Linux/macOS; a Windows service would be PiStation-specific expansion. |
| REMOTE-02 | Missing | Multiple environment connections and selection, stable machine/environment identities, remote project paths, scoped commands/search/drafts/settings and connection removal. | One client controls two machines without mixing paths, credentials, threads, uploads or command receipts. |
| REMOTE-03 | Missing | Windows WSL environments: discover distributions, launch/reuse host, translate paths and run Pi/Git/shell tools inside the chosen distribution. | Create/edit/test a Linux workspace through PiStation and reconnect after WSL restarts. |
| REMOTE-04 | Missing | Managed SSH environments: host bootstrap/version check, tunnels, reconnect, credentials and owned-process cleanup. | Work on a remote checkout and reconnect without terminating unrelated host processes. |
| REMOTE-05 | Missing | Direct LAN pairing, persistent device sessions/scopes, revocation and Tailscale HTTPS integration. Current host binds loopback and rejects non-loopback clients. | Pair a second client, reconnect without a fresh link, revoke it, and verify remote files/terminals/preview. Merely changing the listen address is insufficient. |
| REMOTE-06 | Missing | A PiStation equivalent of T3 Connect: account linking, hosted relay/tunnel, remote environment discovery, device management and diagnostics. | Reach an environment across networks through infrastructure configured for PiStation. Do not assume T3's production relay/accounts are reusable by a fork. |
| CLIENT-01 | Missing | Web client, both host-served and a separately hosted client connecting to environments. | Core conversation, attachments, recovery, workbench, review and settings work in a browser with authenticated remote ownership. |
| CLIENT-02 | Missing | iOS/Android client with the corresponding supported task/model/approval/Git/terminal/file flows, remote reconnect, durable offline message/attachment queue and system share ingestion. | Disconnect, queue a task with attachments, restart, reconnect and deliver once to the original environment. |
| CLIENT-03 | Missing | T3 mobile extras: task-awareness/push notifications and navigation to the correct thread; platform-specific agent activity surfaces; supported iPhone on-device voice transcription. | Notification/deep-link targets are correct, registration/revocation works, and interrupted voice capture preserves the draft. A native Windows notification center would be a separate adaptation. |
| CLIENT-04 | Missing | macOS/Linux desktop distribution if “all T3 features” includes its desktop platform coverage. | WinUI is Windows-specific. Reuse the protocol/runtime where practical; provide another client/shell and platform terminal/preview implementations. |

Sources: [T3 supported agents/platforms](../../t3code/README.md), [remote access](../../t3code/docs/user/remote-access.md), [background service limits](../../t3code/docs/user/background-service.md), [WSL host](../../t3code/apps/desktop/src/wsl/DesktopWslBackend.ts), [provider instances](../../t3code/packages/contracts/src/providerInstance.ts), [Codex account semantics](../../t3code/docs/user/providers-codex.md), [Claude account semantics](../../t3code/docs/user/providers-claude.md), [mobile composer behavior](../../t3code/docs/user/composer.md), [mobile notification registration](../../t3code/apps/mobile/src/features/agent-awareness/remoteRegistration.ts).

Current PiStation boundaries: [bootstrap](../src/PiStation.App/Composition/AppBootstrapper.cs), [embedded host](../src/PiStation.Host/Hosting/EmbeddedEnvironmentHost.cs), [loopback authentication](../src/PiStation.Host/Security/LoopbackAuthentication.cs), [Pi process factory](../src/PiStation.Host/Threads/IPiProcessFactory.cs).

## Finishing and acceptance work

| ID | Status | Remaining work |
| --- | --- | --- |
| QA-01 | Partial acceptance | Actual Pi 0.84.4/0.85.0 offline tests cover skills/templates, tool execution, extension commands/UI, discovery, resource/package toggles, trust, custom-model persistence and resume. Authenticated skill invocation and resumed turns passed on both versions. Remaining: broader providers, first-login onboarding, model/thinking changes, attachments, queue delivery, compaction, subagents and browser automation. |
| QA-02 | Acceptance | Authenticated GitHub/GitLab/Azure end-to-end checks on disposable repositories, including writes, reconnection/uncertain results and actual CLI output. Add Bitbucket after its adapter exists. |
| QA-03 | Acceptance | Measure startup, thread switching, typing during streaming, long transcript/diff behavior, many projects and four terminals; verify idle CPU/memory and runtime eviction. Coalescing and idle cleanup exist; quantitative performance acceptance does not. Include pagination/truncation UX at collection bounds. |
| QA-04 | Acceptance | Remaining native accessibility/visual acceptance: physical DPI and mixed monitors, Windows High Contrast, real OS text scaling, keyboard/screen reader journeys, populated tables/media/review surfaces. Earlier app-owned text-profile checks do not certify all physical DPI settings. |
| RELEASE-01 | Deferred configuration + acceptance | Production publisher identity, signing, HTTPS App Installer feed, signed Release installation/update, package-identity migration and data retention on another machine. Packaging/update code exists; unsigned development MSIX is not production distribution. Existing owner deferral is recorded in [RELEASE-TODO](RELEASE-TODO.md). |

Retained evidence and coverage limits: [implementation status](IMPLEMENTATION-STATUS-2026-09-05.md), [UI verification](UI-VERIFICATION-2026-09-05.md), [install/recovery](INSTALL-AND-RECOVERY.md).

## Recommended implementation order

| Milestone | Work | Exit condition |
| --- | --- | --- |
| 1. Make Pi features actually reachable | PI-01–03 and core PI-04–05 implemented. Finish advanced setup and remaining QA-01 acceptance. | Real skills/extensions load, selected skills invoke correctly, extension status/widgets appear, and provider/trust setup is understandable. |
| 2. Complete the local Pi workflow | PI-06–10; DESK-01–08 | Import/fork/export, plan and subagent workflows, thread organization, sent artifacts/citations and background task creation work through restart. |
| 3. Complete review and browser work | GIT-01–06; BROWSER-01–02; QA-02 | Ask → implement → review → revise → commit → PR → review/merge works in one desktop workflow, including real browser actions. |
| 4. Complete personalization and measured reliability | LOOK-01–02; USAGE-01–02; QA-03–04 | Useful usage/limits where supported, custom appearance, and recorded responsiveness/accessibility acceptance. |
| 5. Add remote environments | REMOTE-01–06; BROWSER-03 | Stable independent host, multiple scoped environments, WSL/SSH/LAN and relay access, remote file/terminal/browser ownership. |
| 6. Expand runtimes and clients | RUNTIME-01–02; CLIENT-01–04 | Capability-tested agent adapters and web/mobile/other desktop surfaces. Runtime expansion can run as a separate workstream after the core contracts are stable. |
| Distribution | RELEASE-01 when the deferred production inputs are chosen | A second machine installs, works, upgrades and recovers with retained data. A dependable Windows/Pi release can precede full multi-platform T3 parity. |

Do not mark a row complete because a protocol enum, tab, or fake fixture exists. Completion needs a reachable user workflow, actual backend behavior, persistence/recovery where relevant, and acceptance evidence. Keep this broader feature backlog separate from the earlier GUI-parity completion claim.
