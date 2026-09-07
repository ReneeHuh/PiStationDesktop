# GitHub pull-request review milestone

PiStation remains a Pi-only, local Windows application. Protocol v28 adds GitHub review; it does not add agent runtimes or remote clients.

## Delivered

- PR body/metadata, commits, latest-head checks, general comments, submitted reviews, inline discussions and hosted patches.
- Inline drafts on real old/new diff lines; submit comments, approvals or request-changes reviews. Comments/request-changes require a summary.
- Inline discussion replies and permission-aware resolve/reopen. Successful UI writes reload server state. General comments and submitted review bodies are read-only.
- Atomic local drafts keyed by project/repository/PR, preserving the inspected head, summary, comments, reply and pending operation ID. Files live in `pr-review-drafts` beside browser-automation data.
- Current-head/repository/line validation, immutable submitted payloads and durable receipts. Disconnects never trigger automatic retries. Pending drafts cannot be discarded or resubmitted; **Refresh operation history** recovers confirmed outcomes.

## Usage and recovery

Open the existing pull-request review command and select a GitHub PR. Creation/link/open actions remain available. Install/authenticate `gh` separately; PiStation does not manage GitHub credentials.

Send or clear an unsent reply before selecting another discussion. A changed head locks the old draft: copy text you want to keep, explicitly discard the stale draft, then review current diff lines before submitting again. Corrupt drafts are retained and not silently overwritten.

An uncertain write stays locked after reconnect. Inspect the provider and recent hosting operations; a missing local receipt does not prove the remote write failed.

## Bounds and remaining scope

Reads stop at 300 files, 20,000 diff lines and 100 items per GitHub connection. Omitted/binary patches and pagination limits are reported. Reviews allow 50 inline comments, 32,768 characters per body and a bounded aggregate payload.

GitLab/Azure retain existing list/create/action support; detailed review is GitHub-only. Cross-project filtering, checkout/worktree creation, title/body/comment editing, reactions and additional adapters remain future work. GIT-01/GIT-02 stay partial; the spreadsheet was not re-rated.

## Verification

Three Luna subagents implemented host support, desktop presentation and tests. The primary agent integrated/reviewed their code and corrected process-start, GraphQL/permission, diff-coordinate, draft-isolation, pending-recovery and payload-boundary problems.

Provider tests use isolated fixtures and temporary local Git repositories; no reviews/comments were posted. Authenticated GitHub acceptance is pending because `gh` is absent. Native visual acceptance is pending: computer-use launch failed twice with `GetCursorPos failed: Access is denied (0x80070005)`.

Debug x64 solution build passed with zero warnings/errors. The serial regression suite passed **286 tests**, with **6 opt-in real-Pi tests skipped**; TRX results are under `TestResults/pr-review-validation`. A checkpoint test timed out in the first parallel run and passed in the serial rerun. The visual contract passed (24 states, 4 responsive layouts, 3 text scales); this is not populated native review acceptance.

## Review corrections verified 2026-09-07

Three Luna subagents addressed the subsequent review findings; the primary agent independently reviewed and corrected their patches:

- Discard, draft saves and submission setup share a lock. Queued submissions cannot use discarded text. Selection changes during successful/cancelled discard preserve the correct draft, and cancellation is checked immediately before file deletion. The original race reproducer now blocks submission without dispatching a write.
- Review API calls capture response status with [`gh api --include`](https://cli.github.com/manual/gh_api). Confirmed validation/permission failures return `Rejected` and retain draft text while unlocking editing. Transport failures, server errors and partial/mixed GraphQL mutation responses remain uncertain. Tests also verify durable receipt replay does not dispatch twice.
- The diff-side selector binds two-way to the retained model instead of resetting visually to Right.
- Discussions, commits and checks have bounded scrolling panels rather than clipped text. Source-contract regression checks guard these controls.

Final Debug x64 build: **zero warnings/errors**. Serial suite: **306 passed, 6 opt-in real-Pi tests skipped, zero failures**. Results are in `TestResults/pr-review-fixes-validation`. The updated visual contract passes. Authenticated GitHub and populated native UI acceptance remain pending; these results do not claim live provider or visual certification.
