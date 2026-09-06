# PiStation compared with T3 Code — September 5, 2026

Implementation follow-up: [changes and remaining acceptance work](IMPLEMENTATION-STATUS-2026-09-05.md). Production distribution configuration is [TODO later](RELEASE-TODO.md), as requested. The findings below describe the pre-implementation audit.

PiStation already implements a substantial coding workspace. Its highest-value remaining work is completing everyday interactions, correcting several integration defects, and proving that a new user can install and rely on it. The existing documentation's blanket “complete” assessment is too strong for the implementation reviewed here.

The recommended product target is **a dependable native Windows workspace for Pi, with T3's efficient conversation, navigation, and review experience**. Keep the WinUI, Pi RPC, host, and client-runtime boundaries. Additional agent runtimes, remote environments, and mobile clients can be separate later milestones.

**Review scope and evidence**

- PiStation: `C:\Users\Bacon21\Workspace\PiStationDesktop1`, base commit `3f3bce9`, including the modified and untracked source present during this review. This is an assessment of the working tree, not just the committed version.
- T3: `C:\Users\Bacon21\Workspace\t3code`, commit `0a590fa01` dated September 4, 2026. Comparisons use this local checkout rather than claims about a moving upstream release.
- Read application code, contracts, persistence, tests, project documentation, T3 user guides, and selected corresponding T3 implementations.
- Inspected the retained T3 reference image and PiStation screenshots. The readable PiStation compatibility screenshot is from September 4; two inspected September 5 workbench screenshots are black. These artifacts do not establish the current working tree's visual quality.
- Executed Debug build, .NET code tests excluding the opt-in real-Pi test, terminal TypeScript typecheck/tests, and the static visual-contract check. Did not run packaged UI journeys, authenticated hosting operations, a real Pi turn, a fresh installation, or Release publishing.
- Findings below distinguish code defects from design recommendations and unverified compatibility. No application implementation was changed for this review.

**What is already present**

| Area | PiStation today | Assessment |
| --- | --- | --- |
| Foundation | Separate protocol, Pi RPC, host, and client runtime; SQLite metadata; loopback authentication; command receipts; stream replay and recovery | A useful foundation to preserve |
| Conversation | Streaming, native Markdown/code blocks, tools/reasoning, approvals/questions, model/reasoning selection, stop/recovery | Broad coverage; important interaction and rendering gaps remain |
| Composer | Durable text/attachment drafts, file mentions, images, slash/skill discovery, queues, stashes, context chips | Feature-rich; newer context features are not integrated into the durable draft model |
| Threads | Rename, archive, pin, title search, settlement/snooze fields, bulk operations, PR links | Backend breadth exceeds the coherence of the client inbox |
| Coding workspace | File tree/search/editing/previews, external editor, Git/worktrees, turn checkpoints and coupled revert | Core tools exist; code review needs a richer surface |
| Terminal | ConPTY, Ghostty/WebView2, splits, session persistence, search, font settings | One of the more developed workbench features |
| Preview | Local discovery, multiple tabs, profiles, viewports, capture, annotations, permissioned Pi browser bridge | Substantial implementation; retain and validate it |
| Agents | Structured activity hierarchy, status, usage, results, interruption through the parent turn | Useful observability; independent child control is a separate runtime capability |
| Productivity | Command palette, global search, custom keybindings, project defaults/scripts, settings routes | Already implemented; improve integration and discoverability |
| Distribution | WinUI/MSIX project and a Debug build/test workflow | Production install/update/release readiness is not demonstrated |

**Concrete defects and unfinished workflows**

