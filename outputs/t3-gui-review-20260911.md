# PiStation GUI review against local T3 Code

**Overall judgment: approximately 6/10 for GUI fidelity, provisional.** PiStation has a recognizable T3-style shell and substantial underlying functionality. It does not yet closely reproduce the current T3 navigation, control density, document surfaces or interaction presentation. The strongest matches are the broad shell, subdued theme and conversation/Markdown layout. The largest gaps are the sidebar, Settings, composer controls, workbench organization and pull-request review presentation.

This is a design review score, not a measured pixel-similarity percentage, implementation-completion percentage or estimate of remaining effort. The tracker’s 219/222 implemented rows answer a different question.

## Baseline and evidence limits

- Reference: `C:/Users/Bacon21/Workspace/t3code`, HEAD `0a590fa01af66ec135d2ebf2d5542b08a37dc275`.
- Subject: PiStation HEAD `4adf4b7` plus the current uncommitted accessibility changes. Source findings below include those changes.
- Compare the Windows desktop-relevant GUI, preserving Pi-only runtime scope. Other agent harnesses, mobile/web clients and hosted cloud accounts are not deductions. Native caption buttons and accessible Windows behavior are legitimate adaptations.
- T3’s current default is the modern thread sidebar: `legacySidebarEnabled` defaults to false in [settings.ts](../../t3code/packages/contracts/src/settings.ts). Both modern and legacy sidebars exist. This review targets the default, not an arbitrary combination of historical screenshots.
- Inspected T3’s [checked-in desktop marketing image](../../t3code/apps/marketing/src/assets/app-desktop.webp) and PiStation’s [older pinned T3 reference](../tests/PiStation.UiTests/References/t3code-updated-screenshot.webp). These are visual references, not freshly rendered captures of the reference commit. Current source resolves structural differences between them.
- Inspected retained PiStation screenshots from September 5–11, listed below. Older captures are used only with current-source corroboration; later fixes are explicitly acknowledged. Different sizes, DPI, content and themes prevent pixel-level comparison.
- Live native window discovery failed on the initial attempt, retry, and reset/reinitialization with `failed to connect native pipe ... (os error 2)`. No live GUI journey or new screenshots were produced. No application code, tracker statuses or user data were changed.
- Grades with source-only evidence are provisional. No score certifies current keyboard, motion, focus, screen-reader or performance behavior.

## Grading scale

9–10: close reference match with minor native differences. 7–8: recognizable match with localized differences. 5–6: comparable purpose, but visible structural or control-presentation differences. 3–4: substantial redesign needed for a close match. 0–2: absent or fundamentally unlike the selected reference. Scores are reviewer judgments; a low fidelity score does not mean the capability is absent or broken.

Evidence: **R+S** = retained rendered PiStation evidence corroborated by current source; **S** = source comparison without adequate corresponding rendered evidence. Confidence applies to the identified structural difference, not complete runtime acceptance.

## Grades by layer and surface

