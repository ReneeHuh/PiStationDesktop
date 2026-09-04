# PiStation Desktop — T3 Code GUI Parity Plan

Status: implementation complete — Stages 0–7 and side-by-side refinement complete  
Target: PiStation Desktop packaged WinUI 3 application  
Baseline: T3 Code `9159b808d35a88e74fc91e11070f3270cdb321f9`, reviewed 2026-09-02  
Last reviewed: 2026-09-03

## 1. Outcome

Rebuild PiStation's presentation layer so it has the density, hierarchy, and workbench character of
current T3 Code while remaining a native Windows application centered on Pi.

This began as a GUI-first program. It created honest, testable places for later Git, files, terminal,
preview, and agent-observability features; Files, Changes, and a first Terminal slice have since
filled those boundaries. A surface
without behavior must be an explicit empty or unavailable state, never a control that silently does
nothing.

The intended first impression is:

- a compact project-and-thread navigator rather than a settings-heavy sidebar;
- a calm, continuous conversation rather than a stack of form cards;
- a docked agent composer rather than a conventional text form;
- a useful thread toolbar rather than configuration occupying the transcript;
- an optional workbench panel ready for files, changes, terminal, preview, and agents;
- native Windows focus, keyboard, scaling, and accessibility behavior throughout.

The goal is visual and interaction parity, not T3 branding. PiStation keeps its own name, Pi icon,
terminology, architecture, and accessibility contract. T3 trademarks and product artwork are not
copied. If source code or assets are ever ported instead of reimplemented, preserve the upstream MIT
license notice and review the result separately.

## 2. Why the current UI does not yet feel like T3 Code

The current dark palette is directionally correct. The larger problem is information hierarchy.

| Current PiStation behavior | Visual consequence | Target correction |
| --- | --- | --- |
| Model and reasoning occupy a full-width card above every transcript | Configuration dominates the task | Move thread configuration into compact composer/header controls |
| User and assistant messages both use bordered cards | The transcript reads as a form or dashboard | Use a continuous document; reserve a subtle bubble for user turns |
| Composer has a label, large empty field, and separate text buttons | It reads like a data-entry form | Use one rounded composer shell with an internal bottom control row |
| Header contains only the title and an idle pill | No workbench hierarchy or next actions | Add project breadcrumb, thread title, status, and compact action rail |
| Sidebar uses section labels and rows but little secondary information | Projects and thread activity are hard to scan | Add project identity, hierarchy, status, preview, and relative time |
| Connection and test controls consume primary space | Infrastructure competes with the conversation | Keep normal status quiet; move details to banners, settings, or diagnostics |
| All content lives in two columns | Future tools have no stable home | Add a collapsible/resizable right-panel host |
| Borders and rounded rectangles identify nearly every region | Too many surfaces have equal weight | Prefer spacing and typography; use borders only for interactive/contained units |

Reference evidence:

- PiStation current-state screenshots are produced by the packaged UI journeys under
  `PiStationDesktop/tests/PiStation.UiTests/artifacts`.
- The workspace's reproducible visual reference is
  `Core/t3code/apps/marketing/public/updated-screenshot.webp`.
- The current comparison uses T3 Code's `Sidebar`, `ChatHeader`, `MessagesTimeline`, `ChatComposer`,
  `BranchToolbar`, and `RightPanelTabs` sources at the baseline commit above.
- `T3-CODE-PISTATIONDESKTOP-GUIDE.md` remains the broader architecture/feature comparison; this
  document owns the GUI redesign sequence.

## 3. Product stance

### 3.1 Match these T3 qualities

- Neutral, low-chroma dark surfaces with restrained borders.
- Compact controls and rows with clear hover, selected, focused, and active states.
- Strong hierarchy from typography and spacing instead of nested cards.
- One centered reading column shared by transcript and composer.
- Persistent project/thread context with controls close to the action they affect.
- Progressive disclosure for reasoning, tools, errors, configuration, and secondary panels.
- A stable workbench shell that can accept real features incrementally.

