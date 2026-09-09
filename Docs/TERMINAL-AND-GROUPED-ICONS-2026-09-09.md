# Saved terminals, activity labels, and grouped icons

Implements TERM-05, TERM-06, and PROJ-07 in [tracking.md](../tracking.md).
Protocol **47** requires updating both Windows host and client before connecting.

## User behavior

- Terminal output and session metadata now survive host restart. Previously running sessions reopen as saved history with a Restart action. Restart opens a fresh shell; running commands and shell variables do not survive host restart.
- Clear removes the selected terminal's visible output and saved history and updates other connected viewers. Close removes that session and its saved history. Output arriving after Clear is retained normally.
- Terminal names show a detected child executable while it runs. The summary reports checking activity, a running child, or no child process detected. A failed process snapshot preserves the previous reading.
- Changing an icon in project customization applies it to the currently grouped checkouts on that host. Images are copied into host storage, so every member uses the same asset. Clearing the icon field restores each checkout's repository icon. Scripts, trust, and model defaults remain specific to each checkout. Editing scripts alone preserves icon selection, including Automatic.

## Implementation and T3 comparison

Reference: local `../t3code` at `0a590fa01af66ec135d2ebf2d5542b08a37dc275` (2026-09-04), particularly `apps/server/src/terminal/Manager.ts` and `apps/web/src/components/ProjectSettingsPanel.tsx`.

T3 keeps bounded terminal history on disk and reconnects using stable terminal identities. PiStation retains its existing 1,048,576-character output limit, adds 5,000-line and 8 MiB UTF-8 ceilings, and stores an atomic JSON snapshot per session under the host data root's `terminal-history` directory. Small output chunks avoid copying the entire transcript for each append. Saves are coalesced over roughly 100 ms, with explicit flushes at creation, Clear, and shutdown. An abrupt crash can lose the most recent unflushed output. Startup restores up to 128 saved sessions; creating a terminal also prunes excess inactive sessions, keeping the newest. ANSI styling remains, while query/reply sequences are filtered from hydration so replay does not generate old terminal responses. Stream epochs distinguish restored history from the previous host's sequence numbers.

T3's activity status is a process-tree heuristic. PiStation uses one native Windows Toolhelp snapshot per second across all active terminals instead of launching PowerShell/CIM helpers. It labels the first direct child executable. This does not identify true foreground jobs or reliably detect shell built-ins; background children also count as active.

T3 applies an icon update to each current group member. PiStation adds an icon-only host operation that validates repository membership and updates all selected records in one SQLite transaction. Dedicated icon overrides avoid copying or freezing other checkout settings. Images use the existing validated, content-addressed host icon storage. New checkouts added later do not automatically inherit a previous group edit. Both icon changes and durable terminal clearing require Operate access; ReadOnly viewers can see the resulting icons and history.

## Verification

Regression coverage includes bounded Unicode history, split ANSI sequences, atomic file replacement and invalid metadata, restart epochs, real ConPTY restart and exit metadata, persistent Clear and Close, native child detection, group image/emoji/Automatic saves and checkout independence, TLS endpoint round trips and permissions, and desktop activity labels and restart controls.

Focused and full code-test results are recorded in the master tracker's evidence log. Native WinUI interaction and physical two-computer acceptance remain under DEL-03/DEL-04/DEL-09; a successful build or managed test does not certify those checks.

## Native acceptance

1. Open two terminals, produce distinct output, and restart the host. Reopen the saved layout, verify both histories and the saved-history hint, and restart one session into a fresh shell.
2. Run a child executable and observe its terminal label. After it exits, verify the shell name and no-child summary. Check the selector and accessibility announcements.
3. Clear one terminal, reconnect, then restart the host and confirm the cleared output stays absent. Close the other terminal and confirm it stays removed.
4. Group two checkouts of the same repository with different scripts/defaults. Save an emoji, then a local or remote-uploaded image, and verify both icons. Clear the field and verify each checkout's automatic icon. Change a script and confirm peer settings and icon selection remain intact.