| Layer / GUI | Score | Evidence / confidence | Main reason |
| --- | --- | --- | --- |
| 1. Window shell and main composition | 7/10 | R+S / medium | Sidebar, conversation, bottom composer and optional right panel are recognizable; panel proportions and stacked chrome still differ. |
| 2. Colors, typography, icons and control density | 7/10 | R+S / medium | Good neutral theme foundation; Fluent controls, glyphs, spacing and radii do not form the same compact system as T3. |
| 3. Sidebar, project navigation and thread inbox | 4/10 | R+S / high | Project expanders, checkout-path buttons and a second thread list compete with the modern T3 thread-focused hierarchy. |
| 4. Thread header, branch and workspace controls | 6/10 | R+S / medium | Similar title/Add/Open shell; project pill and status emphasis differ, with Git workflow actions moved into the workbench. |
| 5. Composer, model controls and context attachments | 5/10 | R+S / high | Rounded dock is recognizable; expanded toolbar is crowded and resting UI replaces the editor with an expansion button. |
| 6. Conversation and message hierarchy | 7/10 | R+S / medium | Continuous assistant text, user bubbles and folding exist; persistent actions and repeated status/metadata add visual weight. |
| 7. Markdown, code blocks and message artifacts | 7/10 | R+S / medium | Readable native rich content; spacing, inline-code treatment and attachment/citation actions need closer alignment. |
| 8. Tools, approvals, questions and recovery notices | 6/10 | R+S / medium | Connected disclosures and actions exist; larger expanders/InfoBars and transcript forms differ from T3’s compact composer-associated presentation. |
| 9. Workbench frame and tab organization | 5/10 | R+S / high | Fixed five-category navigation plus inner tab strips differs from T3’s dynamic surface tabs and compact layout controls. |
| 10. File browser and source editor | 5/10 | R+S / high | File tabs and previews exist; vertically partitioned browser/editor and a plain source TextBox diverge from T3’s code editor surface. |
| 11. Git changes and diff review | 6/10 | R+S / medium | Structured diffs exist; Git operation forms and file list consume space above the code being reviewed. |
| 12. Pull-request inbox and detailed review | 4/10 | S / high | Modal list/review flows with stacked controls replace T3’s inbox route and persistent Summary/Timeline/Code surface. |
| 13. Terminal | 6/10 | R+S / medium | Real terminal, splits and search are present; multiple selector/action rows reduce the terminal canvas. |
| 14. Browser preview and device tools | 5/10 | R+S / medium | Tabs, address and capture tools exist; long horizontal control strips combine browsing with profile and policy administration. |
| 15. Agents and workflow monitoring | 6/10 | R+S / medium | Hierarchy, live status and metrics exist; large variable-height cards and workflow setup compete with monitoring. |
| 16. Settings and preferences | 4/10 | R+S / high | Modal Settings with a large Done footer remains fundamentally different from T3’s full-page Settings navigation. |
| 17. Usage, limits and diagnostics | 5/10 | R+S / medium | Useful data and charts exist, but long text summaries and provenance dominate the Settings-based presentation. |
| 18. Search, command palette, dialogs and menus | 6/10 | S / medium | Search/commands are connected; native modal sizing, row treatment and action hierarchy need a matched-state pass. |
| 19. Connections and setup surfaces | 6/10 | S / medium | Windows connection workflows are available; long nested forms need clearer progressive disclosure and alignment with Settings. |
| 20. Responsive composition | 6/10 | R+S / medium | Explicit breakpoints and overlays exist, but toolbar overflow and narrow form layouts need reference-matched acceptance. |
| Keyboard, focus, motion and screen-reader polish | Not certified | Source and historical partial checks | Implementation exists, including recent accessibility fixes; a live interaction review is required for a defensible behavioral grade. |

## Detailed findings and target behavior

### 1–2. Shell and design system

The broad spatial model is a reasonable foundation. PiStation defines a 260-DIP sidebar, 720-DIP reading column and 420-DIP workbench default. T3’s sidebar defaults to 256 CSS pixels; its composer uses `max-w-3xl` and 22-pixel corners, while PiStation’s composer token is 18 DIPs. These are source units, not evidence of physical-pixel mismatch at arbitrary Windows scale. Compare at matching content size and scale before changing values.

The larger issue is inconsistent consumption of the compact style system. Many late-added buttons and ComboBoxes use native defaults while earlier shell controls use explicit Pi compact styles. The retained images show the resulting difference in height and emphasis. Standardize primary, secondary, icon-only and disclosure controls; use a consistent icon family and optical size. Match neutral foreground/hover/selection contrast, then tune typography and radii. Keep visible keyboard focus.

Sources: [Pi tokens](../src/PiStation.App/Themes/T3DesignTokens.xaml), [Pi shell](../src/PiStation.App/Views/Controls/WorkspaceShell.xaml), [T3 sidebar width](../../t3code/apps/web/src/components/threadSidebarWidth.ts), [T3 CSS](../../t3code/apps/web/src/index.css), [T3 composer surface](../../t3code/apps/web/src/components/chat/ComposerSurface.tsx).

### 3. Sidebar — highest-impact change

PiStation currently shows an Active/Settled/etc. selector, a project-group Expander containing project/checkout controls and task entries, then a separate THREADS list. The same thread appears in both areas in inspected captures. Checkout paths and up/down project-reorder buttons are exposed within the navigation area. This consumes substantial height before the primary thread list.

T3’s default Sidebar uses thread rows/cards with project identity, title, branch/PR and status metadata, plus compact project filtering and settled history. Secondary actions become visible on hover/focus or through menus. The legacy project tree remains optional in T3 and is not the default benchmark.

Target: one authoritative thread list with compact project filtering; put checkout paths, project customization and reorder commands in their appropriate menus. Preserve keyboard-accessible equivalents for hover actions. This is an information-hierarchy change, not merely smaller padding.