### 3.2 Preserve these PiStation strengths

- Native WinUI controls and UI Automation patterns.
- Semantic light, dark, and high-contrast resources.
- Existing Automation IDs unless an explicit migration is documented.
- Ordered streaming, durable drafts, attachments, interactions, and recovery semantics.
- The distinction between transport failure, Pi crash, resync, and uncertain dispatch.
- The existing zero-warning build and deterministic packaged-app gate.

### 3.3 Do not include in the visual-parity program

- Multi-provider support.
- Remote-environment transport or pairing.
- A real Git, terminal, file-editor, or browser backend within the original visual-parity stages.
- Custom theme import/export.
- Mobile or web clients.

Those are separate product features. This plan only creates appropriate view boundaries and honest
placeholder states for the workbench capabilities that are likely to arrive next.

## 4. Target information architecture

```text
┌──────────────────────────────────────────────────────────────────────────────┐
│ title-bar / drag region                                                     │
├───────────────┬──────────────────────────────────────┬───────────────────────┤
│ search        │ project / thread title        status │ Add action  Open  ⋯   │
│               ├──────────────────────────────────────┼───────────────────────┤
│ PROJECTS      │                                      │ panel tabs            │
│ ▾ project A   │          conversation                │ Changes Files Terminal│
│   ● thread 1  │          reading column              │ Preview Agents        │
│   ◌ thread 2  │                                      │                       │
│ ▸ project B   │   assistant content is unboxed       │ explicit empty state  │
│               │   user content uses subtle bubble    │ until feature exists  │
│               │   tools/reasoning stay compact       │                       │
│               │                                      │                       │
│               │    ┌────────────────────────────┐    │                       │
│               │    │ prompt                     │    │                       │
│               │    │ +  model  reason  mode  ↑  │    │                       │
│ settings      │    └────────────────────────────┘    │                       │
│ local • ready │    local checkout             branch │                       │
└───────────────┴──────────────────────────────────────┴───────────────────────┘
```

The right panel is absent by default. Opening a panel reduces the main column without moving the
sidebar. At narrow widths it becomes an overlay sheet rather than crushing the conversation.

## 5. Visual contract

These values are starting contracts, not untouchable constants. Adjust them only through shared
tokens after comparing screenshots at the reference sizes.

### 5.1 Geometry

| Element | Target |
| --- | --- |
| Default window | `1200 × 800`, preserving the current launch size |
| Visual review sizes | `1200 × 800`, `1440 × 900`, and `1920 × 1080` |
| Sidebar | 248–272 px, user-resizable later; 260 px initial target |
| Top workspace bar | 48–52 px |
| Reading/composer column | 720–768 px maximum |
| Right panel | 360–520 px, 420 px default when opened |
| Compact control | 28–32 px high |
| Sidebar row | 28–34 px depending on one- or two-line content |
| Composer | 96 px minimum; grows with content to a bounded maximum |
| Main horizontal gutter | 20–28 px; smaller at constrained widths |
| Standard radius | 6–8 px |
| Composer/user bubble radius | 16–20 px |

### 5.2 Surface hierarchy

Use four principal layers:

1. canvas;
2. sidebar/chrome;
3. raised interactive surface;
4. selected/active surface.

Assistant prose does not receive a card background. User messages use a quiet raised fill and a
maximum width near 80 percent. Reasoning and tool summaries are row-like disclosures. Code blocks,
changed-file summaries, approvals, questions, and errors may remain contained because their borders
communicate a real boundary.

### 5.3 Typography

- Keep Segoe UI Variable for native Windows consistency.
- Use 13–14 px for primary body text with relaxed transcript line height.
- Use 12–13 px for sidebar rows and control labels.
- Use 11–12 px for metadata, timestamps, tokens, and section labels.
- Use semibold sparingly for the active thread, project, important actions, and content headings.
- Preserve Cascadia Mono for code and tool arguments/output.

