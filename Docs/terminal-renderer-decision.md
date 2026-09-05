# Terminal renderer decision

Status: implemented, 2026-09-03

## Decision

PiStation uses the T3 Code Ghostty WebAssembly/Canvas2D terminal surface inside a locked-down WinUI
WebView2. PiStation continues to own ConPTY processes in the host, authenticated SignalR transport,
session recovery, retained output, and resize authority. A small JSON bridge carries terminal bytes,
input, grid dimensions, theme changes, and link activation.

Ghostty is the only client-side terminal engine and renderer. Initialization or process failures are
shown directly in the terminal surface instead of switching to a second parser.

## Why this path

| Candidate | Strength | Reason not selected as the primary path |
|---|---|---|
| T3 Ghostty surface | Matches the target T3 interaction and rendering model; mature VT engine; keeps PiStation's remote session architecture | Adds WebView2/JavaScript lifecycle and a WASM asset |
| XTerm.NET | Headless .NET engine with low transport disruption | PiStation would still own and evolve the entire WinUI renderer, so it would not closely match T3 |
| xterm.js + WebView2 | Broad terminal compatibility and ecosystem | Replaces the exact T3 surface with a different UI and adds a larger third-party runtime |
| `XamlToolkit.WinUI.Terminal` | Native WinUI `TermControl` based on Windows Terminal, with GPU rendering, selection, clipboard, links, and keyboard support | Alpha package, version/deployment coupling, and an unproven adapter for PiStation's remote host-owned sessions |
| Previous C#/XAML renderer | Was dependency-light | Removed because its custom parser duplicated Ghostty and created two terminal behaviors to maintain |

## `XamlToolkit.WinUI.Terminal` assessment

[`XamlToolkit.WinUI.Terminal`](https://github.com/lgztx96/XamlToolkit.WinUI.Terminal) is a real direct
WinUI option and is stronger than the earlier unofficial HWND-hosted terminal wrappers. It exposes a
Windows Terminal `TermControl` to .NET through CsWinRT and advertises x64, x86, and ARM64 assets. Its
sample demonstrates themes, copy/paste, key bindings, URL activation, tab hosting, and proper close
lifecycle.

It was not selected for this implementation because:

- The current package is `0.1.0-alpha`, and the repository contains a very small integration history.
- It packages prebuilt Windows Terminal v1.25-era DLLs, WinMD/PRI files, and `OpenConsole.exe` for each
  architecture rather than building the terminal source in this repository.
- Its NuGet specification depends on `Microsoft.WindowsAppSDK.WinUI` 1.8.260709004, while PiStation is
  on `Microsoft.WindowsAppSDK` 2.4.0. That needs a compatibility spike before it can be a safe app-level
  dependency.
- The documented sample constructs a local `ConptyConnection`. PiStation deliberately keeps ConPTY in
  its host and streams resumable sessions to the UI. A custom Windows Terminal connection adapter would
  need to prove output injection, input/device responses, exact resizing, disconnect/replay behavior,
  and cleanup without creating a second local process owner.
- Native DLL/PRI resolution, MSIX packaging, UI Automation, and upgrades of the Windows Terminal binary
  snapshot all add release risk that the current Ghostty bundle avoids.

This control remains the best native candidate for a later contained spike. Reconsider it after it has
Windows App SDK 2.x compatibility and the custom streamed-connection adapter passes the same packaged
workbench journey used by the current Ghostty renderer.

## Guardrails

- Only `https://pistation-terminal.local` is allowed to navigate in the WebView.
- DevTools, zoom, and the default browser context menu are disabled in production.
- The generated web bundle and pinned WASM/font assets ship with their upstream licenses.
- Ghostty publishes its visible accessible text and mouse-mode state over the bridge; no second VT
  parser runs in the .NET client.
- Search indexes cells obtained by paging Ghostty render snapshots, restores the prior viewport, and
  keeps match coordinates and highlights in the Ghostty surface; it does not parse the VT byte stream.
- Terminal font preferences are normalized and persisted by WinUI, bounded to supported sizes, and
  sent over the theme bridge. Ghostty verifies fixed-width rendering before accepting a family and
  falls back to the bundled default monospace stack when necessary.
- Each visible pane owns a Ghostty/WebView2 surface and an independent host-session projection;
  focused-pane input and per-surface grid resizes never pass through a second terminal parser.
- A recursive split tree supports up to four panes. Every branch persists its orientation and bounded
  ratio together with stable pane IDs, session IDs, and the active leaf on a per-project basis. Missing
  or stale sessions prune their leaf and collapse the affected branch without invalidating siblings.
- Every divider exposes UI Automation `RangeValue` plus pointer and keyboard resizing. Pane shortcuts
  are resolved inside the Ghostty surface and forwarded as explicit host actions instead of terminal
  bytes; focus actions cycle through the flattened pane order.
- Device responses are emitted only by Ghostty.
- The native terminal context menu uses the Windows clipboard, but selection and bracketed-paste
  semantics stay inside Ghostty.