Sources: [Pi sidebar](../src/PiStation.App/Views/Controls/AppSidebar.xaml), [T3 sidebar](../../t3code/apps/web/src/components/Sidebar.tsx), [T3 sidebar selection](../../t3code/apps/web/src/components/AppSidebarLayout.tsx).

### 4. Header and branch context

PiStation’s title, project badge, status pill, Add, Open and workbench toggle provide the right outline. T3 gives project/thread breadcrumbs and compact action menus greater emphasis; branch/worktree/PR controls are integrated with the workspace/composer context. PiStation’s bottom WorkspaceStatusBar is primarily informational, while prominent Git actions live in Changes.

Target: match the reference breadcrumb and action order, provide compact branch/worktree/PR selectors in context, and let the Changes surface prioritize the diff. Preserve native Windows caption clearance; do not copy macOS window controls from a reference image.

Sources: [Pi header](../src/PiStation.App/Views/Controls/ChatHeader.xaml), [Pi status bar](../src/PiStation.App/Views/Controls/WorkspaceStatusBar.xaml), [T3 branch toolbar](../../t3code/apps/web/src/components/BranchToolbar.tsx), [T3 page header](../../t3code/apps/web/src/components/WorkspacePageHeader.tsx).

### 5. Composer

PiStation has the correct rounded bottom surface, attachments, suggestions and model/reasoning selection. Its expanded toolbar uses a horizontal ScrollViewer containing multiple controls, including Stash, model selection, reasoning and a separate Models button. Additional actions/status occupy more space. T3 uses compact controls and an adaptive compact-controls menu rather than relying solely on a horizontally scrolling collection of normal controls.

Both applications implement resting/collapsed composer behavior. The difference is not that T3 never collapses: its current resting path still renders ComposerPromptEditor and can place controls in a resting strip. PiStation hides ExpandedComposer and shows a button with a draft summary, “Click or press Enter to edit,” and Stop.

Target: retain direct editing in the resting surface, consolidate model selection, move secondary actions into an accessible overflow menu, and maintain a clear attachment/send/stop hierarchy. Preserve Pi-specific queue, compaction and permissions behavior through compact controls rather than adding permanent toolbar weight.

Sources: [Pi composer](../src/PiStation.App/Views/Controls/ComposerSurface.xaml), [Pi resting behavior](../src/PiStation.App/Views/Controls/ComposerSurface.Resting.cs), [T3 composer](../../t3code/apps/web/src/components/chat/ChatComposer.tsx), [T3 compact controls](../../t3code/apps/web/src/components/chat/CompactComposerControlsMenu.tsx).

### 6–8. Conversation, rich content and task interaction

Continuous assistant messages, right-aligned user messages, native Markdown, code highlighting and collapsible reasoning/tool activity are good foundations. Retained captures show repeated lifecycle labels and permanent Quote/Cite text actions. Attachment templates add multiple rows of Preview/Open/Save as/Copy path controls; citations use full Expanders. These make short content and artifacts visually heavier than the T3 reference.

T3’s current pending-approval component is compact and integrated into the composer workflow. PiStation renders approval/question templates in the timeline and uses native InfoBars for recovery. The functional distinction between a live pending request and its historical record should stay clear.

Target: give each turn one concise activity summary, reduce redundant lifecycle labels, use restrained message action groups, and make attachments/citations compact with details on demand. Present the pending action near the composer while retaining its resolved history. Keep complete command details accessible; do not truncate critical approval information for appearance.

Sources: [Pi timeline](../src/PiStation.App/Views/Controls/ConversationTimeline.xaml), [Pi Markdown](../src/PiStation.App/Views/MarkdownView.xaml.cs), [Pi recovery](../src/PiStation.App/Views/Controls/RecoveryBannerStack.xaml), [T3 Markdown](../../t3code/apps/web/src/components/ChatMarkdown.tsx), [T3 approval presentation](../../t3code/apps/web/src/components/chat/ComposerPendingApprovalPanel.tsx), [T3 composer banners](../../t3code/apps/web/src/components/chat/ComposerBannerStack.tsx).

### 9. Workbench navigation

PiStation reserves a “Workbench” title row and a fixed Changes/Files/Terminal/Preview/Agents category row; file and browser tabs then add another navigation level. T3’s RightPanelTabs accepts actual open surfaces, with activation, close, close-others/right/all and add-surface actions. It also exposes layout/maximized state through its panel shell.