### 5.4 Interaction and motion

- Hover uses a small surface lift or fill change, not a bright outline.
- Selection uses a stronger fill and optional 2 px Pi accent rail.
- Keyboard focus remains clearly visible and must not be replaced by hover styling.
- Running state uses a status dot and restrained stepped pulse.
- Panel open/close and composer growth use short 120–180 ms transitions where WinUI can deliver them
  without harming UI Automation stability.
- Respect reduced-motion and high-contrast settings.

## 6. Surface map and feature stakes

| Surface | Existing behavior to retain | New visual responsibility | Future feature stake |
| --- | --- | --- | --- |
| App sidebar | Projects, threads, search, pin/archive | Dense hierarchy, project identity, status/preview/time | Remote environments, PR links, auto-settle |
| Chat header | Active title and turn state | Breadcrumb, editable title, compact status and actions | Open-in, Git actions, project actions |
| Timeline | Messages, reasoning, tools, interactions, errors, metrics | Continuous document and compact activity rows | Changed files, plans, task progress, citations |
| Composer | Draft, attachments, model/reasoning, send/stop | Unified shell with internal toolbar and notices | Permission mode, commands, skills, prompt stash |
| Workspace footer | Connection status | Checkout/branch/status rail below composer | Branch and worktree selector |
| Right panel | None | Resizable tab host and empty states | Changes, files, terminal, preview, agents |
| Settings | None as a first-class destination | Stable navigation entry and panel shell | Appearance, shortcuts, providers, connections |
| Diagnostics | Recovery banners and test control | Compact banners plus detail affordance | Logs, environment diagnostics, updates |

Placeholder policy:

- `Changes`, `Files`, `Terminal`, `Preview`, and `Agents` may be represented in the right-panel
  architecture before their backends exist.
- Only implemented panels are primary toolbar actions.
- Unimplemented destinations live behind an explicit preview/development flag or show a clearly
  worded unavailable state.
- Empty states state what is missing; they never imitate live data.

## 7. Component architecture

Break the current `ShellPage` into native WinUI controls before adding substantial new visual state:

```text
MainWindow
├── title bar / ChatHeader
└── WorkspaceShell
    ├── AppSidebar
    │   ├── SidebarSearch
    │   ├── ProjectTree
    │   └── SidebarFooter
    ├── ConversationWorkspace
    │   ├── ConversationTimeline
    │   ├── ComposerNoticeHost
    │   ├── ComposerSurface
    │   └── WorkspaceStatusBar
    └── RightPanelHost
        ├── RightPanelTabs
        └── RightPanelContentPresenter
```

Recommended files:

- `Views/Controls/AppSidebar.xaml`
- `Views/Controls/ChatHeader.xaml`
- `Views/Controls/ConversationTimeline.xaml`
- `Views/Controls/ComposerSurface.xaml`
- `Views/Controls/WorkspaceStatusBar.xaml`
- `Views/Controls/RightPanelHost.xaml`
- `ViewModels/ShellLayoutViewModel.cs`
- `ViewModels/RightPanelViewModel.cs`

`ShellViewModel` remains the coordinator, but layout state, right-panel selection, and future
settings navigation do not belong in its command/transport workflow. Existing focused child
ViewModels remain the source of project, thread, composer, configuration, connection, and mention
state.

All extracted controls must preserve the current binding behavior and Automation IDs. Template
selectors and Markdown rendering can move without changing protocol or host contracts.

## 8. Implementation stages

### Stage 0 — freeze reference and visual acceptance contract

Implementation status (2026-09-03): complete. `visual-contract.psd1` pins the T3 reference image,
commit, and SHA-256; maps twenty named PiStation states to deterministic packaged-journey artifacts;
records the three review sizes and shell geometry; and guards test-only controls in a normal launch.

