# PiStation Ghostty terminal surface

This bundle adapts T3 Code's MIT-licensed Ghostty WebAssembly canvas terminal to the WinUI WebView2 host.
PiStation continues to own ConPTY sessions and SignalR transport; this surface owns VT parsing, rendering,
selection, keyboard, mouse, IME and link interaction. WinUI owns the native context menu and Windows
clipboard, while pasted text returns through Ghostty's bracketed-paste encoder.

Run `npm ci`, `npm run typecheck`, and `npm run build` in this directory after changing the TypeScript
sources. The generated `dist` files are application content and do not require Node.js at runtime.

The upstream Ghostty revision is `9f62873bf195e4d8a762d768a1405a5f2f7b1697`. License files for T3 Code,
Ghostty and Symbols Nerd Font are retained beside this README.

The terminal renderer decision, including the assessment of `XamlToolkit.WinUI.Terminal`, is recorded in
[`Docs/terminal-renderer-decision.md`](../../../Docs/terminal-renderer-decision.md).