Target: a compact document/surface tab strip with an add menu and layout controls. Preserve independent browser/file/terminal state while avoiding multiple competing tab hierarchies. The existence of inner file/browser tabs in PiStation is acknowledged; it is the overall organization that differs.

Sources: [Pi workbench](../src/PiStation.App/Views/Controls/RightPanelHost.xaml), [T3 surface tabs](../../t3code/apps/web/src/components/RightPanelTabs.tsx), [T3 panel shell](../../t3code/apps/web/src/components/preview/PreviewPanelShell.tsx).

### 10–11. Files, editor and diffs

PiStation’s source-editing surface is a multiline TextBox with theme, font and wrapping preferences. T3’s FilePreviewPanel instantiates the Pierre Editor with syntax-highlighting infrastructure. File-opening/editing being implemented does not imply equivalent editing presentation. PiStation has structured diff rendering and unified/split modes, so describing its diffs as only raw text would also be inaccurate.

Retained Files/Changes images show the file list above the content with a large vertical partition. Current Changes XAML retains `0.8*` and `1.2*` rows and several operation/form rows. This makes the code viewport smaller and moves attention away from the review.

Target: make the document the primary surface, use a compact collapsible file navigator and a small context toolbar, and provide editor-grade line gutters/highlighting/selection where appropriate. Move branch creation, initialization, pull/push and commit entry into contextual menus or dedicated actions. Tune diff file headers, old/new line gutters, hunk navigation and annotations against the same changed file in T3.

Sources: [Pi files/changes XAML](../src/PiStation.App/Views/Controls/RightPanelHost.xaml), [Pi structured diff](../src/PiStation.App/Views/Controls/DiffView.cs), [T3 file editor](../../t3code/apps/web/src/components/files/FilePreviewPanel.tsx), [T3 file browser](../../t3code/apps/web/src/components/files/FileBrowserPanel.tsx), [T3 diff](../../t3code/apps/web/src/components/DiffPanel.tsx).

### 12. Pull requests

The new provider integrations are real functionality, but the presentation is much less like T3. PiStation’s inbox is a ContentDialog with an expandable filter form and a ListView whose rows render DisplayText in a wrapped TextBlock. Review selected closes/transitions into another modal review flow. T3 has a pull-requests route and persistent detail surfaces with Summary, Timeline and Code tabs, rich list rows and compact review controls.

Target: a dedicated inbox page with scannable columns/metadata, compact filters, and a persistent review surface. Preserve provider capability differences and durable draft/partial-action recovery while reorganizing the UI. Do not treat unsupported provider actions as visual gaps to fake.

Sources: [Pi inbox](../src/PiStation.App/Views/ShellPage.PullRequestInbox.cs), [Pi review management](../src/PiStation.App/Views/ShellPage.PullRequestManagement.cs), [T3 inbox route](../../t3code/apps/web/src/routes/_chat.pull-requests.tsx), [T3 detail panel](../../t3code/apps/web/src/components/pullRequest/PullRequestDetailPanel.tsx).

### 13–14. Terminal and browser

PiStation’s terminal supports real output and split sessions; the September 11 capture also shows the new accessible Read output entry point. The visible shell selector, session selector, action row, session metadata and split controls take several rows before terminal content. T3 uses compact terminal controls and session/group navigation.

Browser navigation is similarly recognizable, but current XAML puts viewport presets, profile creation/default selection, recent URLs, appearance, zoom, DevTools policy, cookie import, recording, picture-in-picture and agent access into long horizontal strips. T3’s browser device toolbar is a compact, specialized control surface. The current Pi viewport presets are bounded; a larger named-device catalog is a separate scope decision already noted by the tracker, not silently required by this review.

Target: one compact navigation row; show device controls only when needed; move profile maintenance and advanced tools into menus or Settings. Preserve a visible recording indicator and explicit agent access state. Keep the terminal reader keyboard-accessible while integrating its entry point with terminal actions.

Sources: [Pi terminal reader](../src/PiStation.App/Views/Controls/TerminalWebViewSurface.xaml), [Pi panel toolbars](../src/PiStation.App/Views/Controls/RightPanelHost.xaml), [T3 terminal](../../t3code/apps/web/src/components/ThreadTerminalDrawer.tsx), [T3 device toolbar](../../t3code/apps/web/src/browser/BrowserDeviceToolbar.tsx).

### 15. Agents

PiStation’s agent cards display identity, metrics, model, full task/result/error text and actions, with workflow setup/presets in the same surface. T3’s AgentsPanel explicitly reserves three stable lines per agent for identity, activity and metrics and keeps updates from changing row height.