Deliver:

- Keep a local, dated reference to the T3 screenshot/commit used for this plan.
- Select deterministic PiStation screenshots for empty, completed, running/tool, interaction,
  recovery, long-transcript, and attachment states.
- Record geometry at the three visual review sizes.
- Add a normal-build screenshot state that never exposes test-only fault controls.

Exit:

- Every later visual decision can be judged against a named state and window size.
- The reference is reproducible without depending on a moving T3 `main` branch.

### Stage 1 — rebuild tokens and reusable primitives

Implementation status (2026-09-02): complete. The theme dictionary now supplies the semantic
surface roles, shared geometry/motion metrics, focus treatment, status surfaces, and compact shell,
message, toolbar, disclosure, banner, tab, and composer primitives. The executable visual contract
checks all three theme dictionaries and prevents repeated hard-coded view colors.

Deliver:

- Extend `T3DesignTokens.xaml` with semantic chrome, message, overlay, divider, focus, and workbench
  panel roles.
- Add compact icon-button, toolbar-button, sidebar-row, disclosure-row, banner, and composer-toolbar
  styles.
- Reduce default border strength and nested-card use.
- Define animation durations, panel widths, gutters, reading width, and responsive breakpoints as
  shared resources.
- Validate light, dark, high contrast, disabled, hover, pressed, focus, selected, running, warning,
  and error states.

Exit:

- No view hardcodes a color used by more than one surface.
- Common controls do not drift in radius, height, padding, or focus treatment.

### Stage 2 — restructure the shell and sidebar

Implementation status (2026-09-02): complete. `WorkspaceShell` now owns the stable shell regions,
while `AppSidebar` owns project/thread selection, search, lifecycle actions, contextual menus, and
expanded/collapsed visual states. Search is first, project paths provide stable identity, threads
are nested on a tree rail with lifecycle state and relative time, and Settings plus connection
state sit at the bottom. The 52px collapsed rail aligns through the custom title bar, and the normal
driver records and exercises both widths without exposing test-only controls.

Deliver:

- Extract `AppSidebar` and `WorkspaceShell`.
- Put search at the top and Settings plus connection state at the bottom.
- Render projects as collapsible hierarchy parents with stable project identity.
- Render thread title, activity status, optional preview, and relative time in compact rows.
- Keep pin/archive/search/rename behavior and accessible selection intact.
- Add a sidebar-collapse affordance and wide/narrow visual states.
- Remove duplicate shell/title treatments and align the custom title bar with workspace chrome.

Exit:

- At `1200 × 800`, the sidebar reads like T3's project navigator and leaves more space to the task.
- Existing lifecycle and accessibility journeys pass without selector-by-index workarounds.

### Stage 3 — rebuild the chat header and transcript

Implementation status (2026-09-02): complete. `ChatHeader` now owns the project breadcrumb,
thread title, compact model/reasoning controls, configuration state, and live turn status.
`ConversationTimeline` owns the centered document-like transcript: assistant content is unboxed,
user turns use restrained right-aligned bubbles, reasoning and tools are compact disclosures,
interactions and failures retain semantic boundaries, and metrics are quiet footer metadata.
Existing Automation IDs and role information in UI Automation names remain intact.

Deliver:

- Replace the current header with project breadcrumb, thread title, status, and compact action rail.
- Move the model/reasoning card out of the transcript.
- Remove assistant message cards and render assistant content directly in the reading column.
- Render user messages as restrained right-aligned bubbles.
- Reduce author-label repetition; retain role information in UI Automation names.
- Keep reasoning and tools as compact disclosures with bounded expanded details.
- Integrate turn metrics as quiet footer metadata rather than a dominant divider.
- Keep approvals, questions, errors, and recovery states visibly bounded.

Exit:

- A completed conversation reads as one document rather than stacked containers.
- Streaming, tool expansion, Markdown, interaction, recovery, and long-transcript journeys pass.

