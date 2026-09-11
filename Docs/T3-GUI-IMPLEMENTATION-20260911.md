# T3 GUI implementation — September 11, 2026

Target: local T3 Code commit `0a590fa01af66ec135d2ebf2d5542b08a37dc275`, using its default modern sidebar. This follows the [layer review](../outputs/t3-gui-review-20260911.md). Existing Pi-specific workflows remain in scope.

Reviewed native captures: [conversation](../outputs/t3-gui-implementation-20260911/verified-chat.png), [Files workbench](../outputs/t3-gui-implementation-20260911/verified-workbench.png), [Settings](../outputs/t3-gui-implementation-20260911/verified-settings.png).

## Implemented surfaces

- **Sidebar:** one inbox spanning projects, project/checkouts filter, project labels on thread rows, status and branch metadata. Live revisions replace catalog snapshots before shelf/search filtering. Existing pin, rename, archive, bulk actions and project ordering remain accessible.
- **Settings:** full-page navigation with Back/Escape, a 256px navigation pane, responsive compact navigation, and topic search. Existing child-dialog suspension and restoration are preserved.
- **Composer:** directly editable compact prompt; model, attachment, options and Send/Stop on the main row. Configuration, context, stash, external editor, shell and background actions are in the options flyout. Focusing expands the editor.
- **Workbench:** one compact header, tabs for opened categories, and an add-view menu. Browser replaces the Preview display label. Existing individual file, terminal and browser tabs remain within their category. Below the wide-layout breakpoint, the workbench replaces the conversation area until closed, avoiding a partly covered prompt and transcript.
- **Geometry:** 256px sidebar, 768px reading column, 22px composer radius. The historical September 2 screenshot reference remains pinned separately from the new source target.

## Remaining fidelity work

This change does not establish pixel parity for all twenty reviewed layers. Remaining work includes flattening individual workbench documents into one tab model, richer source-editor presentation, PR inbox/detail pages, the usage dashboard layout, and detailed timeline/tool/diff/agent card styling. Full theme, text-scale, responsive-size and assistive-technology acceptance must be rerun against the new layout. Historical screenshots do not certify these changes.

## Verification

- Debug x64 app build: zero errors and warnings. [Final build log](../TestResults/gui-redesign-build-final.log).
- ClientRuntime suite: **541 passed**, including three new cross-project navigation regression tests. [Log](../TestResults/gui-redesign-runtime-tests.log).
- CommandSystem suite: **120 passed, 2 skipped** (HEIF conversion fixtures). [Log](../TestResults/gui-redesign-command-tests.log).
- Static visual contract: passed for the declared 24 states, four responsive layouts and three text scales. This validates source/contracts, not screenshots for all those states.
- Native WinUI launch with FakePi and disposable project/data under `TestResults/gui-redesign-native`: project creation, thread selection across the catalog, project filtering, search returning zero/one row, directly editable prompt, keyboard opening composer options, Settings Back/Escape, topic search (`tokens` exposes Usage), draft preservation through Settings, and opening Files through the workbench add menu exercised.
- Inspected actual 1200×800 window captures at the session's Windows scale. The restart check found and fixed the old project-specific empty-state label overlapping the new global inbox. Native artifacts and UI trees are retained in [the run directory](../TestResults/gui-redesign-native).
- Shared project-selection and project-row assertions now use the project filter/global inbox; exercised successfully against the final build, including the absence of the overlapping empty-state message. Legacy full native journeys that assume permanently visible workbench category tabs or a modal Settings Close button still need migration and rerunning; they are not included in this smoke-check claim.
