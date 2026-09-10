# Pi extensions and sessions

This working tree uses protocol 52. Client and host builds must match.

## Session behavior

The Sessions panel now exports standalone HTML with Markdown formatting, thinking sections, tool arguments/results, usage, bookmarks and embedded raster images. Export follows the active branch. Scripts and raw HTML are escaped; executable links and remote images are disabled. JSONL remains the lossless export. HTML is limited to 128 MiB.

“Share conversation as a GitHub gist” prepares an HTML file on the client PC. The user must open the exact export for review before the Create gist button is enabled. Sharing invokes the local GitHub CLI, defaults to an unlisted gist and requires a separate explicit public choice. A SHA-256 check and file lease prevent publishing changed content. Exports larger than 8 MiB must be saved locally. No gist was published during implementation or testing.

“New temporary local session” opens a separate window in the selected project. PiStation stores its conversation, draft and command records in an in-memory SQLite database; Pi starts with `--no-session`. Temporary runtimes are exempt from idle eviction. Closing the window awaits the client, host and Pi shutdown, then deletes owned recovery files, attachments, child-session files and browser caches. The original window and its draft are separate. An exclusive ownership lease protects live temporary folders from startup cleanup after a crash. Project edits and explicitly saved exports are retained. This workflow is local; it does not create a temporary session on a remote host. File-backed session operations currently require a saved Pi session.

Tree controls support folding and unfolding, expanding all branches, previous/next branch-point navigation, full selected-entry copying and optional bookmark timestamps. Filtered rows use their nearest visible ancestor for indentation. Folding and filtering happen before paging; navigation locates the target page. Keyboard equivalents are Left/Right, Ctrl+Up/Down and Ctrl+C. Entry copying is limited to 128K characters and fails explicitly rather than truncating.

## Pi integration

The bundled `pistation-sdk.ts` bootstrap extension decorates Pi's public `AgentSession.bindExtensions` boundary. Loading through Pi's own extension loader is necessary: bundled Pi installations use SDK classes distinct from an independently imported unbundled package. Pi retains ownership of CLI parsing, startup, trust, providers and session creation. The adapter requires Pi 0.85 or later and is included in desktop and remote-host packages.

Resource inspection reads actual loaded extensions and loader errors, including extensions that register only event handlers. Skill and prompt diagnostics include source paths. Reload uses Pi's supported command-context reload in the existing process. It clears old component state, refreshes the resource inventory and native command/model choices, and reapplies PiStation automation preferences. A failed explicit extension at initial startup still follows Pi's startup rejection behavior; its error is surfaced by the launch failure.

The tool execution selector changes the running Pi agent's public `toolExecution` property. It controls one assistant tool batch, separately from queued-message delivery. It lasts for the current runtime and survives resource reload. Per-tool sequential restrictions, allowlists and approval hooks remain in effect.

Provider inventory includes unconfigured providers and advertises browser/API-key login capabilities. Credential prompts use a PasswordBox. All non-selector authentication answers are treated as secret, forwarded to Pi, cleared from the editor, and recorded only as “Credential supplied” in PiStation's journal. Pi owns credential persistence. Live provider authentication was explicitly skipped at the user's request. Isolated dummy-key onboarding and logout were tested without a provider request; broad real-provider acceptance remains open.

## Extension component compatibility

Component factories execute inside Pi and receive a 100-column render width. Output is limited to 120 lines and 12,000 characters, with ANSI formatting removed. Widget factories and custom header/footer components render in PiStation's extension widget surface. Working messages appear as status text. `custom()` renders through native input interactions: enter text or named keys such as Enter, Escape, Up, Down, Tab and Ctrl+C. Completion, cancellation and disposal are supported. Asynchronous completion retires the correlated native question immediately. Tests exercise a real Pi factory, its input callback and asynchronous completion.

This is a text compatibility surface, not complete terminal rendering parity. Mouse-oriented components, arbitrary TUI layout APIs, exact overlay placement, terminal editor replacements and terminal autocomplete are not supported. Editor replacement and autocomplete attempts produce explicit notices. Pi's TUI keybinding manager is used when the coding-agent SDK does not export its application manager. EXT-15 remains Partial for these limits.

## External child controls

An external extension can opt into independent cancellation through Pi's event bus. It must supply the actual owner callback; PiStation never infers a PID or kills an arbitrary process.

```ts
let handle;
pi.events.emit("pistation:child-controls:v1", {
  stop: () => childAbortController.abort(),
  reply: value => { handle = value; },
});
if (!handle || handle.error) throw new Error(handle?.error ?? "PiStation controls unavailable");
try {
  // Include this metadata in tool update/result details alongside the child's output.
  onUpdate({ content: [], details: {
    integration: handle.integration, mode: "single",
    results: [{ agent: "external reviewer", status: "running", controlId: handle.controlId }],
  } });
  await runChild();
} finally {
  handle.complete();
}
```

For parallel workflows, publish one `results` entry per child with its own registered handle and `mode: "parallel"`. Publish final statuses as the children complete. The host projects independent Stop child controls only for registered-protocol identities. The bridge verifies the live handle, coalesces duplicate stop requests, caps registrations at 64, and retires/cancels handles on shutdown or reload. External continuations are not presented as bundled-agent resume operations. Extensions without this protocol retain the clearly labeled parent-stop fallback.

## Verification

Focused checks cover HTML escaping and media, filtered/folded tree structure and cross-page jumps, memory-only drafts, abandoned/live temporary ownership, gist arguments and review hashes, secret interaction replay, external handle projection, and bridge cancellation/disposal. Five installed-Pi offline checks cover exact attribution and reload, temporary history, dummy-key onboarding/logout, real batch overlap/barriers, and custom component input/disposal. See the tracker for the final managed-suite and native acceptance results.

- Full managed gate: **1,057 passed, zero failed, 27 explicit optional skips** in `TestResults/code-Debug-20260910-035822-e8504706/summary.json`.
- After final fixes: **13 affected host checks passed**, **29 Pi/event checks passed with offline acceptance enabled**, and **three Node bridge checks passed**. Evidence: `TestResults/desktop-final/desktop-final-host.trx` and `desktop-final-pi-corrected.trx`. The first offline run exposed two provider-inventory failures when the SDK adapter was absent; the fallback now accepts the compatibility API's string provider IDs. Its first-failure report is retained as `desktop-final-pi.trx`.
- Final solution and WinUI build: **zero warnings and errors**, `TestResults/desktop-final-build-corrected.log`. Static visual contract: 24 states, four responsive layouts and three text scales passed; this is not rendered visual acceptance.
- Native temporary-session acceptance passed: `TestResults/temporary-session-native/6693e15aa1a2405d95fcbed104ab06fd/result.json`. Conversation/draft cleanup and original-window draft preservation were verified.
- Native session acceptance passed all eight checks: `TestResults/pi-sessions-native/84d3eae2b99543eb996fd1e29990afbb/result.json`. It covers import, folding/expanding, fork/model retention, separate drafts across relaunch, unchanged source JSONL, native JSONL/HTML save pickers, and gist review/cancel without publishing. Gist review suspends and restores Settings through the existing dialog lifecycle. Earlier native failures are retained in the sibling run folders; the test helpers now account for tree indicators and virtualized older tabs.

References: local [Pi SDK](<../../Pi Agent/packages/coding-agent/src/core/agent-session.ts>), [Pi tree selector](<../../Pi Agent/packages/coding-agent/src/modes/interactive/components/tree-selector.ts>), [GitHub CLI gist creation](https://cli.github.com/manual/gh_gist_create), and [Markdig](https://github.com/xoofx/markdig).
