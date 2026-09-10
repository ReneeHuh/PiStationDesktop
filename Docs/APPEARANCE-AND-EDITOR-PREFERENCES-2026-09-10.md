# Appearance and editor preferences — 2026-09-10

Working tree based on `cea6c8f`, protocol 56. Selected tracker rows: LOOK-02, LOOK-03, LOOK-04, LOOK-10.

## T3 source review

Reviewed the local T3 checkout at `0a590fa01`: `packages/contracts/src/settings.ts`, `apps/web/src/appearanceFonts.ts`, `appearanceContrast.ts`, `panelAnimations.ts`, `index.css`, and the diff/file-preview settings consumers. T3 normalizes durable preferences, maintains separate font families/sizes with fallback stacks, defaults to stacked diffs and word wrap, passes whitespace filtering to diff queries, and suppresses panel motion during startup and reduced motion.

PiStation adapts this to native WinUI resources and the existing Windows backdrop. It retains its 13 px interface, 14 px composer, 13 px code and 12 px terminal defaults, plus opaque surfaces and instant panel transitions. CSS rem sizing and browser-specific font smoothing are not copied into WinUI.

## Behavior

Settings → Appearance now includes:

- Independent interface, composer and code font families and sizes. Installed family names can be entered as a comma-separated list; defaults remain as glyph fallbacks. Existing independent terminal choices and monospace validation remain in place.
- Contrast from 50–200% and surface opacity from 40–100%. Contrast adjusts text and boundaries from the original light/dark palette on each change. Lower opacity reveals the Windows backdrop; the WebView2 terminal canvas stays opaque, as required by its background-color API. The Windows High Contrast palette remains authoritative, and disabled advanced effects force opaque surfaces.
- Persistent word wrap for workspace file editors and diff previews, with code typography also used by Markdown code blocks and the hosted review line picker. Unified/split selections in the workspace/checkpoint diff toolbar persist back into the same preference.
- Ignore-whitespace filtering for workspace and checkpoint Git previews, including reloading the current selection and cancellation of obsolete requests. This does not modify files, staging, raw review patches or provider comment anchors. Hosted review line selection continues to use the provider's original lines.
- Panel animation duration from 0–400 ms. Workbench opening/closing uses a fade/slide and retains closing content until completion; sidebar changes fade in the new state. Rapid changes cancel obsolete transitions. Startup, 0 ms and Windows reduced motion settle immediately.
- Reset for the new appearance/editor preferences. Terminal has its existing separate reset. Existing project/layout/browser preferences are retained.

Settings are saved in the existing local layout file and apply immediately to the current window; future windows load the saved values. Existing windows keep their own layout preferences. Old layout files receive defaults. Native text scaling still composes with the selected sizes. Font-file URI sources are not accepted by family inputs.

Protocol 56 adds the optional `GetProjectChangeDiffRequest.IgnoreWhitespace` flag (wire default false preserves older request behavior). PiStation sends its selected preference explicitly; checkpoint requests already support the flag. Local/remote hosts must have the matching protocol.

Native resource updates use WinUI's [ThemeResource re-evaluation](https://learn.microsoft.com/en-us/windows/apps/develop/platform/xaml/themeresource-markup-extension); accessibility and motion follow [Windows UISettings](https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.uisettings). No controls or conversation state are recreated to apply font changes.

## Validation

- Full first-attempt regression: **1,154 passed, 0 failed, 28 optional skips** across five serial suites. ClientRuntime 485, CommandSystem 100, Host 430, PiRpc 79, Protocol 60. Evidence: `TestResults/code-Debug-20260910-175525-ca0965dd/summary.json`.
- Clean final solution build: `TestResults/appearance-build-final-gutters.log` (0 warnings/errors). After the full gate, final UI-only changes clear selection when the patch document changes, preserve it for layout-only renders, allow line-number gutters to grow with code fonts, and explicitly detach animation subscriptions when a window closes. The final focused/native runs below cover the rebuilt result.
- Static visual contract: 24 states, four responsive layouts, three text scales passed (`TestResults/appearance-visual-contract-final.log`). This is source/contract validation, not a screenshot claim.
- Final native acceptance: **seven grouped UI Automation checks passed** on the final build (`TestResults/appearance-native/f8f29ad6e4bf40f2a4d5df38eed8004e/result.json`). Windows TextPattern reports independent interface, composer and code font stacks/sizes; the draft survives, whitespace filtering refreshes the open Git diff, split/wrap preferences reach the renderer, rapid panel interruptions settle, settings survive relaunch/reset, and light-theme controls remain usable. The same long single line exposes **one text rectangle without wrap and two with wrap** (`native-fonts.json` in that directory). Switching unified/split retains the selected row; replacing the patch after whitespace filtering clears it.
- Final focused tests: **16 passed, 0 failed, 0 skipped** across CommandSystem (6), Host (8) and Protocol (2). Evidence: `TestResults/code-Debug-20260910-181035-de759d13/summary.json`.
- Packaged SSH host protocol assembly matches the built host, SHA-256 `E3E522E6E4AE1FD54E7ECB959015B13DCDEA28705B739FEDB42B34A161C71BC9` (`TestResults/appearance-packaging-final.log`).
- An initial extended run encountered WinUI UI Automation `E_UNEXPECTED` while obtaining wrapped whole-document geometry (`TestResults/appearance-native/a30ae0dc95d74d86ab86fb66c84e1836/result.json`). The final driver reacquires text ranges while layout settles and uses a two-line fixture that fits the editor's visible height; the actual rectangle-count assertion passes. The earlier seven-group baseline and unsuccessful attempts remain in `TestResults/appearance-native`.
- **Screenshots remain pending.** The screenshot validator rejected black frames from both window and screen capture, and `OpenInputDesktop` returned Win32 access denied. The user instructed us to continue. No blank capture is counted as visual acceptance. Actual High Contrast/screen-reader and physical-device qualification remains under delivery acceptance.

Native verification found and fixed three implementation issues: `AccessibilitySettings.HighContrastChanged` could not be subscribed in this desktop window, so the native ThemeResource mechanism handles High Contrast directly; WebView2 rejects partial-alpha backgrounds, so the terminal canvas is kept opaque; default WinUI control font resources also need overriding alongside PiStation's typography tokens. Retained logs/artifacts record the earlier failed attempts and selector/font-stack assertion corrections. No live login or real credentials were used.

Reproduce the functional native checks with `tests/PiStation.UiTests/Invoke-AppearanceSlice.ps1 -NoBuild` after a Debug build. The script uses an isolated data root, fake Pi and temporary Git project. Add `-Capture` only when the input desktop is available; captures then use the existing nonblank-image validator.

## Remaining scope

The subsequent [custom palette and theme interchange batch](CUSTOM-PALETTES-AND-THEME-INTERCHANGE-2026-09-10.md) covers LOOK-05 through LOOK-09. Broader Windows High Contrast/screen-reader and physical-device qualification remains under delivery acceptance. Live provider sign-in remains skipped by user request.