1. **High priority: review context can follow the user into the wrong thread.**

   `ComposerPower.ContextChips` belongs to the shell. Switching between two non-null threads saves and reloads the ordinary composer draft, but does not save, clear, or restore the context chips. They are cleared when selecting no thread or after submission. Sending appends the shell's current chips to the prompt.

   Trigger: add a response citation or selected diff context in thread A, switch directly to thread B, then send. The code path retains A's context for B. This is a source-level finding; it was not reproduced through the UI during this audit.

   Move context into the thread's durable draft, including source identity and selected range. Preserve it across relaunch and stash/restore. Verify A → B → A, project switches, submission failure, and reconnect. Ordinary draft persistence should become the single owner of all sendable context.

   Evidence: [thread switching](../src/PiStation.App/ViewModels/ShellViewModel.cs#L798), [context collection and prompt assembly](../src/PiStation.App/ViewModels/ComposerPowerViewModel.cs#L21), [send path](../src/PiStation.App/ViewModels/ShellViewModel.cs#L2789).

2. **High priority: explicit pinned order is discarded by the client.**

   The host orders pinned threads by `PinnedOrder`. `EnvironmentClient.ListThreadsAsync` caches that response and returns `ThreadMetadata.GetProjectThreads`; the cache re-sorts by archive status, pinned flag, updated time, and ID. It ignores `PinnedOrder` and `IsSettled`. Consequently the move-pin operation cannot reliably produce its requested ordering in the normal list refresh.

   Use one shared ordering rule, then test the complete host → client → refresh/reconnect path with several pinned threads whose dates disagree with the requested order. Existing metadata tests do not cover the new ordering fields.

   Evidence: [host ordering](../src/PiStation.Host/Projects/ProjectService.cs#L196), [client return path](../src/PiStation.ClientRuntime/EnvironmentClient.cs#L409), [cache ordering](../src/PiStation.ClientRuntime/ThreadMetadataStore.cs#L188), [move-pin action](../src/PiStation.App/ViewModels/ShellViewModel.cs#L2446).

3. **High priority: normal-turn cost totals are misleading.**

   The ordinary turn-completion path calls `AppendUsageAsync` with a literal zero cost. Settings formats the aggregate as a dollar estimate and says costs come from Pi. Compaction has a separate path that can record reported cost, so this is specifically a gap in normal-turn accounting, not proof that every possible usage row is zero.

   Carry reported cost through normal turn usage, or represent unavailable cost as unknown. Do not display unknown cost as a zero-dollar estimate. Verify nonzero reported cost, missing cost, compaction, and relaunch. Quota is currently a fixed “Provider-managed” explanation, not live quota tracking; label it accordingly. Later add date/model/project breakdowns using the data already available.

   Evidence: [normal turn accounting](../src/PiStation.Host/Threads/PiThreadController.cs#L1326), [summary formatting](../src/PiStation.App/ViewModels/SettingsViewModel.cs#L80), [usage aggregation and quota fallback](../src/PiStation.Host/Persistence/HostDatabase.cs#L1175). T3 reference: [usage behavior](../../t3code/docs/user/usage.md).

4. **High priority: source-control writes lose PiStation's recovery guarantees.**

   PiStation's workspace Git mutations have command IDs, stored receipts, and explicit handling for uncertain dispatch. Hosting operations such as publish, create PR, and comment are direct service calls with no equivalent command identity in their request contracts. PR creation also performs a subsequent list request before returning success. If the write succeeds and the refresh fails, the user can receive a failure after the external operation already happened; retrying a comment can duplicate it.

   Reuse the existing operation/receipt model, record the external result before refreshing, distinguish write outcome from refresh outcome, and reconcile ambiguous outcomes on reconnect. Test disconnect after acceptance and failure after successful write.

   Evidence: [hosting contracts](../src/PiStation.Protocol/Models/SourceControlHosting.cs#L55), [direct host dispatch](../src/PiStation.Host/EnvironmentService.cs#L269), [create/mutate followed by listing](../src/PiStation.Host/SourceControl/SourceControlHostingService.cs#L94). Compare [existing Git recovery](../src/PiStation.ClientRuntime/EnvironmentClient.cs#L285).

5. **High priority before distributing: first-run setup has no complete recovery flow.**

   Bootstrap resolves Pi before creating the host and attaching the client. A missing or invalid installation leaves the shell with a runtime error. The Pi/runtime settings route offers a recheck through the existing client; it does not implement an executable picker, installation/authentication walkthrough, or bootstrap retry. A person with a clean machine therefore lacks a complete in-app route to a working first thread.

   Let the host/settings start in a “Pi needs setup” state. Provide a persistent executable setting, version check, actionable authentication guidance, and a retry that actually rediscovers Pi and establishes the runtime. Verify missing Pi, invalid path, unsupported version, authentication failure, and successful recovery without losing local data.

   Evidence: [bootstrap ordering](../src/PiStation.App/Composition/AppBootstrapper.cs#L28), [startup failure handling](../src/PiStation.App/App.xaml.cs#L75), [runtime settings and recheck](../src/PiStation.App/Views/ShellPage.xaml#L545), [settings refresh](../src/PiStation.App/ViewModels/ShellViewModel.cs#L572). T3 reference: [install and provider setup](../../t3code/docs/user/install.md).

6. **Before claiming four-provider hosting support: validate real provider behavior and expose capabilities.**

   The adapters have argument-building and JSON-fixture tests, but those do not establish authenticated workflows. Concrete issues requiring completion include:

   - Azure publish runs repository creation and immediately attempts to detect local `origin`; there is no explicit local remote-add/push sequence in that path. T3 separates repository creation, remote wiring, and push, and handles an empty repository as partial success.
   - GitLab listing constructs `--state`; the local T3 implementation uses native `--closed`, `--merged`, and `--all` flags. Validate the supported CLI version and arguments rather than treating generated arguments as proof of compatibility.
   - Bitbucket depends on a generic `bb` CLI with no pinned tool/version contract here. T3's implementation uses a token-backed API and explicitly disables reopening declined PRs.
   - PiStation exposes the same PR action menu for all providers even though its own switch rejects some operations. Availability checks use `--version`, which establishes tool presence, not authentication or write permission.

   Start with one fully verified provider, ideally the one used daily, and make other provider capabilities explicit. Add process-level fixtures and opt-in authenticated smoke tests. Handle rate limits, missing authentication, unsupported actions, and partial success as distinct states.

   Evidence: [provider commands](../src/PiStation.Host/SourceControl/SourceControlHostingService.cs#L204), [tool-presence check](../src/PiStation.Host/SourceControl/SourceControlHostingService.cs#L407), [PR UI](../src/PiStation.App/Views/ShellPage.xaml#L604), [current adapter tests](../tests/PiStation.Host.Tests/SourceControlHostingServiceTests.cs). T3 references: [publish workflow](../../t3code/apps/server/src/sourceControl/SourceControlRepositoryService.ts#L211), [GitLab flags](../../t3code/apps/server/src/sourceControl/GitLabCli.ts#L354), [Bitbucket capabilities](../../t3code/apps/server/src/pullRequest/BitbucketPullRequestProvider.ts#L14).

**Improvements that would make PiStation feel closer to T3**

7. **Build a proper code-review surface.**

   The Changes panel displays the patch in a read-only `TextBox`. The backend can already produce turn/file/thread diffs, but the UI lacks T3's structured, highlighted unified/split views. This is a large gap in the central “ask → inspect changes → refine → commit” workflow.

   Add addition/deletion colors, old/new line gutters, hunk boundaries/navigation, syntax highlighting, unified/split choice, collapse per file, and line-anchored review comments. Keep binary/truncated states explicit. Put PR review and creation beside Changes or in a dedicated review destination; the current PR workflow lives in Settings. Generated descriptions currently summarize filenames/recent subjects, so they also need a clear quality upgrade if advertised as change-aware writing.

   Acceptance: review a multi-file turn, select a precise changed range, send a follow-up, inspect the next checkpoint, and commit/create a PR without leaving the coding workflow.

   Evidence: [plain-text diff control](../src/PiStation.App/Views/Controls/RightPanelHost.xaml#L445), [generated text implementation](../src/PiStation.Host/SourceControl/SourceControlHostingService.cs#L140). T3 reference: [diff layout and renderer](../../t3code/apps/web/src/components/DiffPanel.tsx#L808).

8. **Finish transcript reading, navigation, and citations.**

   The Markdown renderer uses the basic CommonMark pipeline. Images become `[Image: …]` text; clickable links are limited to HTTP, HTTPS, and mail. Workspace paths and file-line links do not route into Files, and GitHub-style tables/task lists have no dedicated rendering. The T3 reference itself shows a table, so this difference is visible as well as functional.

   The transcript is a virtualized native `ListView`, which is a good starting point. However, the inspected control has no explicit follow-output, jump-to-latest, or per-thread scroll-anchor behavior; its `ScrollIntoView` path is for revealing a selected message. Implement bottom-follow only while the user is at the bottom, preserve their position while reading history, and show an unread/new-output affordance.

   Quote and Cite currently use the whole message. Citations are generic context chips without structured source navigation. Add selection-based quotes, thread/message/range identity, readable fallback text, and source navigation. Stashes currently hold text without attachments or context chips; make them capture a complete draft.

   Evidence: [Markdown pipeline/rendering](../src/PiStation.App/Views/MarkdownView.xaml.cs#L20), [image/link handling](../src/PiStation.App/Views/MarkdownView.xaml.cs#L195), [timeline control](../src/PiStation.App/Views/Controls/ConversationTimeline.xaml#L557), [timeline navigation](../src/PiStation.App/Views/Controls/ConversationTimeline.xaml.cs#L21), [Quote/Cite](../src/PiStation.App/ViewModels/ShellViewModel.cs#L2772), [stash contract](../src/PiStation.Protocol/Models/Composer.cs#L39). T3 references: [composer workflows](../../t3code/docs/user/composer.md), [timeline scrolling](../../t3code/apps/web/src/components/chat/MessagesTimeline.tsx).

9. **Make thread organization a coherent inbox.**

   The sidebar still has a projects list followed by the selected project's threads, rather than expandable project groups containing their own visible threads. It cannot provide the same cross-project scan of work as T3's sidebar. For closer T3 inspiration, add project grouping, compact per-thread working/waiting/completed/unread indicators, and persistent group expansion.

   Settlement and snooze also need behavioral definition. Every ordinary settled turn immediately marks its thread settled. That equates “agent finished responding” with “task finished.” T3's local documentation distinguishes these, using inactivity/PR policies and exceptions for active work. PiStation's snooze badge checks only whether a timestamp exists, so an expired timestamp can still display “Snoozed”; the inspected listing paths do not implement timed hiding/wakeup shelves. Make Active, Settled, Snoozed, and Archived predictable, provide reversals, and refresh linked PR state.

   Acceptance: operate several tasks across three projects; finish a turn without losing ongoing work; snooze and automatically restore a task at expiry; reorder pins and keep that order after reconnect/relaunch.

   Evidence: [separate project/thread lists](../src/PiStation.App/Views/Controls/AppSidebar.xaml#L174), [snooze badge](../src/PiStation.App/Views/Controls/AppSidebar.xaml#L344), [automatic settlement](../src/PiStation.Host/Threads/PiThreadController.cs#L1327). T3 reference: [thread sidebar behavior](../../t3code/docs/user/thread-sidebar.md).

10. **Measure streaming and long-session performance, then address the expensive paths.**

   Every selected-thread projection event schedules a UI update. Presentation construction traverses the full timeline and creates fresh message records; changing Markdown text clears and rebuilds its visual children. Record equality avoids replacing many unchanged message rows, so this is not a claim that all messages rerender every token. It is nevertheless a concrete place to measure allocations and UI-thread cost.

   Coalesce streaming updates, preserve stable message view models, and reuse parsed/rendered completed content as measurements justify. Add idle Pi-runtime eviction and a resource budget: the registry retains created controllers until explicit removal or application disposal, with no idle reaper visible. T3 includes a session reaper.

   Profile a realistic long transcript, many projects/threads, streaming while typing, large diffs, and four terminals. Record startup, thread-switch time, input responsiveness, and idle CPU/memory. Numeric acceptance budgets should follow a baseline on the supported machine; this audit did not measure a performance regression.

   Evidence: [projection dispatch](../src/PiStation.App/ViewModels/ShellViewModel.cs#L3400), [presentation reconstruction](../src/PiStation.App/ViewModels/ThreadViewModel.cs#L76), [Markdown rebuild](../src/PiStation.App/Views/MarkdownView.xaml.cs#L48), [runtime lifetime](../src/PiStation.Host/Threads/PiThreadRegistry.cs#L34). T3 reference: [idle session reaper](../../t3code/apps/server/src/provider/Layers/ProviderSessionReaper.ts#L17).

11. **Finish production packaging and make the quality gate credible.**

   The manifest still has `CN=AppPublisher`, `AppPublisher`, and version `1.0.0.0`. The visible update state is a fixed sentence asserting that the packaged channel checks updates. No update-check service or App Installer feed configuration was found. The checked-in CI gate builds/tests Debug; Release enables trimming/ReadyToRun and therefore needs its own installed-package verification.

   Choose the supported Windows/architecture matrix, set real package identity/signing/versioning, publish an installable artifact, implement a real update path, and test upgrade/migration/data retention on a clean machine. Add concise install, setup, recovery, backup, and update instructions; the current README is largely an implementation/test inventory.

   Terminal assets are tracked generated files, but the CI workflow does not run Node/TypeScript checks or verify that the bundle matches its sources. Wire the terminal build/typecheck/tests into CI with a drift check.

   The visual-contract script checks declarations, tokens, paths, and other static rules. Its success is useful but does not prove the rendered UI. The inspected September 5 `changes-workbench.png` and `files-workbench.png` are black. Add invalid/blank-capture detection and current populated-scene review before calling visual parity complete. Verify DPI scaling and actual Windows text scaling separately from the app-owned typography multiplier.

   Evidence: [package identity](../src/PiStation.App/Package.appxmanifest#L12), [Release settings](../src/PiStation.App/PiStation.App.csproj#L105), [fixed update state](../src/PiStation.Host/Diagnostics/HostDiagnosticsService.cs#L67), [Debug gate](../Invoke-PullRequestTests.ps1#L3), [terminal build instructions](../src/PiStation.App/TerminalWeb/README.md), [static visual checks](../tests/PiStation.UiTests/Test-VisualContract.ps1). Black images inspected: `tests/PiStation.UiTests/artifacts/workbench-runs/20260905-023454-73673c47696649aaa930eaf9519876fd/{changes,files}-workbench.png`.

**Architecture and visual direction**

Keep the native shell and semantic theme tokens. The readable retained screenshot already communicates a restrained dark workspace and a centered composer. The next visual pass should use populated conversations with tables, tools, errors, attachments, and diffs, plus multiple active projects. Empty-state screenshots alone cannot validate a coding workspace.

Reduce duplicated status text and remove implementation details such as configuration revision numbers from normal composer chrome. Keep model/reasoning near Send, primary Git/review actions near the conversation, and advanced settings in Settings. Rich diffs, file links, and a useful sidebar will change how the product feels more than another theme pass.

`ShellViewModel.cs` is roughly 4,800 lines including blank lines, and `RightPanelHost` combines several large feature surfaces. Continue extracting feature coordinators/views when fixing those areas, especially source control, composer power features, and preview orchestration. Make the stateful logic testable without constructing WinUI controls. A wholesale rewrite to Electron or T3's event-sourcing stack is not required to address these findings.

**A practical completion sequence**

| Milestone | Deliverable | Exit condition |
| --- | --- | --- |
| 1. Correctness | Thread-owned context, consistent pin order, honest cost/availability states, recoverable hosting writes | Focused regressions prove thread isolation, ordering, accounting, and ambiguous-write recovery |
| 2. Daily coding loop | Structured diff/review, file links, selected citations, full-draft stashes, transcript following | Complete ask → review → refine → checkpoint → commit → PR in one coherent workflow |
| 3. Navigation and setup | Project/thread hierarchy, inbox policies, Pi setup/retry, capable provider actions | A clean-machine user reaches a first successful task; several concurrent tasks remain easy to track |
| 4. Reliability and performance | Populated UI tests, real-Pi smoke coverage, idle-runtime lifecycle, measured rendering | Recovery, approvals, queue/subagent flows, relaunch, scaling, and long sessions meet agreed acceptance criteria |
| 5. Release | Signed/versioned package, installed Release tests, update mechanism, migration/data-retention checks, user docs | A second machine can install, work, update, and recover without source checkout or developer intervention |

Treat milestones 1–5 as the definition of a complete Windows/Pi 1.0. Keep optional expansion off that critical path unless it is personally essential:

- WSL and SSH/remote environments, pairing, multi-environment selection, remote preview ownership/proxying.
- Web/mobile clients and independent background hosting when the desktop closes.
- Additional agent harnesses and multiple runtime/account instances. Pi's model-provider selection is a different capability from T3's multiple harness adapters.
- Full provider-specific quota dashboards, theme import/editor, advanced PR collaboration, and independent subagent lifecycle controls where Pi supports them.

**Validation performed during this review**

| Check | Result |
| --- | --- |
| `dotnet build PiStationDesktop.slnx --configuration Debug --no-restore` | Passed; 0 warnings, 0 errors |
| `.NET tests --configuration Debug --no-build --filter Category!=RealPi` | 173 passed: Host 76, ClientRuntime 31, PiRpc 37, Protocol 25, CommandSystem 4 |
| Terminal `npm run typecheck` | Passed |
| Terminal `npm test` | 5 passed |
| `Test-VisualContract.ps1` | Passed; 24 declared states, 4 responsive layouts, 3 text profiles; reference commit `9159b808d35a88e74fc91e11070f3270cdb321f9` |
| Current packaged UI, real Pi, live hosting, Release install/update | Not executed in this review |

Fresh .NET results are under `TestResults/t3-review-2026-09-05`. Passing these tests establishes a healthy baseline; it does not cover the cross-feature defects and release gaps above.
