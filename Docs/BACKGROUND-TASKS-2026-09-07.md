# Independent background tasks

The composer offers **New task** (Ctrl+Shift+Enter or `/background`). It submits a new Pi thread without navigating away, then clears the accepted source draft so another task can be composed. Ordinary Send still targets the selected thread.

Each task inherits the selected thread's project, persisted Pi model/thinking/runtime-mode settings, workspace mode, and branch as its base branch. Worktree mode creates a separate managed worktree and waits for its setup script before dispatch. Local mode shares the project's working directory; it does not isolate concurrent file edits. No additional runtime or remote host is introduced.

Attachments and citation context are copied into a separate task draft. Accepted sent-content records retain the attachment files. Exact draft revisions prevent clearing newer edits. Earlier tasks continue running while subsequent tasks are submitted; submission waits for preparation and acknowledgement, not turn completion.

## Recovery

- The host persists one submission per source draft ID/revision before creating a task.
- Repeating the same request returns the recorded task/receipt, including after a host restart, without dispatching again.
- After a connection failure the button becomes **Check task**, retaining the exact request in the current app session.
- An interrupted dispatch without a conclusive receipt remains uncertain. Inspect its task before manually starting new work; it is never automatically redispatched.
- Preparation failures preserve the source draft and may leave a task thread for inspection. Edit the draft before submitting a new revision.
- A UI restart does not restore the button's in-memory recovery label, but the host ledger still deduplicates the same draft revision/options.

## Verification

Integration coverage submits three running tasks successively in local and worktree modes, checks distinct threads/worktrees and preserved settings/content, and replays submissions after host restart. Additional tests cover stale drafts, interrupted preparation/dispatch, and conflicting replay options.

Native Windows click/keyboard and layout acceptance remains pending. These embedded-host tasks still stop when the application host is closed.

Regression results: 323 passed (client 107, host 117, protocol 29, Pi RPC 66, command system 4); six opt-in real-Pi tests skipped. TRX reports are in `TestResults/background-task-validation`. The source-level visual contract passed; this does not replace native UI acceptance.