Target: a compact stable-height roster with workflow hierarchy and a selected-agent detail view. Keep workflow creation and preset editing behind an intentional action. Pi-specific workflow configuration remains supported; it need not occupy the monitoring view by default.

Sources: [Pi agents UI](../src/PiStation.App/Views/Controls/RightPanelHost.xaml), [T3 agents](../../t3code/apps/web/src/components/AgentsPanel.tsx).

### 16–17. Settings, usage and diagnostics

This is a substantial navigation mismatch. T3 replaces the normal thread sidebar with Settings navigation and renders a full-page route with breadcrumbs, search and grouped setting rows. PiStation uses a ContentDialog containing a NavigationView, scrollable stacked cards and a large Done footer. The dialog has already been widened in current code, and Sessions buttons now wrap; older narrow screenshots must not be treated as proof those changes failed. Widening and wrapping still do not reproduce T3’s full-page structure.

T3 Usage is its own page with metric controls, chart, totals and responsive grid layout. PiStation opens Usage within Settings and gives scan counts, pricing coverage and provenance multiple text blocks before the main chart. Those details are valuable, but do not need primary visual emphasis.

Target: full-page Settings with search, compact grouped label/control rows and clear return navigation. Put Usage on its own dashboard surface, prioritizing key numbers and chart. Move scan/pricing provenance into details; keep cost qualifications readable near cost values. Make diagnostics readable tables and labeled graphs with secondary technical detail.

Sources: [Pi Settings and Usage](../src/PiStation.App/Views/ShellPage.xaml), [Pi usage navigation](../src/PiStation.App/Views/ShellPage.Usage.cs), [T3 Settings route](../../t3code/apps/web/src/routes/settings.tsx), [T3 Settings navigation/search](../../t3code/apps/web/src/components/settings/SettingsSidebarNav.tsx), [T3 setting groups](../../t3code/apps/web/src/components/settings/SettingsPanels.tsx), [T3 Usage page](../../t3code/apps/web/src/components/usage/UsagePage.tsx).

### 18–20. Other dialogs, connections and responsive behavior

Command palette and repository discovery use connected native dialogs, but need matching populated screenshots for row density, shortcut hints, selection, empty/error states and keyboard behavior. PiStation’s palette content currently has a 620-DIP minimum, its PR inbox a 480-DIP minimum; these deserve narrow-window qualification rather than an assumed clipping verdict. Repository discovery’s earlier fixed minimum was removed by the current accessibility changes.

Connections combines direct sharing, pairing, saved connections, Tailscale and SSH through nested forms. Group common actions first and reveal configuration on demand. Keep Windows-only/manual-update boundaries; adding T3 cloud accounts is not part of visual matching.

PiStation has explicit 720/900/1180 breakpoints and workbench overlays. T3 uses responsive and container-aware layout, including adaptive composer controls. Test the actual available content width, not just window width: sidebar, panel and text scale all affect it. Preserve recently added F6 navigation, IME/repeat-safe input, popup isolation, High Contrast and terminal reader work during any redesign. Motion and focus grades remain unverified until live review.

Sources: [Pi dialogs](../src/PiStation.App/Views/ShellPage.xaml), [Pi repository discovery](../src/PiStation.App/Views/ShellPage.RepositoryBrowser.cs), [Pi connections](../src/PiStation.App/Views/Controls/RemoteConnectionsPanel.xaml), [Pi SSH](../src/PiStation.App/Views/Controls/SshConnectionsPanel.xaml), [Pi focus routing](../src/PiStation.App/Views/ShellPage.Accessibility.cs), [T3 palette](../../t3code/apps/web/src/components/CommandPalette.tsx), [T3 connections](../../t3code/apps/web/src/components/settings/ConnectionsSettings.tsx), [remaining native acceptance](../Docs/DEL-03-ACCESSIBILITY-IMPLEMENTATION.md).

## Recommended order of work

These are proposed GUI review items, not newly completed tracker features.

