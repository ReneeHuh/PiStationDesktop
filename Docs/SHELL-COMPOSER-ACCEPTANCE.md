# Shell/composer native acceptance

Status: pending, 2026-09-09. Managed tests and the x64 build are not native UI acceptance.
The native automation connection was unavailable after retry/reset during implementation.
Use an interactive Windows desktop with an isolated test data root and a disposable
project; do not send a paid model request or publish a PR just to exercise these controls.
The master status remains [tracking.md](../tracking.md), IDs UI-16/UI-17,
CHAT-03/CHAT-17/CHAT-18, BUG-05/BUG-07/BUG-08, and DEL-03/DEL-04/DEL-06/DEL-09.

## Settings and focus

1. Open Settings → Appearance → Shell and composer. Confirm hold-to-close,
   proactive panels off, both collapse switches on, and slash-menu skills on.
   Change every setting, relaunch with the same data root, and confirm persistence.
2. Exercise each blur/scroll switch independently and together, including scrolling
   with keyboard focus still in the editor. Expand with pointer, Tab/Enter, and the
   existing focus-composer shortcut. Returning to latest output must not erase text.
3. Preserve typed Unicode text, attachment chips, and context chips across collapse,
   thread switches, restart, and an active turn. Test Stop in both composer states.
   A draft recovery conflict must keep its recovery choices visible.
4. Open the model, reasoning, and permission dropdowns; Context and Stash flyouts;
   and the prompt text context menu. The composer must stay expanded through
   selection, keyboard navigation, and dismissal. Test external-editor return,
   suggestion selection, and paste after returning from a native file picker.
5. Check Narrator names/focus, High Contrast, narrow windows, 100–200% scaling,
   and mixed-monitor scaling. Capture populated nonblank states before signing off.

Stable selectors include `QuitConfirmationModeSelector`, `ProactivePanelsToggle`,
`ComposerCollapseOnBlurToggle`, `ComposerCollapseOnScrollToggle`,
`ShowSkillsInSlashMenuToggle`, `ExpandComposerButton`, and `RestingStopTurnButton`.
Legacy UIA journeys that address `PromptInput` or other expanded controls need to
invoke `ExpandComposerButton` when resting before using those controls. Existing
historical passing reports do not certify those journeys against this change.

## Quit policies

1. Test all three Ctrl+Q modes in a disposable window. Hold requires 1.2 seconds
   followed by Q release; double press requires two distinct presses within 0.5 seconds.
   Autorepeat, an early release, another key, loss of activation, or changing policy
   must not accidentally complete a hold/double-press attempt.
2. Repeat with an unsent draft, unsaved file edits, and unsaved plan edits. Cancel
   the plan confirmation; then accept it and verify draft/file recovery after restart.
3. Verify title-bar close and Alt+F4 retain their existing recovery behavior. With
   two PiStation windows open, Ctrl+Q must close only its owning window. Check key
   routing from the transcript, composer, workbench, and embedded terminal/preview.

## Proactive panels and slash discovery

1. With the preference disabled, completion and new PR linking must not open panels.
2. Enable it and finish a deterministic test turn with a ready, changed-file
   checkpoint. Its diff should open once, including a checkpoint arriving after
   completion. Empty/missing/error checkpoints must not open an empty automatic diff.
3. Keep Files, Terminal, Preview, or Agents open while completing a turn: no panel
   takeover. Switching threads, reconnecting, or refreshing the same projection
   must not reopen a historical completion.
4. Link a PR to the selected thread using a fixture or an existing authorized test
   repository. `LinkedPullRequestCard` should appear in Changes without an automatic
   modal or browser launch. Metadata-only refresh and navigation to an already-linked
   thread must not trigger opening. The explicit Open linked PR action opens HTTPS only.
5. With a discovered skill, compare `/` suggestions before and after disabling the
   skill toggle. Built-ins, prompts, and `$` skill search must remain functional.

## Attachments and bug regressions

1. Attach, drop, and paste HEIC/HEIF storage files with Unicode names and EXIF
   rotation. Verify a readable JPEG preview and retained draft text/context.
   Also paste a bitmap and attach PNG/JPEG/PDF to check unaffected input paths.
2. Test damaged HEIF, missing codec support on an appropriate clean test PC,
   oversized input, cancellation, and host/network failure. Keep the original file
   and draft; show a useful error and permit retry. Confirm that selection changes
   while reading/converting cannot attach to another conversation.
3. On a clean npm-local installation, choose `project/node_modules/.bin/pi.ps1`
   (and `.cmd`); verify discovery resolves Pi, not the project's `package.json`.
4. Settings must advertise only GitHub `gh`, GitLab `glab`, and Azure DevOps `az`;
   Bitbucket must be explicitly unavailable, not a supported `bb` adapter.
5. Remote and SSH panels must offer manual update/reconnect guidance, without
   host-install/update/status buttons or an allow-remote-updates toggle. Preserve
   Open/reconnect, connection removal, and trust controls. Physical two-PC checks
   remain part of DEL-09, not this local managed-test result.

Record the tested commit/build, Windows/display details, exercised rows, actual
results, and screenshots. Keep unexercised or unavailable checks pending.