### Stage 4 — rebuild the composer

Implementation status (2026-09-02): complete. `ComposerSurface` now owns prompt editing, file
mentions, attachment selection/removal, model and reasoning configuration, Send/Stop actions, and
quiet draft/configuration metadata inside one rounded surface aligned to the transcript. The editor
grows from a compact single-line state to a bounded multiline state; attachments render as compact
horizontal chips; icon-led actions retain tooltips, accessible names, and the established Automation
IDs. `ChatHeader` is now limited to project/thread context and live turn status.

Deliver:

- Extract `ComposerSurface` and share its width/alignment with the timeline.
- Use one rounded composer surface containing the expanding text editor.
- Move attachment, model, reasoning, Stop, and Send controls into an internal bottom toolbar.
- Use icon-led controls with tooltips and accessible names; retain visible text where ambiguity would
  otherwise increase.
- Use a circular/accented Send action and an equally discoverable Stop state while running.
- Present attachments as compact chips/tiles within the composer.
- Attach file-mention results and notices visually to the composer instead of as unrelated cards.
- Keep saved/saving/error state quiet but available.

Exit:

- Enter, Shift+Enter, exact prompt dispatch, attachment, draft, and disconnected/busy behavior remain
  unchanged.
- The composer is the visual anchor of the screen and resembles an agent control surface rather
  than a form.

### Side-by-side refinement checkpoint

Implementation status (2026-09-03): complete. Comparison with the pinned T3 screenshot removed the
duplicate workspace bar by hosting `ChatHeader` directly in the custom title bar, changed the
header to a compact single-line title/project/status hierarchy, tightened the reading-column
contract from 768 to 720 px, and consolidated configuration plus draft metadata into the composer's
single control rail. Normal-state screenshots now target the app root so retired dialog windows
cannot contaminate visual artifacts.

Exit:

- The default, draft, completed-turn, and running-tool screenshots share T3's workbench density and
  vertical hierarchy while retaining native Windows caption controls.
- The full pull-request suite remains green after the geometry change.

### Stage 5 — add workbench chrome and honest feature slots

Deliver:

- Add `RightPanelHost` with resizable desktop width and narrow-window overlay behavior.
- Define stable tabs/IDs for Changes, Files, Terminal, Preview, and Agents.
- Initially ship only an overview/empty-state panel unless a corresponding backend is implemented in
  the same increment.
- Add compact header actions for `Add action`, `Open`, and panel toggles, exposing only behavior that
  exists.
- Add `WorkspaceStatusBar` below the composer with local-project identity and room for future
  checkout/branch state.
- Persist sidebar collapsed state, selected panel, and panel width locally.

Implementation status (2026-09-03): complete. `WorkspaceShell` now reserves a stable right-panel
host that docks at desktop widths and overlays only the conversation at compact widths. The
resizable workbench exposes accessible Changes, Files, Terminal, Preview, and Agents tabs. At this
stage each tab used an explicit unavailable explanation instead of simulated data; later product
slices replaced Changes, Files, and Terminal with real behavior. The custom title bar adds a
real thread-creation menu, selected-project folder launch, and panel toggle, while
`WorkspaceStatusBar` reports local-project identity and an honest source-control-unavailable state.
`ShellLayoutViewModel` persists sidebar collapse, panel visibility, selected tab, and clamped panel
width beneath the application data root with safe defaults for absent or invalid settings.

The packaged workbench journey exercises all five tabs, accessible resizing, docked and compact
overlay geometry, sidebar containment, close behavior, and layout restoration after process
relaunch. This stage increased the pinned visual contract to 12 states, including the open
workbench, and the pull-request entry point to ten packaged UI journeys at that checkpoint.

Exit:

- Adding a future workbench feature does not require restructuring the conversation again.
- No placeholder is mistaken for functioning Git, terminal, browser, file, or agent data.