| Priority | Review item | Finish line |
| --- | --- | --- |
| First | GUI-01: Refresh the benchmark | Pin the specified T3 commit and default sidebar; capture matching fixtures in both applications at the same Windows scale and viewport. |
| First | GUI-02: Sidebar hierarchy | One primary thread list; no repeated thread entries or exposed checkout/reorder administration at rest. Match project filtering and settled-history hierarchy. |
| First | GUI-03: Full-page Settings | Settings owns the main content surface, has searchable grouped rows and return navigation, and works at narrow/text-scaled sizes. |
| First | GUI-04: Composer controls | Direct resting editing, compact model/context controls and predictable overflow; no horizontal hunt for Send/Stop or common actions. |
| Next | GUI-05: Workbench surfaces | Compact dynamic tabs and layout actions; remove redundant category/title chrome without losing independent document state. |
| Next | GUI-06: Files and diff presentation | Code is the primary viewport; compact navigator and contextual Git actions; reference-matched editor/diff fixtures. |
| Next | GUI-07: PR inbox/review | Dedicated inbox and persistent Summary/Timeline/Code review with provider-aware controls. |
| Next | GUI-08: Browser/terminal chrome | Compact common toolbar, advanced menus and clear recording/access state; preserve terminal reader and split controls. |
| Then | GUI-09: Conversation and agents | Restrained message/attachment actions, concise task status and stable agent rows with details on demand. |
| Then | GUI-10: Tokens and native polish | Consistent density/icons/type across all controls; verify theme, focus, motion, scaling and accessibility in every redesigned surface. |

## Acceptance needed for a close-match claim

Use a shared fixture with several projects, active/settled/pinned threads, a long Markdown conversation, pending approval/question, code changes, multiple documents, two terminals, browser tabs, agent activity and PR data. Capture both applications at matching effective content sizes: 1200×800, 1440×900 and 1920×1080, plus narrow 720/900 cases; record Windows DPI and app text scale. Repeat relevant paths in dark, light and system High Contrast at 100/150/200% text scaling.

Review resting, hover, focus, expanded, loading, empty, error, disconnected and completed states. Exercise Tab/Shift+Tab, F6, palette, menus, composer suggestions, Escape, pane resizing and focus return. Motion requires a live sequence or recording; screenshots cannot grade it. Use controlled fixtures, not live provider writes, to review the GUI.

The existing [GUI parity plan](../Docs/PISTATION-T3CODE-GUI-PARITY-PLAN.md) says implementation complete against an older September 2 T3 baseline. The [visual contract](../tests/PiStation.UiTests/visual-contract.psd1) pins that older screenshot and geometry. Neither establishes current visual equivalence to `0a590fa01`; retain them as historical work and establish a new matched-state acceptance baseline.

## Retained images inspected

These files are local evidence and some are ignored test artifacts; they are not guaranteed to travel with Git. Fixture failure labels in images are not diagnosed as new product defects by this review.

- [September 11 shell/terminal reader entry](../TestResults/accessibility-uia-only/window.png): populated shell and terminal chrome; includes the documented failed fixture state.
- [Conversation](../tests/PiStation.UiTests/artifacts/runs/20260905-141839-665859e2f50c493e8c13c1349479a5d3/vertical-slice.png) and [Markdown](../tests/PiStation.UiTests/artifacts/runs/20260905-141839-665859e2f50c493e8c13c1349479a5d3/markdown-slice.png): September 5 layout evidence; composer/accessibility has later changes.
- [Changes](../tests/PiStation.UiTests/artifacts/workbench-runs/20260905-152351-9e818c03a63948a49ea81052da4f2451/changes-workbench.png), [Files](../tests/PiStation.UiTests/artifacts/workbench-runs/20260905-152351-9e818c03a63948a49ea81052da4f2451/files-workbench.png), [Preview](../tests/PiStation.UiTests/artifacts/workbench-runs/20260905-152351-9e818c03a63948a49ea81052da4f2451/preview-workbench.png): older workbench captures, checked against current XAML; not certification of later feature additions.
- [Earlier Settings](../tests/PiStation.UiTests/artifacts/settings-open.png) and [Sessions](../TestResults/pi-session-navigation-native/045b6de0eb5949ca9a4d91ba711dc8b3/navigation.png): historical narrow layout; subsequent widening/wrapping is acknowledged.
- [Usage](../TestResults/usage-dashboard-native/7412fa3ffec34b0f8d9cc99dd9791957/usage-overview.png), [Diagnostics](../TestResults/reliability-diagnostics-native/50e64e1f41a54598b7388a3518b1ac85/diagnostics.png), [Agents](../TestResults/pi-agents-native/3fd37264b06d47a0952d954a5e4ad0a3/pi-agents-active.png): populated retained native evidence.
- The blank `increment-3-vertical-slice.png` was excluded as visual proof. A browser `background-browser.png` showed only page content, so it was excluded as browser-chrome evidence.
