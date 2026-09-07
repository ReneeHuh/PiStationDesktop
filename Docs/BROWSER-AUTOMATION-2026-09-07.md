# Browser automation and reliability follow-up

## Implemented slice of BROWSER-02

The bundled `pistation_browser` tool now accepts a stable `tabId` for every action. `status` lists the current thread's tabs, titles, URLs, selection and loaded-surface availability. Omitting the ID captures the selected tab at request dispatch; an invalid explicit ID never falls back to another tab. Navigation targets the captured surface without selecting it.

- `press_key`: letters/digits, Enter, Tab, Escape, Backspace, Delete, arrows, Home/End, PageUp/PageDown and Space. Optional CSS selector focuses an element first. Modifiers use the Chromium bitmask (Alt 1, Control 2, Meta 4, Shift 8). Events go through WebView2's DevTools input API; key-up remains tied to the original WebView.
- `scroll`: bounded pixel deltas, optional CSS container selector, otherwise the document scroll element. Returns the resulting offsets.
- `wait`: visible/hidden selector, text containing `value`, URL containing `value`, or document loaded. Defaults to five seconds, maximum twenty. No arbitrary JavaScript evaluation is exposed.

Keyboard and scroll require Interact access; waits require Inspect or Interact. Dispatch and wait iterations recheck thread/tab ownership, request expiry/cancellation and permissions. Switching tabs does not retarget work. Switching threads cancels it once observed; this is still desktop-owned automation for the selected thread, not a background browser service. Inspect/action requests reject a navigating document and direct the caller to wait for loading to finish.

This does not finish all of BROWSER-02: agent-created tabs, viewport/color controls, recording, arbitrary evaluation and richer semantic/diagnostic snapshots remain outside this slice. Native checks of hidden-tab WebView behavior, keyboard defaults and scrolling are still required.

## Checkpoint test reliability

The existing checkpoint test imposed one 20-second cancellation budget over runtime startup, two turns, multiple Git snapshots and rewind. It reproduced cancellation during rewind. With a 90-second end-to-end test budget and phase timing output, it passed: first turn/checkpoint 7.38 s, both turns/checkpoints 10.00 s, rewind 12.83 s (15 s total test). Production per-Git-command timeouts and functional assertions are unchanged. This is test-budget stabilization, not a claim of improved product performance.

The full host suite subsequently passed all 129 tests, including checkpoint rewind. Results are retained under `TestResults/browser-reliability-final`; isolated phase timings are under `TestResults/checkpoint-reliability`.

## Acceptance limits

The Windows computer-use skill was attempted for native Settings acceptance. App launch twice failed with `GetCursorPos failed: Access is denied. (0x80070005)`; no native acceptance or settings changes are claimed.

Automated input tests cover bounds, invalid actions/keys/conditions and permission classification. An isolated installed-Pi bridge test forwards all three new actions with explicit tab IDs and verifies inspect-only rejection for keyboard/scroll. It uses a fake desktop response, so it does not verify native WebView execution or require an LLM/provider account.