### Stage 6 — overlays, settings, and all non-happy states

Deliver:

- Create a first-class Settings shell opened from the sidebar footer.
- Restyle Add Project, rename, confirmation, approval, and question dialogs consistently.
- Consolidate transport, Pi crash, resync, uncertain-command, capability, and update-style notices in
  a banner stack above the composer.
- Replace the visible test-only transport button with diagnostics-only access gated to test builds.
- Add polished empty states for no project, no thread, no messages, disconnected, and unsupported
  capability cases.
- Standardize flyouts, menus, tooltips, progress, skeleton/loading, and toast feedback.

Implementation status (2026-09-03): complete. The sidebar now opens a first-class native Settings
dialog with persisted-layout summary/reset, local-environment details, and About information. The
transport fault action moved into a diagnostics section that is created only for Debug FakePi
UI-test launches; the normal driver contract proves that it is absent. `RecoveryBannerSurface`
owns the transport, Pi-crash, uncertain-command, and runtime-error notices in a single stack directly
above the composer while preserving their recovery actions and Automation IDs.

No-project, no-thread, and empty-thread timelines now use centered, descriptive empty states rather
than blank canvas. Add Project, approval, question, and lifecycle interactions share the semantic
compact control styles and native dialog/flyout behavior. The workbench journey also verifies that
Settings can restore the default expanded-sidebar/closed-panel layout after relaunch. The pinned
visual contract now contains 13 states, including normal Settings, and all ten packaged journeys
at that checkpoint exercised the resulting non-happy and interaction states.

Exit:

- Every deterministic application state has an intentional layout.
- Debug/test affordances cannot appear in a normal packaged launch.

### Stage 7 — responsive, accessibility, and visual hardening

Deliver:

- Add `VisualStateManager` layouts for wide, standard, compact, and narrow widths.
- Verify sidebar collapse, right-panel overlay, reading width, composer growth, and long content.
- Exercise 100%, 150%, and 200% scale; light, dark, and high contrast; minimum and maximized window.
- Verify full keyboard traversal, focus restoration, screen-reader names, control types, and live
  status.
- Respect text scaling and reduced motion.
- Add deterministic screenshot artifacts and geometry assertions for the named visual states.
- Avoid brittle pixel-perfect CI comparison; use reviewable screenshots plus semantic/layout bounds.

Implementation status (2026-09-03): complete. `WorkspaceShell`, `ShellPage`, `ChatHeader`, and
`ComposerSurface` now declare matching Narrow, Compact, Standard, and Wide visual states at 0, 720,
900, and 1180 px. Workbench docking begins only at the Wide boundary; smaller layouts overlay the
conversation without covering the sidebar. Page gutters, dialog minimums, header labels/status,
and composer selector/status density adapt at the same breakpoints.

The persisted Appearance preference supports Dark, Light, and System; System follows Windows and
therefore honors operating-system high contrast. Settings and Add Project restore keyboard focus to
their invoking controls. PiStation defines no custom view animation, leaving native motion subject
to Windows reduced-motion behavior. The new packaged compatibility journey uses DPI-aware window
sizing to verify all four bands, the 52 px collapsed rail, the 720 px reading bound, composer growth,
minimum and maximized layouts, live/persisted theme changes, and 100/150/200 percent app-owned
typography profiles. It also caught and fixed a cancellation-token ownership race in Pi settings
loading. The visual contract now contains 20 reviewable states, and the pull-request gate runs
eleven packaged journeys.

Exit:

- The shell remains coherent at every supported size and accessibility setting.
- The full pull-request suite and interactive compatibility lane pass.

## 9. Test strategy

Every stage runs:

```powershell
cd PiStationDesktop
pwsh .\Invoke-PullRequestTests.ps1
```

The current functional contract remains non-negotiable:

- zero build warnings and errors;
- all code tests pass;
- all eleven packaged UI journeys pass;
- no unconditional sleeps or coordinate selectors enter the PR lane;
- failure artifacts retain screenshots, UI trees, logs, and write manifests.

Add a visual-shell journey with deterministic fixture data and screenshots for:

1. empty/new project;
2. settled Markdown turn;
3. active reasoning and grouped tools;
4. approval and structured question;
5. attachments and file mentions;
6. transport, Pi-crash, and uncertain-command banners;
7. right panel open and closed;
8. narrow, default, and wide window states.

Prefer semantic geometry assertions such as alignment, containment, visibility, panel width, reading
column maximum, and composer docking. Screenshot review catches aesthetics; UIA assertions protect
behavior and accessibility.

## 10. Definition of done

GUI parity is complete when:

- a side-by-side screenshot immediately shares T3's compact workbench hierarchy without using T3
  branding;
- model/reasoning controls no longer occupy a transcript card;
- assistant content is visually unboxed and user turns have restrained bubbles;
- the composer contains its own action/configuration toolbar;
- sidebar rows communicate project, thread, and activity state at a glance;
- the header has a coherent action rail;
- a tested right-panel architecture exists for future workbench features;
- every existing PiStation behavior remains available by keyboard and UI Automation;
- normal builds contain no visible test controls;
- light, dark, high contrast, scaling, and constrained-window checks pass;
- the complete automated gate remains green.

## 11. Recommended delivery order

Implement one reviewable slice at a time:

1. Stage 0 and Stage 1: visual contract and tokens. Complete.
2. Stage 2: shell/sidebar. Complete.
3. Stage 3: header/timeline. Complete.
4. Stage 4: composer. Complete.
5. Perform a side-by-side visual review before adding more chrome. Complete.
6. Stage 5: right-panel and workbench stakes. Complete.
7. Stage 6: settings and non-happy states. Complete.
8. Stage 7: compatibility and final polish. Complete.

The GUI parity implementation is complete. The first four post-parity product slices are also
complete: Files is a searchable workbench with selection and bounded, project-confined text
previews, while Changes presents branch identity, staged/working-tree/untracked status, refresh,
and bounded read-only diffs. Terminal manages bounded, host-owned PowerShell and Command Prompt
sessions with resumable output and the complete start/input/stop/restart/close workbench lifecycle.
Preview discovers browser-ready local servers and provides a restricted embedded WebView2 browser
with address, history, reload/stop, external-open, error recovery, and per-project URL persistence.
All four are covered from protocol through packaged UI automation. Terminal uses Windows ConPTY
with the T3-derived Ghostty WebAssembly renderer, real pseudoconsole resizes, OSC links, search,
mouse reporting, and recursive split panes.
The next product decision is whether to build Agents or deepen Preview with responsive viewports,
zoom, page color preference, multiple tabs, screenshots, annotations, and later permissioned browser
automation. A final human side-by-side review should accompany that selection.

## 12. Reference paths

- `Core/t3code/apps/marketing/public/updated-screenshot.webp`
- `Core/t3code/apps/web/src/components/Sidebar.tsx`
- `Core/t3code/apps/web/src/components/chat/ChatHeader.tsx`
- `Core/t3code/apps/web/src/components/chat/MessagesTimeline.tsx`
- `Core/t3code/apps/web/src/components/chat/ChatComposer.tsx`
- `Core/t3code/apps/web/src/components/BranchToolbar.tsx`
- `Core/t3code/apps/web/src/components/RightPanelTabs.tsx`
- `Core/t3code/apps/web/src/index.css`
- `PiStationDesktop/src/PiStation.App/MainWindow.xaml`
- `PiStationDesktop/src/PiStation.App/Views/ShellPage.xaml`
- `PiStationDesktop/src/PiStation.App/Themes/T3DesignTokens.xaml`
- `PiStationDesktop/tests/PiStation.UiTests/artifacts`
